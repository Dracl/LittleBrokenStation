using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video;
using LBS.Infrastructure;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace LBS.Engine;

/// <summary>粉丝勋章小牌：等级、名称、应援色。</summary>
public sealed record CommentMedal(int Level, string Name, string? Color);

/// <summary>评论快照里的一层楼（主楼与楼中楼共用；新字段全部可空，老快照文件照常能读）。</summary>
public sealed record CommentItem(
    long Rpid,
    string Name,
    string? Face,
    string Content,
    long Like,
    long Time,
    string? IpLocation,
    bool IsTop,
    long? ReplyCount,
    IReadOnlyList<CommentItem>? Sub,
    string? Mid = null,
    int? Level = null,
    IReadOnlyList<CommentMedal>? Medals = null,
    IReadOnlyList<string>? Pictures = null,
    string? ParentName = null,
    string? FaceLocal = null,
    bool? UpLiked = null,
    IReadOnlyList<string?>? PictureLocals = null);

/// <summary>{base}.comments.json 的完整载荷，观看页评论区直接消费。</summary>
public sealed record CommentSnapshot(
    int Schema,
    long Aid,
    string? Bvid,
    DateTimeOffset FetchedAtUtc,
    int Total,
    CommentItem? Top,
    IReadOnlyList<CommentItem> Replies,
    IReadOnlyDictionary<string, string>? Emotes = null,
    // 表情元数据留档（转义符 → id/包号/类型/人类叫法）：B 站对一部分 UP 专属表情（如 [UP_1802654492_得意]）
    // 连 content.emote 的 key 都不下发，我们永久拿不到图，但接口肯给的这几个字段是将来按包回填的唯一线索。
    // 存这个不存 url：本地图的路径已在 Emotes 里，缺图那批本来就没 url。
    IReadOnlyDictionary<string, EmoteMetaEntry>? EmoteMeta = null);

/// <summary>一枚表情的身份卡（B 站 content.emote 里本来就带的几个字段，我们曾经丢掉）。</summary>
public sealed record EmoteMetaEntry(
    int? Id = null,
    int? PackageId = null,
    int? Type = null,
    string? JumpTitle = null);

