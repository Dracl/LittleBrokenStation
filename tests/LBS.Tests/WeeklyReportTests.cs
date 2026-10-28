using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 分站周报（仿 DanmakuReportTests 造库造席位范式，全走 DB 方法 / internal static 直测）：
/// 周桶是本地周（UTC+8 周一 00:00 起算）——周一 00:00 前后各一条要落进各自的桶，跨年周认 ISO 的星期四；
/// 「最多人看」从 events 的 watch.play / watch.heartbeat 聚合，观众按 actor_mid 去重，访客（mid NULL）不进座、
/// subject_type 不对或 ts 出桶的行都不算；发报水位存 settings["weekly-report-sent"]，同周第二次必须不发（幂等）；
/// 全站家底与站长室柜台（AdminStats）同口径抽查——两个页面的数不许打架。
/// </summary>
public sealed class WeeklyReportTests : IDisposable
{
    // 锚点挂历：2026-10-26 是周一（ISO 2026-W44 之首）。本地 UTC+8 的周桶界 = UTC 周日 16:00。
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 6, 0, 0, TimeSpan.Zero); // 周四 14:00 (+08)
    private static readonly DateTimeOffset W44StartUtc = new(2026, 10, 25, 16, 0, 0, TimeSpan.Zero); // 本地 10-26 00:00

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

    /// <summary>往 events 台账塞一枪观看行为（形状照 WatchPage.vue 的上报实况：subject_type='video'、subject_id=BV 号）。</summary>
    private void Watch(string bvid, long? mid, DateTimeOffset ts, string name = "watch.play", string? subjectType = "video")
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
                ts.ToString("O")),
        });
    }

    [Fact]
    public void 周桶按UTC8周一零点分桶_前后各一条落对家()
    {
        // 稿件：一条卡在本地周一 00:00 前一秒半（归上周 W43），一条正压 00:00（归本周 W44），一条周中到站。
        AddVideo("BVw43a");
        StampVideoCreated("BVw43a", new DateTimeOffset(2026, 10, 25, 15, 59, 59, TimeSpan.Zero).AddMilliseconds(500));
        AddVideo("BVw44b");
        StampVideoCreated("BVw44b", W44StartUtc);
        AddVideo("BVw44c");
        StampVideoCreated("BVw44c", new DateTimeOffset(2026, 10, 29, 5, 59, 0, TimeSpan.Zero));
        AddVideo("BVfuture"); // 未来到站：0 桶上界夹到 now，不许预支
        StampVideoCreated("BVfuture", Now.AddHours(1));

        var vId = AddVideo("BVdm");
        StampVideoCreated("BVdm", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)); // 弹幕载体是老货，别扰新货桶的账
        var before = _db.AddLocalDanmaku(vId, 0, 1, "上周的弹幕", null, false);
        StampDanmakuCreated(before, new DateTimeOffset(2026, 10, 25, 15, 59, 59, TimeSpan.Zero).AddTicks(9_900_000)); // .99 尾数也压在界前
        var boundary = _db.AddLocalDanmaku(vId, 0, 2, "正点整的弹幕", null, false);
        StampDanmakuCreated(boundary, W44StartUtc);
        var tooEarly = _db.AddLocalDanmaku(vId, 0, 3, "上周桶界之前", null, false);
        StampDanmakuCreated(tooEarly, W44StartUtc.AddDays(-7).AddMinutes(-1)); // W43 起算前一分钟，两边都不算
        var tooLate = _db.AddLocalDanmaku(vId, 0, 4, "now 之后的未来弹幕", null, false);
        StampDanmakuCreated(tooLate, Now.AddMinutes(30));

        var thisWeek = _db.GetWeeklyReport(0, Now);
        Assert.Equal("2026-W44", thisWeek.WeekLabel);
        Assert.Equal("2026-10-25T16:00:00.0000000+00:00", thisWeek.SinceUtc); // 本地周一 00:00 (+08) 的 UTC 身
        Assert.Equal(Now.ToString("O"), thisWeek.UntilUtc); // 0 桶上界夹到 now
        Assert.Equal(2, thisWeek.NewsCount); // B、C；A 归上周，未来那条不算
        Assert.Equal(new[] { "BVw44c", "BVw44b" }, thisWeek.News.Select(n => n.Bvid).ToArray()); // 投稿时间倒序
        Assert.Equal(1, thisWeek.DanmakuAdded); // 只数界后那条

        var lastWeek = _db.GetWeeklyReport(1, Now);
        Assert.Equal("2026-W43", lastWeek.WeekLabel);
        Assert.Equal("2026-10-18T16:00:00.0000000+00:00", lastWeek.SinceUtc);
        Assert.Equal("2026-10-25T16:00:00.0000000+00:00", lastWeek.UntilUtc); // 完整周：上界就是桶界，不夹 now
        Assert.Equal(1, lastWeek.NewsCount);
        Assert.Equal("BVw43a", Assert.Single(lastWeek.News).Bvid);
        Assert.Equal(1, lastWeek.DanmakuAdded);
    }

    [Fact]
    public void 周标识认ISO跨年周_越界week夹回()
    {
        // 跨年周跟星期四认年份：2027-01-01（周五）还住在 2026-W53 里。
        Assert.Equal("2026-W53", StationDb.LocalWeekKey(new DateOnly(2027, 1, 1)));
        Assert.Equal("2026-W01", StationDb.LocalWeekKey(new DateOnly(2026, 1, 1)));
        Assert.Equal("2026-W44", StationDb.LocalWeekKey(new DateOnly(2026, 10, 26)));

        // week 参数越界夹回 0..4：-2 落本周，7 落第 4 周前（正对锚点就是周一 09-28 起的那周）。
        Assert.Equal("2026-W44", _db.GetWeeklyReport(-2, Now).WeekLabel);
        Assert.Equal("2026-W40", _db.GetWeeklyReport(7, Now).WeekLabel);
        Assert.Equal(0, WeeklyReportEndpoints.ClampWeek(null));
        Assert.Equal(0, WeeklyReportEndpoints.ClampWeek(-3));
        Assert.Equal(4, WeeklyReportEndpoints.ClampWeek(99));
        Assert.Equal(2, WeeklyReportEndpoints.ClampWeek(2));
    }

    [Fact]
    public void 最多人看Top5按观众去重_访客与出桶行不算人()
    {
        var inWeek = W44StartUtc.AddHours(10); // 桶内随便一点
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

        // 访客（actor_mid NULL）围观 guest 片：一屋子人也不许充人头。
        Watch("BVguest", null, inWeek);
        Watch("BVguest", null, inWeek.AddMinutes(1));

        // 出桶一枪（桶前）与 subject_type 缺失的一枪：都不进聚合。
        Watch("BVh2", 990, W44StartUtc.AddMinutes(-1));
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
        // h1（观众太少排不进前五）与 guest（全是访客，观众 0）都不该在榜上摆着。
        Assert.DoesNotContain(report.Hottest, h => h.Bvid == "BVh1");
        Assert.DoesNotContain(report.Hottest, h => h.Bvid == "BVguest");
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

        // 下个周一下九点后：补发新一周，水位跟着走。
        Assert.Equal("2026-W45", WeeklyReportWorker.RunTick(_db, null, new DateTimeOffset(2026, 11, 9, 1, 30, 0, TimeSpan.Zero)));
        Assert.Equal("2026-W45", _db.GetSetting(StationDb.WeeklySentWatermarkKey));
        Assert.Equal(2, _db.ListMessages(active).Count);
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
