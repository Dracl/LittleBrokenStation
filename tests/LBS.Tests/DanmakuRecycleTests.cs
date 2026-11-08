using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 单条站内弹幕的回收站（11-08 站长拍板「单删弹幕进回收站」）。钉的是这几件事：
/// ① 删不再是硬删——行搬进 danmaku_recycle，原表只剩活弹幕（读路径一个字没改，因为 danmaku_local 没加状态列）；
/// ② 捐回时「谁在什么时候说的」必须原样回来：created_at_utc / sender_dmid / member_id 一字不改，
///    否则站长反悔一次就篡改了一次发言时间，自删判据（认 lbs_dmid）也会认不出他；
/// ③ 捐回要有去处：片子已不在架时拒捐，不塞一条没人看得见的孤儿弹幕；
/// ④ 两条删弹幕的路（本人自删、举报处置删）共用这张表，用 source 分账，重开举报单能把弹幕一起带回来；
/// ⑤ 危险区一键清空（PurgeAllLedger）必须连回收站一起拍掉，否则清空分站后还能从回收站捞出弹幕。
/// 姿势与 DanmakuReportTests 一致：直驱库方法与判据函数，本仓无 HTTP harness。
/// </summary>
public sealed class DanmakuRecycleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly StationDb _db;

    public DanmakuRecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-dmrecycle-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(_videosDir);
        _db = new StationDb(new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir));
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

    private long AddArrivedVideo(string bvid)
    {
        var id = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        Assert.True(_db.MarkArrived(id));
        return id;
    }

    private long AddSeat(string username, string? nickname = null)
    {
        var (id, _) = _db.CreateMemberInvite(null);
        Assert.NotNull(_db.ActivateMemberByRegistration(id, username, "hash-" + username));
        if (nickname is not null)
        {
            Assert.True(_db.SetMemberNickname(id, nickname));
        }

        return id;
    }

    // ── 自删与捐回 ────────────────────────────────────────────

    [Fact]
    public void 自删的弹幕进回收站并原样捐回()
    {
        var videoId = AddArrivedVideo("BVrecycle01");
        var seat = AddSeat("阿梨", "阿梨酱");
        var dmId = _db.AddLocalDanmaku(videoId, 2, 12.5, "这句说错了", "dm-self-1", false, seat);
        var before = _db.GetLocalDanmakuSnapshot(dmId);
        Assert.NotNull(before);

        var recycleId = _db.MoveDanmakuToRecycle(dmId, "阿梨酱", seat, "self");
        Assert.True(recycleId > 0);

        // 原表只剩活弹幕：删掉的那条查不到（danmaku_local 没加状态列，靠搬走实现「看不见」）。
        Assert.Null(_db.GetLocalDanmakuSnapshot(dmId));

        var parked = Assert.Single(_db.ListDanmakuRecycle());
        Assert.Equal(dmId, parked.DmId);
        Assert.Equal("self", parked.Source);
        Assert.Equal("阿梨酱", parked.DeletedBy);
        Assert.Equal("阿梨酱", parked.SenderName);
        Assert.Equal("BVrecycle01", parked.Bvid);
        Assert.True(parked.VideoInStation);
        Assert.Equal(1, _db.CountDanmakuRecycle());

        var (ok, _) = _db.RestoreDanmakuFromRecycle(recycleId);
        Assert.True(ok);
        var after = _db.GetLocalDanmakuSnapshot(dmId);
        Assert.NotNull(after);
        // 说话时刻、浏览器凭证、席位归属一字不改：捐回不能顺手篡改发言时间，也不能让自删判据认不出他。
        Assert.Equal(before!.CreatedAtUtc, after!.CreatedAtUtc);
        Assert.Equal("dm-self-1", after.SenderDmid);
        Assert.Equal(seat, after.SenderMemberId);
        Assert.Equal(12.5, after.TSec);
        Assert.Equal(2, after.PageIdx);
        Assert.Equal("这句说错了", after.Text);
        // 捐回即离开回收站，不留双份。
        Assert.Equal(0, _db.CountDanmakuRecycle());
    }

    [Fact]
    public void 删不存在的弹幕不算搬动()
    {
        // 举报处置那条路要这个 0：原行被发送者先删过时，快照不该凭空造一条。
        Assert.Equal(0, _db.MoveDanmakuToRecycle(424242L, "站长", null, "admin"));
        Assert.Equal(0, _db.CountDanmakuRecycle());
    }

    [Fact]
    public void 片子下架时拒捐不塞孤儿弹幕()
    {
        var videoId = AddArrivedVideo("BVrecycle02");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 3, "趁还在架上说一句", "dm-self-2", false);
        var recycleId = _db.MoveDanmakuToRecycle(dmId, "站长", null, "admin");

        // 片子回落到非到站态（等价于进了视频回收站 / 被清出架）：捐回去也没地方摆。
        Assert.True(_db.MarkFailed(videoId, "测试：把片清出架"));
        var entry = Assert.Single(_db.ListDanmakuRecycle());
        Assert.False(entry.VideoInStation);

        var (ok, message) = _db.RestoreDanmakuFromRecycle(recycleId);
        Assert.False(ok);
        Assert.Contains("不在架上", message);
        // 拒捐不许顺手把快照吃掉：片子哪天回来还能捐。
        Assert.Equal(1, _db.CountDanmakuRecycle());
        Assert.False(_db.RestoreDanmakuByDmId(999999L).Ok);
    }

    [Fact]
    public void 按原弹幕id捐回认最新那条()
    {
        var videoId = AddArrivedVideo("BVrecycle03");
        var oldId = _db.AddLocalDanmaku(videoId, 0, 1, "早年那句", "dm-old", false);
        var oldRecycle = _db.MoveDanmakuToRecycle(oldId, "站长", null, "admin");
        var newId = _db.AddLocalDanmaku(videoId, 0, 2, "刚删那句", "dm-new", false);
        var newRecycle = _db.MoveDanmakuToRecycle(newId, "站长", null, "admin");
        Assert.True(oldRecycle > 0 && newRecycle > 0);

        // 同名多条（同一条弹幕被删过两回）时捐最新那一回。
        var (ok, _) = _db.RestoreDanmakuByDmId(newId);
        Assert.True(ok);
        Assert.Equal(oldRecycle, Assert.Single(_db.ListDanmakuRecycle()).Id);
        Assert.NotNull(_db.GetLocalDanmakuSnapshot(newId));
    }

    // ── 与举报单联动 ──────────────────────────────────────────

    [Fact]
    public void 举报删的弹幕进回收站重开单时自动捐回()
    {
        var videoId = AddArrivedVideo("BVrecycle04");
        var sender = AddSeat("被告");
        var reporter = AddSeat("举报人");
        var dmId = _db.AddLocalDanmaku(videoId, 1, 8.5, "这句不该飘", "dm-suspect", false, sender);

        var (error, outcome, reportId) = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, "影响观感", "dm-reporter");
        Assert.Null(error);
        Assert.Equal(DanmakuReportEndpoints.ReportOutcome.Created, outcome);

        var (status, message) = DanmakuReportEndpoints.HandleReport(_db, reportId, "delete");
        Assert.Equal(200, status);
        Assert.NotNull(message);
        Assert.Null(_db.GetLocalDanmakuSnapshot(dmId));

        // 举报处置与本人自删共用这张回收表，靠 source 分账——审计上「谁动的手」永远查得清。
        var entry = Assert.Single(_db.ListDanmakuRecycle());
        Assert.Equal("report", entry.Source);
        Assert.Equal(dmId, entry.DmId);

        var (reopenStatus, reopenMessage) = DanmakuReportEndpoints.ReopenReport(_db, reportId);
        Assert.Equal(200, reopenStatus);
        Assert.Contains("捐回去", reopenMessage);
        Assert.NotNull(_db.GetLocalDanmakuSnapshot(dmId));
        Assert.Equal(0, _db.CountDanmakuRecycle());
        // 单也真的回到待处理，能被重新处置一次。
        var pending = _db.ListDanmakuReports(pendingOnly: true);
        Assert.Contains(pending, r => r.Id == reportId);
    }

    [Fact]
    public void 重开只许一次待处理单不许反复重开()
    {
        var videoId = AddArrivedVideo("BVrecycle05");
        var sender = AddSeat("被告乙");
        var reporter = AddSeat("举报人乙");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "忽略就好那句", "dm-ok", false, sender);
        var (_, _, reportId) = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, null, null);

        // 本来就待处理的单：重开不算事，吃 409（别让「重开」变成可以反复点的盖章）。
        var (busyStatus, busyMessage) = DanmakuReportEndpoints.ReopenReport(_db, reportId);
        Assert.Equal(409, busyStatus);
        Assert.Contains("不用重开", busyMessage);

        Assert.Equal(200, DanmakuReportEndpoints.HandleReport(_db, reportId, "dismiss").Status);
        // 忽略处置没删弹幕，重开时话里不该提捐回。
        var (okStatus, okMessage) = DanmakuReportEndpoints.ReopenReport(_db, reportId);
        Assert.Equal(200, okStatus);
        Assert.DoesNotContain("捐回", okMessage);
        Assert.NotNull(_db.GetLocalDanmakuSnapshot(dmId));

        // 第二次重开（已经是 pending 了）再吃 409。
        Assert.Equal(409, DanmakuReportEndpoints.ReopenReport(_db, reportId).Status);
        // 不存在的单 404。
        Assert.Equal(404, DanmakuReportEndpoints.ReopenReport(_db, 888888L).Status);
    }

    [Fact]
    public void 弹幕早被本人删过举报处置照样结单()
    {
        var videoId = AddArrivedVideo("BVrecycle06");
        var sender = AddSeat("被告丙");
        var reporter = AddSeat("举报人丙");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 5, "自己先删了那句", "dm-self3", false, sender);
        var (_, _, reportId) = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, null, null);

        // 本人先自删（进回收站），站长随后处置删除：原行已不在，处置不该报错，也不该造第二条快照。
        Assert.True(_db.MoveDanmakuToRecycle(dmId, "被告丙", sender, "self") > 0);
        Assert.Equal(1, _db.CountDanmakuRecycle());
        var (status, _) = DanmakuReportEndpoints.HandleReport(_db, reportId, "delete");
        Assert.Equal(200, status);
        var entry = Assert.Single(_db.ListDanmakuRecycle());
        Assert.Equal("self", entry.Source);
    }

    // ── 清账 ─────────────────────────────────────────────────

    [Fact]
    public void 销毁单条与清空与一键清空都连回收站一起拍()
    {
        var videoId = AddArrivedVideo("BVrecycle07");
        var a = _db.AddLocalDanmaku(videoId, 0, 1, "第一条", "dm-a", false);
        var b = _db.AddLocalDanmaku(videoId, 0, 2, "第二条", "dm-b", false);
        var c = _db.AddLocalDanmaku(videoId, 0, 3, "第三条", "dm-c", false);
        var recycleA = _db.MoveDanmakuToRecycle(a, "站长", null, "admin");
        var recycleB = _db.MoveDanmakuToRecycle(b, "站长", null, "admin");
        Assert.Equal(2, _db.CountDanmakuRecycle());

        Assert.True(_db.PurgeDanmakuRecycle(recycleA));
        Assert.Equal(1, _db.CountDanmakuRecycle());
        Assert.False(_db.PurgeDanmakuRecycle(recycleA)); // 已经销毁过的不重复算
        Assert.Equal(1, _db.PurgeAllDanmakuRecycle());
        Assert.Equal(0, _db.PurgeAllDanmakuRecycle()); // 空站再清回 0，不炸

        // 危险区一键清空：新表必须在那串 DELETE 里，否则清空分站后还能从回收站捞出弹幕。
        Assert.True(_db.MoveDanmakuToRecycle(c, "站长", null, "admin") > 0);
        Assert.Equal(1, _db.CountDanmakuRecycle());
        _db.PurgeAllLedger();
        Assert.Equal(0, _db.CountDanmakuRecycle());
    }

    [Fact]
    public void 回收站里的名片席位走了只是没脸()
    {
        var videoId = AddArrivedVideo("BVrecycle08");
        var seat = AddSeat("阿戊", "阿戊同学");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "我在的时候说过一句", "dm-pent", false, seat);
        Assert.True(_db.MoveDanmakuToRecycle(dmId, "站长", null, "admin") > 0);
        Assert.Equal("阿戊同学", Assert.Single(_db.ListDanmakuRecycle()).SenderName);

        _db.DeleteMember(seat);

        // 快照列还在（正文、凭证、席位 id 都在），只是名片取不到人——与弹幕列表那套「只是没脸」一致。
        var entry = Assert.Single(_db.ListDanmakuRecycle());
        Assert.Null(entry.SenderName);
        Assert.Equal("我在的时候说过一句", entry.Text);
        Assert.Equal(seat, entry.MemberId);
    }
}
