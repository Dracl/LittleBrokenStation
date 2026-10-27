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
    string PublishedAtUtc);

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

        // 作者与时间：FORWARD 用 orig 的 module_author（更贴原文），无 orig 或字段缺则回落顶层。
        var srcAuthor = Path(src, "modules", "module_author");
        var topAuthor = Path(item, "modules", "module_author");
        var authorMid = Num(srcAuthor ?? topAuthor, "mid") ?? Num(topAuthor, "mid");
        var authorName = Str(Path(srcAuthor ?? topAuthor, "name")) ?? Str(Path(topAuthor, "name"));
        var pubTs = Num(srcAuthor, "pub_ts") ?? Num(topAuthor, "pub_ts");
        var published = pubTs is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(pubTs.Value).ToString("O")
            : DateTimeOffset.UtcNow.ToString("O");

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
            parts.Add($"转发自 {(string.IsNullOrWhiteSpace(authorName) ? "他人" : authorName)}：");
        }

        if (!string.IsNullOrWhiteSpace(srcDesc) && (parts.Count == 0 || parts[^1] != srcDesc.Trim()))
        {
            parts.Add(srcDesc.Trim());
        }

        if (!string.IsNullOrWhiteSpace(srcOpus) && (parts.Count == 0 || parts[^1] != srcOpus.Trim()))
        {
            parts.Add(srcOpus.Trim());
        }

        var text = parts.Count == 0 ? null : string.Join("\n\n", parts);

        // 图片三套路径依次兜底（图文 opus / 旧相簿 draw / 专栏 article），FORWARD 全从 orig 里取。
        var pics = CollectPics(src);
        var bvid = Str(Path(src, "modules", "module_dynamic", "major", "archive", "bvid"));

        return new ParsedDynamic(dynamicId, type, authorMid, authorName, text, pics, bvid, published);
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
