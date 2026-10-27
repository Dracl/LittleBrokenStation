using System.Text.Json;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>解析出的一条动态（落库形态的原料）。</summary>
public sealed record ParsedDynamic(
    string DynamicId,
    string Type,
    long? AuthorMid,
    string? AuthorName,
    string? Text,
    IReadOnlyList<string> Pics,
    string? Bvid,
    string? PublishedAtUtc,
    // 卡面原料随抓取定格：作者头像、档案三件套（未入站视频卡靠它们不剃光头）。
    string? AuthorFaceUrl = null,
    string? ArchiveTitle = null,
    string? ArchiveCover = null,
    string? ArchiveDuration = null,
    // 底栏三计数（B 站报的评论/转发/赞）+ 转发卡灰块里的原作者名片（卡头归转发人，灰块归原作者）。
    long? StatComment = null,
    long? StatForward = null,
    long? StatLike = null,
    long? OrigAuthorMid = null,
    string? OrigAuthorName = null,
    string? OrigAuthorFace = null,
    // 评论区寻址坐标：B 站自己把评论区挂在 basic.comment_type/comment_id_str 上——
    // 视频卡挂视频评论区（type=1+aid）、图文卡挂相簿（type=11+rid）、纯文/转发卡才是 type=17+动态 id。
    long? CommentType = null,
    string? CommentOid = null);

/// <summary>一条动态评论（root=0 一级楼；子回复 root/parent 挂父楼）。</summary>
public sealed record ParsedComment(
    long Rpid,
    long RootRpid,
    long ParentRpid,
    long? Mid,
    string? Name,
    string? Face,
    string Message,
    long Like,
    string? CtimeUtc);

public enum FeedParseStatus
{
    Ok,
    /// <summary>风控三形态：HTTP 非 200、code=-352、data.v_voucher 现形。调用方弃这轮，不重试连打。</summary>
    RiskControlled,
    /// <summary>其他非 0 code / 结构崩坏。</summary>
    Failed,
}

/// <summary>一页动态的解析结果。</summary>
public sealed record FeedParseResult(
    FeedParseStatus Status,
    IReadOnlyList<ParsedDynamic> Items,
    string? Offset,
    bool HasMore,
    string? Reason);

/// <summary>评论拉取战报。RiskControlled 时调用方弃单不再连打，欠账留下轮补。</summary>
public sealed record CommentFetchOutcome(
    bool Ok,
    bool RiskControlled,
    IReadOnlyList<ParsedComment> Comments,
    string? Error,
    // 一级评论总数（B 站 page.count）：动态流不带 stat，底栏评论数就靠巡捕回执回住。
    int TotalCount = 0);

/// <summary>一个 target 这一轮的拉取战报。Offset=null 表示历史见底/取齐，下轮从头开始。</summary>
public sealed record DynamicFetchOutcome(
    bool Ok,
    bool RiskControlled,
    int Fresh,
    int Seen,
    string? Offset,
    string? Error);

