using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 封面补员：给到站时没勾封面的视频从 cover_url 批量补下封面原图——
/// 开机延时 20 秒补首轮，之后每小时一轮；每轮封顶 40 条、条间 300ms 节流（动态评论巡捕同款节奏，B 站风控友好）。
/// 文件落 data/videos、与 downkyi 产物同族命名（&lt;基目录&gt;/&lt;基名&gt;.Cover.jpg，基名从该视频任一产物行 rel 推导），
/// 文件先落盘、再进 artifacts 行：缺件回炉（MissingMediaReconciler）拿 artifacts 行验盘，先插行会把「有行无件」误判整套回炉。
/// 失败不建行、下轮重试——封面是锦上添花，前端随时有热链兑底，不许挡任何人的路。
/// 抓 → 落盘 → 换主 → 登记那一串与稿件设置里那枚「重拉封面与媒体」共用 <see cref="CoverFileFetcher"/>，一份实现两处调。
/// <b>整轮挂在 <see cref="LbsCollectPolicy"/> 的出号上下文里</b>：LbsQueryClient 第一行就是 RequireCookie()，
/// 裸注入 LbsQueryClient 而不挂 policy 时整轮都是匿名请求，逐条抛的无号异常被 catch 吞成 Debug 日志——
/// 于是「每小时跑一轮、轮轮空跑 0 条」还看不出毛病（本轮就是来还这笔账的）。
/// </summary>
public sealed class CoverBackfillWorker(
    StationDb db,
    LbsQueryClient client,
    LbsCollectPolicy policy,
    DataPaths paths,
    ILogger<CoverBackfillWorker> logger) : BackgroundService
{
    private const int BatchPerRound = 40;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("封面补员到岗，20 秒后补首轮，之后每小时一轮");
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
            logger.LogWarning(ex, "封面补员首轮没跑成，等例行轮");
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
                logger.LogWarning(ex, "封面补员这轮出错，下一轮再试");
            }
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        var queue = db.ListVideosMissingCover(BatchPerRound);
        if (queue.Count == 0)
        {
            return;
        }

        // 号池空了不装忙（与动态巡捕 / 订阅爬工同一姿势）：先问一句再出号，省得 40 支都抛一次无号异常刷日志。
        if (!policy.HasAccount())
        {
            logger.LogInformation("封面补员：站里没可用的采集号，这轮 {Taken} 条先欠着，绑好活号就自动恢复", queue.Count);
            return;
        }

        await policy.RunForActivityAsync(() => FillAsync(queue, cancellationToken));
    }

    private async Task FillAsync(IReadOnlyList<StationDb.CoverBackfillCandidate> queue, CancellationToken cancellationToken)
    {
        var filled = 0;
        foreach (var candidate in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await CoverFileFetcher.TryReplaceAsync(
                client, db, paths, candidate.VideoId, candidate.CoverUrl, candidate.BaseRel, cancellationToken);
            switch (result.Outcome)
            {
                case CoverFetch.Replaced:
                    filled++;
                    break;
                case CoverFetch.Failed:
                    logger.LogDebug(result.Error, "封面补员：{Bvid} 这趟没下载成，下轮再来", candidate.Bvid);
                    break;
                default:
                    // Kept：封面链认不出或基名退化（没处落盘），这单今天办不了，不算失败也不建行。
                    break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        logger.LogInformation("封面补员：本轮补齐 {Filled}/{Taken} 条", filled, queue.Count);
        if (filled == 0)
        {
            // 整轮一条没补上还只记 Debug，就是当年那副「看着在跑、其实空转」的样子——这里至少留一句能看见的。
            logger.LogWarning("封面补员：这轮 {Taken} 条一条没补上（多半是封面链抓不动或被风控），下轮再试", queue.Count);
        }
    }
}
