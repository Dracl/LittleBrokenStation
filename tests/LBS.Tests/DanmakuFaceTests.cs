using LBS.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 站内弹幕的席位真容：发弹幕时带着席位会话就把 member_id 存下，列表按 member_id 现取 members.face_url。
/// 钉住这几件事：没登录照样发得出去（member_id 为空，画小圆）、席位没头像/被删都只是没脸而弹幕不灭、
/// 头像只存 members 一处（朋友改头像，旧弹幕跟着换脸，不会冻成两张）、老库缺列升级后旧行照读、
/// 回收站往返连 member_id 一起回来、入站统计带上每人发过多少条弹幕。
/// </summary>
public sealed class DanmakuFaceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly string _recycleDir;
    private readonly StationDb _db;

    public DanmakuFaceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-dmface-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        _recycleDir = Path.Combine(_tempDir, "recycle");
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

    private long AddVideo(string bvid) =>
        _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;

    /// <summary>开一个席位并认领 B 站号（faceUrl 就是弹幕小圆要挂的那张脸）。</summary>
    private long Seat(string? faceUrl, string name = "小友")
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        var bound = _db.BindMember(id, 900_000 + id, name, faceUrl);
        Assert.NotNull(bound);
        return id;
    }

    [Fact]
    public void 席位发的弹幕_存住member_id并带出头像()
    {
        var videoId = AddVideo("BV1dmface01");
        var memberId = Seat("https://i0.hdslb.com/bfs/face/aaa.jpg");

        _db.AddLocalDanmaku(videoId, 0, 1.2, "有脸的一条", "dm-1", false, memberId);
        _db.AddLocalDanmaku(videoId, 0, 2.2, "匿名的一条", "dm-2", false);

        var rows = _db.ListLocalDanmaku(videoId);
        Assert.Equal(2, rows.Count);

        var withFace = rows.Single(r => r.Text == "有脸的一条");
        Assert.Equal(memberId, withFace.MemberId);
        Assert.Equal("https://i0.hdslb.com/bfs/face/aaa.jpg", withFace.MemberFace);

        // 未登录发的照旧入库，只是没脸（前端画那颗白圆粉小人）。
        var anon = rows.Single(r => r.Text == "匿名的一条");
        Assert.Null(anon.MemberId);
        Assert.Null(anon.MemberFace);
    }

    [Fact]
    public void 席位没头像_弹幕照发_只是没脸()
    {
        var videoId = AddVideo("BV1dmface02");
        var memberId = Seat(null);

        _db.AddLocalDanmaku(videoId, 0, 1, "素净席位发的", "dm-3", false, memberId);

        var row = Assert.Single(_db.ListLocalDanmaku(videoId));
        Assert.Equal(memberId, row.MemberId);
        Assert.Null(row.MemberFace);
    }

    [Fact]
    public void 朋友改头像_旧弹幕跟着换脸_库里不存第二份URL()
    {
        var videoId = AddVideo("BV1dmface03");
        var memberId = Seat("https://face/old.jpg");
        var member = _db.GetMemberById(memberId);
        Assert.NotNull(member);
        var mid = member!.Mid!.Value;

        _db.AddLocalDanmaku(videoId, 0, 1, "去年发的一条", "dm-4", false, memberId);
        Assert.Equal("https://face/old.jpg", Assert.Single(_db.ListLocalDanmaku(videoId)).MemberFace);

        // 重新登录时刷一次 members.face_url，旧弹幕就换脸：URL 只存 members 一处。
        _db.TouchMemberLogin(mid, "小友", "https://face/new.jpg");
        Assert.Equal("https://face/new.jpg", Assert.Single(_db.ListLocalDanmaku(videoId)).MemberFace);
    }

    [Fact]
    public void 席位被删_弹幕不灭_只是没脸()
    {
        var videoId = AddVideo("BV1dmface04");
        var memberId = Seat("https://face/gone.jpg");
        _db.AddLocalDanmaku(videoId, 0, 1, "席位走的弹幕", "dm-5", false, memberId);

        _db.DeleteMember(memberId);

        var row = Assert.Single(_db.ListLocalDanmaku(videoId));
        Assert.Equal(memberId, row.MemberId); // 归属留在原地，不因为席位没了就改口
        Assert.Null(row.MemberFace);          // LEFT JOIN 落空：只是没脸
    }

    [Fact]
    public void 老库缺member_id列_升级后旧行照读()
    {
        var videoId = AddVideo("BV1dmface05");
        // 身份体系之前的老行：无凭证、当主人手笔。
        _db.AddLocalDanmaku(videoId, 0, 1, "老库里的弹幕", null, true);

        var dbFile = Path.Combine(_tempDir, "station.db");
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={dbFile}"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE danmaku_local DROP COLUMN member_id;";
            drop.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        _db.Initialize(); // 重开闸门：EnsureColumn 把列补回来

        var row = Assert.Single(_db.ListLocalDanmaku(videoId));
        Assert.Null(row.MemberId);
        Assert.Null(row.MemberFace);
        Assert.True(row.SenderOwner);

        // 补回来的列能用：新发的席位弹幕照样带脸。
        var memberId = Seat("https://face/fresh.jpg");
        _db.AddLocalDanmaku(videoId, 0, 2, "升级后发的", "dm-6", false, memberId);
        Assert.Equal("https://face/fresh.jpg", _db.ListLocalDanmaku(videoId).Single(r => r.Text == "升级后发的").MemberFace);
    }

    [Fact]
    public void 站长席位头像_只认在席的头名owner()
    {
        // 还没主：没脸可兼底，主人行也就画小圆。
        Assert.Null(_db.GetOwnerSeatFace());

        var ownerSeat = Seat("https://face/owner.jpg", "站长小八");
        Assert.Equal("https://face/owner.jpg", _db.GetOwnerSeatFace());

        // 封席就不算在席：不能再拿他的脸去顶主人弹幕。
        _db.SetMemberStatus(ownerSeat, "banned");
        Assert.Null(_db.GetOwnerSeatFace());
    }

    [Fact]
    public void 站长席位没头像_兼底脸为空()
    {
        Seat(null, "素净站长"); // 头名继位 owner，但没绑 B 站号→没脸
        Assert.Null(_db.GetOwnerSeatFace());
    }

    [Fact]
    public void 入站统计_带弹幕条数_只发弹幕的席位也在表里()
    {
        var videoId = AddVideo("BV1dmstat01");
        var other = AddVideo("BV1dmstat02");
        var author = Seat("https://face/author.jpg", "投稿人");
        var chatter = Seat("https://face/chatter.jpg", "只说话的人");
        _db.SetVideoSubmitterMember(videoId, author);

        _db.AddLocalDanmaku(videoId, 0, 1, "作者自己盖楼1", "dm-21", false, author);
        _db.AddLocalDanmaku(videoId, 0, 2, "作者自己盖楼2", "dm-22", false, author);
        for (var i = 0; i < 3; i++)
        {
            _db.AddLocalDanmaku(other, 0, i, $"旁人刷的{i}", $"dm-2{i}", false, chatter);
        }

        // 没身份的行人不能塑到任何席位头上。
        _db.AddLocalDanmaku(videoId, 0, 9, "匿名的一条", "dm-29", false);

        var stats = _db.ListMemberIngestStats();
        var mine = stats.Single(s => s.SubmitterMemberId == author);
        Assert.Equal(1, mine.VideoCount);
        Assert.Equal(2, mine.DanmakuCount);

        // 只发弹幕没投过稿：过去这种人在表里根本没行，个人空间会把 3 条显示成 0。
        var talker = stats.Single(s => s.SubmitterMemberId == chatter);
        Assert.Equal(0, talker.VideoCount);
        Assert.Equal(3, talker.DanmakuCount);
    }

    [Fact]
    public void 入站统计_弹幕被删_条数跟着减()
    {
        var videoId = AddVideo("BV1dmstat03");
        var memberId = Seat("https://face/clean.jpg", "发言的人");
        var keep = _db.AddLocalDanmaku(videoId, 0, 1, "留着", "dm-31", false, memberId);
        var gone = _db.AddLocalDanmaku(videoId, 0, 2, "要删", "dm-32", false, memberId);

        Assert.Equal(2, Assert.Single(_db.ListMemberIngestStats(), s => s.SubmitterMemberId == memberId).DanmakuCount);
        Assert.True(_db.DeleteLocalDanmaku(gone));

        Assert.Equal(1, Assert.Single(_db.ListMemberIngestStats(), s => s.SubmitterMemberId == memberId).DanmakuCount);
        Assert.Equal(keep, Assert.Single(_db.ListLocalDanmaku(videoId)).Id);
    }

    [Fact]
    public void 回收站往返_连席位身份一起回来()
    {
        var videoId = AddVideo("BV1dmface06");
        var memberId = Seat("https://face/recycle.jpg");
        var danmakuId = _db.AddLocalDanmaku(videoId, 0, 3.5, "带走的那条", "dm-7", false, memberId);

        var moved = _db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0);
        Assert.True(moved.Ok, moved.Message);
        Assert.Empty(_db.ListLocalDanmaku(videoId));

        var entry = Assert.Single(_db.ListRecycle());
        var restored = _db.RestoreFromRecycle(entry.Id, _videosDir, _recycleDir);
        Assert.True(restored.Ok, restored.Message);

        var back = Assert.Single(_db.ListLocalDanmaku(videoId));
        Assert.Equal(danmakuId, back.Id);
        Assert.Equal(memberId, back.MemberId);
        Assert.Equal("https://face/recycle.jpg", back.MemberFace);
    }
}