/// <summary>
/// 评论归档器：用 fork 的 wbi 签名设施翻 x/v2/reply/main，把评论区按提交时刻的样貌存成
/// {base}.comments.json 放在成片旁边；头像全量落盘到 data/avatars（热链不过夜）。
/// 热评取前若干楼的楼中楼（每楼一页，ps=20），其余楼层由观看页按需补采（FetchSubsAsync）。
/// 抓取失败一律返回 null / 空集，绝不向上抛——评论是锦上添花，不许挡下载。
/// </summary>
public sealed class LbsCommentArchiver(
    LbsQueryClient client,
    IWbiKeyProvider wbiKeys,
    TimeProvider timeProvider,
    DataPaths paths,
    ILogger<LbsCommentArchiver> logger)
{
    private const int MaxMainPages = 10;
    private const int MaxSubFetches = 20;
    private const int SubPageSize = 20;
    private const int MaxAvatarDownloads = 800;
    private const int MaxEmoteDownloads = 400;
    private const int MaxPictureDownloads = 300;
    private const string Referer = "https://www.bilibili.com";

    /// <summary>快照读写共用的 JSON 约定（camelCase，null 省略）。</summary>
    public static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>抓一条视频的评论快照；失败或无 aid 时返回 null。</summary>
    public async Task<CommentSnapshot?> FetchAsync(string bvid, long? knownAid, CancellationToken cancellationToken)
    {
        try
        {
            var aid = knownAid;
            if (aid is null or <= 0)
            {
                aid = await ResolveAidAsync(bvid, cancellationToken);
                if (aid is null or <= 0)
                {
                    logger.LogWarning("评论归档跳过：{Bvid} 解析不到 aid", bvid);
                    return null;
                }
            }

            var collected = new List<ReplyRaw>();
            var seen = new HashSet<long>();
            ReplyPage? firstPage = null;

            void Absorb(ReplyPage? body)
            {
                if (body is null)
                {
                    return;
                }

                firstPage ??= body;
                foreach (var reply in EnumerateFirstScreen(body))
                {
                    if (reply.Rpid > 0 && seen.Add(reply.Rpid))
                    {
                        collected.Add(reply);
                    }
                }
            }

            Absorb((await RequestWbiAsync<ReplyPageOrigin>(
                "https://api.bilibili.com/x/v2/reply/main",
                new Dictionary<string, object?> { ["type"] = 1, ["oid"] = aid, ["mode"] = 3, ["next"] = 1 },
                "评论首页",
                cancellationToken))?.Data);

            for (var next = 2; next <= MaxMainPages; next++)
            {
                if (collected.Count == 0)
                {
                    break;
                }

                await Delay(cancellationToken);
                var more = await RequestWbiAsync<ReplyPageOrigin>(
                    "https://api.bilibili.com/x/v2/reply/main",
                    new Dictionary<string, object?> { ["type"] = 1, ["oid"] = aid, ["mode"] = 3, ["next"] = next },
                    $"评论第 {next} 页",
                    cancellationToken);

                var before = collected.Count;
                Absorb(more?.Data);
                if (collected.Count == before)
                {
                    break; // B 站不再给新楼，收工
                }
            }

            // 给回复最活跃的楼层预配楼中楼，预算 20 楼，其余楼层观看页按需补采。
            var subBudget = MaxSubFetches;
            foreach (var raw in collected
                         .Where(r => EffectiveReplyCount(r) > 0)
                         .OrderByDescending(EffectiveReplyCount)
                         .Take(MaxSubFetches))
            {
                if (subBudget <= 0 || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                subBudget--;
                await Delay(cancellationToken);
                var sub = await RequestWbiAsync<SubReplyPageOrigin>(
                    "https://api.bilibili.com/x/v2/reply/reply",
                    new Dictionary<string, object?>
                    {
                        ["type"] = 1, ["oid"] = aid, ["root"] = raw.Rpid, ["pn"] = 1, ["ps"] = SubPageSize,
                    },
                    $"楼中楼 {raw.Rpid}",
                    cancellationToken);
                raw.LbsSub = sub?.Data?.Replies is { Count: > 0 } list
                    ? list.Where(r => r.Rpid > 0).ToList()
                    : null;
            }

            // x/v2/reply/main 不给置顶，老版 /x/v2/reply 的 upper.top 才有：补采一次，失败不拦。
            ReplyRaw? topOverride = await FetchUpperTopAsync(aid.Value, cancellationToken);

            // 置顶补采来的 topOverride 不在 collected 里，头像/表情/图片的落盘集合要把它带上。
            var allRaw = topOverride is null ? (IEnumerable<ReplyRaw>)collected : collected.Append(topOverride);
            var faceMap = await CacheAvatarsAsync(Flatten(allRaw), cancellationToken);
            var emoteMap = await CacheEmotesAsync(Flatten(allRaw), cancellationToken);
            // 元数据与落盘分开走：落盘只认有 url 的，留档要连没 url 的一起收（那批才是回填时唯一能对上号的）。
            var emoteMeta = CollectEmoteMeta(Flatten(allRaw));
            var pictureMap = await CachePicturesAsync(Flatten(allRaw), cancellationToken);

            var snapshot = MapSnapshot(aid.Value, bvid, firstPage, collected, faceMap, topOverride, pictureMap, emoteMap, emoteMeta);
            logger.LogInformation(
                "评论归档完成：{Bvid} 主楼 {Count} 条（B 站报总数 {Total}），头像落盘 {Avatars} 张",
                bvid, snapshot.Replies.Count, snapshot.Total, faceMap.Count);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "评论归档失败（不影响馆藏）：{Bvid}", bvid);
            return null;
        }
    }

    /// <summary>观看页按需补采某楼的楼中楼（page 从 1 起）。失败返回空表，不抛。</summary>
    public async Task<IReadOnlyList<CommentItem>> FetchSubsAsync(long aid, long rpid, int page, CancellationToken cancellationToken)
    {
        try
        {
            var sub = await RequestWbiAsync<SubReplyPageOrigin>(
                "https://api.bilibili.com/x/v2/reply/reply",
                new Dictionary<string, object?>
                {
                    ["type"] = 1, ["oid"] = aid, ["root"] = rpid, ["pn"] = Math.Max(1, page), ["ps"] = SubPageSize,
                },
                $"楼中楼 {rpid} 第 {page} 页",
                cancellationToken);

            var raws = sub?.Data?.Replies?.Where(r => r.Rpid > 0).ToList() ?? [];
            var faceMap = await CacheAvatarsAsync(raws, cancellationToken);
        var pictureMap = await CachePicturesAsync(raws, cancellationToken);
            var parentNames = ResolveParentNames(raws, rpid);
            return raws.Select((r, index) => ToItem(r, false, faceMap, parentNames[index], pictureMap)).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "楼中楼补采失败 aid={Aid} rpid={Rpid} page={Page}", aid, rpid, page);
            return [];
        }
    }

    /// <summary>bvid 换 aid：走 wbi 版 video/view，拿不到返回 null。</summary>
    public async Task<long?> ResolveAidAsync(string bvid, CancellationToken cancellationToken)
    {
        try
        {
            var view = await WbiRequestExecutor.ExecuteAsync(
                wbiKeys,
                (keys, wts) => client.VideoViewInfoAsync(keys, wts, bvid, -1, cancellationToken),
                timeProvider,
                cancellationToken);
            return view is { Aid: > 0 } ? view.Aid : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "aid 解析失败：{Bvid}", bvid);
            return null;
        }
    }

    /// <summary>把「首页 body + 已收集主楼」拼成快照。置顶依序从 topOverride / upper.top / top_replies / data.top 里找。</summary>
    public static CommentSnapshot MapSnapshot(
        long aid,
        string? bvid,
        ReplyPage? firstPage,
        IReadOnlyList<ReplyRaw> collected,
        IReadOnlyDictionary<string, string>? faceMap = null,
        ReplyRaw? topOverride = null,
        IReadOnlyDictionary<string, string>? pictureMap = null,
        IReadOnlyDictionary<string, string>? emoteMap = null,
        IReadOnlyDictionary<string, EmoteMetaEntry>? emoteMeta = null)
    {
        var topRaw = topOverride
            ?? firstPage?.Upper?.Top
            ?? firstPage?.TopReplies?.FirstOrDefault(r => r.Rpid > 0)
            ?? firstPage?.TopHolder?.Top;
        var top = topRaw is { Rpid: > 0 } ? ToItem(topRaw, isTop: true, faceMap, null, pictureMap) : null;
        var replies = collected
            .Where(r => top is null || r.Rpid != top.Rpid)
            .Select(r => ToItem(r, isTop: false, faceMap, null, pictureMap))
            .ToList();
        var total = Math.Max(firstPage?.Page?.Count ?? 0, replies.Count + (top is null ? 0 : 1));
        return new CommentSnapshot(1, aid, bvid, DateTimeOffset.UtcNow, total, top, replies,
            emoteMap is { Count: > 0 } ? emoteMap : null,
            emoteMeta is { Count: > 0 } ? emoteMeta : null);
    }

    public static string Serialize(CommentSnapshot snapshot) => System.Text.Json.JsonSerializer.Serialize(snapshot, SnapshotJson);

    private static IEnumerable<ReplyRaw> EnumerateFirstScreen(ReplyPage body)
    {
        foreach (var hot in body.Upper?.Hot ?? [])
        {
            yield return hot;
        }

        foreach (var reply in body.Replies ?? [])
        {
            yield return reply;
        }
    }

    private static IEnumerable<ReplyRaw> Flatten(IEnumerable<ReplyRaw> raws)
    {
        foreach (var raw in raws)
        {
            yield return raw;
            foreach (var subRaw in Flatten(raw.LbsSub ?? []))
            {
                yield return subRaw;
            }
        }
    }

    /// <summary>老版 /x/v2/reply 免 wbi，专为补采 upper.top；失败返回 null。</summary>
    private async Task<ReplyRaw?> FetchUpperTopAsync(long aid, CancellationToken cancellationToken)
    {
        try
        {
            var query = string.Join('&', new Dictionary<string, object?>
            {
                ["type"] = 1, ["oid"] = aid, ["sort"] = 2, ["pn"] = 1, ["ps"] = 3,
            }.Select(kv => $"{kv.Key}={kv.Value}"));
            var raw = await client.GetStringAsync(new BilibiliHttpRequest($"https://api.bilibili.com/x/v2/reply?{query}", Referer), cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
            {
                return null;
            }

            var page = JsonConvert.DeserializeObject<ReplyPageOrigin>(raw)?.Data;
            return page?.Upper?.Top is { Rpid: > 0 } top ? top : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "置顶补采失败 aid={Aid}（无置顶或接口搭桥）", aid);
            return null;
        }
    }

    /// <summary>楼中楼的「回复 @谁」：直接回根楼的不标；父楼在本页内就取它的昵称；parent_str 是数字（其实是 rpid）则丢弃。</summary>
    public static IReadOnlyList<string?> ResolveParentNames(IReadOnlyList<ReplyRaw> raws, long rootRpid)
    {
        var byRpid = raws.Where(r => r.Rpid > 0).ToDictionary(r => r.Rpid, r => r.Member?.Uname);
        return raws.Select(r =>
        {
            if (r.Parent <= 0 || r.Parent == rootRpid)
            {
                return null;
            }

            if (byRpid.TryGetValue(r.Parent, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return !string.IsNullOrWhiteSpace(r.ParentStr) && !r.ParentStr!.All(char.IsDigit) ? r.ParentStr : null;
        }).ToList();
    }

    private static CommentItem ToItem(
        ReplyRaw raw,
        bool isTop,
        IReadOnlyDictionary<string, string>? faceMap,
        string? parentNameOverride = null,
        IReadOnlyDictionary<string, string>? pictureMap = null)
    {
        var face = raw.Member?.Avatar;
        var faceLocal = face is not null && faceMap is not null && faceMap.TryGetValue(face, out var local) ? local : null;
        var level = raw.Member switch
        {
            { LevelRank: > 0 } => (int)(raw.Member.LevelRank / 1000),
            { LevelInfo.CurrentLevel: > 0 } => raw.Member.LevelInfo.CurrentLevel,
            _ => (int?)null,
        };
        var picUrls = raw.Content?.Pictures is { Count: > 0 } pics
            ? pics.Where(p => Uri.TryCreate(p.ImgSrc, UriKind.Absolute, out _)).Select(p => p.ImgSrc!).Take(9).ToList()
            : null;
        var picLocals = picUrls is { Count: > 0 } && pictureMap is { Count: > 0 }
            ? picUrls.Select(u => pictureMap.TryGetValue(u, out var lp) ? lp : null).ToList()
            : null;
        if (picLocals is not null && picLocals.All(l => l is null))
        {
            picLocals = null; // 全没存下就别占快照体积，前端自会热链回退。
        }

        return new CommentItem(
            raw.Rpid,
            raw.Member?.Uname ?? "匿名",
            face,
            raw.Content?.Message ?? string.Empty,
            raw.Like,
            raw.Ctime,
            ResolveIp(raw),
            isTop,
            EffectiveReplyCount(raw),
            raw.LbsSub?.Select(s => ToItem(s, false, faceMap, null, pictureMap)).ToList(),
            string.IsNullOrWhiteSpace(raw.Member?.Mid) ? null : raw.Member.Mid.Trim(),
            level,
            raw.Member?.Medals is { Count: > 0 } medals
                ? medals.Where(m => !string.IsNullOrWhiteSpace(m.Name)).Select(m => new CommentMedal(m.Level, m.Name!.Trim(), m.Color)).Take(3).ToList()
                : null,
            picUrls,
            parentNameOverride ?? (string.IsNullOrWhiteSpace(raw.ParentStr) || raw.ParentStr!.All(char.IsDigit) ? null : raw.ParentStr.Trim()),
            faceLocal,
            raw.UpAction is null ? null : raw.UpAction.Like,
            picLocals);
    }

    private static string? ResolveIp(ReplyRaw raw)
    {
        var ip = raw.IpLocation ?? raw.Member?.IpLocation ?? raw.ReplyControl?.Location;
        if (string.IsNullOrWhiteSpace(ip)) return null;
        ip = ip.Trim();
        foreach (var prefix in new[] { "IP属地：", "IP属地:", "IP 属地：", "发表于" })
        {
            if (ip.StartsWith(prefix, StringComparison.Ordinal))
            {
                ip = ip[prefix.Length..].Trim();
                break;
            }
        }
        return string.IsNullOrWhiteSpace(ip) ? null : ip;
    }

    private static long EffectiveReplyCount(ReplyRaw raw) => Math.Max(raw.Rcount, raw.ReplyCount);

    /// <summary>头像批量落盘（公共入口，动态评论与 UP 主名片都复用）：data/avatars/{sha1(url)}.{ext}，已存在即跳过。
    /// 存档原则：本地一律存最高清——裸链直取原图，不挂 _220x220 缩略后缀（显示端缩水是 img CSS 的事）；
    /// 单张失败只丢那张（不进 map，前端回退热链）。</summary>
    public Task<Dictionary<string, string>> CacheAvatarUrlsAsync(IEnumerable<string?> urls, CancellationToken cancellationToken)
    {
        return CacheAvatarsCoreAsync(urls, cancellationToken);
    }

    private Task<Dictionary<string, string>> CacheAvatarsAsync(IEnumerable<ReplyRaw> raws, CancellationToken cancellationToken)
    {
        return CacheAvatarsCoreAsync(raws.Select(r => r.Member?.Avatar), cancellationToken);
    }

    private async Task<Dictionary<string, string>> CacheAvatarsCoreAsync(IEnumerable<string?> urls, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = urls
            .Where(u => !string.IsNullOrWhiteSpace(u) && u!.StartsWith("http", StringComparison.Ordinal))
            .Distinct()
            .Take(MaxAvatarDownloads)
            .ToList();

        foreach (var url in targets)
        {
            if (map.TryGetValue(url!, out _))
            {
                continue;
            }

            try
            {
                // 存档原则：本地一律存最高清——裸链直取原图（不挂 _220x220，命名惯例 sha1(url)+原扩展不变）；
                // 半路存下的 0 字节残件算缺件，重下。
                var dest = Path.Combine(paths.Avatars, AvatarFileName(url!));
                if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
                {
                    await client.DownloadFileAsync(Request(url!), dest, cancellationToken);
                }

                map[url!] = "/avatars/" + Path.GetFileName(dest);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "头像下载失败（前端会回退热链）：{Url}", url);
            }
        }

        return map;
    }

    /// <summary>表情落盘（公共入口，动态评论也复用）：只下给到的 (转义符, url) 对；下载失败的映射直接指远程热链。</summary>
    public Task<Dictionary<string, string>> CacheEmoteUrlsAsync(IEnumerable<(string Seq, string Url)> pairs, CancellationToken cancellationToken)
    {
        return CacheEmotesCoreAsync(pairs, cancellationToken);
    }

    private Task<Dictionary<string, string>> CacheEmotesAsync(IEnumerable<ReplyRaw> raws, CancellationToken cancellationToken)
    {
        var pairs = new List<(string Seq, string Url)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in raws)
        {
            foreach (var (seq, emote) in raw.Content?.Emote ?? [])
            {
                if (!string.IsNullOrWhiteSpace(seq) && !string.IsNullOrWhiteSpace(emote.Url)
                    && emote.Url!.StartsWith("http", StringComparison.Ordinal) && seen.Add(seq))
                {
                    pairs.Add((seq, emote.Url!));
                }
            }
        }

        return CacheEmotesCoreAsync(pairs, cancellationToken);
    }

    /// <summary>
    /// 表情元数据收集（与落盘同一次扫，但不靠落盘）：<c>content.emote</c> 里出现过的 token 全收，
    /// 包括 <c>url</c> 为空、根本没进 <see cref="CommentSnapshot.Emotes"/> 的那批——UP 专属表情停发后，
    /// 它们的 package_id 与 jump_title 是日后回填的唯一把手。上限跟下载共用 <see cref="MaxEmoteDownloads"/>，
    /// 不另开一个闸门管份数。
    /// </summary>
    public static Dictionary<string, EmoteMetaEntry> CollectEmoteMeta(IEnumerable<ReplyRaw> raws)
    {
        var meta = new Dictionary<string, EmoteMetaEntry>(StringComparer.Ordinal);
        foreach (var raw in raws)
        {
            foreach (var (seq, emote) in raw.Content?.Emote ?? [])
            {
                if (string.IsNullOrWhiteSpace(seq) || emote is null || meta.Count >= MaxEmoteDownloads)
                {
                    continue;
                }

                if (emote.Id is null && emote.PackageId is null && emote.Type is null && string.IsNullOrWhiteSpace(emote.JumpTitle))
                {
                    continue; // 四个字段全空的条目没考古价值，不收进来占位
                }

                meta[seq] = new EmoteMetaEntry(emote.Id, emote.PackageId, emote.Type, string.IsNullOrWhiteSpace(emote.JumpTitle) ? null : emote.JumpTitle);
            }
        }

        return meta;
    }

    private async Task<Dictionary<string, string>> CacheEmotesCoreAsync(IEnumerable<(string Seq, string Url)> pairs, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (seq, url) in pairs.Take(MaxEmoteDownloads))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var dest = Path.Combine(paths.Emotes, AvatarHash(url) + OriginalExtension(url));
            try
            {
                if (!File.Exists(dest))
                {
                    await client.DownloadFileAsync(Request(url), dest, cancellationToken);
                }

                map[seq] = "/emotes/" + Path.GetFileName(dest);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                map[seq] = url; // 没存下就热链兑底，总比裸转义符强。
            }
        }

        return map;
    }

    /// <summary>评论图片落盘（公共入口，动态九宫格/封面也复用）：data/cmt-img 原图（灯箱要看原图）；
    /// 失败的不进映射（前端热链兑底），命名与视频侧同库同 sha1 规矩。</summary>
    public Task<Dictionary<string, string>> CachePictureUrlsAsync(IEnumerable<string?> urls, CancellationToken cancellationToken)
    {
        return CachePicturesCoreAsync(urls, cancellationToken);
    }

    private Task<Dictionary<string, string>> CachePicturesAsync(IEnumerable<ReplyRaw> raws, CancellationToken cancellationToken)
    {
        return CachePicturesCoreAsync(
            raws.SelectMany(r => r.Content?.Pictures ?? []).Select(p => p.ImgSrc),
            cancellationToken);
    }

    private async Task<Dictionary<string, string>> CachePicturesCoreAsync(IEnumerable<string?> sourceUrls, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var urls = sourceUrls
            .Where(u => !string.IsNullOrWhiteSpace(u) && u!.StartsWith("http", StringComparison.Ordinal))
            .Distinct()
            .Take(MaxPictureDownloads)
            .ToList();

        foreach (var url in urls)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var dest = Path.Combine(paths.CmtImg, AvatarHash(url!) + OriginalExtension(url!));
            try
            {
                if (!File.Exists(dest))
                {
                    await client.DownloadFileAsync(Request(url!), dest, cancellationToken);
                }

                map[url!] = "/cmt-img/" + Path.GetFileName(dest);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // 单张失败不碍事，前端拿原 URL 热链。
            }
        }

        return map;
    }

    public static string AvatarFileName(string url) => AvatarHash(url) + OriginalExtension(url);

    private static string AvatarHash(string url) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20].ToLowerInvariant();

    private static string OriginalExtension(string url)
    {
        var clean = url.Split('?', '#')[0];
        return Path.GetExtension(clean).ToLowerInvariant() switch
        {
            ".png" or ".gif" or ".webp" or ".jpeg" or ".bmp" => Path.GetExtension(clean).ToLowerInvariant(),
            _ => ".jpg",
        };
    }

    private BilibiliHttpRequest Request(string url) =>
        new(url, Referer, includeCredentials: false, includeBuvid: false);

    private async Task<T?> RequestWbiAsync<T>(
        string path,
        Dictionary<string, object?> paras,
        string operation,
        CancellationToken cancellationToken) where T : class
    {
        return await WbiRequestExecutor.ExecuteAsync(
            wbiKeys,
            async (keys, wts) =>
            {
                var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(paras, keys.ImgKey, keys.SubKey, wts));
                var raw = await client.GetStringAsync(new BilibiliHttpRequest($"{path}?{query}", Referer), cancellationToken);

                using var doc = JsonDocument.Parse(raw);
                var code = doc.RootElement.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
                if (code != 0)
                {
                    var message = doc.RootElement.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                    throw new BilibiliApiResponseException(operation, $"B 站返回 code={code} message={message}", code: code);
                }

                return JsonConvert.DeserializeObject<T>(raw);
            },
            timeProvider,
            cancellationToken);
    }

    private static Task Delay(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
}

