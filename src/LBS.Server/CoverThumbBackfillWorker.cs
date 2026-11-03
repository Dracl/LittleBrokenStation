using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 封面缩略补员员（站长 11-03 当日改的主意：原本说「老视频不管、存量不回填」，随后拍板
/// 「要不现在来一次存量回填吧，把所有缓存干掉，然后全部重新裁切一遍，反正电脑性能足够」）。
///
/// 开机稍候就开切：一轮 40 条，只要还有欠账就连轴转，切干净了才退到每 5 分钟看一眼。
/// 新入站的稿件不靠这一轮——同步链与封面补员都就地调 <see cref="CoverThumbs.TryBuildAsync"/>，
/// 这里只兜四类漏网：切片时服务没开、ffmpeg 当时没装、站长手删过 <c>data/thumbs</c>（靠每轮开头的
/// <see cref="StationDb.ReconcileCoverThumbRowsWithDisk"/> 对账打回才点得回来）、以及单档切砸过的旧局
/// （failed 行也会进名单被点名重试，到封顶才搁笔）。
///
/// 与 <see cref="CoverBackfillWorker"/> 的分别：那位要上网问 B 站，得看号池脸色、要节流；
/// 这位纯本地 ffmpeg，一趟几十毫秒，不用求人。
/// </summary>
public sealed class CoverThumbBackfillWorker(StationDb db, DataPaths paths, ILogger<CoverThumbBackfillWorker> logger) : BackgroundService
{
    // 40 条一轮：一条五档、每档 ffmpeg 约 80 毫秒，一轮约 20 秒，开站首屏感受不到它。
    private const int BatchPerRound = 40;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ItemGap = TimeSpan.FromMilliseconds(80);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 开机先让位：媒体对账、封面补员、预览转码都要抢磁盘与 ffmpeg，别跟首屏挤在一起。
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var built = 0;
            try
            {
                built = await RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw; // 站点关停照原样上抛
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "封面缩略补员这一轮没跑完，下一轮再来");
            }

            // 还有欠账就立刻续下一轮（存量回填要在几分钟内自己消化完），空了才歇。
            await Task.Delay(built > 0 ? TimeSpan.FromMilliseconds(200) : IdleDelay, stoppingToken);
        }
    }

    private async Task<int> RunRoundAsync(CancellationToken stoppingToken)
    {
        // 先对账再点名：欠账判定探不到磁盘，台账说落了、盘上却没件的幽灵行（手删 data/thumbs、磁盘事故）
        // 得靠这一刀销掉才会被下面的名单点回来。销行也算工分：销完就地续下一轮重切，不多睡 5 分钟。
        var swept = db.ReconcileCoverThumbRowsWithDisk();
        if (swept > 0)
        {
            logger.LogInformation("封面缩略台账对账：{Count} 行说落了盘上却没有，已打成 failed 待重切", swept);
        }

        var queue = db.ListVideosNeedingCoverThumbs(BatchPerRound);
        if (queue.Count == 0)
        {
            return 0;
        }

        var built = 0;
        foreach (var candidate in queue)
        {
            stoppingToken.ThrowIfCancellationRequested();
            var outcome = await CoverThumbs.TryBuildAsync(db, paths, candidate.VideoId, stoppingToken);
            if (outcome.Built)
            {
                built += 1;
                logger.LogInformation("封面缩略切好 {Tiers} 档：{Bvid}", outcome.Arrived, candidate.Bvid);
            }
            else
            {
                logger.LogDebug("封面缩略没切成（{Bvid}）：{Error}", candidate.Bvid, outcome.Error);
            }

            // 每切完一条让一拍：ffmpeg 单趟不重，但连轴转 168 条能把前台拖慢，站长正在看的那屏优先。
            await Task.Delay(ItemGap, stoppingToken);
        }

        return built + swept; // 销了行也算干了活：当轮就续下一轮，把销掉的行切回来
    }
}
