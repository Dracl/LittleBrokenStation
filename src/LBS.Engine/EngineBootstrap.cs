using DownKyi.Application.Settings;
using DownKyi.Services.Download;
using LBS.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>
/// IHostedService 薄包装：分站进程生命周期驱动下载 runtime 启停，
/// 并在启动时自检 ffmpeg（FfmpegProcessRunner 会把 exec 失败吞成 mux 阶段失败，
/// 提前探测一次能把「容器没装 ffmpeg」从玄学变成一条日志）。
/// 首次开站时把画质种子成探针上限（validator 默认会把未设值扫到 120，分站承诺的是 127 自动上限）。
/// </summary>
public sealed class EngineBootstrap(
    DownloadBootstrapHostedService inner,
    ISettingsStore settingsStore,
    DataPaths paths,
    ILogger<EngineBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SeedStationDefaultsOnce();
        EnsureFfmpegAvailable();
        logger.LogInformation("下载引擎到站，启动下载 runtime");
        await inner.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 停机耗时入账：引擎优雅停机若超时未收摊会静默跳过相位回写（DownloadShutdownCoordinator），
        // 没有这条日志，下次出现僵尸任务时无从判断残留相位是哪来的。
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            logger.LogInformation("下载引擎离站，停止下载 runtime");
            await inner.StopAsync(cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation("下载 runtime 已停止（耗时 {Ms} 毫秒）", stopwatch.ElapsedMilliseconds);
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
            {
                logger.LogWarning("停机耗时过久，引擎可能没来得及回写任务相位，下次开站请留意自愈员的自愈记录");
            }
        }
    }

    private void SeedStationDefaultsOnce()
    {
        // 标记文件保证只种一次；v2 追加命名默认（站长钦定的 downkyi 同款序列），站长之后改过的不会被重启抹掉。
        var marker = Path.Combine(paths.Downkyi, "lbs-settings-seeded-v2");
        if (File.Exists(marker))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(paths.Downkyi);
            settingsStore.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    Quality = DownKyi.Core.BiliApi.BiliUtils.PlaybackQualityCatalog.MaximumProbeQuality,
                    FileNameParts = StationEngineSettingsMapper.DefaultFileNameParts,
                    OrderFormat = DownKyi.Application.Settings.OrderFormat.Natural,
                    FileNamePartTimeFormat = "yyyy-MM-dd",
                },
            });
            settingsStore.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            logger.LogInformation("首次开站：画质种子为探针上限（8K 自动探顶），命名种子为分站默认序列");
        }
        catch (Exception ex)
        {
            // 种子失败不阻断开站，下一次启动还会再试
            logger.LogWarning(ex, "画质种子没写成，本次启动用设置现值");
        }
    }

    private void EnsureFfmpegAvailable()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                throw new InvalidOperationException("无法启动 ffmpeg 进程");
            }

            process.WaitForExit(10_000);
            if (process.HasExited && process.ExitCode == 0)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PATH 里找不到可用的 ffmpeg，视频合流阶段会失败；容器镜像应自带系统 ffmpeg");
        }
    }
}
