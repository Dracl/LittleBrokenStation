using System.Text.Json;
using System.Text.RegularExpressions;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Infrastructure.Bilibili;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>单个订阅这一轮的爬取结果。</summary>
public sealed record SubscriptionCrawlEntry(string Kind, string Target, bool Ok, int Added, int Duplicated, string? Error);

/// <summary>一轮爬取的总报告，存 settings KV 供站长室展示。</summary>
public sealed record SubscriptionCrawlReport(string RunAtUtc, IReadOnlyList<SubscriptionCrawlEntry> Results);

/// <summary>
/// 订阅爬工：每小时把订阅台账里的收藏夹与博主投稿各爬一遍，新 BV 走投稿路径入站。
/// 去重靠馆藏台账（AddVideosBulk 对重复 bvid 静默计 Duplicated），所以爬工可以放心反复爬。
/// 博主订阅只收最新 20 条，防大 UP 主一轮灌爆分站；收藏夹整架拉（本来就是站长攒的架）。
/// 出号姿势与收藏导入端点同款：LbsCollectPolicy.RunForActivityAsync 按采集偏好选主/副号。
/// </summary>
public sealed class SubscriptionCrawler(
    StationDb db,
    DownloadQueue queue,
    LbsFavoritesImport favorites,
    LbsQueryClient client,
    IWbiKeyProvider wbiKeys,
    TimeProvider timeProvider,
    LbsCollectPolicy policy,
    LbsApiThrottle throttle,
    ILogger<SubscriptionCrawler> logger) : BackgroundService
{
    private const int UpPageKeep = 20;

    /// <summary>定时巡视与站长手动触发共用的串行闸：同一时刻只允许一趟爬。</summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("订阅爬工上岗，每小时前后随机抖一抖巡站一遍订阅台账");

        // 开站后先补一轮（站长可能刚登记了订阅）；首巡延时也随机，别永远卡在整点后 45 秒出号。
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(30, 121)), stoppingToken);
            await CrawlOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "开站首巡没爬成，等例行巡视");
        }

        // 周期不固定：每轮完重新抽 50~80 分钟（RC-WebGallery 同款思路），固定周期的爬工在日志里是条直线。
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRound = TimeSpan.FromMinutes(Random.Shared.Next(50, 81));
            try
            {
                await Task.Delay(nextRound, stoppingToken);
                // 没号就不出门：订阅巡捕吃的也是采集号，空着出去整轮只会被 -101 打回，
                // 还白耗节流窗口。柜台那块采集号状态会标灰，站长看得到原因。
                if (!policy.HasAccount())
                {
                    logger.LogInformation("订阅巡视本轮歇脚：站里没有可用的 B 站号");
                    continue;
                }

                await CrawlOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "订阅巡视出错，下一轮再试");
            }
        }
    }

    /// <summary>爬一轮全部订阅；每个订阅独立成败，一个塌了不连坐。结果落 settings KV。</summary>
    public async Task<SubscriptionCrawlReport> CrawlOnceAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 风控冷却期不硬顶：留一份带原因的战报直接收工，到期自动回暖。
            if (throttle.IsCoolingDown)
            {
                var left = throttle.CooldownRemaining ?? TimeSpan.Zero;
                var cooled = new SubscriptionCrawlReport(DateTimeOffset.UtcNow.ToString("O"),
                    [new SubscriptionCrawlEntry("throttle", "分站", false, 0, 0, $"风控冷却中，约 {Math.Ceiling(left.TotalMinutes)} 分钟后自动回暖")]);
                db.SetSetting("subscription-crawl", JsonSerializer.Serialize(cooled));
                return cooled;
            }

            var results = new List<SubscriptionCrawlEntry>();
            foreach (var sub in ReadSubscriptions())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    results.Add(sub.Kind == "fav"
                        ? await CrawlFavoriteAsync(sub, cancellationToken)
                        : await CrawlUpAsync(sub, cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "订阅 {Kind}:{Target} 爬取翻车", sub.Kind, sub.Target);
                    results.Add(new SubscriptionCrawlEntry(sub.Kind, sub.Target, false, 0, 0, ex.Message));
                }
            }

            var report = new SubscriptionCrawlReport(DateTimeOffset.UtcNow.ToString("O"), results);
            db.SetSetting("subscription-crawl", JsonSerializer.Serialize(report));
            var added = results.Sum(r => r.Added);
            if (added > 0)
            {
                logger.LogInformation("订阅爬工本轮收编 {Added} 件新货进站", added);
            }

            return report;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SubscriptionCrawlEntry> CrawlFavoriteAsync(SubscriptionItem sub, CancellationToken cancellationToken)
    {
        // 与收藏导入端点同一条上游链（FavoritesResource 自动翻页），非视频条目由它计入 Ignored。
        var outcome = await policy.RunForActivityAsync(() => favorites.ImportAsync(sub.Target, cancellationToken));
        if (!outcome.Success)
        {
            return new SubscriptionCrawlEntry(sub.Kind, sub.Target, false, 0, 0, outcome.Error);
        }

        return await IngestAsync(sub, outcome.Videos, cancellationToken);
    }

    private async Task<SubscriptionCrawlEntry> CrawlUpAsync(SubscriptionItem sub, CancellationToken cancellationToken)
    {
        var mid = ParseMid(sub.Target);
        if (mid is null)
        {
            return new SubscriptionCrawlEntry(sub.Kind, sub.Target, false, 0, 0, "认不出这个博主，填纯数字 MID 或 space.bilibili.com/… 空间链接");
        }

        // 最新 20 条一发拿齐（PUBDATE 序），不再往深处翻：老货想收用收藏导入或直接投稿。
        var page = await policy.RunForActivityAsync(() => WbiRequestExecutor.ExecuteAsync(
            wbiKeys,
            async (keys, wts) => await client.GetPublicationPageAsync(
                keys, wts, mid.Value, pn: 1, ps: UpPageKeep, policy.HasAccount(),
                order: PublicationOrder.PUBDATE, cancellationToken: cancellationToken),
            timeProvider,
            cancellationToken));

        var videos = (page?.List.Vlist ?? Array.Empty<SpacePublicationListVideo>())
            .Where(v => !string.IsNullOrWhiteSpace(v.Bvid))
            .Select(v => new NewVideo(
                $"https://www.bilibili.com/video/{v.Bvid.Trim()}",
                v.Bvid.Trim(),
                string.IsNullOrWhiteSpace(v.Title) ? null : v.Title.Trim(),
                string.IsNullOrWhiteSpace(v.Pic) ? null : v.Pic.Trim(),
                v.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(v.Created).ToString("O") : null))
            .ToList();

        return await IngestAsync(sub, videos, cancellationToken);
    }

    private async Task<SubscriptionCrawlEntry> IngestAsync(SubscriptionItem sub, IReadOnlyList<NewVideo> videos, CancellationToken cancellationToken)
    {
        if (videos.Count == 0)
        {
            // 架子空/全是非视频，不算爬取失败，只是没货。
            return new SubscriptionCrawlEntry(sub.Kind, sub.Target, true, 0, 0, "这轮没捞到新货");
        }

        var outcome = db.AddVideosBulk(videos);
        foreach (var videoId in outcome.InsertedIds)
        {
            await queue.EnqueueAsync(videoId, cancellationToken);
        }

        if (outcome.Added > 0)
        {
            logger.LogInformation("订阅 {Kind}:{Target} 本轮 {Added} 件新货提交进站（{Dup} 件馆藏已有）",
                sub.Kind, sub.Target, outcome.Added, outcome.Duplicated);
        }

        return new SubscriptionCrawlEntry(sub.Kind, sub.Target, true, outcome.Added, outcome.Duplicated, null);
    }

    private List<SubscriptionItem> ReadSubscriptions() =>
        db.GetSetting("subscriptions") is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<SubscriptionItem>>(json) ?? new()
            : new();

    /// <summary>MID 认三种写法：纯数字、space.bilibili.com/123（含 h5/big 变体）、带斜杠尾巴。UP 主页投稿端点也用它。</summary>
    public static long? ParseMid(string target)
    {
        var t = target.Trim().TrimEnd('/');
        if (long.TryParse(t, out var direct) && direct > 0)
        {
            return direct;
        }

        var match = Regex.Match(t, @"space\.bilibili\.com/(?:h5/)?(?:big/)?(\d{2,})", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, out var mid) && mid > 0 ? mid : null;
    }
}
