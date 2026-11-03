using System.Text.Json;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 到站播报员（站长 11-03 点头的路线图 🔭 首件「到站通知」）：订阅巡站的新货完整到站后，
/// 系统名义往站内信箱播一条结构化通报——顶栏铃铛亮红点，点开是一版排好的到货卡（封面缩略＋标题＋UP），
/// 不再靠人自己打开首页才发现「站里不知何时多了几枚新货」。
///
/// 形状照周报发报员（<see cref="WeeklyReportWorker"/>）的样板学：BackgroundService 十分钟醒一次、
/// 巡检逻辑收进 <see cref="RunTick"/> 纯函数（Timer 壳不测、测试直捣纯函数）、水位住 settings KV、
/// 先播后盖（播砸了水位不动，下轮补发；宁重播一条，不漏播一批）。
///
/// 为什么盯「到站」而不是盯「投稿」：爬工 <c>AddVideosBulk</c> 成功只是 Pending 入队，货还没到——
/// 那时候播「到货」是空报。判据「Arrived 且无归属行」把该播的与不该播的分得干净：
/// 爬工货恒无归属（SubmissionIntake.cs:22 注释钉着），成员自投恒有归属（MemberMail 的回执已各自伺候）。
/// 爬工本体一个字不改——主流程零加戏，这是它和周报同族的另一条理由。
/// </summary>
public sealed class ArrivalReportWorker(StationDb db, ILogger<ArrivalReportWorker> logger) : BackgroundService
{
    /// <summary>一波最多播几件：8 件正好铺满一屏通报卡；超过的留在台账里，下一波接着播（水位分批推进）。</summary>
    internal const int BatchCap = 8;

    /// <summary>messages.msg_type 的到货卡标识：前端认这一枚决定「渲染卡面还是纯文本」。</summary>
    internal const string MsgTypeArrivals = "arrivals";

    internal const string WatermarkKey = StationDb.ArrivalReportWatermarkKey;

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BootDelay = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 开机先让位：媒体对账、封面补员、预览转码都在抢磁盘与 ffmpeg；且刚开站没人看信箱，抢播无益。
        await Task.Delay(BootDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunTick(db, logger);
            }
            catch (OperationCanceledException)
            {
                throw; // 站点关停照原样上抛
            }
            catch (Exception ex)
            {
                // 播报是锦上添花：播砸只留痕，下一轮重来（水位没动，货不会丢）。
                logger.LogWarning(ex, "到站播报员这一轮没播出去，下一轮再看");
            }

            await Task.Delay(Tick, stoppingToken);
        }
    }

    /// <summary>
    /// 一轮巡检（now 不用给——判据里没有日历，只有台账）：从水位之后捞出到货 → 组卡 → 播全站 → 盖水位，
    /// 回送达席数；无新货回 null（水位也不动：这里的水位记「消费到哪件」，与周报的「发到哪周」不同族，
    /// 没有「零动静照样盖」一说）。先播后盖：播砸水位不动，下轮补发。
    /// </summary>
    internal static int? RunTick(StationDb db, ILogger? logger)
    {
        // 首岗垫底：水位从没盖过 = 新功能上岗，站里存量（可能几百件）一律算「已播」，
        // 直接静默盖到当前最大 id——不开岗即补播二十通报，把站长室炸成到货超市。
        // （与封面缩略当年「存量不回填、新货才切」同一族思路；水位在但认不出的脏值仍按 0 从头播，
        // 那是台账在说谎，宁重播不漏播。）
        var raw = db.GetSetting(WatermarkKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            var baseline = db.GetMaxVideoId();
            db.SetSetting(WatermarkKey, baseline.ToString());
            logger?.LogInformation("到站播报员首岗：站内存量 {Count} 件一律算已播，水位静默盖到 {Watermark}", baseline, baseline);
            return null;
        }

        var watermark = ReadWatermark(raw);
        var batch = db.ListArrivalsSince(watermark, BatchCap);
        if (batch.Count == 0)
        {
            return null;
        }

        var title = $"分站到货 · {batch.Count} 件新货";
        // 正文是纯文本兜底（渲染器认不出卡面也不白屏）：一件一行、标题超 24 字加省略号——
        // 周报公告同款排版律，一行只讲一件，绝不把几枚标题挤成一坨。
        var body = string.Join('\n', batch.Select((card, i) =>
        {
            var display = string.IsNullOrWhiteSpace(card.Title) ? card.Bvid : ShortTitle(card.Title!);
            var up = string.IsNullOrWhiteSpace(card.UpName) ? "" : $" ——{card.UpName}";
            return $"{i + 1}. 《{display}》{up}";
        }));
        // 卡面快照在播送这一刻定格（与 ShareCard 的 dyn_json 同一先例）：读路径不现查，
        // 30 秒的顶栏轮询不陪跑；日后删片，点卡走现成「迷路小电视」，通报是到站那一刻的记事。
        var payloadJson = JsonSerializer.Serialize(batch.Select(card => new
        {
            bvid = card.Bvid,
            title = card.Title,
            cover = card.Cover,
            coverUrl = card.CoverUrl,
            coverThumbs = card.CoverThumbs,
            up = card.UpName,
            duration = card.DurationText,
        }));

        var sent = db.BroadcastToActive(MemberMail.KindArrival, title, body, MsgTypeArrivals, payloadJson);
        var newWatermark = batch[^1].VideoId;
        db.SetSetting(WatermarkKey, newWatermark.ToString());
        logger?.LogInformation("到站播报已播送：{Count} 件新货送达 {Seats} 席，水位盖到 {Watermark}",
            batch.Count, sent, newWatermark);
        return sent;
    }

    /// <summary>水位语义是「id 大于它」的数字比值；缺失或读不出按 0（从头播起，宁重播不漏播——
    /// 与周报「认不出的水位当落后」同一立场）。负数也当 0。首岗垫底（从没盖过）不走这里，见 RunTick。</summary>
    internal static long ReadWatermark(string? raw)
    {
        return long.TryParse(raw, out var id) && id > 0 ? id : 0;
    }

    /// <summary>标题截短：超 24 字加省略号，不让一枚长标题把一行顶坏（周报同款尺子）。</summary>
    internal static string ShortTitle(string title) => title.Length <= 24 ? title : title[..24] + "…";
}
