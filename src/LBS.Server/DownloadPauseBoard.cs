using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 下载暂停板（站长室总闸）：
/// 暂停 = 提交闸停投新任务 + 在途引擎任务逐个 PauseAsync 挂起（下载面板一键止血）；
/// 继续 = 放闸 + 挂起中的任务 Resume 重排回队列；
/// 暂停状态持久在 settings KV（"download-paused"），重启后记得住——自愈员与同步员都认这块牌子，
/// 免得把「暂停本该不动」的任务当僵尸救活或销案。
/// </summary>
public sealed class DownloadPauseBoard(
    StationDb db,
    IDownloadTaskLookup lookup,
    IDownloadTaskStateService state,
    IDownloadTaskQueue taskQueue,
    ILogger<DownloadPauseBoard> logger)
{
    private readonly object initLock = new();
    private bool initialized;
    private volatile bool paused;
    private volatile int intervalSec = -1;

    /// <summary>可选的逐件提交间隔（秒）：0=极速（5~15s），其余白名单 1/5/10/30 分钟。</summary>
    public static readonly int[] IntervalChoices = [0, 60, 300, 600, 1800];

    public bool IsPaused
    {
        get
        {
            if (!initialized)
            {
                lock (initLock)
                {
                    if (!initialized)
                    {
                        paused = db.GetSetting("download-paused") == "1";
                        initialized = true;
                    }
                }
            }

            return paused;
        }
    }

    /// <summary>暂停一切：闸先落下，再把在途引擎任务逐个挂起（个别挂不动不拦路，闸已停新提交）。</summary>
    public async Task<string> PauseAllAsync(CancellationToken cancellationToken)
    {
        paused = true;
        initialized = true;
        db.SetSetting("download-paused", "1");

        var hung = 0;
        foreach (var (_, _, _, _, taskIdsJson) in db.GetDownloadingRows())
        {
            foreach (var raw in StatusSyncWorker.ParseTaskIds(taskIdsJson))
            {
                try
                {
                    var result = await state.PauseAsync(new DownloadTaskId(raw), cancellationToken);
                    if (result.IsSuccess)
                    {
                        hung++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "引擎任务 {TaskId} 暂停没应（闸已停新提交，无伤大雅）", raw);
                }
            }
        }

        logger.LogInformation("下载暂停板落下：不再投新任务，{Hung} 个在途任务挂起", hung);
        return hung > 0 ? $"已暂停：不再投新任务，{hung} 个在途任务已挂起" : "已暂停：不再投新任务";
    }

    /// <summary>继续：闸抬起，挂起中的引擎任务逐个 Resume 并重排回调度队列。</summary>
    public async Task<string> ResumeAllAsync(CancellationToken cancellationToken)
    {
        paused = false;
        initialized = true;
        db.SetSetting("download-paused", "0");

        var woken = 0;
        foreach (var (_, _, _, _, taskIdsJson) in db.GetDownloadingRows())
        {
            foreach (var raw in StatusSyncWorker.ParseTaskIds(taskIdsJson))
            {
                var taskId = new DownloadTaskId(raw);
                try
                {
                    var task = await lookup.FindAsync(taskId, cancellationToken);
                    if (task is null)
                    {
                        continue; // 已搬历史库 = 活本来就好了
                    }

                    if (task.Phase is not (DownloadPhase.Paused or DownloadPhase.Pausing))
                    {
                        continue; // 没挂起的别去打扰
                    }

                    var result = await state.ResumeAsync(taskId, cancellationToken);
                    if (result.IsSuccess)
                    {
                        await taskQueue.EnqueueAsync(taskId, cancellationToken);
                        woken++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "引擎任务 {TaskId} 唤醒没应（下一轮状态同步会兜底）", raw);
                }
            }
        }

        logger.LogInformation("下载暂停板抬起：{Woken} 个挂起任务重新排队", woken);
        return woken > 0 ? $"已继续：{woken} 个挂起任务重新排队" : "已继续：新任务恢复投放";
    }

    /// <summary>逐件提交间隔（持久 KV，懒加载）；非法值一律当极速。</summary>
    public int IntervalSec
    {
        get
        {
            if (intervalSec < 0)
            {
                var stored = int.TryParse(db.GetSetting("download-interval-sec"), out var n) && IntervalChoices.Contains(n) ? n : 0;
                intervalSec = stored;
            }

            return intervalSec;
        }
    }

    /// <summary>站长定间隔：白名单外拒绝，改完立刻生效（提交工人下一件就认）。</summary>
    public bool SetInterval(int seconds)
    {
        if (!IntervalChoices.Contains(seconds))
        {
            return false;
        }

        intervalSec = seconds;
        db.SetSetting("download-interval-sec", seconds.ToString());
        logger.LogInformation("逐件提交间隔改为 {Seconds}s（带 ±10% 随机抖动）", seconds);
        return true;
    }

    /// <summary>提交工每件任务之间的节奏闸：选中间隔 ±10% 抖动；极速仍是 5~15s 随机。</summary>
    public async Task WaitBetweenTasksAsync(CancellationToken cancellationToken)
    {
        var baseSec = IntervalSec > 0 ? IntervalSec : Random.Shared.Next(5, 16);
        var jittered = baseSec * (0.9 + (Random.Shared.NextDouble() * 0.2));
        await Task.Delay(TimeSpan.FromSeconds(jittered), cancellationToken);
    }

    /// <summary>提交闸：DownloadWorker 每次投任务前过一下，暂停中就原地等。</summary>
    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (IsPaused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }
}
