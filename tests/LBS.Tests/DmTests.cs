using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// M5.3 用户间私信：发送与升序往来、按 peer 聚合的会话摘要、标读只销「对方发来」的、
/// 第三人隔离，以及收进 ValidateSend 的全部发送校验（自书/超长/封禁/待邀/查无/发件人非 active）。
/// 端点层无 WebApplicationFactory 先例，lambda 只薄薄一层皮，校验与读写全在此直测。
/// </summary>
public sealed class DmTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public DmTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-dm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
        _gate = new MemberGate(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static BilibiliIdentity Identity(long mid, string name = "小张") => new(mid, name, "https://face/y.jpg");

    /// <summary>走真收编路径造 active 席位（首扫继位或持码进站）。</summary>
    private void AdmitActive(long mid, string name = "小张")
    {
        if (!_db.HasOwner())
        {
            MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, name), invite: null, "SESSDATA=a");
            return;
        }

        var (_, code) = _db.CreateMemberInvite(null);
        MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, name), code, "SESSDATA=a");
    }

    [Fact]
    public void SendAndConversation_AreAscending()
    {
        AdmitActive(1001, "甲");
        AdmitActive(1002, "乙");

        var id1 = _db.SendDm(1001, 1002, "第一句");
        var id2 = _db.SendDm(1002, 1001, "回一句");
        var id3 = _db.SendDm(1001, 1002, "再追一句");

        var convo = _db.ListDmConversation(1001, 1002);
        Assert.Equal(3, convo.Count);
        Assert.Equal([id1, id2, id3], convo.Select(m => m.Id));
        Assert.Equal(["第一句", "回一句", "再追一句"], convo.Select(m => m.Body));
        Assert.True(convo[0].Id < convo[^1].Id);
    }

    [Fact]
    public void Threads_AggregateByPeer_NewestFirst_WithUnread()
    {
        AdmitActive(2001, "我");
        AdmitActive(2002, "甲");
        AdmitActive(2003, "乙");

        _db.SendDm(2002, 2001, "甲来的老话");
        _db.SendDm(2002, 2001, "甲来的新话");
        _db.SendDm(2001, 2003, "我发给乙");
        _db.SendDm(2003, 2001, "乙来的话");

        var threads = _db.ListDmThreads(2001);
        Assert.Equal(2, threads.Count);
        // 乙的最后一句最新 → 排前
        Assert.Equal(2003, threads[0].PeerMid);
        Assert.Equal("乙来的话", threads[0].LastBody);
        Assert.Equal(1, threads[0].Unread);
        Assert.Equal(2002, threads[1].PeerMid);
        Assert.Equal("甲来的新话", threads[1].LastBody);
        Assert.Equal(2, threads[1].Unread);
        // 名片现查 members
        Assert.Equal("甲", threads[1].PeerName);
    }

    [Fact]
    public void MarkDmRead_OnlyClearsIncoming()
    {
        AdmitActive(3001, "我");
        AdmitActive(3002, "对方");

        _db.SendDm(3002, 3001, "给你的未读一");
        _db.SendDm(3002, 3001, "给你的未读二");
        _db.SendDm(3001, 3002, "我发出去的不算未读");

        Assert.Equal(1, _db.CountDmUnread(3002)); // 我发出的对对方而言是未读
        Assert.Equal(2, _db.MarkDmRead(3001, 3002));
        Assert.Equal(0, _db.CountDmUnread(3001));
        Assert.Equal(1, _db.CountDmUnread(3002)); // 没替对方把收到的销掉

        // 我发出去的那条永远保持未读态（由对方来销）
        var mine = Assert.Single(_db.ListDmConversation(3001, 3002), m => m.FromMid == 3001);
        Assert.Null(mine.ReadAtUtc);
        // 再标一次：无未读可销，幂等归零
        Assert.Equal(0, _db.MarkDmRead(3001, 3002));
    }

    [Fact]
    public void ThirdParty_CannotSeeOthersConversation()
    {
        AdmitActive(4001, "甲");
        AdmitActive(4002, "乙");
        AdmitActive(4003, "丙");

        _db.SendDm(4001, 4002, "甲乙的悄悄话");
        _db.SendDm(4002, 4001, "乙的回话");

        Assert.Empty(_db.ListDmConversation(4003, 4001));
        Assert.Empty(_db.ListDmConversation(4003, 4002));
        Assert.Empty(_db.ListDmThreads(4003));
        Assert.Equal(0, _db.CountDmUnread(4003));
    }

    [Fact]
    public void ValidateSend_BlocksSelfWrite()
    {
        AdmitActive(5001, "自书人");
        var (error, _, _) = DmEndpoints.ValidateSend(_db, 5001, 5001, "写给自己");
        Assert.NotNull(error);
        Assert.Equal(400, error!.Value.Status);
        Assert.Contains("信箱就在顶栏", error.Value.Message);
    }

    [Fact]
    public void ValidateSend_BlocksEmptyAndOversized()
    {
        AdmitActive(5101, "甲");
        AdmitActive(5102, "乙");

        var (blank, _, _) = DmEndpoints.ValidateSend(_db, 5101, 5102, "   ");
        Assert.NotNull(blank);
        Assert.Equal(400, blank!.Value.Status);

        var (tooLong, _, _) = DmEndpoints.ValidateSend(_db, 5101, 5102, new string('字', DmEndpoints.MaxDmLength + 1));
        Assert.NotNull(tooLong);
        Assert.Contains("论文站", tooLong!.Value.Message);

        // 恰好卡线：正好 2000 字放行
        var (edge, _, body) = DmEndpoints.ValidateSend(_db, 5101, 5102, new string('字', DmEndpoints.MaxDmLength));
        Assert.Null(edge);
        Assert.Equal(DmEndpoints.MaxDmLength, body.Length);
    }

    [Fact]
    public void ValidateSend_BlocksBannedInvitedAndGhost()
    {
        AdmitActive(5201, "甲");
        AdmitActive(5202, "将被封");
        AdmitActive(5203, "乙");
        var bannedSeat = _db.GetMemberByMid(5202)!;
        _db.SetMemberStatus(bannedSeat.Id, MemberStatus.Banned);
        _db.CreateMemberInvite(null); // 待邀空席（无 mid，不能被当 target 命中）

        var (banned, _, _) = DmEndpoints.ValidateSend(_db, 5201, 5202, "找被封的你");
        Assert.NotNull(banned);
        Assert.Equal(404, banned!.Value.Status);
        Assert.Contains("对方不在站", banned.Value.Message);

        var (ghost, _, _) = DmEndpoints.ValidateSend(_db, 5201, 999999, "查无此人");
        Assert.Equal(404, ghost!.Value.Status);

        // 发件人自己被封 → 403 落闸
        var (fallen, _, _) = DmEndpoints.ValidateSend(_db, 5202, 5203, "封了还想发");
        Assert.Equal(403, fallen!.Value.Status);
    }

    [Fact]
    public void ValidateSend_TrimsAndPassesGoodSend()
    {
        AdmitActive(5301, "甲");
        AdmitActive(5302, "乙");
        var (error, target, body) = DmEndpoints.ValidateSend(_db, 5301, 5302, "  晚上守站吗  ");
        Assert.Null(error);
        Assert.Equal(5302, target);
        Assert.Equal("晚上守站吗", body);
    }

    [Fact]
    public void UnreadCounts_InboxAndDmAreIndependentBuckets()
    {
        // 合流值的底座：顶栏铃铛 = 信箱未读 + 私信未读，两本账各记各的
        AdmitActive(6000, "垫背站长"); // 先给站点立个主，后面两位都是持码进站的普通成员
        AdmitActive(6001, "甲");
        _db.SendToMember(6001, "announce", "公告一", "正文");
        _db.SendToMember(6001, "announce", "公告二", "正文");
        AdmitActive(6002, "乙");
        _db.SendDm(6002, 6001, "未读私信一");
        _db.SendDm(6002, 6001, "未读私信二");
        _db.SendDm(6002, 6001, "未读私信三");

        // 信箱三封 = 两封公告 + 进站欢迎信
        Assert.Equal(3, _db.CountUnread(6001));
        Assert.Equal(3, _db.CountDmUnread(6001));

        _db.MarkRead(_db.ListMessages(6001)[0].Id, 6001);
        _db.MarkDmRead(6001, 6002);
        Assert.Equal(2, _db.CountUnread(6001) + _db.CountDmUnread(6001)); // 只剩信箱那两封
    }

    [Fact]
    public void AdminInspection_SeesAllAndCanPairLock()
    {
        AdmitActive(7001, "甲");
        AdmitActive(7002, "乙");
        AdmitActive(7003, "丙");
        _db.SendDm(7001, 7002, "甲乙话");
        _db.SendDm(7002, 7001, "回甲乙话");
        _db.SendDm(7003, 7001, "丙插一嘴");

        var all = _db.ListDmForAdmin(null, null);
        Assert.Equal(3, all.Count);
        Assert.Contains(all, r => r.FromName == "丙");

        var pair = _db.ListDmForAdmin(7001, 7002);
        Assert.Equal(2, pair.Count);
        Assert.All(pair, r => Assert.True(
            (r.FromMid == 7001 && r.ToMid == 7002) || (r.FromMid == 7002 && r.ToMid == 7001)));

        // 只给一头不锁对（管理员视野保底全量）
        Assert.Equal(3, _db.ListDmForAdmin(7001, null).Count);
    }

    [Fact]
    public void Directory_ListsActiveOnly()
    {
        AdmitActive(8001, "在站甲");
        AdmitActive(8002, "在站乙");
        var banned = _db.GetMemberByMid(AdmitActiveReturning(8003, "被封丙"))!;
        _db.SetMemberStatus(banned.Id, MemberStatus.Banned);
        _db.CreateMemberInvite("待邀丁");

        var roster = _db.GetActiveMembersForDirectory();
        Assert.Equal([8001L, 8002L], roster.Where(m => m.Mid is not null).Select(m => m.Mid).OrderBy(m => m));
    }

    private long AdmitActiveReturning(long mid, string name)
    {
        AdmitActive(mid, name);
        return mid;
    }
}
