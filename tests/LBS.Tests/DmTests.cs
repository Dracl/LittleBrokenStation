using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;

namespace LBS.Tests;

/// <summary>
/// M5.3 用户间私信（M5.4 起全面挂站内席位 id）：发送与升序往来、按 peer 聚合的会话摘要、
/// 标读只销「对方发来」的、第三人隔离，以及收进 ValidateSend 的全部发送校验
/// （自书/超长/封禁/待邀/查无/发件人非 active）。
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

    /// <summary>走真收编路径造 active 席位，返回站内席位 id（私信的收发钥匙）。</summary>
    private long AdmitSeat(long mid, string name = "小张")
    {
        if (!_db.HasOwner())
        {
            MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, name), invite: null, "SESSDATA=a");
        }
        else
        {
            var (_, code) = _db.CreateMemberInvite(null);
            MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, name), code, "SESSDATA=a");
        }

        return _db.GetMemberByMid(mid)!.Id;
    }

    [Fact]
    public void SendAndConversation_AreAscending()
    {
        var a = AdmitSeat(1001, "甲");
        var b = AdmitSeat(1002, "乙");

        var id1 = _db.SendDm(a, b, "第一句");
        var id2 = _db.SendDm(b, a, "回一句");
        var id3 = _db.SendDm(a, b, "再追一句");

        var convo = _db.ListDmConversation(a, b);
        Assert.Equal(3, convo.Count);
        Assert.Equal([id1, id2, id3], convo.Select(m => m.Id));
        Assert.Equal(["第一句", "回一句", "再追一句"], convo.Select(m => m.Body));
        Assert.True(convo[0].Id < convo[^1].Id);
    }

    [Fact]
    public void Threads_AggregateByPeer_NewestFirst_WithUnread()
    {
        var me = AdmitSeat(2001, "我");
        var jia = AdmitSeat(2002, "甲");
        var yi = AdmitSeat(2003, "乙");

        _db.SendDm(jia, me, "甲来的老话");
        _db.SendDm(jia, me, "甲来的新话");
        _db.SendDm(me, yi, "我发给乙");
        _db.SendDm(yi, me, "乙来的话");

        var threads = _db.ListDmThreads(me);
        Assert.Equal(2, threads.Count);
        // 乙的最后一句最新 → 排前
        Assert.Equal(yi, threads[0].PeerMemberId);
        Assert.Equal("乙来的话", threads[0].LastBody);
        Assert.Equal(1, threads[0].Unread);
        Assert.Equal(jia, threads[1].PeerMemberId);
        Assert.Equal("甲来的新话", threads[1].LastBody);
        Assert.Equal(2, threads[1].Unread);
        // 名片现查 members
        Assert.Equal("甲", threads[1].PeerName);
    }

    [Fact]
    public void MarkDmRead_OnlyClearsIncoming()
    {
        var me = AdmitSeat(3001, "我");
        var peer = AdmitSeat(3002, "对方");

        _db.SendDm(peer, me, "给你的未读一");
        _db.SendDm(peer, me, "给你的未读二");
        _db.SendDm(me, peer, "我发出去的不算未读");

        Assert.Equal(1, _db.CountDmUnread(peer)); // 我发出的对对方而言是未读
        Assert.Equal(2, _db.MarkDmRead(me, peer));
        Assert.Equal(0, _db.CountDmUnread(me));
        Assert.Equal(1, _db.CountDmUnread(peer)); // 没替对方把收到的销掉

        // 我发出去的那条永远保持未读态（由对方来销）
        var mine = Assert.Single(_db.ListDmConversation(me, peer), m => m.FromMemberId == me);
        Assert.Null(mine.ReadAtUtc);
        // 再标一次：无未读可销，幂等归零
        Assert.Equal(0, _db.MarkDmRead(me, peer));
    }

    [Fact]
    public void ThirdParty_CannotSeeOthersConversation()
    {
        var jia = AdmitSeat(4001, "甲");
        var yi = AdmitSeat(4002, "乙");
        var bing = AdmitSeat(4003, "丙");

        _db.SendDm(jia, yi, "甲乙的悄悄话");
        _db.SendDm(yi, jia, "乙的回话");

        Assert.Empty(_db.ListDmConversation(bing, jia));
        Assert.Empty(_db.ListDmConversation(bing, yi));
        Assert.Empty(_db.ListDmThreads(bing));
        Assert.Equal(0, _db.CountDmUnread(bing));
    }

    [Fact]
    public void ValidateSend_BlocksSelfWrite()
    {
        var self = AdmitSeat(5001, "自书人");
        var (error, _, _) = DmEndpoints.ValidateSend(_db, self, self, "写给自己");
        Assert.NotNull(error);
        Assert.Equal(400, error!.Value.Status);
        Assert.Contains("信箱就在顶栏", error.Value.Message);
    }

    [Fact]
    public void ValidateSend_BlocksEmptyAndOversized()
    {
        var a = AdmitSeat(5101, "甲");
        var b = AdmitSeat(5102, "乙");

        var (blank, _, _) = DmEndpoints.ValidateSend(_db, a, b, "   ");
        Assert.NotNull(blank);
        Assert.Equal(400, blank!.Value.Status);

        var (tooLong, _, _) = DmEndpoints.ValidateSend(_db, a, b, new string('字', DmEndpoints.MaxDmLength + 1));
        Assert.NotNull(tooLong);
        Assert.Contains("论文站", tooLong!.Value.Message);

        // 恰好卡线：正好 2000 字放行
        var (edge, _, body) = DmEndpoints.ValidateSend(_db, a, b, new string('字', DmEndpoints.MaxDmLength));
        Assert.Null(edge);
        Assert.Equal(DmEndpoints.MaxDmLength, body.Length);
    }

    [Fact]
    public void ValidateSend_BlocksBannedInvitedAndGhost()
    {
        var a = AdmitSeat(5201, "甲");
        var doomed = AdmitSeat(5202, "将被封");
        var b = AdmitSeat(5203, "乙");
        _db.SetMemberStatus(doomed, MemberStatus.Banned);
        _db.CreateMemberInvite(null); // 待邀空席（没绑任何人，不能被当 target 命中）

        var (banned, _, _) = DmEndpoints.ValidateSend(_db, a, doomed, "找被封的你");
        Assert.NotNull(banned);
        Assert.Equal(404, banned!.Value.Status);
        Assert.Contains("对方不在站", banned.Value.Message);

        var (ghost, _, _) = DmEndpoints.ValidateSend(_db, a, 999999, "查无此人");
        Assert.Equal(404, ghost!.Value.Status);

        // 发件人自己被封 → 403 落闸
        var (fallen, _, _) = DmEndpoints.ValidateSend(_db, doomed, b, "封了还想发");
        Assert.Equal(403, fallen!.Value.Status);
    }

    [Fact]
    public void ValidateSend_TrimsAndPassesGoodSend()
    {
        var a = AdmitSeat(5301, "甲");
        var b = AdmitSeat(5302, "乙");
        var (error, target, body) = DmEndpoints.ValidateSend(_db, a, b, "  晚上守站吗  ");
        Assert.Null(error);
        Assert.Equal(b, target);
        Assert.Equal("晚上守站吗", body);
    }

    [Fact]
    public void UnreadCounts_InboxAndDmAreIndependentBuckets()
    {
        // 合流值的底座：顶栏铃铛 = 信箱未读 + 私信未读，两本账各记各的
        AdmitSeat(6000, "垫背站长"); // 先给站点立个主，后面都是持码进站的普通成员
        var me = AdmitSeat(6001, "甲");
        _db.SendToMember(me, "announce", "公告一", "正文");
        _db.SendToMember(me, "announce", "公告二", "正文");
        var peer = AdmitSeat(6002, "乙");
        _db.SendDm(peer, me, "未读私信一");
        _db.SendDm(peer, me, "未读私信二");
        _db.SendDm(peer, me, "未读私信三");

        // 信箱三封 = 两封公告 + 进站欢迎信
        Assert.Equal(3, _db.CountUnread(me));
        Assert.Equal(3, _db.CountDmUnread(me));

        _db.MarkRead(_db.ListMessages(me)[0].Id, me);
        _db.MarkDmRead(me, peer);
        Assert.Equal(2, _db.CountUnread(me) + _db.CountDmUnread(me)); // 只剩信箱那两封
    }

    [Fact]
    public void AdminInspection_SeesAllAndCanPairLock()
    {
        var jia = AdmitSeat(7001, "甲");
        var yi = AdmitSeat(7002, "乙");
        var bing = AdmitSeat(7003, "丙");
        _db.SendDm(jia, yi, "甲乙话");
        _db.SendDm(yi, jia, "回甲乙话");
        _db.SendDm(bing, jia, "丙插一嘴");

        var all = _db.ListDmForAdmin(null, null);
        Assert.Equal(3, all.Count);
        Assert.Contains(all, r => r.FromName == "丙");

        var pair = _db.ListDmForAdmin(jia, yi);
        Assert.Equal(2, pair.Count);
        Assert.All(pair, r => Assert.True(
            (r.FromMemberId == jia && r.ToMemberId == yi) || (r.FromMemberId == yi && r.ToMemberId == jia)));

        // 只给一头不锁对（管理员视野保底全量）
        Assert.Equal(3, _db.ListDmForAdmin(jia, null).Count);
    }

    [Fact]
    public void Directory_ListsActiveOnly()
    {
        AdmitSeat(8001, "在站甲");
        AdmitSeat(8002, "在站乙");
        var banned = AdmitSeat(8003, "被封丙");
        _db.SetMemberStatus(banned, MemberStatus.Banned);
        _db.CreateMemberInvite("待邀丁");

        var roster = _db.GetActiveMembersForDirectory();
        Assert.Equal([8001L, 8002L], roster.Where(m => m.Mid is not null).Select(m => m.Mid).OrderBy(m => m));
    }

    [Fact]
    public void VideoShare_ValidatesAndFreezesCard_SameMouth()
    {
        // 放映厅安利：端点 lambda 薄薄一层皮，校验与定格全收进 ValidateVideoShare（与动态分享同规），这里直驱同一张嘴。
        var me = AdmitSeat(7101, "我");
        var peer = AdmitSeat(7102, "对方");
        var vid = _db.AddVideo("https://www.bilibili.com/video/BVshare001", "BVshare001").VideoId;
        _db.MarkSubmitted(vid, new[] { "t1" }, "好片子", "https://i0.hdslb.com/c.jpg");
        _db.MarkArrived(vid);

        var (error, target, note, dynJson) = DmEndpoints.ValidateVideoShare(_db, me, peer, "BVshare001", "  都给我去看！  ");
        Assert.Null(error);
        Assert.Equal(peer, target);
        Assert.Equal("都给我去看！", note);
        using (var card = System.Text.Json.JsonDocument.Parse(dynJson!))
        {
            Assert.Equal("video", card.RootElement.GetProperty("type").GetString());
            Assert.Equal("BVshare001", card.RootElement.GetProperty("bvid").GetString());
            Assert.Equal("好片子", card.RootElement.GetProperty("title").GetString());
            Assert.Equal("https://i0.hdslb.com/c.jpg", card.RootElement.GetProperty("cover").GetString());
            Assert.Equal("/watch/BVshare001", card.RootElement.GetProperty("watchUrl").GetString());
        }

        // 归档封面后卡面升级 /media/（与观看页同形，本地优先）；安利语可不写（空串，卡即正文）。
        _db.RegisterCoverArtifact(vid, "BVshare001.Cover.jpg");
        var (_, _, emptyNote, upgraded) = DmEndpoints.ValidateVideoShare(_db, me, peer, "BVshare001", null);
        Assert.Equal(string.Empty, emptyNote);
        using (var card2 = System.Text.Json.JsonDocument.Parse(upgraded!))
        {
            Assert.Equal("/media/BVshare001.Cover.jpg", card2.RootElement.GetProperty("cover").GetString());
        }

        // 随信落库（video 族与动态分享同表同形），对方会话里拿得到卡。
        var id = _db.SendDm(me, peer, emptyNote, "video", "BVshare001", upgraded);
        var convo = _db.ListDmConversation(peer, me);
        var landed = Assert.Single(convo, m => m.Id == id);
        Assert.Equal("video", landed.MsgType);
        Assert.Contains("/watch/BVshare001", landed.DynJson);

        // 校验嘴脸与动态分享同款：空气收信人/自娱自乐/查无此片/安利语超长/待邀空席。
        Assert.Equal(StatusCodes.Status400BadRequest, DmEndpoints.ValidateVideoShare(_db, me, null, "BVshare001", null).Error!.Value.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, DmEndpoints.ValidateVideoShare(_db, me, me, "BVshare001", null).Error!.Value.Status);
        Assert.Equal(StatusCodes.Status404NotFound, DmEndpoints.ValidateVideoShare(_db, me, peer, "BV查无此片0", null).Error!.Value.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, DmEndpoints.ValidateVideoShare(_db, me, peer, "BVshare001", new string('字', 501)).Error!.Value.Status);
        var (pendingId, _) = _db.CreateMemberInvite(null);
        Assert.Equal(StatusCodes.Status404NotFound, DmEndpoints.ValidateVideoShare(_db, me, pendingId, "BVshare001", null).Error!.Value.Status);
    }
}
