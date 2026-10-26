using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 引擎任务复活专员：硬重启后卡在 Downloading 不动的馆藏由它救活（手动端点与启动自愈共用）。
/// 两条路径：
///  1) 软续下：在途表里的引擎任务逐个 Resume（Failed 走 Retry），再经队列 Enqueue 重排——
///     调度器只执行 Queued，队列 Enqueue 自带去重，重复投无害；
///  2) 硬重投：有任一任务软续不动（legacy 来源被拒等）时整视频推倒：全部任务 Delete 删行
///     （判重查询不看相位，行不删重投会被判重打成「无需进站」终态）+ 清 staging + 台账回 Pending 重投。
/// 删除前先调队列层 CancelAsync 打断可能还活着的执行，对齐引擎自家删除编排。
/// </summary>
public sealed class LbsTaskReviver(
    StationDb db,
    DownloadQueue queue,
    IDownloadTaskLookup lookup,
    IDownloadTaskStateService state,
    IDownloadTaskQueue taskQueue,
    IDownloadRuntimeAvailability availability,
    DownloadTaskStaging staging,
    ILogger<LbsTaskReviver> logger)
{
    /// <summary>复活一个下载中的馆藏；返回给站长看的话术。非下载中视为无需复活。</summary>
    public async Task<(bool Ok, string Message)> ReviveAsync(long videoId, CancellationToken cancellationToken)
    {
        var row = db.GetDownloadingVideos().FirstOrDefault(v => v.Id == videoId);
        if (row.Id == 0)
        {
            return (false, "这条馆藏不在下载中，不用续");
        }

        var taskIds = StatusSyncWorker.ParseTaskIds(row.TaskIdsJson);
        if (taskIds.Count == 0)
        {
            // 台账挂着下载中却没有引擎任务（提交半途被杀等），直接回炉重投最干净。
            db.ResetToPending(videoId);
            await queue.EnqueueAsync(videoId, cancellationToken);
            return (true, "没有引擎任务在途，已退回待提交重新排队");
        }

        try
        {
            availability.EnsureAcceptingTasks();
        }
        catch (DownloadRuntimeUnavailableException)
        {
            return (false, "下载 runtime 还没就绪，稍后再按");
        }

        var soft = 0;
        var stuck = 0;
        foreach (var raw in taskIds)
        {
            var taskId = new DownloadTaskId(raw);
            var task = await lookup.FindAsync(taskId, cancellationToken);
            if (task is null)
            {
                soft++; // 已搬历史库 = 这路活本来就好了
                continue;
            }

            TryCancelExecution(taskId);
            var result = task.Phase == DownloadPhase.Failed
                ? await state.RetryAsync(taskId, cancellationToken)
                : await state.ResumeAsync(taskId, cancellationToken);
            if (result.IsSuccess)
            {
                await taskQueue.EnqueueAsync(taskId, cancellationToken);
                soft++;
            }
            else
            {
                stuck++;
                logger.LogWarning("引擎任务 {TaskId} 软续不动（{Phase}：{Error}），准备整视频重投",
                    taskId, task.Phase, result.Error?.Message ?? "未知");
            }
        }

        if (stuck == 0)
        {
            return (true, soft > 0 ? $"软续下 {soft} 个引擎任务，已重新排队" : "引擎任务都还在跑，不用动");
        }

        // 有一枚软续不动就整视频推倒：留着别的旧行会让重投撞判重，全线白干。
        foreach (var raw in taskIds)
        {
            var taskId = new DownloadTaskId(raw);
            TryCancelExecution(taskId);
            var task = await lookup.FindAsync(taskId, cancellationToken);
            if (task is null)
            {
                continue;
            }

            await state.CancelAsync(taskId, cancellationToken);
            var removed = await state.DeleteAsync(taskId, cancellationToken);
            if (!removed.IsSuccess)
            {
                return (false, $"引擎任务删不动（{removed.Error?.Message ?? "原因未知"}），重投会撞判重，先手动清任务或重启分站再试");
            }

            try
            {
                staging.CleanupTask(task);
            }
            catch (Exception ex)
            {
                // staging 身份漂移之类清不掉不拦重投：新提交会用新的 staging token。
                logger.LogWarning(ex, "引擎任务 {TaskId} 的 staging 没清动（继续重投）", taskId);
            }
        }

        db.ResetToPending(videoId);
        await queue.EnqueueAsync(videoId, cancellationToken);
        logger.LogInformation("馆藏 {VideoId} 整视频重投：{Stuck} 个任务软续不动已删行", videoId, stuck);
        return (true, "旧任务已清理，馆藏退回待提交重新进站");
    }

    /// <summary>打断队列里可能还活着的执行（对齐引擎删除编排先 cancel 再 delete）；没在跑会返回 false，异常一律无伤大雅。</summary>
    private void TryCancelExecution(DownloadTaskId taskId)
    {
        try
        {
            taskQueue.CancelAsync(taskId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "队列层取消任务 {TaskId} 没成功（可能本就没在跑）", taskId);
        }
    }
}