/// <summary>快照文件的读改写：楼中楼按需补采后并回原文件（只留最新一版）。</summary>
public static class CommentSnapshotOps
{
    public static CommentSnapshot? Load(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CommentSnapshot>(File.ReadAllText(absolutePath), LbsCommentArchiver.SnapshotJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static void Save(string absolutePath, CommentSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, LbsCommentArchiver.Serialize(snapshot));
    }

    /// <summary>增量归档的结果：Added 是新面孔，Refreshed 两边都有取最新，KeptGone 是 B 站已撤但留档继续养着的。</summary>
    public sealed record ArchiveMerge(CommentSnapshot Merged, int Added, int Refreshed, int KeptGone, int Total);

    /// <summary>
    /// 重拉快照时的增量合并：只进不退。
    /// 楼层按 rpid 并集——B 站那边被删、被锁、被作者自删的楼继续留在档里，两边都有的取最新内容（改了正文/改了庙）；
    /// 楼中楼同规并集，免得一次重拉把上次按需补采到的楼中楼洗掉。留档站的「刷新」不该是覆盖。
    /// </summary>
    public static ArchiveMerge MergeArchive(CommentSnapshot oldSnapshot, CommentSnapshot fresh)
    {
        static List<CommentItem> MergeList(IReadOnlyList<CommentItem> older, IReadOnlyList<CommentItem> newer)
        {
            var merged = older.ToList();
            var index = new Dictionary<long, int>();
            for (var i = 0; i < merged.Count; i++)
            {
                index[merged[i].Rpid] = i;
            }

            foreach (var item in newer)
            {
                if (index.TryGetValue(item.Rpid, out var at))
                {
                    // 新内容覆盖旧内容，但楼中楼取两边并集（新档可能只带了前几楼）。
                    merged[at] = item with { Sub = MergeList(merged[at].Sub ?? [], item.Sub ?? []) };
                }
                else
                {
                    index[item.Rpid] = merged.Count;
                    merged.Add(item);
                }
            }

            return merged;
        }

        var older = oldSnapshot.Replies;
        var replies = MergeList(older, fresh.Replies);
        var olderIds = older.Select(r => r.Rpid).ToHashSet();
        var freshIds = fresh.Replies.Select(r => r.Rpid).ToHashSet();
        var refreshed = olderIds.Count(freshIds.Contains);
        var added = freshIds.Count(id => !olderIds.Contains(id));
        var keptGone = olderIds.Count(id => !freshIds.Contains(id));

        var merged = fresh with
        {
            Replies = replies,
            Top = fresh.Top ?? oldSnapshot.Top,
            Total = Math.Max(fresh.Total, replies.Count),
            Emotes = fresh.Emotes is { Count: > 0 } ? fresh.Emotes : oldSnapshot.Emotes,
            EmoteMeta = fresh.EmoteMeta is { Count: > 0 } ? fresh.EmoteMeta : oldSnapshot.EmoteMeta,
        };
        return new ArchiveMerge(merged, added, refreshed, keptGone, replies.Count);
    }

    /// <summary>把一批楼中楼并到指定楼层；page==1 替换，否则去重追加。返回更新后的快照与命中标志。</summary>
    public static (CommentSnapshot Snapshot, bool Found) MergeSubs(
        CommentSnapshot snapshot,
        long rpid,
        IReadOnlyList<CommentItem> subs,
        int page)
    {
        CommentItem MergeOne(CommentItem item)
        {
            if (item.Rpid != rpid)
            {
                return item;
            }

            var existing = page <= 1 ? [] : item.Sub?.ToList() ?? [];
            var known = existing.Select(s => s.Rpid).ToHashSet();
            var merged = existing.ToList();
            foreach (var sub in subs)
            {
                if (known.Add(sub.Rpid))
                {
                    merged.Add(sub);
                }
            }

            return item with { Sub = merged, ReplyCount = item.ReplyCount is null ? merged.Count : Math.Max(item.ReplyCount.Value, merged.Count) };
        }

        var top = snapshot.Top is { } t ? MergeOne(t) : null;
        var replies = snapshot.Replies.Select(MergeOne).ToList();
        var found = (snapshot.Top?.Rpid == rpid) || snapshot.Replies.Any(r => r.Rpid == rpid);
        return (snapshot with { Top = top, Replies = replies }, found);
    }
}

// ─── x/v2/reply 的响应模型（fork 里没有任何评论模型，按现行接口字段自建；Newtonsoft 反序列化）───

public sealed class ReplyPageOrigin
{
    [JsonProperty("data")]
    public ReplyPage? Data { get; set; }
}

public sealed class ReplyPage
{
    [JsonProperty("upper")]
    public ReplyUpper? Upper { get; set; }

