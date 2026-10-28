using LBS.Domain;

namespace LBS.Infrastructure;

/// <summary>
/// 分站周报聚合（StationDb partial，零新表）：所有数都从现成台账里推——videos / danmaku_local / events / members，
/// 全站大数直接复用站长室柜台的 AdminStats（同一张嘴算出来的数，两个页面不打架）。
/// 周桶口径是「本地周」：UTC+8 的周一 00:00 起算一整周，换算成 UTC 区间再查库——
/// 台账里的 ts_utc / created_at_utc 一律是 "O" 定长格式（+00:00、七位小数）的文本，同格式字符串比对即时间序。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>周报水位 settings 键：值 = 已发的最近完整周标识（ISO 周，如 "2026-W44"，本地 UTC+8 周口径）。</summary>
    public const string WeeklySentWatermarkKey = "weekly-report-sent";

    /// <summary>分站挂钟：UTC+8。周桶一律按这个时区的周一 00:00 起算，与服务器时区无关。</summary>
    public static readonly TimeSpan LocalWeekOffset = TimeSpan.FromHours(8);

    /// <summary>「本周新货」清单封顶：横排卡摆一排的量。</summary>
    public const int WeeklyNewsTake = 8;

    /// <summary>「最多人看」榜长。</summary>
    public const int WeeklyHotTake = 5;

    /// <summary>周报能回看的最远完整周（1..N 往前数；0 是本周至今）。</summary>
    public const int WeeklyMaxWeekBack = 4;

    // 观看事件在台账里的名字是 EventIngestEndpoints 白名单那几枚字符串：heartbeat 有 EventNames 常量挂号，
    // watch.play 从来只有白名单字面量（StationEvents.cs 里没有 WatchPlay），这里照实况对齐，不另造名字。
    private const string WatchPlayEvent = "watch.play";

    public sealed record WeeklyBucket(DateTimeOffset SinceUtc, DateTimeOffset UntilUtc, string WeekLabel);

    /// <summary>新货一行：封面走馆藏 localCover 优先的现成口径（有 kind='cover' 产物行吃 /media/…，没有回落热链）。</summary>
    public sealed record WeeklyNewsItem(string Bvid, string? Title, string? CoverUrl);

    /// <summary>最多人看一行：viewers 是 COUNT(DISTINCT actor_mid)——观众按 B 站 mid 去重，访客（mid 为 NULL）不进座。</summary>
    public sealed record WeeklyHotItem(string Bvid, string? Title, string? CoverUrl, long Viewers);

    /// <summary>全站家底：videos/bytes 复用 AdminStats 口径（总稿件行数含未到站的、bytes 是视频库磁盘现扫）；members 数 members 全行（与数据交换的清点同口径）。</summary>
    public sealed record WeeklyTotals(long Videos, long Members, long Danmaku, long Bytes);

    /// <summary>一张周报的全部家当（端点按契约形外发，这里只管给数）。</summary>
    public sealed record WeeklyReport(
        string WeekLabel,
        string SinceUtc,
        string UntilUtc,
        long NewsCount,
        IReadOnlyList<WeeklyNewsItem> News,
        IReadOnlyList<WeeklyHotItem> Hottest,
        long DanmakuAdded,
        WeeklyTotals Totals);

    /// <summary>
    /// 周桶换算（纯函数，测试直捣）：weeksBack 夹进 0..4，0=本地本周（可至今），1..4=往前数第 N 个完整周。
    /// 桶界取本地（UTC+8）周一 00:00，翻成 UTC 端点返回；0 桶的上界夹到 now（未来不预支）。
    /// WeekLabel 是 ISO 周标识（周一为一周之首，含首个星期四的周记为该年第 1 周），如 "2026-W44"。
    /// </summary>
    public static WeeklyBucket LocalWeekBucket(int weeksBack, DateTimeOffset nowUtc)
    {
        var back = Math.Clamp(weeksBack, 0, WeeklyMaxWeekBack);
        var localNow = nowUtc.ToOffset(LocalWeekOffset);
        var mondayThisWeek = DateOnly.FromDateTime(localNow.DateTime)
            .AddDays(-((int)localNow.DayOfWeek + 6) % 7); // DayOfWeek 周日=0，掰成周一=0
        var start = mondayThisWeek.AddDays(-7 * back);
        var since = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), LocalWeekOffset);
        var until = since.AddDays(7);
        if (back == 0 && localNow < until)
        {
            until = localNow;
        }

        return new WeeklyBucket(since.ToUniversalTime(), until.ToUniversalTime(), LocalWeekKey(start));
    }

    /// <summary>ISO 周标识（本地日历日）："2026-W44"。跨年周跟着星期四认年份，与水位比大小同一把尺。</summary>
    public static string LocalWeekKey(DateOnly localDate)
    {
        var thursday = localDate.AddDays(3 - ((int)localDate.DayOfWeek + 6) % 7);
        var week = (thursday.DayOfYear - 1) / 7 + 1;
        return $"{thursday.Year}-W{week:D2}";
    }

    /// <summary>取一张周报（正对现在）。weeksBack：0=本地本周至今，1..4=往前数完整周（越界夹回）。</summary>
    public WeeklyReport GetWeeklyReport(int weeksBack) => GetWeeklyReport(weeksBack, DateTimeOffset.UtcNow);

    /// <summary>取一张周报（now 给死版，周桶测试与发送员用）：新货 / 最多人看 / 弹幕增量 / 全站家底一趟拿齐。</summary>
    public WeeklyReport GetWeeklyReport(int weeksBack, DateTimeOffset nowUtc)
    {
        var bucket = LocalWeekBucket(weeksBack, nowUtc);
        var since = bucket.SinceUtc.ToString("O");
        var until = bucket.UntilUtc.ToString("O");

        long newsCount;
        var news = new List<WeeklyNewsItem>();
        var hottest = new List<WeeklyHotItem>();
        long danmakuAdded;

        using (var connection = Open(_dbPath))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM videos WHERE created_at_utc >= $since AND created_at_utc < $until;";
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                newsCount = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }

            using (var command = connection.CreateCommand())
            {
                // 新货按到站投稿时间倒序；封面口径与首页卡阵同形（本地原图优先，没归档的吃 cover_url 热链）。
                command.CommandText = """
                    SELECT v.bvid, v.title,
                           COALESCE((SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                                    v.cover_url)
                    FROM videos v
                    WHERE v.created_at_utc >= $since AND v.created_at_utc < $until
                    ORDER BY v.created_at_utc DESC, v.id DESC
                    LIMIT $take;
                    """;
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                command.Parameters.AddWithValue("$take", WeeklyNewsTake);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    news.Add(new WeeklyNewsItem(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            using (var command = connection.CreateCommand())
            {
                // 最多人看：events 台账里 watch.play / watch.heartbeat 的实况行（subject_type='video'、subject_id=BV 号字符串，
                // 上报处 WatchPage.vue reportEvent 钉的死这个形）。观众按 actor_mid 去重——访客没 mid，COUNT(DISTINCT) 天然不数，
                // 全员皆访客的一条进不了榜（宁可空榜，不拿 IP 冒充人头）。
                command.CommandText = """
                    SELECT v.bvid, v.title,
                           COALESCE((SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                                    v.cover_url),
                           COUNT(DISTINCT e.actor_mid),
                           COUNT(*)
                    FROM events e
                    JOIN videos v ON v.bvid = e.subject_id
                    WHERE e.event IN ($play, $beat)
                      AND e.subject_type = 'video'
                      AND e.ts_utc >= $since AND e.ts_utc < $until
                    GROUP BY v.id, v.bvid, v.title
                    HAVING COUNT(DISTINCT e.actor_mid) > 0
                    ORDER BY COUNT(DISTINCT e.actor_mid) DESC, COUNT(*) DESC, MAX(e.id) DESC
                    LIMIT $take;
                    """;
                command.Parameters.AddWithValue("$play", WatchPlayEvent);
                command.Parameters.AddWithValue("$beat", EventNames.WatchHeartbeat);
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                command.Parameters.AddWithValue("$take", WeeklyHotTake);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    hottest.Add(new WeeklyHotItem(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt64(3)));
                }
            }

            using (var command = connection.CreateCommand())
            {
                // 最热讨论只算站内弹幕：B 站档案弹幕住在 ass 文件里、视频侧评论住在 {base}.comments.json 快照里，
                // 两边都没有逐条落库的时间行，周增量无从数起——不编数。
                command.CommandText = "SELECT COUNT(*) FROM danmaku_local WHERE created_at_utc >= $since AND created_at_utc < $until;";
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                danmakuAdded = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }
        }

        // 全站大数抄柜台：/api/admin/stats 背后就是 AdminStats，这里直接调同一个方法取 videos/bytes，
        // 口径焊死在源头；members 用数据交换清点同款 COUNT(*)（全行含封禁/待邀），danmaku 总数是站内弹幕全表。
        var stats = AdminStats();
        long members;
        long danmakuTotal;
        using (var connection = Open(_dbPath))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM members;";
                members = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM danmaku_local;";
                danmakuTotal = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }
        }

        return new WeeklyReport(bucket.WeekLabel, since, until, newsCount, news, hottest, danmakuAdded,
            new WeeklyTotals(stats.Total, members, danmakuTotal, stats.TotalBytes));
    }
}
