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

        // 名册不再整本递出（全量接口已拆），只有搜索命中可见：封禁与待邀同样不露面。
        var hits = StationDb.MatchMembersForDirectory(_db.ListMembers(), "在站");
        Assert.Equal([8001L, 8002L], hits.Where(m => m.Mid is not null).Select(m => m.Mid).OrderBy(m => m));
        Assert.Empty(StationDb.MatchMembersForDirectory(_db.ListMembers(), "被封"));
        Assert.Empty(StationDb.MatchMembersForDirectory(_db.ListMembers(), "待邀"));
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

    // ─── 选人框搜索（directory?q=）：门槛、命中、只搜不兑底 ───

    private static StationMember Seat(long id, long? mid, string? nickname, string? username, string status = MemberStatus.Active) =>
        new(id, mid, nickname, null, null, status, "member", "2026-10-31T00:00:00Z", null, null, username);

    [Fact]
    public void 选人门槛_纯数字一位即搜_文字单字不收()
    {
        Assert.False(StationDb.DirectorySearchAllowed(null));
        Assert.False(StationDb.DirectorySearchAllowed(""));
        Assert.False(StationDb.DirectorySearchAllowed("   "));
        Assert.False(StationDb.DirectorySearchAllowed("猫")); // 单字不收，防广撒网
        Assert.False(StationDb.DirectorySearchAllowed("a")); // 单字母同样不收
        Assert.True(StationDb.DirectorySearchAllowed("猫与"));
        Assert.True(StationDb.DirectorySearchAllowed("ab"));
        Assert.True(StationDb.DirectorySearchAllowed("2")); // 纯数字走等值，无枚举面
        Assert.True(StationDb.DirectorySearchAllowed(" 7禾 ")); // 首尾空白不当字数
    }

    [Fact]
    public void 选人搜索_命中昵称用户名_数字撞席位与mid_不达门槛绝不兑底()
    {
        var pool = new[]
        {
            Seat(2, 111, "测试账号", "ceshi"),
            Seat(3, 222, "猫与深巷", null),
            Seat(4, null, "ClearSky", "clearsky_fan"),
            Seat(5, 333, "封禁的猫", null, MemberStatus.Banned),
            Seat(6, 606, "小猫猫", null),
        };

        // 文字命中昵称/用户名，忽略大小写；封禁席位不露面。
        Assert.Equal(new long[] { 6 }, StationDb.MatchMembersForDirectory(pool, "猫猫").Select(m => m.Id).ToArray());
        Assert.Equal(new long[] { 3 }, StationDb.MatchMembersForDirectory(pool, "猫与").Select(m => m.Id).ToArray());
        Assert.Equal(new long[] { 2 }, StationDb.MatchMembersForDirectory(pool, "测试").Select(m => m.Id).ToArray());
        Assert.Equal(new long[] { 4 }, StationDb.MatchMembersForDirectory(pool, "CLEARSKY_FAN").Select(m => m.Id).ToArray());
        Assert.Empty(StationDb.MatchMembersForDirectory(pool, "封禁"));

        // 纯数字撞席位 id 与 B 站 mid（等值，不搞前缀模糊）。
        Assert.Equal(new long[] { 2 }, StationDb.MatchMembersForDirectory(pool, "2").Select(m => m.Id).ToArray());
        Assert.Equal(new long[] { 2 }, StationDb.MatchMembersForDirectory(pool, "111").Select(m => m.Id).ToArray());
        Assert.Empty(StationDb.MatchMembersForDirectory(pool, "1")); // mid 前缀不算命中

        // 不达门槛/空词一律空手，绝不全量兑底。
        Assert.Empty(StationDb.MatchMembersForDirectory(pool, null));
        Assert.Empty(StationDb.MatchMembersForDirectory(pool, "  "));
        Assert.Empty(StationDb.MatchMembersForDirectory(pool, "小"));

        // 限流：前 20 条。
        var crowd = Enumerable.Range(0, 30).Select(i => Seat(100 + i, null, "同名兄弟", null)).ToList();
        Assert.Equal(20, StationDb.MatchMembersForDirectory(crowd, "同名").Count);
    }

    [Fact]
    public void 素净席位展示名_不搭at前缀()
    {
        // 站长钦定：站内人名不搭 @（前院风味不进分站）。
        Assert.Equal("测试账号", StationDb.DisplayOf(Seat(9, null, null, "测试账号")));
        Assert.Equal("昵称优先", StationDb.DisplayOf(Seat(9, 999, "昵称优先", "用户名")));
        Assert.Null(StationDb.DisplayOf(Seat(9, null, null, null)));
    }

    [Fact]
    public void 会话单边销账_列表隐身_发信复活_对方不动()
    {
        var me = AdmitSeat(8201, "销账甲");
        var peer = AdmitSeat(8202, "被销乙");
        _db.SendDm(me, peer, "在吗");
        _db.SendDm(peer, me, "在的");
        Assert.Single(_db.ListDmThreads(me));
        Assert.Single(_db.ListDmThreads(peer));

        _db.HideDmThread(me, peer);
        Assert.Empty(_db.ListDmThreads(me));            // 我这边隐身
        Assert.Single(_db.ListDmThreads(peer));          // 对方账本不动
        _db.HideDmThread(me, peer);                      // 幂等再销不炸
        Assert.Empty(_db.ListDmThreads(me));

        _db.SendDm(me, peer, "重新聊聊");                 // 开口即复活
        var revived = Assert.Single(_db.ListDmThreads(me));
        Assert.Equal(peer, revived.PeerMemberId);
    }

    [Fact]
    public void 清空聊天记录_单边盖章_旧话隐身_对方照看_新话露面()
    {
        var me = AdmitSeat(8211, "清空甲");
        var peer = AdmitSeat(8212, "被清乙");
        _db.SendDm(me, peer, "老话一");
        _db.SendDm(peer, me, "老话二");

        Assert.False(_db.ClearDmThread(me, peer));         // 只盖了自己的章
        Assert.Empty(_db.ListDmConversation(me, peer, viewerMemberId: me));  // 我这边旧话全隐身
        Assert.Equal(2, _db.ListDmConversation(peer, me, viewerMemberId: peer).Count); // 对方账本不动
        Assert.Equal(2, _db.ListDmConversation(me, peer).Count);                 // 审计视角（不传 viewer）全量仍在
        Assert.Empty(_db.ListDmThreads(me));               // 列表也抹了
        Assert.Single(_db.ListDmThreads(peer));            // 对方列表照旧

        _db.SendDm(peer, me, "章后的新话");                 // 对方来话：列表摆回，且只看得见章后的
        var revived = Assert.Single(_db.ListDmThreads(me));
        Assert.Equal(peer, revived.PeerMemberId);
        Assert.Equal("章后的新话", revived.LastBody);      // 摘要也不偷看隐身旧事
        var convo = _db.ListDmConversation(me, peer, viewerMemberId: me);
        var only = Assert.Single(convo);
        Assert.Equal("章后的新话", only.Body);
    }

    [Fact]
    public void 清空聊天记录_双键集齐_往来真删_墓碑同销()
    {
        var jia = AdmitSeat(8221, "甲");
        var yi = AdmitSeat(8222, "乙");
        _db.SendDm(jia, yi, "悄悄话一");
        _db.SendDm(yi, jia, "悄悄话二");
        _db.SendDm(jia, yi, "悄悄话三");

        Assert.False(_db.ClearDmThread(jia, yi));          // 甲先盖章：消息还在（乙可见）
        Assert.Equal(3, _db.ListDmConversation(jia, yi).Count);

        Assert.True(_db.ClearDmThread(yi, jia));           // 乙盖章：两把钥匙集齐
        Assert.Empty(_db.ListDmConversation(jia, yi));     // 物理销毁，审计视角也没了
        Assert.Empty(_db.ListDmThreads(jia));
        Assert.Empty(_db.ListDmThreads(yi));

        // 墓碑一并销账：重新开口是全新会话，旧话不会因盖章时刻早于旧消息而回魂
        _db.SendDm(jia, yi, "重新来过");
        var fresh = Assert.Single(_db.ListDmConversation(jia, yi, viewerMemberId: yi));
        Assert.Equal("重新来过", fresh.Body);
    }

    [Fact]
    public void 撤回_发件人真删自己那句_销别人的不行()
    {
        var me = AdmitSeat(8241, "撤回甲");
        var peer = AdmitSeat(8242, "围观乙");
        var mine = _db.SendDm(me, peer, "手滑说错");
        var theirs = _db.SendDm(peer, me, "乙的正常话");

        Assert.False(_db.DeleteDmMessageAsSender(peer, mine));   // 别人代发不得（锁发件人）
        Assert.False(_db.DeleteDmMessageAsSender(me, 999999));   // 查无此句
        Assert.Equal(2, _db.ListDmConversation(me, peer).Count); // 两试皆未伤账

        Assert.True(_db.DeleteDmMessageAsSender(me, mine));      // 自己销自己的，成
        var rest = Assert.Single(_db.ListDmConversation(me, peer)); // 双方同灭：谁看都只剩乙那句
        Assert.Equal("乙的正常话", rest.Body);
        Assert.True(_db.DeleteDmMessageAsSender(peer, theirs));  // 乙也销自己那句，账本彻底清空
        Assert.Empty(_db.ListDmConversation(me, peer));
    }

    [Fact]
    public void 清空聊天记录_第三人账本不受波及()
    {
        var jia = AdmitSeat(8231, "甲");
        var yi = AdmitSeat(8232, "乙");
        var bing = AdmitSeat(8233, "丙");
        _db.SendDm(jia, yi, "甲乙话");
        _db.SendDm(bing, jia, "丙甲话");

        _db.ClearDmThread(jia, yi);
        var kept = Assert.Single(_db.ListDmConversation(jia, bing, viewerMemberId: jia)); // 丙甲往来不遭波及
        Assert.Equal("丙甲话", kept.Body);
        Assert.Single(_db.ListDmThreads(jia));
    }

    [Fact]
    public void 会话带查看者章时_仍按时间升序_且条数上限生效()
    {
        // 回归：盖过章的那串过滤 SQL 曾以分号收尾，而它拼在 ORDER BY / LIMIT 之前——
        // SQLite 只预备首个分号前的半句，排序与上限双双静默失效，行按 rowid 进来再被
        // list.Reverse 翻成倒序（会话变成新的压在上面）。旧测试只在不传 viewer 时验过顺序，没到这条路。
        var me = AdmitSeat(8251, "排序甲");
        var peer = AdmitSeat(8252, "排序乙");
        var ids = new long[5];
        for (var i = 0; i < 5; i++)
        {
            ids[i] = i % 2 == 0 ? _db.SendDm(me, peer, $"第{i + 1}句") : _db.SendDm(peer, me, $"第{i + 1}句");
        }

        var convo = _db.ListDmConversation(me, peer, viewerMemberId: me);
        Assert.Equal(ids, convo.Select(m => m.Id)); // 旧的在上、新的在下
        Assert.True(convo[0].Id < convo[^1].Id);

        var capped = _db.ListDmConversation(me, peer, viewerMemberId: me, limit: 2);
        Assert.Equal(new long[] { ids[3], ids[4] }, capped.Select(m => m.Id)); // 上限生效，且截的是最新一批
    }
}