    [JsonProperty("replies")]
    public List<ReplyRaw>? Replies { get; set; }

    [JsonProperty("top_replies")]
    public List<ReplyRaw>? TopReplies { get; set; }

    [JsonProperty("top")]
    public ReplyTopHolder? TopHolder { get; set; }

    [JsonProperty("page")]
    public ReplyPageInfo? Page { get; set; }
}

public sealed class ReplyTopHolder
{
    [JsonProperty("top")]
    public ReplyRaw? Top { get; set; }
}

public sealed class ReplyUpper
{
    [JsonProperty("top")]
    public ReplyRaw? Top { get; set; }

    [JsonProperty("hot")]
    public List<ReplyRaw>? Hot { get; set; }
}

public sealed class ReplyPageInfo
{
    [JsonProperty("count")]
    public int Count { get; set; }

    [JsonProperty("acount")]
    public int Acount { get; set; }
}

public sealed class SubReplyPageOrigin
{
    [JsonProperty("data")]
    public SubReplyPage? Data { get; set; }
}

public sealed class SubReplyPage
{
    [JsonProperty("replies")]
    public List<ReplyRaw>? Replies { get; set; }
}

public sealed class ReplyRaw
{
    [JsonProperty("rpid")]
    public long Rpid { get; set; }

    [JsonProperty("like")]
    public long Like { get; set; }

