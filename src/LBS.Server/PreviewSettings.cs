using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 悬停预览的站长设定：六项全存 settings KV（preview-* 前缀），与 downkyi 引擎设置划清界限——
/// 那是引擎的下载参数，这里是分站自己的派生加工参数，混进 StationEngineSettingsDto 会把引擎设置表单搅脏，
/// 还得同步改 TryApplyDraft 的白名单。读写照 DownloadPauseBoard 的节奏：懒读进内存、写时白名单校验并立刻生效。
/// </summary>
public sealed class PreviewSettings(StationDb db, ILogger<PreviewSettings> logger)
{
    public const string KeyEnabled = "preview-enabled";
    public const string KeyRes = "preview-res";
    public const string KeySeconds = "preview-seconds";
    public const string KeyBatch = "preview-batch";
    public const string KeyConcurrency = "preview-concurrency";
    public const string KeyYield = "preview-yield";

    public const int SecondsDefault = 45;
    public const int BatchDefault = 40;
    public const int ConcurrencyDefault = 1;

    public const int SecondsMin = 5;
    public const int SecondsMax = 120;
    public const int BatchMin = 5;
    public const int BatchMax = 200;
    public const int ConcurrencyMin = 1;
    /// <summary>
    /// 同时转码的封顶路数。上限放到 32（站长要快时能真推得动），但这个数不是越大越好：
    /// 每一路 ffmpeg 都是一个 CPU 核，NAS 上开高了会把下载、页面应答与转码三边一起拖慢，
    /// 而且让路开关关着时它是真会抢带宽的。日常建议 1～4，追齐积压时再往上拱。
    /// </summary>
    public const int ConcurrencyMax = 32;

    private readonly object pad = new();
    private bool initialized;
    private bool enabled = true;
    private int res = 360;
    private int seconds = SecondsDefault;
    private int batch = BatchDefault;
    private int concurrency = ConcurrencyDefault;
    private bool yieldOnDownload = true;

    public bool Enabled => Read().Enabled;

    /// <summary>目标高度（360/480/720）；库里存了野值一律退回 360。</summary>
    public int Res => Read().Res;

    /// <summary>预览取成片开头几秒（5..120）。</summary>
    public int Seconds => Read().Seconds;

    /// <summary>每轮封顶几条（5..200）。</summary>
    public int Batch => Read().Batch;

    /// <summary>同时转码几路（1..2，NAS 上多开只会把两边都拖慢）。</summary>
    public int Concurrency => Read().Concurrency;

    /// <summary>有没有下载就让路。</summary>
    public bool YieldOnDownload => Read().YieldOnDownload;

    /// <summary>一次读齐六个值（懒加载 + 白名单校验），调用方各取所需。</summary>
    public (bool Enabled, int Res, int Seconds, int Batch, int Concurrency, bool YieldOnDownload) Snapshot() => Read();

    private (bool Enabled, int Res, int Seconds, int Batch, int Concurrency, bool YieldOnDownload) Read()
    {
        if (!initialized)
        {
            lock (pad)
            {
                if (!initialized)
                {
                    enabled = db.GetSetting(KeyEnabled) != "0";
                    res = ParseInt(db.GetSetting(KeyRes)) is { } r && Ffmpeg.HeightChoices.Contains(r) ? r : 360;
                    seconds = Clamp(ParseInt(db.GetSetting(KeySeconds)), SecondsMin, SecondsMax, SecondsDefault);
                    batch = Clamp(ParseInt(db.GetSetting(KeyBatch)), BatchMin, BatchMax, BatchDefault);
                    concurrency = Clamp(ParseInt(db.GetSetting(KeyConcurrency)), ConcurrencyMin, ConcurrencyMax, ConcurrencyDefault);
                    yieldOnDownload = db.GetSetting(KeyYield) != "0";
                    initialized = true;
                }
            }
        }

        return (enabled, res, seconds, batch, concurrency, yieldOnDownload);
    }

    /// <summary>
    /// 站长改设定：清晰度只认白名单（360/480/720），不在名单里整单打回（回 false，让前端把话说明白）；
    /// 秒数/每轮条数/并发数是范围量，越界就地夹回边界——滑块拖过头不该吃一个 400。
    /// </summary>
    public bool Update(bool? enabled, int? res, int? seconds, int? batch, int? concurrency, bool? yieldOnDownload)
    {
        if (res is { } requestedRes && !Ffmpeg.HeightChoices.Contains(requestedRes))
        {
            logger.LogInformation("预览清晰度 {Res} 不在白名单里，整单没写", requestedRes);
            return false;
        }

        Read(); // 先把懒加载走完，下面的写入才有底子
        lock (pad)
        {
            if (enabled is { } on)
            {
                this.enabled = on;
                db.SetSetting(KeyEnabled, on ? "1" : "0");
            }

            if (res is { } resValue)
            {
                this.res = resValue;
                db.SetSetting(KeyRes, resValue.ToString());
            }

            if (seconds is { } sec)
            {
                this.seconds = Clamp(sec, SecondsMin, SecondsMax, SecondsDefault);
                db.SetSetting(KeySeconds, this.seconds.ToString());
            }

            if (batch is { } size)
            {
                this.batch = Clamp(size, BatchMin, BatchMax, BatchDefault);
                db.SetSetting(KeyBatch, this.batch.ToString());
            }

            if (concurrency is { } ways)
            {
                this.concurrency = Clamp(ways, ConcurrencyMin, ConcurrencyMax, ConcurrencyDefault);
                db.SetSetting(KeyConcurrency, this.concurrency.ToString());
            }

            if (yieldOnDownload is { } yieldOn)
            {
                this.yieldOnDownload = yieldOn;
                db.SetSetting(KeyYield, yieldOn ? "1" : "0");
            }

            initialized = true;
        }

        logger.LogInformation(
            "预览设定已改：开关={Enabled} 清晰度={Res} 秒数={Seconds} 每轮={Batch} 并发={Concurrency} 让路={Yield}",
            this.enabled, this.res, this.seconds, this.batch, this.concurrency, this.yieldOnDownload);
        return true;
    }

    private static int? ParseInt(string? raw) =>
        int.TryParse(raw, out var value) ? value : null;

    private static int Clamp(int? value, int min, int max, int fallback) =>
        value is null ? fallback : Math.Clamp(value.Value, min, max);
}
