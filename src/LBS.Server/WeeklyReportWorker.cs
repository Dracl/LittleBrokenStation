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
    /// 该不该发（纯函数）：候选周 = 相对 now 的最近一个**已过完**的整周（就是周报的 0 桶，同一把尺）；
    /// 发报窗口 = 该周收尾（本地周一 00:00）之后的 SendHourLocal 小时起。
    /// 已进窗口且水位落后候选周 → 回该发周的标识；否则 null（没到点、或这周早就发过）。
    /// </summary>
    internal static string? DueWeekKey(DateTimeOffset nowUtc, string? watermark)
    {
        var bucket = StationDb.LocalWeekBucket(0, nowUtc);
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

    /// <summary>这一周是不是真的什么都没干过（只看周内的流水，不看全站家底——家底永远有数，不算这周的动静）。</summary>
    internal static bool IsQuietWeek(StationDb.WeeklyReport report) =>
        report.NewsCount == 0
        && report.DanmakuAdded == 0
        && report.WatchOpens == 0
        && report.WatchPlays == 0
        && report.Duration.Shots == 0
        && report.Searches == 0
        && report.Submissions == 0
        && report.CommentsArchived == 0
        && report.DynamicsArchived == 0
        && report.DmSent == 0
        && report.NewMembers == 0;

    /// <summary>时行人话：够一小时报「2 小时 13 分」，不够报「35 分钟」，十分种以下报「40 秒」（短到零秒也报零秒，不拿空白冒充没看）。</summary>
    internal static string HumanDuration(long seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds} 秒";
        }

        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        return h > 0 ? (m > 0 ? $"{h} 小时 {m} 分" : $"{h} 小时") : $"{m} 分钟";
    }

    /// <summary>公告里的标题截短：超 24 字加省略号，不让一条长标题把整行顶坏。</summary>
    internal static string ShortTitle(string title) => title.Length <= 24 ? title : title[..24] + "…";

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

        var report = db.GetWeeklyReport(0, nowUtc); // 0 = 上一个已过完的整周（周桶语义改过：不给看未过完的本周）

        // 整周零动静就不打扰：分站常有闲着的一周，播一条「新增 0 件、弹幕 +0 条」的公告只是噪音
        // （家底那行永远有数，不能拿它当动静看）。水位照样盖上，免得下周回头又把同一周重播一遍。
        if (IsQuietWeek(report))
        {
            db.SetSetting(WatermarkKey, target);
            logger?.LogInformation("周报 {Week} 整周零动静，跳过播送只盖水位", target);
            return target;
        }

        // 公告正文的排版（站长 11-03「这条公告排版也修改一下，现在太乱了」）：
        // 一行只讲一类、行内最多三项；标签统一四字并用全角空格（'　'）补齐——中文字符与全角空格同宽，
        // 前端按 whitespace-pre-wrap 直出时那一列天然对齐（站内信正文没有 HTML 可用，纯文本能借的只有这一招）。
        // 值为 0 的项不摆、整行空了就整行不摆：原来那串「私信 0 封 · 没新人入席」是最吵的空账。
        var lines = new List<string>();
        void Row(string label, params string?[] items)
        {
            var kept = items.Where(i => !string.IsNullOrEmpty(i)).ToArray();
            if (kept.Length == 0)
            {
                return;
            }

            lines.Add(label.PadRight(5, '　') + string.Join(" · ", kept)); // 四字标签 + 一枚全角空格作间隔（PadRight(4) 会贴在一起）
        }

        var hottest = report.Hottest.Count > 0 ? report.Hottest[0] : null;
        Row("新增稿件", $"{report.NewsCount} 件", report.DanmakuAdded > 0 ? $"弹幕 +{report.DanmakuAdded} 条" : null);
        Row("本期之最", hottest is not null
            ? $"《{ShortTitle(hottest.Title ?? hottest.Bvid)}》{hottest.Viewers} 位观众看过"
            : null);
        Row("观看热度",
            report.UniqueViewers > 0 ? $"去重观众 {report.UniqueViewers} 人" : null,
            report.WatchPlays > 0 ? $"播放 {report.WatchPlays} 次" : null,
            report.Searches > 0 ? $"搜索 {report.Searches} 回" : null);
        Row("投稿入库",
            report.Submissions > 0 ? $"投稿 {report.Submissions} 次" : null,
            report.CommentsArchived > 0 ? $"评论 {report.CommentsArchived} 条" : null,
            report.DynamicsArchived > 0 ? $"动态 {report.DynamicsArchived} 条" : null);
        Row("席位变化", report.NewMembers > 0 ? $"新席位 +{report.NewMembers}" : null);
        // 家底是全站存量而不是本周流水，永远有数（整周零动静的周压根不发报，见 IsQuietWeek），所以它不参与「空了就整行不摆」；
        // 六项拆两行，续行用全角空格缩到标签那一列下面。
        Row("全站家底", $"稿件 {report.Totals.Videos}", $"成员 {report.Totals.Members}", $"弹幕 {report.Totals.Danmaku}");
        Row("　　", $"动态 {report.Totals.Dynamics}", $"存档评论 {report.Totals.DynamicComments}", $"私信 {report.Totals.DmMessages}");
        // 游客：有游客才拼（零游客不摆空账）；只报去重人数与次数，绝不外发 IP 本体。
        if (report.Guest.Heads > 0)
        {
            Row("站外游客",
                $"{report.Guest.Heads} 位未登录",
                report.Guest.Opens > 0 ? $"进厅 {report.Guest.Opens} 次" : null,
                report.Guest.BlockedWatch > 0 ? $"{report.Guest.BlockedWatch} 回想进片页被门挡住" : null);
        }

        // 时长那一块：只报成员心跳换算的墙钟秒（游客看不了片，一枪不入账），零心跳就不摆空账。
        // 三条各占一行——原来它们挤在一句长句里，标题一长就把整行撑爆。
        if (report.Duration.Shots > 0)
        {
            var topTimeVideo = report.Duration.TopVideos.Count > 0 ? report.Duration.TopVideos[0] : null;
            var topTimeUp = report.Duration.TopUps.Count > 0 ? report.Duration.TopUps[0] : null;
            Row("观看时长",
                $"真看了 {HumanDuration(report.Duration.Seconds)}",
                report.Duration.Finished > 0 ? $"看完 {report.Duration.Finished} 部" : null,
                $"心跳 {report.Duration.Shots} 枪");
            Row("最耗时长", topTimeVideo is not null
                ? $"《{ShortTitle(topTimeVideo.Title ?? topTimeVideo.Bvid)}》{HumanDuration(topTimeVideo.Seconds)}"
                : null);
            Row("看得最多", topTimeUp is not null ? $"{topTimeUp.Key} {HumanDuration(topTimeUp.Seconds)}" : null);
        }

        // 末尾不再写「去站里看 /weekly」那行纯文本（站长 11-03）：跳转交给前端那枚按钮，
        // 周号由它从公告标题「分站周报 · 2026-W44」自己认——正文里不留路径，也不留第二份指引。
        var body = string.Join('\n', lines);

        // 周报挂 weekly 静音频道（11-03）：与站长公告同 kind 不同路——嫌周报吵的人能关它，
        // 真公告（不带频道的 BroadcastToActive）照旧送达每一席，关不掉。
        var sent = db.BroadcastToActive(MemberMail.KindAnnounce, $"分站周报 · {target}", body, muteChannel: NotifyMutes.Weekly);
        db.SetSetting(WatermarkKey, target);
        logger?.LogInformation("周报 {Week} 已播送，送达 {Count} 席", target, sent);
        return target;
    }
}
