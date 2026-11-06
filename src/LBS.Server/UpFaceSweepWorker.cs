using System.Globalization;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// UP 巡脸员：每周给站内每一位 UP 去 card 口问一次脸，换脸就在 <c>data/up_faces</c> 存一张、
/// <c>up_faces</c> 表里添一行，没换脸只把 <c>last_seen</c> 挪一挪 —— 攒的是「这位在本站的历张脸」，
/// 与 <c>video_meta.up_face</c> 那份当前名片互不打扰（名片怎么采归 <see cref="LbsUpInfoFetcher.FetchAsync"/>，这里一个字不碰）。
///
/// 壳照周报发报员（<see cref="WeeklyReportWorker"/>）学：<c>BackgroundService</c> 十分钟醒一次、开机缓 60 秒
/// 让位给自愈队与备份员、水位住现成的 settings KV（键 <see cref="WatermarkKey"/>，值 = 本周周一的 <c>yyyy-MM-dd</c>，UTC 口径）。
/// 该不该巡 / 盖不盖水位全是 internal static 纯函数（测格直捣），Timer 壳不测。
///
/// 三条闸门定在轮次上（都是「宁可多问一次，不把没干成写成干完」）：
/// ▸ 名单空 → 本周没活，直接盖水位（空库十分钟醒一次问空气没意义）；
/// ▸ 没采集号 → 整轮跳过<b>不盖水位</b>（照 UP 认证存量回填那口径：没号跳过不算跑过，下一轮再来）；
/// ▸ 跑完整轮一张脸都没带回 → <b>不盖水位</b>，十分钟后重试（card 口整轮不认时盖了就是装「干完活」，这周再不巡）；
///   带回过哪怕一张就盖 —— 老 UP 十年不换脸是常态，为这个每十分钟扰一发 B 站永不收敛（StatusSyncWorker 那轮的教训）。
/// 整轮挂在 <see cref="LbsCollectPolicy.RunForActivityAsync"/> 的出号上下文里（LbsQueryClient 第一行就 RequireCookie）。
/// </summary>
public sealed class UpFaceSweepWorker(
    StationDb db,
    LbsUpInfoFetcher upInfo,
    LbsCollectPolicy policy,
    ILogger<UpFaceSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepGap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    /// <summary>位与位之间的歇脚（与 UP 认证存量回填同款 300ms）：一轮几十位，不歇就是在敲 B 站门。</summary>
    private static readonly TimeSpan MidGap = TimeSpan.FromMilliseconds(300);

    /// <summary>水位 settings 键：值 = 已巡过的最近一周的周一（UTC，<c>yyyy-MM-dd</c>）。</summary>
    internal const string WatermarkKey = "up-face-sweep-week";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("UP 巡脸员上岗，每 {Minutes} 分钟醒一次，跨周才逐位巡一轮", SweepGap.TotalMinutes);
        try
        {
            // 开站先缓一口气：别跟自愈队 / 备份员 / 封面补员抢开工那几分钟的库锁与磁盘。
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepOnceAsync(stoppingToken);
                await Task.Delay(SweepGap, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 收工
        }
    }

    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunRoundAsync(DateTime.UtcNow, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 关门收兵：水位没动，下次开机接着巡这一周。
        }
        catch (Exception ex)
        {
            // 巡脸是锦上添花（档案缺几张不伤放映厅）：一轮砸了只吼 warning，水位不动，下一轮自然补上。
            logger.LogWarning(ex, "UP 巡脸这一轮没跑成（不致命），下一轮再试");
        }
    }

    /// <summary>
    /// 一轮巡场（now 给死版供直测）：该巡才出门 → 点名 → 逐位巡 → 按 <see cref="ShouldStampWatermark"/> 决定盖不盖水位。
    /// 先干完再盖（与 UP 认证存量回填同族：中途挂掉水位不动，下次还能重跑这一周）。
    /// </summary>
    internal async Task RunRoundAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!NeedsSweep(nowUtc, db.GetSetting(WatermarkKey)))
        {
            return;
        }

        var mids = db.ListSweepableMids();
        if (mids.Count == 0)
        {
            // 站内一个可巡的人都没有（空库 / 存量都被洗掉）：这周确实没活，水位照盖，免得十分钟醒一次问空气。
            db.SetSetting(WatermarkKey, WeekStamp(nowUtc));
            logger.LogInformation("UP 巡脸：站内没有可巡的 UP，本周水位直接盖上");
            return;
        }

        // 号池空了不装忙（UP 认证存量回填同款姿势）：先问一句再出号，也不拿水位把这一周一了百了。
        if (!policy.HasAccount())
        {
            logger.LogInformation("UP 巡脸：站里没可用的采集号，这 {Total} 位先欠着（水位不盖，下一轮再试）", mids.Count);
            return;
        }

        await policy.RunForActivityAsync(() => SweepMidsAsync(mids, nowUtc, cancellationToken));
    }

    private async Task SweepMidsAsync(IReadOnlyList<long> mids, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var filled = 0; // 带回过脸的位数：「真没人有脸」与「接口整轮不认」的唯一分辨凭证
        foreach (var mid in mids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await upInfo.SweepFaceAsync(mid, cancellationToken) is not null)
            {
                filled++;
            }

            await Task.Delay(MidGap, cancellationToken);
        }

        if (!ShouldStampWatermark(mids.Count, filled))
        {
            logger.LogWarning("UP 巡脸：{Total} 位问完一张脸都没带回——判为接口不认这趟，本周水位不盖，十分钟后再试", mids.Count);
            return;
        }

        db.SetSetting(WatermarkKey, WeekStamp(nowUtc));
        logger.LogInformation("UP 巡脸：巡脸补了 {Filled} 位 / 共 {Total} 位", filled, mids.Count);
    }

    /// <summary>
    /// 该不该巡（纯函数，真值表由测格钉死）：水位是「已巡过的最近一周的周一」，比的是<b>周</b>不是天 ——
    /// 同一周内怎么醒都不出门（换脸不是按小时发生的事，天天敲 card 口只是扰 B 站）；跨进新的一周才逐位巡一趟。
    /// 水位缺失 / 格式认不出（手贱改过 settings）一律按「该巡」处理：多巡一轮不过多发 N 发请求，漏巡一整周才是亏。
    /// </summary>
    internal static bool NeedsSweep(DateTime nowUtc, string? watermark) =>
        !DateOnly.TryParseExact(watermark, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamped)
        || stamped != WeekMonday(nowUtc);

    /// <summary>本周的周一（UTC 口径的日历日）：周界按本地历法周一起算，周日 23:59 与周一 00:01 分属两周。</summary>
    internal static DateOnly WeekMonday(DateTime nowUtc)
    {
        // DayOfWeek 周日=0…周六=6，(n+6)%7 把它折成「距本周一几天」：周一 0、周日 6。
        var offsetIntoWeek = ((int)nowUtc.DayOfWeek + 6) % 7;
        return DateOnly.FromDateTime(nowUtc.Date).AddDays(-offsetIntoWeek);
    }

    /// <summary>水位要盖的那个值：本周周一的 <c>yyyy-MM-dd</c>（格式写死Invariant，别拿机器区域设置赌大小）。</summary>
    internal static string WeekStamp(DateTime nowUtc) =>
        WeekMonday(nowUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 盖戳判据（纯函数供测格钉死）：整轮一张脸都没带回（attempted&gt;0 且 filled==0）不盖 —— card 口整轮不认，
    /// 盖了是把「没采到」焊成「这周干完了」，十分钟后再试；至少带回一张就盖（没脸可换的那位是常态，不盖就是永不收敛）；
    /// 名单空（attempted==0）也盖 —— 那周的账本就是「没人可巡」。<c>attempted</c> 即本轮点名的位数。
    /// </summary>
    internal static bool ShouldStampWatermark(int attempted, int filled) => attempted == 0 || filled > 0;
}