    [JsonProperty("ctime")]
    public long Ctime { get; set; }

    [JsonProperty("rcount")]
    public long Rcount { get; set; }

    [JsonProperty("reply_count")]
    public long ReplyCount { get; set; }

    [JsonProperty("ip_location")]
    public string? IpLocation { get; set; }

    [JsonProperty("reply_control")]
    public ReplyControlRaw? ReplyControl { get; set; }

    [JsonProperty("parent_str")]
    public string? ParentStr { get; set; }

    [JsonProperty("parent")]
    public long Parent { get; set; }

    [JsonProperty("root")]
    public long Root { get; set; }

    [JsonProperty("content")]
    public ReplyContent? Content { get; set; }

    [JsonProperty("member")]
    public ReplyMember? Member { get; set; }

    [JsonProperty("up_action")]
    public ReplyUpAction? UpAction { get; set; }

    /// <summary>楼中楼预采结果，只在本轮归档内使用，不进 JSON。</summary>
    public List<ReplyRaw>? LbsSub { get; set; }
}

public sealed class ReplyContent
{
    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("pictures")]
    public List<ReplyPicture>? Pictures { get; set; }

    /// <summary>转义符→表情图映射（B 站随每条回复自带，只含正文真正用到的）。</summary>
    [JsonProperty("emote")]
    public Dictionary<string, ReplyEmote>? Emote { get; set; }
}

