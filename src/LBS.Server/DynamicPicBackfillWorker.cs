using System.Text.Json;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 动态配图补员：给到站时九宫格没落盘（pics_local_json 为 NULL 或含 null 洞）的动态从 pics_json 补下原图——
/// 开机延时 20 秒补首轮，之后每小时一轮；每轮封顶 40 条、条间 300ms 节流（封面补员同款节奏，B 站风控友好）。
/// 下载只走 <see cref="LbsCommentArchiver.CachePictureUrlsAsync"/>：与采集侧同一份实现、同一个库
/// （data/cmt-img，sha1(url)+原扩展命名），存上一位写一位。<b>写回只碰 pics_local_json 一列</b>
/// （<c>SetDynamicAssetLocals</c> 是一根 UPDATE 写三列，会把同行的 cover_local / body_emotes_json 抹掉）。
/// 同序对齐是硬规矩：数组与 pics_json 同长，已有的非 null 位原样保留（不覆盖、不重下），单张失败那位继续存 null，
/// 前端逐格本地优先、热链兑底。
/// <b>整轮挂在 <see cref="LbsCollectPolicy"/> 的出号上下文里</b>：落盘终点是 LbsQueryClient.DownloadFileAsync，
/// 第一行就是 RequireCookie()，而 2026-10-28「站级登录态退役」后 cookie 只剩顺着调用链流这一个来源——
/// 挂在 policy 之外每张图都抛 <see cref="LbsNoAccountException"/>（动态巡捕当年就是这么把动态图全灭掉的，见 DynamicFetchWorker）。
/// pics_json 链子认不出（坏 JSON 或一项没有）的行今天办不了：跳过，不算失败也不算成功。
/// 表上没有 attempts / last_error / status 列（站长定死不加），补不上的老行靠 ORDER BY id 每轮自然重逢。
/// </summary>
public sealed class DynamicPicBackfillWorker(
    StationDb db,
    LbsCommentArchiver assetCache,
    LbsCollectPolicy policy,
    ILogger<DynamicPicBackfillWorker> logger) : BackgroundService
{
    private const int BatchPerRound = 40;

    /// <summary>读侧宽容口径与橱窗端点一致（历史上有 PascalCase 脏行）；写侧直接吃采集侧那份 camelCase。</summary>
    private static readonly JsonSerializerOptions LooseJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>一条动态的图全在库里时不必开下载闸，拿空表兑一下（只读）。</summary>
    private static readonly IReadOnlyDictionary<string, string> NoPictures = new Dictionary<string, string>(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("动态配图补员到岗，20 秒后补首轮，之后每小时一轮");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            await RunRoundAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "动态配图补员首轮没跑成，等例行轮");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                await RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "动态配图补员这轮出错，下一轮再试");
            }
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        var queue = db.ListDynamicsMissingPics(BatchPerRound);
        if (queue.Count == 0)
        {
            return;
        }

        // 号池空了不装忙（与封面补员 / 动态巡捕同一姿势）：先问一句再出号，省得 40 条 ×N 张图都抛一次无号异常刷日志。
        if (!policy.HasAccount())
        {
            logger.LogInformation("动态配图补员：站里没可用的采集号，这轮 {Taken} 条动态的图先欠着，绑好活号就自动恢复", queue.Count);
            return;
        }

        await policy.RunForActivityAsync(() => FillAsync(queue, cancellationToken));
    }

    private async Task FillAsync(IReadOnlyList<StationDb.DynamicPicsBackfillCandidate> queue, CancellationToken cancellationToken)
    {
        var filled = 0;
        var skipped = 0;
        foreach (var candidate in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 只补还空着的位（已有本地路径的不重下）；下载 map 里缺席就算失败，那位继续存 null。
            var pending = PendingPicUrls(candidate.PicsJson, candidate.PicsLocalJson);
            var map = pending.Count > 0
                ? await assetCache.CachePictureUrlsAsync(pending, cancellationToken)
                : NoPictures;
            var next = ComposePicsLocal(candidate.PicsJson, candidate.PicsLocalJson, map);
            if (next is null)
            {
                // pics_json 整体读不出或一项没有：链子认不出，这单今天办不了，不算失败也不计成功。
                skipped++;
                logger.LogDebug("动态配图补员：{DynamicId} 的 pics_json 读不出图链，这单今天办不了", candidate.DynamicId);
            }
            else if (CountNewlyFilled(candidate.PicsLocalJson, next) == 0)
            {
                logger.LogDebug("动态配图补员：{DynamicId} 这轮一张没补上（图床拒链/网络抖），一位没变就不写库，留下轮", candidate.DynamicId);
            }
            else
            {
                db.SetDynamicPicsLocal(candidate.DynamicId, JsonSerializer.Serialize(next, DynamicFetchWorker.AssetJson));
                filled++;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        logger.LogInformation("动态配图补员：本轮补齐 {Filled}/{Taken} 条（{Skipped} 条 pics_json 读不出图链，今天办不了）",
            filled, queue.Count, skipped);
        if (filled == 0)
        {
            // 整轮一条没补上还只记 Debug，就是当年那副「看着在跑、其实空转」的样子——这里至少留一句能看见的。
            logger.LogWarning("动态配图补员：这轮 {Taken} 条一条没补上（多半是图床抓不动或被风控），下轮再试", queue.Count);
        }
    }

    /// <summary>本轮该下的图链：按 pics_json 顺序取那些同序位置还没本地路径的 url（去重、只认 http）。
    /// pics_json 读不出（坏 JSON / 零项）时回空表——「今天办不了」由 <see cref="ComposePicsLocal"/> 定夺。</summary>
    internal static List<string> PendingPicUrls(string? picsJson, string? picsLocalJson)
    {
        var pics = ParsePics(picsJson);
        if (pics.Count == 0)
        {
            return [];
        }

        var have = ParseLocals(picsLocalJson);
        var urls = new List<string>();
        for (var i = 0; i < pics.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(ElementAt(have, i)))
            {
                continue; // 这位早就落过盘，不重下。
            }

            var url = pics[i].Url;
            if (!string.IsNullOrWhiteSpace(url) && url!.StartsWith("http", StringComparison.Ordinal) && !urls.Contains(url))
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    /// <summary>组装新的 pics_local 数组（纯函数，测试直接钉）：与 pics_json 同长；已有的非 null 位原样保留（不覆盖、不重下）；
    /// 空位吃下载 map，map 里没有（单张失败）继续存 null。pics_json 一项没有或整体坏 JSON ⇒ 回 null = 这行今天办不了。</summary>
    internal static List<string?>? ComposePicsLocal(string? picsJson, string? picsLocalJson, IReadOnlyDictionary<string, string> map)
    {
        var pics = ParsePics(picsJson);
        if (pics.Count == 0)
        {
            return null;
        }

        var have = ParseLocals(picsLocalJson);
        var next = new List<string?>(pics.Count);
        for (var i = 0; i < pics.Count; i++)
        {
            var kept = ElementAt(have, i);
            if (!string.IsNullOrWhiteSpace(kept))
            {
                next.Add(kept);
                continue;
            }

            var url = pics[i].Url;
            next.Add(!string.IsNullOrWhiteSpace(url) && map.TryGetValue(url!, out var local) ? local : null);
        }

        return next;
    }

    /// <summary>本轮新补上的位数（旧值 NULL / 坏 JSON 视作全空位）：一位没补上就不写库，省下无谓的 UPDATE，
    /// 也让「整轮 filled==0」那句告警真正代表空转。</summary>
    internal static int CountNewlyFilled(string? picsLocalJson, IReadOnlyList<string?> next)
    {
        var have = ParseLocals(picsLocalJson);
        var filled = 0;
        for (var i = 0; i < next.Count; i++)
        {
            if (next[i] is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(ElementAt(have, i)))
            {
                filled++;
            }
        }

        return filled;
    }

    private static string? ElementAt(List<string?>? have, int index) =>
        have is not null && index < have.Count ? have[index] : null;

    /// <summary>读 pics_json：与橱窗端点同一套宽容口径（PropertyNameCaseInsensitive 认得 PascalCase 脏行），坏 JSON 当没图。</summary>
    private static List<ParsedPic> ParsePics(string? picsJson)
    {
        if (string.IsNullOrWhiteSpace(picsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ParsedPic>>(picsJson, LooseJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>读现有的本地板（允许 null 元素）：坏 JSON 当没存过，整列重下。</summary>
    private static List<string?>? ParseLocals(string? picsLocalJson)
    {
        if (string.IsNullOrWhiteSpace(picsLocalJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string?>>(picsLocalJson, LooseJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
