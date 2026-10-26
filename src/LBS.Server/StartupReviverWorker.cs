using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 启动自愈员：开站后等下载 runtime 安顿（60 秒），再对引擎在途表隔 60 秒采两次样。
/// 两次样之间纹丝不动（字节数与相位都没变）且处于可复活相位的任务即崩溃遗留，交 LbsTaskReviver 自动救活。
/// 采样窗口给到 60 秒是给合流/解析重试这类「活着但暂时不写字节」的环节留余地，避免误伤健康下载；
/// Failed/Canceled 不碰——那是引擎的正式判决，该由失败流程接手而不是自愈通道洗白。
/// </summary>
public sealed class StartupReviverWorker(
    LbsTaskReviver reviver,
    StationDb db,
    IDownloadTaskLookup lookup,
    DownloadPauseBoard board,
    ILogger<StartupReviverWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SampleGap = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 暂停板落着的时候别自愈：挂起的引擎任务在自愈员眼里就是僵尸，救活等于违背站长意图。
            await board.WaitIfPausedAsync(stoppingToken);
            await Task.Delay(Warmup, stoppingToken);

            var unfinished = await lookup.GetUnfinishedAsync(stoppingToken);
            var candidates = unfinished
                .Where(task => IsStallable(task.Phase))
                .ToDictionary(task => task.Id, task => (task.Progress.DownloadedBytes ?? 0L, task.Phase));
            if (candidates.Count == 0)
            {
                return; // 在途表干净，本员直接收工
            }

            await Task.Delay(SampleGap, stoppingToken);

            var handled = new HashSet<long>();
            foreach (var (taskId, snapshot) in candidates)
            {
                if (board.IsPaused)
                {
                    return; // 中途被站长暂停了，剩下的僵尸交给下轮或继续后手动处置
                }

                var current = await lookup.FindAsync(taskId, stoppingToken);
                if (current is null || !IsStallable(current.Phase))
                {
                    continue; // 这一分钟里它自己搬走了
                }

                if ((current.Progress.DownloadedBytes ?? 0L) != snapshot.Item1 || current.Phase != snapshot.Item2)
                {
                    continue; // 字节在涨或相位在挪 = 活得好好的，别多管闲事
                }

                var owner = db.GetDownloadingVideos()
                    .FirstOrDefault(row => StatusSyncWorker.ParseTaskIds(row.TaskIdsJson).Contains(taskId.Value));
                if (owner.Id == 0 || !handled.Add(owner.Id))
                {
                    continue; // 对不上馆藏（孤儿任务）或同视频已救过
                }

                logger.LogWarning("启动自愈：馆藏 {VideoId} 的引擎任务 {TaskId} 停滞（相位 {Phase}），自动复活",
                    owner.Id, taskId, current.Phase);
                var (ok, message) = await reviver.ReviveAsync(owner.Id, stoppingToken);
                logger.LogInformation("启动自愈馆藏 {VideoId} 的结果：{Message}（{Ok}）", owner.Id, message, ok);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 关站，本员下班
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "启动自愈员巡视翻车（不影响开站，卡住的馆藏还能手动续下）");
        }
    }

    private static bool IsStallable(DownloadPhase phase) =>
        phase is DownloadPhase.Queued or DownloadPhase.Downloading or DownloadPhase.Pausing or DownloadPhase.Paused;
}