public sealed class ReplyPicture
{
    [JsonProperty("img_src")]
    public string? ImgSrc { get; set; }
}

public sealed class ReplyEmote
{
    [JsonProperty("url")]
    public string? Url { get; set; }

    [JsonProperty("id")]
    public int? Id { get; set; }

    [JsonProperty("package_id")]
    public int? PackageId { get; set; }

    [JsonProperty("type")]
    public int? Type { get; set; }

    /// <summary>B 站给这枚表情的人类叫法（「打call」这种），前端失效胶囊就念它。</summary>
    [JsonProperty("jump_title")]
    public string? JumpTitle { get; set; }
}

public sealed class ReplyUpAction
{
    [JsonProperty("like")]
    public bool Like { get; set; }

    [JsonProperty("reply")]
    public bool Reply { get; set; }
}

public sealed class ReplyControlRaw
{
    // 登录态下 B 站在 reply_control.location 里给 IP属地，形如「IP属地：上海」，仅 2022-07-25 后的评论有效。
    [JsonProperty("location")]
    public string? Location { get; set; }
}

public sealed class ReplyMember
{
    [JsonProperty("mid")]
    public string? Mid { get; set; }

    [JsonProperty("uname")]
    public string? Uname { get; set; }

    [JsonProperty("avatar")]
    public string? Avatar { get; set; }

    [JsonProperty("ip_location")]
    public string? IpLocation { get; set; }

    [JsonProperty("level_rank")]
    public long LevelRank { get; set; }

    [JsonProperty("level_info")]
    public ReplyLevelInfo? LevelInfo { get; set; }

    [JsonProperty("medals")]
    public List<ReplyMedal>? Medals { get; set; }
}

public sealed class ReplyLevelInfo
{
    [JsonProperty("current_level")]
    public int CurrentLevel { get; set; }
}

public sealed class ReplyMedal
{
    [JsonProperty("level")]
    public int Level { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("color")]
    public string? Color { get; set; }
}