/// <summary>
/// UP 主动态取数员（M6）：GET /x/polymer/web-dynamic/v1/feed/space，
/// 请求姿势完全仿订阅爬工——LbsOwnerPolicy 选号在外层包，WbiRequestExecutor 签 wbi，
/// UA/Referer/Cookie 由 LbsQueryClient 的分流底座供给（Referer 按调研实践用空间页）。
/// features 串照抄 RSSHub 可用集；分页只认 offset（page 参数是官方标注的无效参数）。
/// 解析全走 ParseFeed 纯静态函数——测试直接喂手写 JSON，不联网。
/// </summary>
public sealed class LbsDynamicFetcher(
    LbsQueryClient client,
    IWbiKeyProvider wbiKeys,
    TimeProvider timeProvider,
    ILogger<LbsDynamicFetcher> logger)
{
    private const string FeedPath = "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space";
    private const string ReplyOldPath = "https://api.bilibili.com/x/v2/reply"; // 一级热评走这家：新 main 接口不返 page，总数只在老口的 page.count
    private const string ReplySubPath = "https://api.bilibili.com/x/v2/reply/reply";
    private const int TypeDynamicReply = 17; // B 站评论区对动态的编号
    private const int MaxTopComments = 20;
    private const int MaxSubThreads = 3; // 只给前 3 个带子回复的楼挖到底，防单条动态吃光一轮预算
    private const int MaxSubPerThread = 20;
    private const string Features = "itemOpusStyle,listOnlyfans,opusBigCover,onlyfansVote";

    /// <summary>单 target 单轮续页封顶：五页足够追平一小时的量，再多就是在刷库。</summary>
    public const int MaxPagesPerRound = 5;

    public async Task<DynamicFetchOutcome> FetchTargetAsync(
        long mid, string? startOffset, Func<ParsedDynamic, Task<bool>> persist, CancellationToken cancellationToken)
    {
        var offset = startOffset;
        var fresh = 0;
        var seen = 0;
        string? lastOffset = startOffset;

        for (var page = 0; page < MaxPagesPerRound; page++)
        {
            FeedParseResult parsed;
            try
            {
                parsed = await FetchPageAsync(mid, offset, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "动态取数：target {Mid} 第 {Page} 页翻车", mid, page + 1);
                return new DynamicFetchOutcome(false, false, fresh, seen, lastOffset, ex.Message);
            }

            if (parsed.Status == FeedParseStatus.RiskControlled)
            {
                logger.LogWarning("动态取数：target {Mid} 撞上风控（{Reason}），这轮弃了，下小时再来", mid, parsed.Reason);
                return new DynamicFetchOutcome(false, true, fresh, seen, lastOffset, parsed.Reason);
            }

            if (parsed.Status == FeedParseStatus.Failed)
            {
                logger.LogWarning("动态取数：target {Mid} 没取成（{Reason}）", mid, parsed.Reason);
                return new DynamicFetchOutcome(false, false, fresh, seen, lastOffset, parsed.Reason);
            }

            var pageFresh = 0;
            foreach (var item in parsed.Items)
            {
                seen++;
                if (await persist(item))
                {
                    pageFresh++;
                    fresh++;
                }
            }

            // 页内全 IGNORE = 历史见底；has_more=false 或没给新游标 = 取齐。两种都归零游标下轮从头。
            if (parsed.Items.Count == 0 || pageFresh == 0 || !parsed.HasMore || string.IsNullOrEmpty(parsed.Offset))
            {
                return new DynamicFetchOutcome(true, false, fresh, seen, null, null);
            }

            offset = parsed.Offset;
            lastOffset = parsed.Offset;
        }

        // 五页还没见底：存游标下轮续着翻。
        return new DynamicFetchOutcome(true, false, fresh, seen, lastOffset, null);
    }

    private async Task<FeedParseResult> FetchPageAsync(long mid, string? offset, CancellationToken cancellationToken)
    {
        var paras = new Dictionary<string, object?>
        {
            ["host_mid"] = mid,
            ["timezone_offset"] = -480,
            ["platform"] = "web",
            ["features"] = Features,
        };
        if (!string.IsNullOrEmpty(offset))
        {
            paras["offset"] = offset;
        }

        return await WbiRequestExecutor.ExecuteAsync(
            wbiKeys,
            async (keys, wts) =>
            {
                var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(paras, keys.ImgKey, keys.SubKey, wts));
                try
                {
                    var raw = await client.GetStringAsync(
                        new BilibiliHttpRequest($"{FeedPath}?{query}", $"https://space.bilibili.com/{mid}/"),
                        cancellationToken);
                    var parsed = ParseFeed(raw);
                    // 空手而归最迷惑人：留一段原货供断案（code 正常却零条，多半是身份/参数问题而非真无动态）。
                    if (parsed.Status == FeedParseStatus.Ok && parsed.Items.Count == 0)
                    {
                        logger.LogWarning("动态取数：mid {Mid} 空手而归，原货首 {Snippet}", mid,
                            raw.Length > 400 ? raw[..400] : raw);
                    }

                    return parsed;
                }
                catch (HttpRequestException ex)
                {
                    // 裸请求被 HTTP 层直接拒（调研实测 412）也按风控办：弃轮不连打。
                    return new FeedParseResult(FeedParseStatus.RiskControlled, [], null, false, $"HTTP 层拒了：{ex.Message}");
                }
            },
            timeProvider,
            cancellationToken);
    }

    // ─── 纯解析层：字段路径照调研报告的表，三套图片路径依次兜底；测试直喂 JSON ───

    /// <summary>
    /// 拉一条动态的评论：热评前 20 条 + 前 3 个带子回复的楼各挖 20 条子回复。
    /// 风控与动态流同规矩：一旦现形立即弃单，剩下的楼不追，欠账由 worker 下轮补。
    /// </summary>
    public async Task<CommentFetchOutcome> FetchCommentsAsync(string dynamicId, long? commentType, string? commentOid, CancellationToken cancellationToken)
    {
        var replyType = commentType is > 0 ? commentType.Value : TypeDynamicReply;
        var replyOid = string.IsNullOrWhiteSpace(commentOid) ? dynamicId : commentOid;
        var collected = new List<ParsedComment>();
        var (mainStatus, topComments, subRoots, totalCount, reason) = await FetchRepliesAsync(
            ReplyOldPath, dynamicId, replyType, replyOid, rootRpid: 0, extra: new Dictionary<string, object?>
            {
                ["sort"] = 2, // 热度序，与新 main 的 mode=3 同源；而且只有老口肯给 page.count 总评数
                ["pn"] = 1,
                ["ps"] = MaxTopComments,
            },
            MaxTopComments,
            collectRoots: true,
            cancellationToken);
        if (mainStatus != FeedParseStatus.Ok)
        {
            return new CommentFetchOutcome(false, mainStatus == FeedParseStatus.RiskControlled, collected, reason);
        }

        collected.AddRange(topComments);
        foreach (var root in subRoots.Take(MaxSubThreads))
        {
            var (subStatus, subComments, _, _, subReason) = await FetchRepliesAsync(
                ReplySubPath, dynamicId, replyType, replyOid, rootRpid: root, extra: new Dictionary<string, object?>
                {
                    ["pn"] = 1, // 楼中楼翻页认 pn/ps（同归档器 FetchSubsAsync）
                    ["ps"] = MaxSubPerThread,
                },
                MaxSubPerThread,
                collectRoots: false,
                cancellationToken);
            if (subStatus == FeedParseStatus.RiskControlled)
            {
                // 子楼里撞风控：一级热评已经到手，就此收兵，剩余子楼留下轮。
                return new CommentFetchOutcome(false, true, collected, subReason, totalCount);
            }

            collected.AddRange(subComments);
        }

        return new CommentFetchOutcome(true, false, collected, null, totalCount);
    }

    private async Task<(FeedParseStatus Status, IReadOnlyList<ParsedComment> Replies, IReadOnlyList<long> Roots, int TotalCount, string? Reason)> FetchRepliesAsync(
        string path, string dynamicId, long replyType, string replyOid, long rootRpid, Dictionary<string, object?> extra, int max, bool collectRoots, CancellationToken cancellationToken)
    {
        var paras = new Dictionary<string, object?>
        {
            ["type"] = replyType,
            ["oid"] = replyOid,
        };
        foreach (var (key, value) in extra)
        {
            paras[key] = value;
        }

        if (rootRpid != 0)
        {
            paras["root"] = rootRpid;
        }

        return await WbiRequestExecutor.ExecuteAsync(
            wbiKeys,
            async (keys, wts) =>
            {
                var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(paras, keys.ImgKey, keys.SubKey, wts));
                try
                {
                    var raw = await client.GetStringAsync(
                        new BilibiliHttpRequest($"{path}?{query}", $"https://t.bilibili.com/{dynamicId}"),
                        cancellationToken);
                    return ParseReplies(raw, rootRpid, max, collectRoots);
                }
                catch (HttpRequestException ex)
                {
                    return (FeedParseStatus.RiskControlled, (IReadOnlyList<ParsedComment>)[], (IReadOnlyList<long>)[], 0, $"HTTP 层拒了：{ex.Message}");
                }
            },
            timeProvider,
            cancellationToken);
    }

    /// <summary>评论页纯解析：与 ParseFeed 同一张嘴认 code/v_voucher，replies[] 按形状剥平；顺手拿 page.count 当一级评论总数。测试直喂 JSON。</summary>
    public static (FeedParseStatus Status, IReadOnlyList<ParsedComment> Replies, IReadOnlyList<long> Roots, int TotalCount, string? Reason) ParseReplies(
        string json, long rootRpid, int max, bool collectRoots)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (FeedParseStatus.Failed, [], [], 0, "B 站回的不是 JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : -1;
            if (code == -352)
            {
                return (FeedParseStatus.RiskControlled, [], [], 0, "code=-352 风控校验失败");
            }

            if (code != 0)
            {
                // -404 = 这个评论区地址本家没开评论（如开播提醒卡、被销号的作品）：当空评论页交差，免它永世欠账。
                if (code == -404)
                {
                    return (FeedParseStatus.Ok, [], [], 0, null);
                }

                return (FeedParseStatus.Failed, [], [], 0, $"B 站返回 code={code}");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return (FeedParseStatus.Failed, [], [], 0, "data 没影");
            }

            if (data.TryGetProperty("v_voucher", out var voucher) && voucher.ValueKind != JsonValueKind.Null)
            {
                return (FeedParseStatus.RiskControlled, [], [], 0, "data.v_voucher 现形");
            }

            // page.count = 一级评论总数（不管抽了几条），拿回住底栏评论数。
            var totalCount = 0;
            if (data.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
            {
                totalCount = (int)(Num(page, "count") ?? 0);
            }

            var replies = new List<ParsedComment>();
            var roots = new List<long>();
            if (data.TryGetProperty("replies", out var repliesEl) && repliesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in repliesEl.EnumerateArray())
                {
                    if (replies.Count >= max)
                    {
                        break;
                    }

                    var rpid = Num(el, "rpid") ?? 0;
                    var message = Str(Path(el, "content", "message"));
                    if (rpid <= 0 || string.IsNullOrWhiteSpace(message))
                    {
                        continue; // 没楼号或空正文的行楼都开不了，丢了比猜强
                    }

                    var ctime = Num(el, "ctime");
                    replies.Add(new ParsedComment(
                        rpid,
                        rootRpid,
                        rootRpid == 0 ? 0 : rootRpid,
                        Num(Path(el, "member"), "mid"),
                        Str(Path(el, "member", "uname")),
                        Str(Path(el, "member", "face")),
                        message.Trim(),
                        Num(el, "like") ?? 0,
                        ctime is > 0 ? DateTimeOffset.FromUnixTimeSeconds(ctime.Value).ToString("O") : null));

                    if (collectRoots && (Num(el, "rcount") ?? 0) > 0)
                    {
                        roots.Add(rpid);
                    }
                }
            }

            return (FeedParseStatus.Ok, replies, roots, totalCount, null);
        }
    }

    public static FeedParseResult ParseFeed(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new FeedParseResult(FeedParseStatus.Failed, [], null, false, "B 站回的不是 JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : -1;
            if (code == -352)
            {
                return new FeedParseResult(FeedParseStatus.RiskControlled, [], null, false, "code=-352 风控校验失败");
            }

            if (code != 0)
            {
                return new FeedParseResult(FeedParseStatus.Failed, [], null, false, $"B 站返回 code={code}");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return new FeedParseResult(FeedParseStatus.Failed, [], null, false, "data 没影");
            }

            if (data.TryGetProperty("v_voucher", out var voucher) && voucher.ValueKind != JsonValueKind.Null)
            {
                return new FeedParseResult(FeedParseStatus.RiskControlled, [], null, false, "data.v_voucher 现形");
            }

            var hasMore = data.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
            var offset = Str(Path(data, "offset"));

            var items = new List<ParsedDynamic>();
            if (data.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsEl.EnumerateArray())
                {
                    if (ParseItem(item) is { } parsed)
                    {
                        items.Add(parsed);
                    }
                }
            }

            return new FeedParseResult(FeedParseStatus.Ok, items, offset, hasMore, null);
        }
    }

    internal static ParsedDynamic? ParseItem(JsonElement item)
    {
        var dynamicId = Str(Path(item, "id_str"));
        if (string.IsNullOrWhiteSpace(dynamicId))
        {
            return null; // 没 id 落不了库，丢掉比猜强
        }

        var type = Str(Path(item, "type")) ?? "UNKNOWN";
        var isForward = type.Contains("FORWARD", StringComparison.OrdinalIgnoreCase);
        var orig = Path(item, "orig");
        var src = isForward && orig is not null ? orig.Value : item;

        // 作者与时间：卡头代表「发帖的人」。FORWARD 时顶层是转发人、顶层 pub_ts 是转发时刻——与 B 站卡同款；
        // 原文正文、图与视频信息仍从 orig 里挖，灰块里的原作者名由「转发自 XX：」行携带。
        var srcAuthor = Path(src, "modules", "module_author");
        var topAuthor = Path(item, "modules", "module_author");
        var authorMid = Num(topAuthor, "mid") ?? Num(srcAuthor, "mid");
        var authorName = Str(Path(topAuthor, "name")) ?? Str(Path(srcAuthor, "name"));
        var authorFace = Str(Path(topAuthor, "face")) ?? Str(Path(srcAuthor, "face"));
        var pubTs = Num(topAuthor, "pub_ts") ?? Num(srcAuthor, "pub_ts");
        // 发榜时间拿不到就 null：页面宁可显「—」，也好过给老动态盖个「抓取此刻」的戳穿越给人看。
        string? published = pubTs is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(pubTs.Value).ToString("O")
            : null;

        // 正文：顶层评语 + （转发时）原文评语 + 图文正文，空段剔除、相邻重复剔除。
        var comment = Str(Path(item, "modules", "module_dynamic", "desc", "text"));
        var srcDesc = Str(Path(src, "modules", "module_dynamic", "desc", "text"));
        var srcOpus = Str(Path(src, "modules", "module_dynamic", "major", "opus", "summary", "text"));
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(comment))
        {
            parts.Add(comment.Trim());
        }

        if (isForward)
        {
            // 「转发自」认的是原作者（src=orig），不是卡头上的转发人——否则就成了自己转发自自己。
            var origName = Str(Path(srcAuthor, "name"));
            var fwdFrom = !string.IsNullOrWhiteSpace(origName) ? origName
                : !string.IsNullOrWhiteSpace(authorName) ? authorName : "他人";
            parts.Add($"转发自 {fwdFrom}：");
        }

        if (!string.IsNullOrWhiteSpace(srcDesc) && (parts.Count == 0 || parts[^1] != srcDesc.Trim()))
        {
            parts.Add(srcDesc.Trim());
        }

        if (!string.IsNullOrWhiteSpace(srcOpus) && (parts.Count == 0 || parts[^1] != srcOpus.Trim()))
        {
            parts.Add(srcOpus.Trim());
        }

        // 开播提醒卡：把直播题接进正文，页面直播卡有料可显。
        var liveTitle = Str(Path(src, "modules", "module_dynamic", "major", "live", "title"));
        if (!string.IsNullOrWhiteSpace(liveTitle) && (parts.Count == 0 || parts[^1] != liveTitle.Trim()))
        {
            parts.Add(liveTitle.Trim());
        }

        var text = parts.Count == 0 ? null : string.Join("\n\n", parts);

        // 图片三套路径依次兜底（图文 opus / 旧相簿 draw / 专栏 article），FORWARD 全从 orig 里取。
        var pics = CollectPics(src);
        var bvid = Str(Path(src, "modules", "module_dynamic", "major", "archive", "bvid"));
        var archiveTitle = Str(Path(src, "modules", "module_dynamic", "major", "archive", "title"));
        var archiveCover = Str(Path(src, "modules", "module_dynamic", "major", "archive", "cover"));
        var archiveDuration = Str(Path(src, "modules", "module_dynamic", "major", "archive", "duration_text"));

        // 评论区寻址：B 站自己的坐标优先（视频卡→视频评论区，图文卡→相簿）；老卡没带就退回动态本家 17+id。
        var commentType = Num(Path(item, "basic"), "comment_type");
        var commentOid = Str(Path(item, "basic", "comment_id_str"));

        // 底栏三计数：顶层 stat 是整卡口径（FORWARD 的评论/赞也算在转发卡上），与 B 站卡底同源。
        // 实测 space feed 不带 stat（B 站只给关注页 feed 发），拿不到就留 null，底栏评论数由巡捕回执补写。
        var stat = Path(item, "modules", "module_dynamic", "stat");
        var statComment = Num(Path(stat, "comment"), "count");
        var statForward = Num(Path(stat, "forward"), "count");
        var statLike = Num(Path(stat, "like"), "count");

        // 转发灰块的原作者名片：FORWARD 时 srcAuthor 就是 orig 的 module_author，直接采脸与名。
        long? origAuthorMid = null;
        string? origAuthorName = null;
        string? origAuthorFace = null;
        if (isForward)
        {
            origAuthorMid = Num(srcAuthor, "mid");
            origAuthorName = Str(Path(srcAuthor, "name"));
            origAuthorFace = Str(Path(srcAuthor, "face"));
        }

        return new ParsedDynamic(dynamicId, type, authorMid, authorName, text, pics, bvid, published,
            authorFace, archiveTitle, archiveCover, archiveDuration,
            statComment, statForward, statLike, origAuthorMid, origAuthorName, origAuthorFace,
            commentType, commentOid);
    }

    private static IReadOnlyList<string> CollectPics(JsonElement src)
    {
        var pics = ReadStringArray(Path(src, "modules", "module_dynamic", "major", "opus", "pics"), "url");
        if (pics.Count > 0)
        {
            return pics;
        }

        pics = ReadStringArray(Path(src, "modules", "module_dynamic", "major", "draw", "items"), "src");
        if (pics.Count > 0)
        {
            return pics;
        }

        // 专栏封面是裸字符串数组。
        return ReadStringArray(Path(src, "modules", "module_dynamic", "major", "article", "covers"), null);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement? node, string? propName)
    {
        if (node is not { ValueKind: JsonValueKind.Array } arr)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            var value = propName is null ? Str(el) : Str(Path(el, propName));
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value.Trim());
            }
        }

        return list;
    }

    private static JsonElement? Path(JsonElement? element, params string[] names)
    {
        if (element is null)
        {
            return null;
        }

        var cur = element.Value;
        foreach (var name in names)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(name, out var next))
            {
                return null;
            }

            cur = next;
        }

        return cur;
    }

    private static string? Str(JsonElement? node) =>
        node is { ValueKind: JsonValueKind.String } el ? el.GetString() : null;

    private static long? Num(JsonElement? node, string name)
    {
        if (node is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty(name, out var el))
        {
            return null;
        }

        // pub_ts/mid 有时以字符串下发，两种形态都认。
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
        {
            return n;
        }

        return el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var s) ? s : null;
    }
}
