using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 分站周报（仿 DanmakuReportTests 造库造席位范式，全走 DB 方法 / internal static 直测）：
/// 周桶是本地周（UTC+8 周一 00:00 起算）——周一 00:00 前后各一条要落进各自的桶，跨年周认 ISO 的星期四；
/// 「最多人看」从 events 的 watch.play / watch.heartbeat 聚合，观众按 actor_mid 去重，游客（mid NULL）不进座、
/// subject_type 不对或 ts 出桶的行都不算；发报水位存 settings["weekly-report-sent"]，同周第二次必须不发（幂等）；
/// 全站家底与站长室柜台（AdminStats）同口径抽查——两个页面的数不许打架。
/// </summary>
public sealed class WeeklyReportTests : IDisposable
{
    // 锚点挂历：2026-10-26 是周一（ISO 2026-W44 之首）。本地 UTC+8 的周桶界 = UTC 周日 16:00。
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 6, 0, 0, TimeSpan.Zero); // 周四 14:00 (+08)，住在 W44 里
    private static readonly DateTimeOffset W44StartUtc = new(2026, 10, 25, 16, 0, 0, TimeSpan.Zero); // 本地 10-26 00:00（本周，未过完）
    private static readonly DateTimeOffset W43StartUtc = new(2026, 10, 18, 16, 0, 0, TimeSpan.Zero); // 本地 10-19 00:00（上一个整周 = 周报的 0 桶）
    private static readonly DateTimeOffset W42StartUtc = new(2026, 10, 11, 16, 0, 0, TimeSpan.Zero); // 本地 10-12 00:00（再往前一週 = 1 桶）

    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;

    public WeeklyReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-weekly-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(_videosDir);
        _paths = new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
        _db.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    private long AddVideo(string bvid) =>
        _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;

    /// <summary>把到站时刻钉死到指定 UTC（AddVideo 只会盖当下，周桶测试要的是日历边界）。</summary>
    private void StampVideoCreated(string bvid, DateTimeOffset createdUtc) =>
        Execute("UPDATE videos SET created_at_utc = $t WHERE bvid = $b;", ("$t", createdUtc.ToString("O")), ("$b", bvid));

    private void StampDanmakuCreated(long dmId, DateTimeOffset createdUtc) =>
        Execute("UPDATE danmaku_local SET created_at_utc = $t WHERE id = $id;", ("$t", createdUtc.ToString("O")), ("$id", dmId));

    private void Execute(string sql, params (string Name, object Value)[] args)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>造一个在席席位（发报员只给 active 席位播送，封禁的要被筛掉）。</summary>
    private long Seat(string name, long? mid = null)
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        var bound = _db.BindMember(id, mid ?? 940_000 + id, name, null);
        Assert.NotNull(bound);
        return id;
    }

    /// <summary>往 events 台账塞一枪观看行为（形状照 WatchPage.vue 的上报实况：subject_type='video'、subject_id=BV 号）。
    /// memberId 给上才算「成员自己干的」（个人账靠它认人；真实上报两个身份都带，不给就是 NULL）。</summary>
    private void Watch(string bvid, long? mid, DateTimeOffset ts, string name = "watch.play", string? subjectType = "video", long? memberId = null)
    {
        _db.InsertEventsBatch(new[]
        {
            new EventDraft(
                mid is null ? EventActors.Guest : EventActors.Member,
                mid,
                name,
                subjectType,
                bvid,
                name == "watch.heartbeat" ? """{"t":42}""" : null,
                mid is null ? "9.9.9.9" : null,
                ts.ToString("O"),
                memberId),
        });
    }

    [Fact]
    public void 周桶只给过完的整周_本周动静谁都看不见()
    {
        // 锚点：Now 是 W44 的周四——周报的 0 桶必须是已过完的 W43，未过完的 W44 谁都拿不到。
        AddVideo("BVw42a");
        StampVideoCreated("BVw42a", W43StartUtc.AddMinutes(-1).AddMilliseconds(-500)); // W43 起算前一秒半 → 归 W42
        AddVideo("BVw43b");
        StampVideoCreated("BVw43b", W43StartUtc); // 正压本地周一 00:00 → 进 W43
        AddVideo("BVw43c");
        StampVideoCreated("BVw43c", W43StartUtc.AddHours(30)); // W43 周中
        AddVideo("BVw44now");
        StampVideoCreated("BVw44now", W44StartUtc.AddHours(9)); // 本周的货：未盖棺，0/1 桶都不许漏出来
        AddVideo("BVfuture");
        StampVideoCreated("BVfuture", Now.AddHours(1)); // 未来的货：哪个桶都不算

        var vId = AddVideo("BVdm");
        StampVideoCreated("BVdm", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)); // 弹幕载体是老货，别扰新货桶
        var inW42 = _db.AddLocalDanmaku(vId, 0, 1, "W42 的弹幕", null, false);
        StampDanmakuCreated(inW42, W43StartUtc.AddTicks(-1)); // 压在 W43 界前一跳
        var inW43 = _db.AddLocalDanmaku(vId, 0, 2, "整点的弹幕", null, false);
        StampDanmakuCreated(inW43, W43StartUtc); // 正点整的进 W43
        var outBoth = _db.AddLocalDanmaku(vId, 0, 3, "两桶之外", null, false);
        StampDanmakuCreated(outBoth, W42StartUtc.AddMinutes(-1)); // 两个桶的界之前
        var inW44 = _db.AddLocalDanmaku(vId, 0, 4, "本周的弹幕", null, false);
        StampDanmakuCreated(inW44, W44StartUtc.AddHours(9)); // 本周弹幕：也不许漏
        var future = _db.AddLocalDanmaku(vId, 0, 5, "未来弹幕", null, false);
        StampDanmakuCreated(future, Now.AddMinutes(30));

        var last = _db.GetWeeklyReport(0, Now);
        Assert.Equal("2026-W43", last.WeekLabel);
        Assert.Equal("2026-10-18T16:00:00.0000000+00:00", last.SinceUtc); // 本地 10-19 00:00 (+08) 的 UTC 身
        Assert.Equal("2026-10-25T16:00:00.0000000+00:00", last.UntilUtc); // 整周就是整周：上界不夹 now
        Assert.Equal(2, last.NewsCount); // BVw43b、BVw43c
        Assert.Equal(new[] { "BVw43c", "BVw43b" }, last.News.Select(n => n.Bvid).ToArray()); // 投稿时间倒序
        Assert.Equal(1, last.DanmakuAdded); // 只数整点那条

        var beforeLast = _db.GetWeeklyReport(1, Now);
        Assert.Equal("2026-W42", beforeLast.WeekLabel);
        Assert.Equal("2026-10-11T16:00:00.0000000+00:00", beforeLast.SinceUtc);
        Assert.Equal("2026-10-18T16:00:00.0000000+00:00", beforeLast.UntilUtc);
        Assert.Equal(1, beforeLast.NewsCount);
        Assert.Equal("BVw42a", Assert.Single(beforeLast.News).Bvid);
        Assert.Equal(1, beforeLast.DanmakuAdded);

        // 本周与未来：新语义下的硬断言——未过完那一周的任何动静都不进周报（不夹 now，也不预支）
        Assert.DoesNotContain("BVw44now", last.News.Select(n => n.Bvid));
        Assert.DoesNotContain("BVw44now", beforeLast.News.Select(n => n.Bvid));
        Assert.DoesNotContain("BVfuture", last.News.Select(n => n.Bvid));

        // 同周内哪天问都是同一个桶：周四看与周日深夜看拿到的数一模一样（改这条逻辑的由头就在这）
        var sundayLate = StationDb.LocalWeekBucket(0, new DateTimeOffset(2026, 11, 1, 15, 59, 0, TimeSpan.Zero)); // W44 的周日深夜 23:59 (+08)
        var thursday = StationDb.LocalWeekBucket(0, Now);
        Assert.Equal(thursday.WeekLabel, sundayLate.WeekLabel);
        Assert.Equal(thursday.SinceUtc, sundayLate.SinceUtc);
        Assert.Equal(thursday.UntilUtc, sundayLate.UntilUtc);
    }

    [Fact]
    public void 周标识认ISO跨年周_越界week夹回()
    {
        // 跨年周跟星期四认年份：2027-01-01（周五）还住在 2026-W53 里。
        Assert.Equal("2026-W53", StationDb.LocalWeekKey(new DateOnly(2027, 1, 1)));
        Assert.Equal("2026-W01", StationDb.LocalWeekKey(new DateOnly(2026, 1, 1)));
        Assert.Equal("2026-W44", StationDb.LocalWeekKey(new DateOnly(2026, 10, 26)));

        // week 参数越界夹回 0..4：-2 夹到 0（上一个整周 W43），7 夹到 4（W44 再往前五个整周 = W39）。
        Assert.Equal("2026-W43", _db.GetWeeklyReport(-2, Now).WeekLabel);
        Assert.Equal("2026-W39", _db.GetWeeklyReport(7, Now).WeekLabel);
        Assert.Equal(0, WeeklyReportEndpoints.ClampWeek(null));
        Assert.Equal(0, WeeklyReportEndpoints.ClampWeek(-3));
        Assert.Equal(4, WeeklyReportEndpoints.ClampWeek(99));
        Assert.Equal(2, WeeklyReportEndpoints.ClampWeek(2));
    }

    [Fact]
    public void 最多人看Top5按观众去重_游客与出桶行不算人()
    {
        var inWeek = W43StartUtc.AddHours(10); // 桶内随便一点（W43 = 周报的 0 桶）
        foreach (var bvid in new[] { "BVh1", "BVh2", "BVh3", "BVh4", "BVh5", "BVh6", "BVguest" })
        {
            AddVideo(bvid);
        }

        // 观众数设计：h6 六人、h5 五人……h1 一人（同 mid 反复播只算一位观众）。
        var mid = 1L;
        for (var rank = 1; rank <= 6; rank++)
        {
            for (var i = 0; i < rank; i++)
            {
                Watch($"BVh{rank}", mid++, inWeek);
            }
        }

        // h1 再来两条重复观众（mid 归位）：去重后仍该是 1。
        Watch("BVh1", 1, inWeek.AddMinutes(5));
        Watch("BVh1", 1, inWeek.AddMinutes(9), "watch.heartbeat");

        // 游客（actor_mid NULL）围观 guest 片：一屋子人也不许充人头。
        Watch("BVguest", null, inWeek);
        Watch("BVguest", null, inWeek.AddMinutes(1));

        // 出桶一枪（上桶 W42）与 subject_type 缺失的一枪：都不进聚合。
        Watch("BVh2", 990, W43StartUtc.AddMinutes(-1));
        Watch("BVh1", 777, inWeek, "watch.heartbeat", null);

        // 封面口径抽查：有 kind='cover' 产物行走 /media/，没有的吃 cover_url 热链。
        var h6Id = _db.ListVideos().First(v => v.Bvid == "BVh6").Id;
        Execute("INSERT INTO artifacts (video_id, page_idx, kind, rel_path) VALUES ($v, NULL, 'cover', 'covers/BVh6.jpg');", ("$v", h6Id));
        Execute("UPDATE videos SET cover_url = $c WHERE bvid = 'BVh5';", ("$c", "https://i0.hdslb.com/bfs/baseline/h5.jpg"));

        var report = _db.GetWeeklyReport(0, Now);
        Assert.Equal(new[] { "BVh6", "BVh5", "BVh4", "BVh3", "BVh2" }, report.Hottest.Select(h => h.Bvid).ToArray());
        Assert.Equal(new long[] { 6, 5, 4, 3, 2 }, report.Hottest.Select(h => h.Viewers).ToArray());
        Assert.Equal("/media/covers/BVh6.jpg", report.Hottest[0].CoverUrl);
        Assert.Equal("https://i0.hdslb.com/bfs/baseline/h5.jpg", report.Hottest[1].CoverUrl);
        // h1（观众太少排不进前五）与 guest（全是游客，观众 0）都不该在榜上摆着。
        Assert.DoesNotContain(report.Hottest, h => h.Bvid == "BVh1");
        Assert.DoesNotContain(report.Hottest, h => h.Bvid == "BVguest");
    }

    /// <summary>连打几枪心跳（payload 的 t 从 startT 起每枪 +30 秒，照 WatchPage 实况）；mid 为空就是游客枪，
    /// memberId 决个人账归属（素净席位无 mid 也靠它认人）。</summary>
    private void Heart(string bvid, long? mid, long? memberId, DateTimeOffset ts, int shots, long startT)
    {
        for (var i = 0; i < shots; i++)
        {
            _db.InsertEventsBatch(new[]
            {
                new EventDraft(
                    mid is null ? EventActors.Guest : EventActors.Member,
                    mid,
                    "watch.heartbeat",
                    "video",
                    bvid,
                    """{"t":""" + (startT + i * 30) + "}",
                    mid is null ? "9.9.9.9" : null,
                    ts.AddSeconds(i * 30).ToString("O"),
                    memberId),
            });
        }
    }

    /// <summary>给片子卡一条 pages 行与片长文本（AddVideo 不造分 P，时长账要的就是这列）；text 为空就不插行（片长没档）。</summary>
    private void StampDuration(string bvid, string? text)
    {
        if (text is null)
        {
            return;
        }

        Execute(
            "INSERT INTO pages (video_id, task_id, idx, title, duration_text, base_path) "
            + "VALUES ((SELECT id FROM videos WHERE bvid = $b), $t, 1, '第一 P', $d, 'x');",
            ("$b", bvid), ("$t", "task-" + bvid), ("$d", text));
    }

    /// <summary>给片子卡 UP 与分区（UP 榜 / 分区榜拿 video_meta 现推）。</summary>
    private void SetMeta(string bvid, string upName, string zone) =>
        Execute(
            "INSERT INTO video_meta (video_id, up_name, zone) VALUES ((SELECT id FROM videos WHERE bvid = $b), $u, $z);",
            ("$b", bvid), ("$u", upName), ("$z", zone));

    [Fact]
    public void 片长文本解析_字母形冒号形光板数都认_认不出为空()
    {
        Assert.Equal(254L, StationDb.ParseDurationText("4m14s"));
        Assert.Equal(3723L, StationDb.ParseDurationText("1h2m3s"));
        Assert.Equal(3723L, StationDb.ParseDurationText("1:02:03"));
        Assert.Equal(125L, StationDb.ParseDurationText("2:05"));
        Assert.Equal(90L, StationDb.ParseDurationText("90s"));
        Assert.Equal(254L, StationDb.ParseDurationText("254")); // 光板数字按秒认
        Assert.Null(StationDb.ParseDurationText(null));
        Assert.Null(StationDb.ParseDurationText("   "));
        Assert.Null(StationDb.ParseDurationText("abc"));
        Assert.Null(StationDb.ParseDurationText("1:2:3:4")); // 段数不对：不猜
    }

    [Fact]
    public void 心跳播放头解析_只认t的数_脏payload与缺字段都不炸()
    {
        Assert.Equal(149L, StationDb.ReadHeartbeatPosition("""{"t":149}"""));
        Assert.Equal(0L, StationDb.ReadHeartbeatPosition("""{"t":0}"""));
        Assert.Equal(12L, StationDb.ReadHeartbeatPosition("{\r\n        \"t\": 12.7\r\n      }")); // 探针手填的多余空白 + 取整
        Assert.Null(StationDb.ReadHeartbeatPosition(null));
        Assert.Null(StationDb.ReadHeartbeatPosition(""));
        Assert.Null(StationDb.ReadHeartbeatPosition("""{"from":"probe"}""")); // 进厅那种不带 t 的 payload
        Assert.Null(StationDb.ReadHeartbeatPosition("{t:}")); // 非法 JSON：当没报过位置，不能顶炸整张周报
        Assert.Null(StationDb.ReadHeartbeatPosition("""{"t":-3}"""));
    }

    [Fact]
    public void 时长账只数成员心跳_完播认片长九成_UP与分区按枪数排()
    {
        var inWeek = W43StartUtc.AddHours(10);
        var me = Seat("看片的", 777_001);
        var other = Seat("另一个", 777_002);

        AddVideo("BVlong"); // 我 10 枪 + 别人 5 枪，片长 5m0s，maxT=290 → 看完
        AddVideo("BVnodur"); // 3 枪，无 pages 行 → 片长没档
        AddVideo("BVshort"); // 2 枪，片长 4m0s，maxT=40 → 没看完
        AddVideo("BVguest"); // 游客 4 枪：时长账一枪都不许进
        AddVideo("BVout"); // 两个桶之外（20 天前）：不许预支

        StampDuration("BVlong", "5m0s");
        StampDuration("BVnodur", null);
        StampDuration("BVshort", "4m0s");
        SetMeta("BVlong", "甲UP", "音乐>翻唱");
        SetMeta("BVnodur", "甲UP", "音乐>翻唱");
        SetMeta("BVshort", "乙UP", "动画>综合");
        SetMeta("BVguest", "丙UP", "动画>综合");
        SetMeta("BVout", "丁UP", "动画>综合");

        Heart("BVlong", 777_001, me, inWeek, 10, 20); // t = 20…290
        Heart("BVnodur", 777_001, me, inWeek, 3, 20); // t = 20…80
        Heart("BVshort", 777_001, me, inWeek, 2, 20); // t = 20、50
        Heart("BVlong", 777_002, other, inWeek.AddHours(1), 5, 20); // 别人的枪：进全站账，不进我账
        Heart("BVguest", null, null, inWeek, 4, 20);
        Heart("BVout", 777_001, me, W43StartUtc.AddDays(-20), 1, 20);
        Heart("BVout", 777_001, me, W43StartUtc.AddDays(-2), 2, 20); // 上桶 W42 里 2 枪：专等时长环比的 Last

        // 成员进厅：我 3 次 + 别人 2 次（时长均长的分母；进厅也得挂上自己的席位 id，个人账靠它认人）
        for (var i = 0; i < 3; i++)
        {
            Watch("BVlong", 777_001, inWeek.AddMinutes(i), "watch.open", memberId: me);
        }

        for (var i = 0; i < 2; i++)
        {
            Watch("BVlong", 777_002, inWeek.AddMinutes(10 + i), "watch.open", memberId: other);
        }

        var report = _db.GetWeeklyReport(0, Now, me);
        var d = report.Duration;
        Assert.Equal(20, d.Shots); // 10 + 3 + 2 + 5；游客 4 枪与桶外 1 枪都不算
        Assert.Equal(600, d.Seconds); // 枪数×30：宁少不多
        Assert.Equal(3, d.Videos);
        Assert.Equal(1, d.Finished); // 只有 BVlong 拖到片长九成
        Assert.Equal(120, d.AvgOpenSeconds); // 600 ÷ 5 次成员进厅

        Assert.Equal(new[] { "BVlong", "BVnodur", "BVshort" }, d.TopVideos.Select(v => v.Bvid).ToArray());
        Assert.Equal(450, d.TopVideos[0].Seconds); // 15 枪 ×30
        Assert.Equal(966, d.TopVideos[0].FinishPermille); // 290 ÷ 300 = 96.6%
        Assert.Null(d.TopVideos[1].FinishPermille); // 片长没档：不拿 0 冒充「没看完」
        Assert.Equal(208, d.TopVideos[2].FinishPermille); // 50 ÷ 240

        Assert.Equal(new[] { "甲UP", "乙UP" }, d.TopUps.Select(u => u.Key).ToArray()); // 丙UP 只有游客枪 → 不上榜
        Assert.Equal(540, d.TopUps[0].Seconds); // BVlong 15 枪（我 10 + 别人 5）+ BVnodur 3 枪 = 18 枪 ×30
        Assert.Equal(2, d.TopUps[0].Videos);
        Assert.Equal(1, d.TopUps[0].Finished);
        Assert.Equal(60, d.TopUps[1].Seconds);
        Assert.Equal(new[] { "音乐>翻唱", "动画>综合" }, d.TopZones.Select(z => z.Key).ToArray());
        Assert.Equal(540, d.TopZones[0].Seconds);

        // 个人时长账：只认自己的枪（别人的 5 枪与游客的 4 枪都不进我账）
        var mine = report.PersonalDuration!;
        Assert.Equal(450, mine.Seconds);
        Assert.Equal(15, mine.Shots);
        Assert.Equal(3, mine.Videos);
        Assert.Equal(1, mine.Finished);
        Assert.Equal(150, mine.AvgOpenSeconds); // 450 ÷ 我自己 3 次进厅

        var theirs = _db.GetWeeklyReport(0, Now, other).PersonalDuration!;
        Assert.Equal(150, theirs.Seconds);
        Assert.Equal(5, theirs.Shots);
        Assert.Equal(1, theirs.Videos);
        Assert.Equal(75, theirs.AvgOpenSeconds); // 150 ÷ 自己 2 次进厅

        // 时长环比：上周桶里 2 枪 = 60 秒，本桶 20 枪 = 600 秒（时长 = 枪数×30 是恒等式）
        var watchTime = Assert.Single(report.Deltas, x => x.Key == "watchTime");
        Assert.Equal(600, watchTime.Current);
        Assert.Equal(60, watchTime.Last);
    }

    [Fact]
    public void 游客账只数guest行_IP去重_片页与弹幕被挡分开数()
    {
        var inWeek = W43StartUtc.AddHours(9);
        AddVideo("BVg1");

        // 游客看片：两个出口 IP（9.9.9.9 / 9.9.9.8）各若干枪 → 人数只能去重成 2 位，次数照枪数计。
        Watch("BVg1", null, inWeek, "watch.open");
        Watch("BVg1", null, inWeek.AddSeconds(1), "watch.play");
        Watch("BVg1", null, inWeek.AddSeconds(2), "watch.play"); // 暂停后恢复会再报一次 play
        _db.InsertEventsBatch(new[]
        {
            new EventDraft(EventActors.Guest, null, "watch.open", "video", "BVg1", null, "9.9.9.8", inWeek.AddMinutes(3).ToString("O")),
        });

        // 成员一枪：绝不能洘进游客账（actor_type=member）。
        Watch("BVg1", 4242, inWeek.AddMinutes(4));

        // 被挡拆两枩：片页被拦（想看看不了）与弹幕轮询被拦（自家代码空转）分开数。
        _db.InsertEventsBatch(new[]
        {
            new EventDraft(EventActors.Guest, null, "watch.blocked", "path", "/api/watch/BVg1", null, "9.9.9.9", inWeek.AddMinutes(5).ToString("O")),
            new EventDraft(EventActors.Guest, null, "watch.blocked", "path", "/api/videos/9/danmaku", null, "9.9.9.9", inWeek.AddMinutes(6).ToString("O")),
            new EventDraft(EventActors.Guest, null, "watch.blocked", "path", "/api/videos/9/danmaku", null, "9.9.9.9", inWeek.AddMinutes(6).ToString("O")),
            // 游客搜索：只报回数，内容不入报。
            new EventDraft(EventActors.Guest, null, "search.query", "query", "miku", """{"hits":1}""", "9.9.9.9", inWeek.AddMinutes(7).ToString("O")),
            // 登录尝试两枪（扫码开台 + 密码试错）。
            new EventDraft(EventActors.Guest, null, "login.qr.start", "qr", "abc", null, "9.9.9.9", inWeek.AddMinutes(8).ToString("O")),
            new EventDraft(EventActors.Guest, null, "login.password.denied", "member", "ceshi", null, "9.9.9.8", inWeek.AddMinutes(9).ToString("O")),
            // 出桶一枪（上周 W42）：游客账也不许预支桶外的数。
            new EventDraft(EventActors.Guest, null, "watch.open", "video", "BVg1", null, "8.8.8.8", W43StartUtc.AddMinutes(-30).ToString("O")),
        });

        var guest = _db.GetWeeklyReport(0, Now).Guest;
        Assert.Equal(2, guest.Heads); // 8.8.8.8 在桶外，两个在桶内的 IP 去重成 2 位
        Assert.Equal(2, guest.Opens);
        Assert.Equal(2, guest.Plays);
        Assert.Equal(1, guest.Searches);
        Assert.Equal(1, guest.BlockedWatch);
        Assert.Equal(2, guest.BlockedDanmaku);
        Assert.Equal(2, guest.LoginTries);
    }

    [Fact]
    public void 公告时长人话_秒分钟小时三档_长标题截短()
    {
        Assert.Equal("0 秒", WeeklyReportWorker.HumanDuration(0));
        Assert.Equal("40 秒", WeeklyReportWorker.HumanDuration(40));
        Assert.Equal("35 分钟", WeeklyReportWorker.HumanDuration(35 * 60));
        Assert.Equal("1 小时", WeeklyReportWorker.HumanDuration(3600));
        Assert.Equal("2 小时 13 分", WeeklyReportWorker.HumanDuration(2 * 3600 + 13 * 60));

        Assert.Equal("短标题", WeeklyReportWorker.ShortTitle("短标题"));
        Assert.Equal(new string('长', 24) + "…", WeeklyReportWorker.ShortTitle(new string('长', 30)));
    }

    [Fact]
    public void 发报员周一下九点开窗_同周发第二次必须不发()
    {
        var active = Seat("值守的", 950_001);
        var banned = Seat("封着的", 950_002);
        _db.SetMemberStatus(banned, MemberStatus.Banned);

        // 上周有点动静：1 件新货 + 2 条弹幕，公告里要带得出数字。
        AddVideo("BVnews1");
        StampVideoCreated("BVnews1", W44StartUtc.AddHours(3));
        var vId = _db.ListVideos().First(v => v.Bvid == "BVnews1").Id;
        StampDanmakuCreated(_db.AddLocalDanmaku(vId, 0, 1, "顶", null, false), W44StartUtc.AddHours(4));
        StampDanmakuCreated(_db.AddLocalDanmaku(vId, 0, 2, "肺", null, false), W44StartUtc.AddHours(5));

        // 闸门：本地（UTC+8）周一 08:59:59 还没到点，09:00 整就放行。
        Assert.Null(WeeklyReportWorker.DueWeekKey(new DateTimeOffset(2026, 11, 2, 0, 59, 59, TimeSpan.Zero), null));
        Assert.Equal("2026-W44", WeeklyReportWorker.DueWeekKey(new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero), null));

        var sentAt = new DateTimeOffset(2026, 11, 2, 2, 0, 0, TimeSpan.Zero); // 周一 10:00 (+08) 巡到
        Assert.Equal("2026-W44", WeeklyReportWorker.RunTick(_db, null, sentAt));
        Assert.Equal("2026-W44", _db.GetSetting(StationDb.WeeklySentWatermarkKey));

        var inbox = _db.ListMessages(active);
        var announce = Assert.Single(inbox);
        Assert.Equal(MemberMail.KindAnnounce, announce.Kind);
        Assert.Equal("分站周报 · 2026-W44", announce.Title);
        Assert.Contains("新增稿件 1 件", announce.Body);
        Assert.Contains("弹幕 +2 条", announce.Body);
        Assert.Contains("/weekly", announce.Body);
        Assert.Empty(_db.ListMessages(banned)); // 封席位不收报

        // 幂等：同周第二次（哪怕同一个时刻再巡一遍）必须哑火，一条公告都不许多发。
        Assert.Null(WeeklyReportWorker.RunTick(_db, null, sentAt));
        Assert.Null(WeeklyReportWorker.RunTick(_db, null, sentAt.AddMinutes(10)));
        Assert.Single(_db.ListMessages(active));

        // 下个周一下九点后：补发新一周，水位跟着走。新一周得有真动静，否则空周守卫会哑火（见下一条用例）。
        AddVideo("BVnews2");
        StampVideoCreated("BVnews2", new DateTimeOffset(2026, 11, 3, 3, 0, 0, TimeSpan.Zero)); // W45 里到站的货
        Assert.Equal("2026-W45", WeeklyReportWorker.RunTick(_db, null, new DateTimeOffset(2026, 11, 9, 1, 30, 0, TimeSpan.Zero)));
        Assert.Equal("2026-W45", _db.GetSetting(StationDb.WeeklySentWatermarkKey));
        Assert.Equal(2, _db.ListMessages(active).Count);
        Assert.Contains("分站周报 · 2026-W45", _db.ListMessages(active).Select(m => m.Title).ToArray());
    }

    [Fact]
    public void 整周零动静不发空报_只盖水位()
    {
        var active = Seat("值守的", 950_011); // 席位落在真下的 W44，故拿 W43 测空周

        // 周一 10:00 (+08) 巡到：候选周 W43 整周没人来过——该哑火，不许播一条「新增 0 件」的空报扰人。
        var quietAt = new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero);
        Assert.True(WeeklyReportWorker.IsQuietWeek(_db.GetWeeklyReport(0, quietAt)));
        Assert.Equal("2026-W43", WeeklyReportWorker.RunTick(_db, null, quietAt));
        Assert.Empty(_db.ListMessages(active));

        // 但水位必须盖上：不然下个周巡站又会把同一周翻出来重播。
        Assert.Equal("2026-W43", _db.GetSetting(StationDb.WeeklySentWatermarkKey));
        Assert.Null(WeeklyReportWorker.RunTick(_db, null, quietAt.AddDays(3)));
        Assert.Empty(_db.ListMessages(active));

        // 反向保险：周里有动静就必须播，不能误哑。
        var loudAt = new DateTimeOffset(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);
        AddVideo("BVonlyone");
        StampVideoCreated("BVonlyone", new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.Zero)); // W44 里一件新货
        Assert.False(WeeklyReportWorker.IsQuietWeek(_db.GetWeeklyReport(0, loudAt)));
        Assert.Equal("2026-W44", WeeklyReportWorker.RunTick(_db, null, loudAt));
        var announce = Assert.Single(_db.ListMessages(active));
        Assert.Equal("分站周报 · 2026-W44", announce.Title);
    }

    [Fact]
    public void 水位比的是周号大小_认不出的水位宁可重发()
    {
        Assert.True(WeeklyReportWorker.WatermarkBehind(null, "2026-W44"));
        Assert.True(WeeklyReportWorker.WatermarkBehind("", "2026-W44"));
        Assert.True(WeeklyReportWorker.WatermarkBehind("上周发过了吧", "2026-W44")); // 认不出格式 → 当落后，宁可重发不静默吞
        Assert.True(WeeklyReportWorker.WatermarkBehind("2026-W43", "2026-W44"));
        Assert.True(WeeklyReportWorker.WatermarkBehind("2025-W53", "2026-W01")); // 跨年比大小跟年份走
        Assert.False(WeeklyReportWorker.WatermarkBehind("2026-W44", "2026-W44")); // 同周 → 不发（幂等的水位侧）
        Assert.False(WeeklyReportWorker.WatermarkBehind("2026-W45", "2026-W44")); // 水位超前（表把拨回去过）→ 不发

        Assert.True(WeeklyReportWorker.TryParseWeekKey("2026-W44", out var parsed));
        Assert.Equal((2026, 44), parsed);
        Assert.False(WeeklyReportWorker.TryParseWeekKey("2026-44", out _));
        Assert.False(WeeklyReportWorker.TryParseWeekKey("2026-W54", out _));
        Assert.False(WeeklyReportWorker.TryParseWeekKey("26-W44", out _));
        Assert.False(WeeklyReportWorker.TryParseWeekKey(null, out _));
    }

    [Fact]
    public void 全站家底与站长室柜台同口径()
    {
        AddVideo("BVbig1");
        AddVideo("BVbig2");
        AddVideo("BVbig3");
        Seat("甲", 960_001);
        Seat("乙", 960_002);
        var (invited, _) = _db.CreateMemberInvite("待邀的席位"); // 没认主的也算成员总数（数据交换同款全行口径）
        Assert.True(invited > 0);

        var dmVideo = _db.ListVideos().First().Id;
        for (var i = 0; i < 5; i++)
        {
            _db.AddLocalDanmaku(dmVideo, 0, i, $"刷{i}", null, false);
        }

        File.WriteAllBytes(Path.Combine(_videosDir, "stage.mp4"), new byte[1024]);

        var report = _db.GetWeeklyReport(0, Now);
        var counter = _db.AdminStats(); // /api/admin/stats 背后那张嘴

        Assert.Equal(counter.Total, report.Totals.Videos); // 两个页面的稿件数必须同源同数
        Assert.Equal(3, report.Totals.Videos);
        Assert.Equal(counter.TotalBytes, report.Totals.Bytes);
        Assert.Equal(1024, report.Totals.Bytes);
        Assert.Equal(3, report.Totals.Members);
        Assert.Equal(5, report.Totals.Danmaku);
    }
}
