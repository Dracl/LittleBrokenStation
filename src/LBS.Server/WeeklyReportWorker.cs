using System.Globalization;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 周报发报员：每 10 分钟醒一次，问纯函数「当前是否已进某完整周的周一 09:00（本地 UTC+8）之后、且水位落后」。
/// 到点就往 active 席位播一条全站公告（与站长室 /api/admin/messages/announce 同款：BroadcastToActive + KindAnnounce），
/// 再把 settings["weekly-report-sent"] 盖上已发周标识（"2026-W44"）。零新表，水位就住在现成的 settings KV 里。
/// 该不该发 / 发哪周 / 水位比什么全是 internal static 纯函数（测试直捣），Timer 壳不测；
/// 一轮砸了只吼 warning 不写水位，下一轮自然补发——漏一周周报不是死刑事。
/// </summary>
public sealed class WeeklyReportWorker(StationDb db, ILogger<WeeklyReportWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepGap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    /// <summary>发报闸门：完整周收尾后，等到本地（UTC+8）周一几点才发——09:00 之前让人先睡个整觉。</summary>
    internal const int SendHourLocal = 9;

    /// <summary>水位 settings 键（值 = 已发的最近完整周标识），住在 StationDb 侧与聚合层同源。</summary>
    internal const string WatermarkKey = StationDb.WeeklySentWatermarkKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("周报发报员上岗，每 {Minutes} 分钟醒一次，周一 {Hour}:00（UTC+8）后补发上周周报", SweepGap.TotalMinutes, SendHourLocal);
        try
        {
            // 开站先缓一口气再巡：别跟自愈队 / 备份员抢开工那几分钟的库锁。
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                SweepOnce();
                await Task.Delay(SweepGap, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 收工
        }
    }

    private void SweepOnce()
    {
        try
        {
            RunTick(db, logger, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "周报发报砸了一轮，下一轮再试");
        }
    }

    /// <summary>
    /// 该不该发（纯函数）：候选周 = 相对 now 的最近一个完整周；发报窗口 = 该周收尾（本地周一 00:00）之后的 SendHourLocal 小时起。
    /// 已进窗口且水位落后候选周 → 回该发周的标识；否则 null（没到点、或这周早就发过）。
    /// </summary>
    internal static string? DueWeekKey(DateTimeOffset nowUtc, string? watermark)
    {
        var bucket = StationDb.LocalWeekBucket(1, nowUtc);
        if (nowUtc < bucket.UntilUtc.AddHours(SendHourLocal))
        {
            return null;
        }

        return WatermarkBehind(watermark, bucket.WeekLabel) ? bucket.WeekLabel : null;
    }

    /// <summary>
    /// 水位比什么（纯函数）：水位是「已发的最近完整周」标识，比的是 ISO 周 (年, 周号) 的大小，不是字符串新旧。
    /// 水位缺失 / 认不出格式当「落后」处理——宁可重发一条公告，不静默吞掉一周；水位超前（比如表把拨回去过）不发。
    /// </summary>
    internal static bool WatermarkBehind(string? watermark, string targetWeekKey)
    {
        if (string.IsNullOrWhiteSpace(watermark))
        {
            return true;
        }

        if (!TryParseWeekKey(watermark, out var sent) || !TryParseWeekKey(targetWeekKey, out var target))
        {
            return true;
        }

        return sent.CompareTo(target) < 0;
    }

    /// <summary>周标识认格式："2026-W44"（四位年 + -W + 两位周号共 8 字符，周号 1..53）。</summary>
    internal static bool TryParseWeekKey(string? key, out (int Year, int Week) parsed)
    {
        parsed = default;
        if (key is not { Length: 8 } || key[4] != '-' || key[5] != 'W')
        {
            return false;
        }

        if (!int.TryParse(key[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year <= 0)
        {
            return false;
        }

        if (!int.TryParse(key[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var week) || week is < 1 or > 53)
        {
            return false;
        }

        parsed = (year, week);
        return true;
    }

    /// <summary>
    /// 一轮巡检（now 给死版供直测）：该发 → 聚合上周数、BroadcastToActive 播全站公告、盖水位，回已发周标识；不该发 → null。
    /// 先播后盖：播砸了水位不动，下轮补发；播成功盖水位前进程没了才会重播一次——公告重发一条，不伤台账。
    /// </summary>
    internal static string? RunTick(StationDb db, ILogger? logger, DateTimeOffset nowUtc)
    {
        var target = DueWeekKey(nowUtc, db.GetSetting(WatermarkKey));
        if (target is null)
        {
            return null;
        }

        var report = db.GetWeeklyReport(1, nowUtc);
        var highlight = report.Hottest.Count > 0
            ? $"上周新增稿件 {report.NewsCount} 件，弹幕 +{report.DanmakuAdded} 条，最多人看的一条有 {report.Hottest[0].Viewers} 位观众。"
            : $"上周新增稿件 {report.NewsCount} 件，弹幕 +{report.DanmakuAdded} 条。";
        var body = string.Join('\n',
            highlight,
            $"全站家底：{report.Totals.Videos} 件稿件 · {report.Totals.Members} 位成员 · {report.Totals.Danmaku} 条弹幕。",
            "去站里看 /weekly");

        var sent = db.BroadcastToActive(MemberMail.KindAnnounce, $"分站周报 · {target}", body);
        db.SetSetting(WatermarkKey, target);
        logger?.LogInformation("周报 {Week} 已播送，送达 {Count} 席", target, sent);
        return target;
    }
}
