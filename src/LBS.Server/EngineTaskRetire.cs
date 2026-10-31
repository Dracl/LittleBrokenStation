using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;

namespace LBS.Server;

/// <summary>
/// 销引擎任务的统一编排：打断队列 → 挂起 → 销账，三步一套，对齐引擎自家的删除编排
/// （先打断可能还活着的执行，再销账，否则它会把字节继续往已删的行里写）。
/// 删档与取消加档两条路共用这一份：以前两边各写一遗，而删档那遗漏了加档任务。
/// </summary>
public static class EngineTaskRetire
{
    /// <summary>
    /// 逐枚销任务。查无此枚（已完成搬进历史库的）本就无需安顿，跳过不算失败。
    /// <paramref name="tolerateFailures"/> = false 时销不动立刻回 Blocking（删档宁可先不删，别留个还在写盘的孤儿）；
    /// = true 时销不动只记一笔，由调用方继续收尾（取消加档那条路：账先销，残存任务交给看门狗下一轮）。
    /// </summary>
    public static async Task<(int Retired, string? Blocking)> RunAsync(
        IEnumerable<string> taskIds,
        IDownloadTaskLookup lookup,
        IDownloadTaskStateService state,
        IDownloadTaskQueue taskQueue,
        bool tolerateFailures,
        CancellationToken cancellationToken)
    {
        var retired = 0;
        foreach (var raw in taskIds)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var taskId = new DownloadTaskId(raw);
            if (await lookup.FindAsync(taskId, cancellationToken) is null)
            {
                continue;
            }

            try
            {
                await taskQueue.CancelAsync(taskId);
            }
            catch (Exception)
            {
                // 本就没在跑，取消不掉无妨
            }

            if (tolerateFailures)
            {
                try
                {
                    await state.CancelAsync(taskId, cancellationToken);
                    if ((await state.DeleteAsync(taskId, cancellationToken)).IsSuccess)
                    {
                        retired++;
                    }
                }
                catch (Exception)
                {
                    // 引擎销不动也不拦：分站这侧的账先销，残存任务由看门狗、开机清扫或引擎自己收尾
                }

                continue;
            }

            await state.CancelAsync(taskId, cancellationToken);
            var remove = await state.DeleteAsync(taskId, cancellationToken);
            if (!remove.IsSuccess)
            {
                return (retired, remove.Error?.Message ?? "原因未知");
            }

            retired++;
        }

        return (retired, null);
    }
}
