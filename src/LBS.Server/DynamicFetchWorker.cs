using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 动态巡捕（M6）：上岗先补一轮（错峰 LedgerBackup 的 2 分钟，站刚开别急着出号），
/// 从订阅台账灌一次 UP 种子，之后每小时前后随机抖 10 分钟逐 enabled target 拉动态。
/// 一切异常只 log 不炸站；风控信号交给 LbsApiThrottle 的统一熔断，这里只管弃轮。
/// 站长室「立即巡一轮」走 TryStartOnce：内存闸防重入，busy 时直接回绝。
/// </summary>
public sealed class DynamicFetchWorker(
    StationDb db,
    LbsDynamicFetcher fetcher,
    LbsOwnerPolicy policy,
    ILogger<DynamicFetchWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim roundGate = new(1, 1);
    private int manualBusy;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("动态巡捕上岗，90 秒后灌种子补首轮，之后每小时抖一抖");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
            var seeded = db.SeedTargetsFromSubscriptions();
            if (seeded > 0)
            {
                logger.LogInformation("动态关注名单从订阅台账灌进 {Seeded} 位 UP", seeded);
            }

            await RunRoundAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "动态首轮没跑成，等例行巡视");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // 1h ± 10min 抖动（仿订阅爬工）：固定周期的爬工在 B 站日志里是条直线。
            var nextRound = TimeSpan.FromMinutes(Random.Shared.Next(50, 71));
            try
            {
                await Task.Delay(nextRound, stoppingToken);
                await RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "动态巡视出错，下一轮再试");
            }
        }
    }

    /// <summary>站长手动催一轮：进不来闸就回 false（防重入），成功起跑回 true。</summary>
    public bool TryStartOnce(long? onlyMid)
    {
        if (Interlocked.CompareExchange(ref manualBusy, 1, 0) != 0)
        {
            return false;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunRoundAsync(CancellationToken.None, onlyMid);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "站长催的这轮动态没跑成");
                }
                finally
                {
                    Interlocked.Exchange(ref manualBusy, 0);
                }
            });
        return true;
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken, long? onlyMid = null)
    {
        await roundGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var target in db.ListDynamicTargets())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!target.Enabled || (onlyMid is not null && target.Mid != onlyMid.Value))
                {
                    continue;
                }

                try
                {
                    var outcome = await policy.RunForActivityAsync(() => fetcher.FetchTargetAsync(
                        target.Mid, target.LastOffset,
                        parsed => Task.FromResult(db.UpsertDynamic(
                            parsed.DynamicId, target.Mid, parsed.AuthorMid, parsed.AuthorName,
                            parsed.Type, parsed.Text,
                            parsed.Pics.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(parsed.Pics) : null,
                            parsed.Bvid, parsed.PublishedAtUtc,
                            parsed.AuthorFaceUrl, parsed.ArchiveTitle, parsed.ArchiveCover, parsed.ArchiveDuration)),
                        cancellationToken));
                    if (outcome.Ok)
                    {
                        db.SetTargetOffset(target.Mid, outcome.Offset, DateTimeOffset.UtcNow.ToString("O"));
                        if (outcome.Fresh > 0)
                        {
                            logger.LogInformation("动态巡捕：UP {Mid} 本轮新收 {Fresh} 条（过目 {Seen} 条）",
                                target.Mid, outcome.Fresh, outcome.Seen);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "动态巡捕：UP {Mid} 这轮翻车，下个 target 继续", target.Mid);
                }
            }
        }
        finally
        {
            roundGate.Release();
        }
    }
}
