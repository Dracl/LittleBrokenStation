using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>「本周新货」清单封顶：分页一排四列两行（八张）；总数另走 newsCount，分页翻的是全量。</summary>
    public const int WeeklyNewsTake = 8;

    /// <summary>七日活动柱图的柱数（本地周一为首；整周都是过完的，固定七根，没动静的日子不补零柱）。</summary>
    public const int WeeklyDaySlots = 7;

    /// <summary>断更榜最多摆几位（只在订阅台账里的人中间排）。</summary>
    public const int WeeklyStaleTake = 6;

    /// <summary>本周新货分页拉取封顶（端点第二页用）：周新货百件量级，200 够撑 25 页。</summary>
    public const int WeeklyNewsPagedTake = 200;

    /// <summary>「最多人看」榜长。</summary>
    public const int WeeklyHotTake = 5;

    /// <summary>周报能回看的最远完整周（0..N 往前数；0 就是上一个刚过完的整周，不给看未过完的本周）。</summary>
    public const int WeeklyMaxWeekBack = 4;

    /// <summary>心跳一枪代表的墙钟秒数：前端 WatchPage 每 30 秒报一枪且「没在播就不报」，
    /// 时长 = 枪数×30；看半分钟以下走人记 0，所以时长永远宁少不多。</summary>
    public const int WeeklyHeartbeatSeconds = 30;

    /// <summary>「看完」的门槛千分比：心跳报过的最大播放头达片长 90% 就算看完（拖到片尾即认，不为最后几秒较真）。</summary>
    public const long WeeklyFinishPermille = 900;

    /// <summary>片长文本的字母形（"4m14s" / "1h2m3s"）。</summary>
    private static readonly Regex DurationPartsRegex = new(@"(\d+)\s*([hms])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>时长榜的榜长（与热榜同长，五排一屏）。</summary>
    public const int WeeklyDurationTake = 5;

    // 观看事件在台账里的名字是 EventIngestEndpoints 白名单那几枚字符串：heartbeat 有 EventNames 常量挂号，
    // watch.play 从来只有白名单字面量（StationEvents.cs 里没有 WatchPlay），这里照实况对齐，不另造名字。
    private const string WatchPlayEvent = "watch.play";

    public sealed record WeeklyBucket(DateTimeOffset SinceUtc, DateTimeOffset UntilUtc, string WeekLabel);

    /// <summary>新货一行：封面走馆藏 localCover 优先的现成口径（有 kind='cover' 产物行吃 /media/…，没有回落热链）。</summary>
    public sealed record WeeklyNewsItem(string Bvid, string? Title, string? CoverUrl);

    /// <summary>最多人看一行：viewers 是 COUNT(DISTINCT actor_mid)——观众按 B 站 mid 去重，访客（mid 为 NULL）不进座。</summary>
    public sealed record WeeklyHotItem(string Bvid, string? Title, string? CoverUrl, long Viewers);

    /// <summary>最受欢迎 UP 一行：靠本周播放事件 join video_meta 现推——viewers 同热榜去重口径，plays 是播放次数，videos 是被看的片数。</summary>
    public sealed record WeeklyUpItem(string UpName, long Viewers, long Plays, long Videos);

    /// <summary>最受欢迎分区一行：zone 住在 video_meta（「音乐>翻唱」这种全称），空串归「未标分区」。</summary>
    public sealed record WeeklyZoneItem(string Zone, long Viewers, long Plays, long Videos);

    /// <summary>个人战报：登录者自己这周的动静（events.actor_member_id / 弹幕 sender / 私信发件人锁人），访客凑不了数。</summary>
    public sealed record WeeklyPersonal(
        long Opens, long Plays, long VideosWatched, long Searches, long Submissions, long Logins,
        long DanmakuSent, long DmSent, long CommentsPosted);

    /// <summary>个人对比全站：占比是本人播放数除全站播放数，名次是本周观众按播放数排的座次（没观众时名次为 null，不编第几名）。</summary>
    public sealed record WeeklyPersonalVsAll(long SharePermille, long? Rank, long HeadCount);

    /// <summary>环比一行：本桶数与上桶数，涨跌由前端现算（上一周桶没数据时 last 是 0，前端自己辨「无从比」还是「真涨」）。</summary>
    public sealed record WeeklyDelta(string Key, long Current, long Last);

    /// <summary>更新榜一行：订阅台账（dynamics_targets）里的人本周冒没冒泡——lastDays 是从最后一条动态到今天的天数；
    /// 一条都没抓过的人为 null（前端显「没档」，不拿空白冒充断更天数）。</summary>
    public sealed record WeeklyStale(long Mid, string? Nickname, string? FaceUrl, long DynThisWeek, long DynTotal, long? LastDays, string? LastAtUtc);

    /// <summary>七日活动柱图的一根柱：本地日历日为键，柱高看播放（play），淡柱是进厅（open），柱上人头另标；
    /// GuestShots 是当日游客的观映枪数（画第三根柱用），GuestHeads 是当日游客去重 IP 数（游客无 mid，只能拿 IP 当尺，同一出口会并成一人）。</summary>
    public sealed record WeeklyDayBar(string LocalDate, long Opens, long Plays, long Heads, long GuestShots, long GuestHeads);

    /// <summary>站外游客账：全部拿 actor_type='guest' 的行走，身份只能拿 IP 去重（游客无 mid）。
    /// BlockedWatch 是游客想进片页被门禁挡下的枪数（subject_id 形如 /api/watch/BV…，与弹幕轮询的空转分开数）；
    /// BlockedDanmaku 是游客读弹幕被挡的枪数（前端重试造成的噪音，不当「想看被拦」的硬信号）。外发禁带 IP 本体。</summary>
    public sealed record WeeklyGuest(
        long Heads, long Opens, long Plays, long Searches, long BlockedWatch, long BlockedDanmaku, long LoginTries);

    /// <summary>全站家底：videos/bytes 复用 AdminStats 口径（总稿件行数含未到站的、bytes 是视频库磁盘现扫）；members 数 members 全行（与数据交换的清点同口径）；动态/评论/私信/弹幕总数都取全表 COUNT。</summary>
    public sealed record WeeklyTotals(long Videos, long Members, long Danmaku, long Bytes, long Dynamics, long DynamicComments, long DmMessages);

    /// <summary>播放实况：本周放映厅的真动静——进厅（open）/播放（play）/心跳各多少回，观众按人头去重（访客没 mid 不数进座）。</summary>
    public sealed record WeeklyPlay(long Opens, long Plays, long Beams, long ViewerHeads);

    /// <summary>时长榜一行（视频粒度）：seconds 是成员心跳枪数×30 的墙钟秒（游客看不了片，一枪不入账）；
    /// finishPermille 是完播千分比——该 BV 心跳报过的最大播放头 ÷ 片长，封顶 1000；
    /// 片长缺档（无 pages 行或文本认不出）时为 null，前端显「片长没档」，不拿 0 冒充「没看完」。</summary>
    public sealed record WeeklyDurationVideo(string Bvid, string? Title, string? CoverUrl, long Seconds, long Shots, long? FinishPermille);

    /// <summary>时长榜一行（UP / 分区粒度）：seconds 同视频榜口径（心跳枪数×30）；
    /// finished 是名下「看完」的片数（完播千分比达 WeeklyFinishPermille 记一次）。</summary>
    public sealed record WeeklyDurationGroup(string Key, long Seconds, long Shots, long Videos, long Finished);

    /// <summary>时长总账：本周成员真在看的墙钟秒（心跳一枪记 30 秒；前端「没在播就不报」的规矩守着口径，
    /// 30 秒是最短粒度，看半分钟以下走人记 0，所以时长永远宁少不多）；
    /// AvgOpenSeconds 是每次进厅平均看多久（进厅是母集；播放会因暂停恢复重报，拿它当分母会把时长压小）。</summary>
    public sealed record WeeklyDuration(
        long Seconds, long Shots, long Videos, long Finished, long AvgOpenSeconds,
        IReadOnlyList<WeeklyDurationVideo> TopVideos,
        IReadOnlyList<WeeklyDurationGroup> TopUps,
        IReadOnlyList<WeeklyDurationGroup> TopZones);

    /// <summary>个人时长账：只数登录者自己的心跳（别人与游客的一枪都不进自己账），看完口径与全站同一条线。</summary>
    public sealed record WeeklyPersonalDuration(long Seconds, long Shots, long Videos, long Finished, long AvgOpenSeconds);

    /// <summary>一张周报的全部家当（端点按契约形外发，这里只管给数）。</summary>
    public sealed record WeeklyReport(
        string WeekLabel,
        string SinceUtc,
        string UntilUtc,
        long NewsCount,
        IReadOnlyList<WeeklyNewsItem> News,
        IReadOnlyList<WeeklyHotItem> Hottest,
        long DanmakuAdded,
        long WatchPlays,
        long WatchOpens,
        long UniqueViewers,
        long Searches,
        long Submissions,
        long Logins,
        long NewMembers,
        long CommentsArchived,
        long DynamicsArchived,
        long DmSent,
        WeeklyPlay Play,
        WeeklyDuration Duration,
        IReadOnlyList<WeeklyUpItem> TopUps,
        IReadOnlyList<WeeklyZoneItem> TopZones,
        WeeklyPersonal? Personal,
        WeeklyPersonalVsAll? PersonalVsAll,
        WeeklyPersonalDuration? PersonalDuration,
        IReadOnlyList<WeeklyDelta> Deltas,
        IReadOnlyList<WeeklyDayBar> DayBars,
        IReadOnlyList<WeeklyStale> StaleUps,
        WeeklyGuest Guest,
        WeeklyTotals Totals);

    /// <summary>
    /// 周桶换算（纯函数，测试直捣）：weeksBack 夹进 0..4，**全是已过完的整周**——0=上一个完整周（最近的那一周），
    /// 1..4=再往前数。桶界取本地（UTC+8）周一 00:00 到下周一 00:00，翻成 UTC 端点返回。
    /// 为何不开「本周至今」那一档：周报要的是盖棺定论的数——挂着未过完的一周，新货、时长、弹幕天天在长，
    /// 同一个 URL 今天回看跟明天回看不一样，环比也变成「未过完的一周 ÷ 整周」这种偏心账（发报员就是周一 9:00 播上一周，同一把尺）。
    /// WeekLabel 是 ISO 周标识（周一为一周之首，含首个星期四的周记为该年第 1 周），如 "2026-W44"。
    /// </summary>
    public static WeeklyBucket LocalWeekBucket(int weeksBack, DateTimeOffset nowUtc)
    {
        var back = Math.Clamp(weeksBack, 0, WeeklyMaxWeekBack);
        var localNow = nowUtc.ToOffset(LocalWeekOffset);
        var mondayThisWeek = DateOnly.FromDateTime(localNow.DateTime)
            .AddDays(-((int)localNow.DayOfWeek + 6) % 7); // DayOfWeek 周日=0，掰成周一=0
        var start = mondayThisWeek.AddDays(-7 * (back + 1)); // back=0 就是本周之前的那个整周
        var since = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), LocalWeekOffset);
        var until = since.AddDays(7);

        return new WeeklyBucket(since.ToUniversalTime(), until.ToUniversalTime(), LocalWeekKey(start));
    }

    /// <summary>ISO 周标识（本地日历日）："2026-W44"。跨年周跟着星期四认年份，与水位比大小同一把尺。</summary>
    public static string LocalWeekKey(DateOnly localDate)
    {
        var thursday = localDate.AddDays(3 - ((int)localDate.DayOfWeek + 6) % 7);
        var week = (thursday.DayOfYear - 1) / 7 + 1;
        return $"{thursday.Year}-W{week:D2}";
    }

    /// <summary>片长文本解析（纯函数直测）："4m14s"→254、"1h2m3s"→3723、"1:02:03"→3723、"2:05"→125、
    /// 光板数字按秒认；认不出返回 null（报告里显「片长没档」，绝不拿 0 当成「一秒都没看」）。</summary>
    public static long? ParseDurationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim();
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3)
            {
                return null;
            }

            var hms = new long[3];
            var start = 3 - parts.Length;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!long.TryParse(parts[i].Trim(), out hms[start + i]))
                {
                    return null;
                }
            }

            return hms[0] * 3600 + hms[1] * 60 + hms[2];
        }

        var matches = DurationPartsRegex.Matches(s);
        if (matches.Count == 0)
        {
            return long.TryParse(s, out var bare) && bare >= 0 ? bare : null;
        }

        long total = 0;
        foreach (Match m in matches)
        {
            var v = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += char.ToLowerInvariant(m.Groups[2].Value[0]) switch
            {
                'h' => v * 3600,
                'm' => v * 60,
                _ => v,
            };
        }

        return total;
    }

    /// <summary>心跳 payload 里的播放头秒数（形如 <c>{"t":149}</c>，前端 WatchPage 报的 v.currentTime 取整）；
    /// 读不出返回 null（探针手填的脏 payload 或老数据不带 t），当「没报过位置」认，不炸报表。</summary>
    public static long? ReadHeartbeatPosition(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("t", out var t)
                && t.ValueKind == JsonValueKind.Number)
            {
                var v = t.GetDouble();
                return v >= 0 ? (long)Math.Floor(v) : null;
            }
        }
        catch (JsonException)
        {
            // 探针手填过不合法的 payload（见库内实际数据）：当成没报过位置，不能it 把整张周报顶炸。
        }

        return null;
    }

    /// <summary>取一张周报（正对现在）。weeksBack：0=上一个已过完的整周，1..4=再往前数（越界夹回）；viewer 非空时附个人战报；newsTake 控新货清单封顶（分页版拉大批量）。</summary>
    public WeeklyReport GetWeeklyReport(int weeksBack, long? viewerMemberId = null, int newsTake = WeeklyNewsTake)
        => GetWeeklyReport(weeksBack, DateTimeOffset.UtcNow, viewerMemberId, newsTake);

    /// <summary>取一张周报（now 给死版，周桶测试与发送员用）：新货 / 最多人看 / 弹幕增量 / 全站家底 / UP 榜 / 分区榜 / 个人战报一趟拿齐。</summary>
    public WeeklyReport GetWeeklyReport(int weeksBack, DateTimeOffset nowUtc, long? viewerMemberId = null, int newsTake = WeeklyNewsTake)
    {
        var bucket = LocalWeekBucket(weeksBack, nowUtc);
        var since = bucket.SinceUtc.ToString("O");
        var until = bucket.UntilUtc.ToString("O");

        long newsCount;
        var news = new List<WeeklyNewsItem>();
        var hottest = new List<WeeklyHotItem>();
        long danmakuAdded;
        WeeklyPlay play = new(0, 0, 0, 0);
        long submissions, searches, logins, newMembers, commentsArchived, dynamicsArchived, dmSent;
        var topUps = new List<WeeklyUpItem>();
        var topZones = new List<WeeklyZoneItem>();
        WeeklyPersonal? personal = null;
        WeeklyPersonalVsAll? personalVsAll = null;
        WeeklyPersonalDuration? personalDuration = null;
        var durationTopVideos = new List<WeeklyDurationVideo>();
        var durationTopUps = new List<WeeklyDurationGroup>();
        var durationTopZones = new List<WeeklyDurationGroup>();
        // 逐 BV 的时长归堆（全站成员心跳），个人时长账复用它查片长，不跑第二遗库
        var durShotsByBvid = new Dictionary<string, long>();
        var durMaxTByBvid = new Dictionary<string, long>();
        var durMetaByBvid = new Dictionary<string, (string? Title, string? Cover, string Up, string Zone, long? DurSec)>();
        long durationShots = 0;
        long durationSeconds = 0;
        long durationFinished = 0;
        long durationAvgOpen = 0;
        var deltas = new List<WeeklyDelta>();
        var dayBars = new List<WeeklyDayBar>();
        var staleUps = new List<WeeklyStale>();
        WeeklyGuest guest = new(0, 0, 0, 0, 0, 0, 0);

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
                command.Parameters.AddWithValue("$take", newsTake);
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

            // 放映厅实况一摄：进厅（open）/播放（play）/心跳各多少回 + 观众去重人头（访客没 mid 不进座，与热榜同口径）
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT COALESCE(SUM(CASE WHEN event = $open THEN 1 ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN event = $play THEN 1 ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN event = $beat THEN 1 ELSE 0 END), 0),
                           COUNT(DISTINCT CASE WHEN event IN ($play, $beat) THEN NULLIF(actor_mid, '') END)
                    FROM events
                    WHERE subject_type = 'video'
                      AND event IN ($open, $play, $beat)
                      AND ts_utc >= $since AND ts_utc < $until;
                    """;
                command.Parameters.AddWithValue("$open", "watch.open");
                command.Parameters.AddWithValue("$play", WatchPlayEvent);
                command.Parameters.AddWithValue("$beat", EventNames.WatchHeartbeat);
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                using var reader = command.ExecuteReader();
                reader.Read();
                play = new WeeklyPlay(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            }

            // 台账动静一把数：投稿、搜索、登录成功、新席位、评论/动态入档、私信——全部数 events 白名单行与入库时间行，
            // 口径全是现成落库字段，没一列需要现造。
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM events WHERE event = $submit AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events WHERE event = $search AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events WHERE event IN ($loginOk, $loginPw) AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM members WHERE created_at_utc >= $since AND created_at_utc < $until),
                        (SELECT COUNT(*) FROM dynamic_comments WHERE cached_at_utc >= $since AND cached_at_utc < $until),
                        (SELECT COUNT(*) FROM dynamics WHERE cached_at_utc >= $since AND cached_at_utc < $until),
                        (SELECT COUNT(*) FROM dm_messages WHERE created_at_utc >= $since AND created_at_utc < $until);
                    """;
                command.Parameters.AddWithValue("$submit", EventNames.VideoSubmit);
                command.Parameters.AddWithValue("$search", "search.query");
                command.Parameters.AddWithValue("$loginOk", EventNames.LoginSuccess);
                command.Parameters.AddWithValue("$loginPw", "login.password");
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                using var reader = command.ExecuteReader();
                reader.Read();
                submissions = reader.GetInt64(0);
                searches = reader.GetInt64(1);
                logins = reader.GetInt64(2);
                newMembers = reader.GetInt64(3);
                commentsArchived = reader.GetInt64(4);
                dynamicsArchived = reader.GetInt64(5);
                dmSent = reader.GetInt64(6);
            }

            // 最受欢迎 UP / 分区两榜：本周播放事件 join video_meta 现推，观众去重口径与热榜同（访客没 mid 不进座）；
            // 榜面取 Top5，排座次看人头，人头并列看播放次数。
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT COALESCE(NULLIF(m.up_name, ''), '未署名 UP'),
                           COUNT(DISTINCT e.actor_mid), COUNT(*), COUNT(DISTINCT e.subject_id)
                    FROM events e
                    JOIN videos v ON v.bvid = e.subject_id
                    JOIN video_meta m ON m.video_id = v.id
                    WHERE e.event IN ($play, $beat) AND e.subject_type = 'video'
                      AND e.ts_utc >= $since AND e.ts_utc < $until
                    GROUP BY COALESCE(NULLIF(m.up_name, ''), '未署名 UP')
                    HAVING COUNT(DISTINCT e.actor_mid) > 0
                    ORDER BY COUNT(DISTINCT e.actor_mid) DESC, COUNT(*) DESC
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
                    topUps.Add(new WeeklyUpItem(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT COALESCE(NULLIF(m.zone, ''), '未标分区'),
                           COUNT(DISTINCT e.actor_mid), COUNT(*), COUNT(DISTINCT e.subject_id)
                    FROM events e
                    JOIN videos v ON v.bvid = e.subject_id
                    JOIN video_meta m ON m.video_id = v.id
                    WHERE e.event IN ($play, $beat) AND e.subject_type = 'video'
                      AND e.ts_utc >= $since AND e.ts_utc < $until
                    GROUP BY COALESCE(NULLIF(m.zone, ''), '未标分区')
                    HAVING COUNT(DISTINCT e.actor_mid) > 0
                    ORDER BY COUNT(DISTINCT e.actor_mid) DESC, COUNT(*) DESC
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
                    topZones.Add(new WeeklyZoneItem(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
                }
            }

            // 时长账：只数成员心跳（游客看不了片，一枪不入账）。一枪 = 30 秒墙钟，口径焊在前端「没在播就不报」那条规矩上；
            // 片长现解 pages.duration_text（"4m14s" 这种文本），多 P 片按第一 P 论：心跳只报 bvid 不报 P 号，
            // 没有更细的依据可分，tooltip 里也这么认。
            var durShotsByUp = new Dictionary<string, long>();
            var durShotsByZone = new Dictionary<string, long>();
            var durBvidsByUp = new Dictionary<string, HashSet<string>>();
            var durBvidsByZone = new Dictionary<string, HashSet<string>>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT e.subject_id, e.payload, v.title,
                           COALESCE((SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                                    v.cover_url),
                           COALESCE(NULLIF(m.up_name, ''), '未署名 UP'),
                           COALESCE(NULLIF(m.zone, ''), '未标分区'),
                           (SELECT p.duration_text FROM pages p WHERE p.video_id = v.id ORDER BY p.idx LIMIT 1)
                    FROM events e
                    JOIN videos v ON v.bvid = e.subject_id
                    LEFT JOIN video_meta m ON m.video_id = v.id
                    WHERE e.event = $beat AND e.subject_type = 'video' AND e.actor_type = $member
                      AND e.ts_utc >= $since AND e.ts_utc < $until;
                    """;
                command.Parameters.AddWithValue("$beat", EventNames.WatchHeartbeat);
                command.Parameters.AddWithValue("$member", EventActors.Member);
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var bvid = reader.GetString(0);
                    var up = reader.GetString(4);
                    var zone = reader.GetString(5);
                    durationShots++;
                    durShotsByBvid[bvid] = durShotsByBvid.TryGetValue(bvid, out var n) ? n + 1 : 1;
                    if (!durMetaByBvid.ContainsKey(bvid))
                    {
                        durMetaByBvid[bvid] = (
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            up,
                            zone,
                            ParseDurationText(reader.IsDBNull(6) ? null : reader.GetString(6)));
                    }

                    if (ReadHeartbeatPosition(reader.IsDBNull(1) ? null : reader.GetString(1)) is long pos
                        && (!durMaxTByBvid.TryGetValue(bvid, out var maxT) || pos > maxT))
                    {
                        durMaxTByBvid[bvid] = pos;
                    }

                    durShotsByUp[up] = durShotsByUp.TryGetValue(up, out var us) ? us + 1 : 1;
                    if (!durBvidsByUp.TryGetValue(up, out var ub))
                    {
                        ub = new HashSet<string>();
                        durBvidsByUp[up] = ub;
                    }

                    ub.Add(bvid);
                    durShotsByZone[zone] = durShotsByZone.TryGetValue(zone, out var zs) ? zs + 1 : 1;
                    if (!durBvidsByZone.TryGetValue(zone, out var zb))
                    {
                        zb = new HashSet<string>();
                        durBvidsByZone[zone] = zb;
                    }

                    zb.Add(bvid);
                }
            }

            // 看完口径（全站与个人同一条线）：该 BV 心跳报过的最大播放头达片长 90%；
            // 片长没档的 BV 既不算看完也不算没看完（FinishPermille 为空，前端显「片长没档」）。
            bool IsFinished(string bvid) =>
                durMetaByBvid.TryGetValue(bvid, out var meta)
                && meta.DurSec is long d && d > 0
                && durMaxTByBvid.TryGetValue(bvid, out var mt)
                && mt * 1000 >= d * WeeklyFinishPermille;

            long? FinishPermilleOf(string bvid) =>
                durMetaByBvid.TryGetValue(bvid, out var meta) && meta.DurSec is long d && d > 0
                    ? Math.Min(1000L, (durMaxTByBvid.TryGetValue(bvid, out var mt) ? mt : 0L) * 1000 / d)
                    : null;

            IEnumerable<WeeklyDurationGroup> ShapeDurationGroups(Dictionary<string, long> shots, Dictionary<string, HashSet<string>> bvids) =>
                shots.OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                     .Take(WeeklyDurationTake)
                     .Select(kv => new WeeklyDurationGroup(
                         kv.Key,
                         kv.Value * WeeklyHeartbeatSeconds,
                         kv.Value,
                         bvids[kv.Key].Count,
                         bvids[kv.Key].Count(IsFinished)));

            durationTopVideos.AddRange(durShotsByBvid
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(WeeklyDurationTake)
                .Select(kv =>
                {
                    var meta = durMetaByBvid[kv.Key];
                    return new WeeklyDurationVideo(
                        kv.Key, meta.Title, meta.Cover, kv.Value * WeeklyHeartbeatSeconds, kv.Value, FinishPermilleOf(kv.Key));
                }));
            durationTopUps.AddRange(ShapeDurationGroups(durShotsByUp, durBvidsByUp));
            durationTopZones.AddRange(ShapeDurationGroups(durShotsByZone, durBvidsByZone));

            // 进厅均值拿成员自己的进厅数做分母（全站 play.Opens 里混着游客那几枪，会把人均压小）。
            long memberOpens;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT COUNT(*) FROM events
                    WHERE subject_type = 'video' AND actor_type = $member AND event = $open
                      AND ts_utc >= $since AND ts_utc < $until;
                    """;
                command.Parameters.AddWithValue("$member", EventActors.Member);
                command.Parameters.AddWithValue("$open", "watch.open");
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                memberOpens = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }

            durationSeconds = durationShots * WeeklyHeartbeatSeconds;
            durationFinished = durShotsByBvid.Keys.Count(IsFinished);
            durationAvgOpen = memberOpens > 0 ? durationSeconds / memberOpens : 0L;

            // 个人战报：登录者锁自己（events.actor_member_id / 弹幕 member_id / 私信发件人）；
            // 自己发的评论按 members.mid 对 dynamic_comments.mid 认人（没绑 mid 的席位这项永零，不编）。
            if (viewerMemberId is long viewer)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM events WHERE actor_member_id = $me AND event = $open AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events WHERE actor_member_id = $me AND event = $play AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(DISTINCT subject_id) FROM events WHERE actor_member_id = $me AND event IN ($play, $beat) AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events WHERE actor_member_id = $me AND event = $search AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events WHERE actor_member_id = $me AND event = $submit AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events WHERE actor_member_id = $me AND event IN ($loginOk, $loginPw) AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM danmaku_local WHERE member_id = $me AND created_at_utc >= $since AND created_at_utc < $until),
                        (SELECT COUNT(*) FROM dm_messages WHERE from_member_id = $me AND created_at_utc >= $since AND created_at_utc < $until),
                        (SELECT COUNT(*) FROM dynamic_comments c
                          JOIN members m ON m.id = $me AND m.mid IS NOT NULL AND c.mid = m.mid
                          WHERE c.cached_at_utc >= $since AND c.cached_at_utc < $until);
                    """;
                command.Parameters.AddWithValue("$me", viewer);
                command.Parameters.AddWithValue("$open", "watch.open");
                command.Parameters.AddWithValue("$play", WatchPlayEvent);
                command.Parameters.AddWithValue("$beat", EventNames.WatchHeartbeat);
                command.Parameters.AddWithValue("$search", "search.query");
                command.Parameters.AddWithValue("$submit", EventNames.VideoSubmit);
                command.Parameters.AddWithValue("$loginOk", EventNames.LoginSuccess);
                command.Parameters.AddWithValue("$loginPw", "login.password");
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                using var reader = command.ExecuteReader();
                reader.Read();
                personal = new WeeklyPersonal(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8));
            }

            // 个人对比全站：占比看播放数（千分比存后端，前端只除 10）；名次是本周有 mid 的观众按播放数倒序的座次，
            // 本人不在观众席（本周没播过片）就不编名次，名次为空。
            // 另起一条 command：上一条的 reader 还开着，Sqlite 不允许同一 command 换 CommandText。
            if (viewerMemberId is long vsMe)
            {
                using var vsCommand = connection.CreateCommand();
                vsCommand.CommandText = """
                    WITH ranked AS (
                        SELECT actor_mid, COUNT(*) AS plays
                        FROM events
                        WHERE event = $play AND subject_type = 'video'
                          AND actor_mid IS NOT NULL AND ts_utc >= $since AND ts_utc < $until
                        GROUP BY actor_mid
                    )
                    SELECT (SELECT plays FROM ranked WHERE actor_mid = (SELECT mid FROM members WHERE id = $me)),
                           (SELECT COUNT(*) FROM ranked r WHERE r.plays >= (SELECT plays FROM ranked WHERE actor_mid = (SELECT mid FROM members WHERE id = $me))),
                           (SELECT COUNT(*) FROM ranked);
                    """;
                vsCommand.Parameters.AddWithValue("$me", vsMe);
                vsCommand.Parameters.AddWithValue("$play", WatchPlayEvent);
                vsCommand.Parameters.AddWithValue("$since", since);
                vsCommand.Parameters.AddWithValue("$until", until);
                using var vsReader = vsCommand.ExecuteReader();
                if (vsReader.Read() && !vsReader.IsDBNull(0))
                {
                    var myPlays = vsReader.GetInt64(0);
                    var rank = vsReader.GetInt64(1);
                    var heads = vsReader.GetInt64(2);
                    var permille = play.Plays > 0 ? myPlays * 1000 / play.Plays : 0L;
                    personalVsAll = new WeeklyPersonalVsAll(permille, heads > 0 ? rank : null, heads);
                }
            }

            // 个人时长账：只认 actor_member_id 是自己的心跳（别人与游客的一枪不进自己账），
            // 看完口径与全站同一条线，片长复用上面那一遗归堆（durMetaByBvid），不重复跑库。
            if (viewerMemberId is long durMe)
            {
                var myShotsByBvid = new Dictionary<string, long>();
                var myMaxTByBvid = new Dictionary<string, long>();
                long myShots = 0;
                long myOpens;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        SELECT e.subject_id, e.payload, e.event
                        FROM events e
                        JOIN videos v ON v.bvid = e.subject_id
                        WHERE e.actor_member_id = $me AND e.subject_type = 'video'
                          AND e.event IN ($beat, $open) AND e.ts_utc >= $since AND e.ts_utc < $until;
                        """;
                    command.Parameters.AddWithValue("$me", durMe);
                    command.Parameters.AddWithValue("$beat", EventNames.WatchHeartbeat);
                    command.Parameters.AddWithValue("$open", "watch.open");
                    command.Parameters.AddWithValue("$since", since);
                    command.Parameters.AddWithValue("$until", until);
                    using var reader = command.ExecuteReader();
                    long opens = 0;
                    while (reader.Read())
                    {
                        var bvid = reader.GetString(0);
                        if (reader.GetString(2) == "watch.open")
                        {
                            opens++;
                            continue;
                        }

                        myShots++;
                        myShotsByBvid[bvid] = myShotsByBvid.TryGetValue(bvid, out var n) ? n + 1 : 1;
                        if (ReadHeartbeatPosition(reader.IsDBNull(1) ? null : reader.GetString(1)) is long pos
                            && (!myMaxTByBvid.TryGetValue(bvid, out var maxT) || pos > maxT))
                        {
                            myMaxTByBvid[bvid] = pos;
                        }
                    }

                    myOpens = opens;
                }

                if (myShots > 0 || myOpens > 0)
                {
                    var mySeconds = myShots * WeeklyHeartbeatSeconds;
                    personalDuration = new WeeklyPersonalDuration(
                        mySeconds,
                        myShots,
                        myShotsByBvid.Count,
                        myShotsByBvid.Keys.Count(b =>
                            durMetaByBvid.TryGetValue(b, out var meta) && meta.DurSec is long d && d > 0
                            && myMaxTByBvid.TryGetValue(b, out var mt) && mt * 1000 >= d * WeeklyFinishPermille),
                        myOpens > 0 ? mySeconds / myOpens : 0L);
                }
            }

            // 七日活动柱图：整周按本地（UTC+8）日历日分桶——SQL 只能按定长偏移切出日期前缀，
            // 加 8 小时是把 UTC 时戳推回本地日历；没动静的日子不补零柱（柱图只摆真实出没的那几天）。
            // 当日访客数同栛里取：只数看过片子的 guest 行（watch.*）且 ip 非空，同出口多人会并成一个，口径写在列名上。
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT substr(datetime(ts_utc, $shift), 1, 10) AS local_day,
                           COALESCE(SUM(CASE WHEN event = $open THEN 1 ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN event = $play THEN 1 ELSE 0 END), 0),
                           COUNT(DISTINCT CASE WHEN event IN ($play, $beat) THEN NULLIF(actor_mid, '') END),
                           COALESCE(SUM(CASE WHEN actor_type = $guest THEN 1 ELSE 0 END), 0),
                           COUNT(DISTINCT CASE WHEN actor_type = $guest AND ip IS NOT NULL AND ip <> '' THEN ip END)
                    FROM events
                    WHERE subject_type = 'video' AND event IN ($open, $play, $beat)
                      AND ts_utc >= $since AND ts_utc < $until
                    GROUP BY local_day
                    ORDER BY local_day;
                    """;
                command.Parameters.AddWithValue("$shift", "+8 hours");
                command.Parameters.AddWithValue("$open", "watch.open");
                command.Parameters.AddWithValue("$play", WatchPlayEvent);
                command.Parameters.AddWithValue("$beat", EventNames.WatchHeartbeat);
                command.Parameters.AddWithValue("$guest", EventActors.Guest);
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    dayBars.Add(new WeeklyDayBar(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
                }
            }

            // 站外访客账：全部限 actor_type='guest'，身份拿 IP 去重（本库访客无 mid，也没存 dmid 进台账）。
            // BlockedWatch 与 BlockedDanmaku 分开数：后者是前端弹幕轮询被门禁挡回的空转（实测占 blocked 的大头），
            // 当「有人想看被拦」的硬信号会吹胀，所以不当观众看、只当运维噪音摆。外发只给计数，IP 本体一律不出库。
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT
                        (SELECT COUNT(DISTINCT ip) FROM events
                          WHERE actor_type = $guest AND ip IS NOT NULL AND ip <> ''
                            AND event IN ($open, $play, $beat, $block, $search) AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events
                          WHERE actor_type = $guest AND event = $open AND subject_type = 'video' AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events
                          WHERE actor_type = $guest AND event = $play AND subject_type = 'video' AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events
                          WHERE actor_type = $guest AND event = $search AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events
                          WHERE actor_type = $guest AND event = $block AND subject_type = 'path' AND subject_id LIKE '/api/watch/%' AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events
                          WHERE actor_type = $guest AND event = $block AND subject_id LIKE '%/danmaku%' AND ts_utc >= $since AND ts_utc < $until),
                        (SELECT COUNT(*) FROM events
                          WHERE actor_type = $guest AND event IN ($qrStart, $qrScan, $pwDeny) AND ts_utc >= $since AND ts_utc < $until);
                    """;
                command.Parameters.AddWithValue("$guest", EventActors.Guest);
                command.Parameters.AddWithValue("$open", "watch.open");
                command.Parameters.AddWithValue("$play", WatchPlayEvent);
                command.Parameters.AddWithValue("$beat", EventNames.WatchHeartbeat);
                command.Parameters.AddWithValue("$block", "watch.blocked");
                command.Parameters.AddWithValue("$search", "search.query");
                command.Parameters.AddWithValue("$qrStart", "login.qr.start");
                command.Parameters.AddWithValue("$qrScan", "login.qr.scanned");
                command.Parameters.AddWithValue("$pwDeny", "login.password.denied");
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                using var reader = command.ExecuteReader();
                reader.Read();
                guest = new WeeklyGuest(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
            }

            // 更新榜：订阅台账 dynamics_targets 是「该冒泡的人」名单，拿 dynamics 的 published_at_utc 算距今——
            // 排序先勤后懒（本周条数倒序、最后动态新的在前），榜头就是本周最活跃的那位；真没人更新时（全零）同一张卡就是断更榜。
            // 一条动态都没抓过的人 LastDays 为空（前端显「没档」而不是「无穷久没更新」），不拿空白冒充断更天数。
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT t.mid, t.nickname, t.face_url,
                           (SELECT COUNT(*) FROM dynamics d
                             WHERE (d.target_mid = t.mid OR d.author_mid = t.mid)
                               AND d.published_at_utc >= $since AND d.published_at_utc < $until),
                           (SELECT COUNT(*) FROM dynamics d WHERE d.target_mid = t.mid OR d.author_mid = t.mid),
                           (SELECT MAX(d.published_at_utc) FROM dynamics d WHERE d.target_mid = t.mid OR d.author_mid = t.mid)
                    FROM dynamics_targets t
                    ORDER BY (SELECT COUNT(*) FROM dynamics d
                               WHERE (d.target_mid = t.mid OR d.author_mid = t.mid)
                                 AND d.published_at_utc >= $since AND d.published_at_utc < $until) DESC,
                             (SELECT MAX(d.published_at_utc) FROM dynamics d WHERE d.target_mid = t.mid OR d.author_mid = t.mid) DESC,
                             t.mid ASC
                    LIMIT $take;
                    """;
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$until", until);
                command.Parameters.AddWithValue("$take", WeeklyStaleTake);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var lastAt = reader.IsDBNull(5) ? null : reader.GetString(5);
                    long? lastDays = null;
                    if (lastAt is not null && DateTimeOffset.TryParse(lastAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastDto))
                    {
                        lastDays = Math.Max(0, (long)(nowUtc - lastDto).TotalDays);
                    }

                    staleUps.Add(new WeeklyStale(
                        reader.GetInt64(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        lastDays,
                        lastAt));
                }
            }

            // 环比：上桶 = 紧邻的上一个 7 天（本桶 since 往前推 7 天到 since），口径与本桶同口径同字段的六枚关键数。
            // 环比：上桶就是「本桶再往前推七天」的两个整周对拼，口径天生公平（两桶都是已过完的整周，
            // 不存在「未过完的一周 ÷ 上周整周」那种把涨幅押小的偏心账）；涨跌由前端展示。
            var prevSince = bucket.SinceUtc.AddDays(-7).ToString("O");
            var prevUntil = bucket.SinceUtc.ToString("O");
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM videos WHERE created_at_utc >= $psince AND created_at_utc < $psince2),
                        (SELECT COUNT(*) FROM danmaku_local WHERE created_at_utc >= $psince AND created_at_utc < $psince2),
                        (SELECT COUNT(*) FROM events WHERE event = $open AND subject_type = 'video' AND ts_utc >= $psince AND ts_utc < $psince2),
                        (SELECT COUNT(*) FROM events WHERE event = $play AND subject_type = 'video' AND ts_utc >= $psince AND ts_utc < $psince2),
                        (SELECT COUNT(DISTINCT CASE WHEN event IN ($play, $beat) THEN NULLIF(actor_mid, '') END) FROM events WHERE subject_type = 'video' AND event IN ($play, $beat) AND ts_utc >= $psince AND ts_utc < $psince2),
                        (SELECT COUNT(*) FROM events WHERE event = $search AND ts_utc >= $psince AND ts_utc < $psince2),
                        (SELECT COUNT(*) FROM events WHERE event = $beat AND subject_type = 'video' AND actor_type = $member AND ts_utc >= $psince AND ts_utc < $psince2);
                    """;
                command.Parameters.AddWithValue("$psince", prevSince);
                command.Parameters.AddWithValue("$psince2", prevUntil);
                command.Parameters.AddWithValue("$open", "watch.open");
                command.Parameters.AddWithValue("$play", WatchPlayEvent);
                command.Parameters.AddWithValue("$beat", EventNames.WatchHeartbeat);
                command.Parameters.AddWithValue("$search", "search.query");
                command.Parameters.AddWithValue("$member", EventActors.Member);
                using var reader = command.ExecuteReader();
                reader.Read();
                deltas.Add(new WeeklyDelta("news", newsCount, reader.GetInt64(0)));
                deltas.Add(new WeeklyDelta("danmaku", danmakuAdded, reader.GetInt64(1)));
                deltas.Add(new WeeklyDelta("opens", play.Opens, reader.GetInt64(2)));
                deltas.Add(new WeeklyDelta("plays", play.Plays, reader.GetInt64(3)));
                deltas.Add(new WeeklyDelta("viewers", play.ViewerHeads, reader.GetInt64(4)));
                deltas.Add(new WeeklyDelta("searches", searches, reader.GetInt64(5)));
                // 时长环比也拿枪数换算（时长 = 枪数×30 是恒等式，上桶不必重跑一遍归堆）。
                deltas.Add(new WeeklyDelta("watchTime", durationSeconds, reader.GetInt64(6) * WeeklyHeartbeatSeconds));
            }
        }

        // 全站大数抄柜台：/api/admin/stats 背后就是 AdminStats，这里直接调同一个方法取 videos/bytes，
        // 口径焊死在源头；members 用数据交换清点同款 COUNT(*)（全行含封禁/待邀），danmaku 总数是站内弹幕全表。
        var stats = AdminStats();
        long members;
        long danmakuTotal;
        long dynamicTotal;
        long dynamicCommentTotal;
        long dmTotal;
        using (var connection = Open(_dbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM members),
                       (SELECT COUNT(*) FROM danmaku_local),
                       (SELECT COUNT(*) FROM dynamics),
                       (SELECT COUNT(*) FROM dynamic_comments),
                       (SELECT COUNT(*) FROM dm_messages);
                """;
            using var reader = command.ExecuteReader();
            reader.Read();
            members = reader.GetInt64(0);
            danmakuTotal = reader.GetInt64(1);
            dynamicTotal = reader.GetInt64(2);
            dynamicCommentTotal = reader.GetInt64(3);
            dmTotal = reader.GetInt64(4);
        }

        return new WeeklyReport(bucket.WeekLabel, since, until, newsCount, news, hottest, danmakuAdded,
            play.Plays, play.Opens, play.ViewerHeads, searches, submissions, logins, newMembers,
            commentsArchived, dynamicsArchived, dmSent, play,
            new WeeklyDuration(durationSeconds, durationShots, durShotsByBvid.Count, durationFinished, durationAvgOpen,
                durationTopVideos, durationTopUps, durationTopZones),
            topUps, topZones, personal, personalVsAll, personalDuration,
            deltas, dayBars, staleUps, guest,
            new WeeklyTotals(stats.Total, members, danmakuTotal, stats.TotalBytes, dynamicTotal, dynamicCommentTotal, dmTotal));
    }
}
