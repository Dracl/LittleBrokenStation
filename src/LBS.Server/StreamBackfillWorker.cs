using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 分轨码率补员：探子只在「刚落盘/刚定档」那两个事件点执勤，上岗前就躺在盘上的老货永远没码率，
/// 播放页版本面板的「视频 X kbps / 音频 Y kbps」标签就一直空着。补员每小时上岗一轮，
/// 每张表最探 40 行没盖 stream_probed 戳的存量，探过即盖戳永不重扰。
/// ffprobe 是毫秒级轻活，不跟下载抢带宽，无需让路闸；开工抖 45 秒是给开机首屏让道。
/// </summary>
public sealed class StreamBackfillWorker(MediaStreamProber prober, ILogger<StreamBackfillWorker> logger) : BackgroundService
{
    private const int RoundLimit = 40;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("分轨码率补员到岗，45 秒后补首轮，之后每小时一轮收尾活");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = 0;
            try
            {
                var (pages, codecs) = prober.BackfillOnce(RoundLimit);
                worked = pages + codecs;
                if (worked > 0)
                {
                    logger.LogInformation("分轨码率补员收摊一轮：主版 {Pages} 行、加档 {Codecs} 行", pages, codecs);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "分轨码率补员本轮失手（下一轮再补）");
            }

            // 探满一轮 = 盘里还有存货，呾口气接着干；探空才退到每小时一巡的收尾节奏。
            var nextDelay = worked >= RoundLimit ? TimeSpan.FromSeconds(2) : TimeSpan.FromHours(1);
            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
