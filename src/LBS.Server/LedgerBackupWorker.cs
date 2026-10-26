using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 台账备份周期工：上岗两分钟后交头一份（若有），此后每小时巡一次，
/// 最新快照超龄（按站长的 backup-interval-hours）就补一张。
/// 备份不打扰下载，也不看暂停板脸色——台账的安稳与闸门无关。
/// </summary>
public sealed class LedgerBackupWorker(LedgerBackup backup, ILogger<LedgerBackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SweepGap = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            Sweep();

            using var timer = new PeriodicTimer(SweepGap);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // 收工
        }
    }

    private void Sweep()
    {
        try
        {
            var last = backup.LastBackupUtc();
            if (last is null || DateTimeOffset.UtcNow - last.Value >= TimeSpan.FromHours(backup.IntervalHours))
            {
                backup.Run();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "台账快照没落成，下小时再试");
        }
    }
}
