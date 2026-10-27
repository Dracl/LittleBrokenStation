using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// M5.2 站内信箱（M5.4 起挂站内席位 id）：落库读写（倒序/limit/未读统计/标读）、越席防护、
/// 公告只发 active，以及三封系统信的钩子——欢迎信钉在 AdmitMember 首扫建档处（真路径），
/// 归档/失败回执直接驱动送信员 MemberMail（worker 侧调用点是同款一行，编译期已验）。
/// </summary>
public sealed class StationMessageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public StationMessageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-msg-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>走真收编路径造 active 席位，返回站内席位 id（M5.4 后信箱的收件钥匙）。</summary>
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
    public void SendAndList_NewestFirst_WithLimit()
    {
        for (var i = 1; i <= 5; i++)
        {
            _db.SendToMember(1001, "announce", $"第{i}封", $"正文{i}");
        }

        var all = _db.ListMessages(1001);
        Assert.Equal(5, all.Count);
        Assert.Equal("第5封", all[0].Title);
        Assert.Equal("第1封", all[^1].Title);
        Assert.True(all[0].Id > all[^1].Id);

        var capped = _db.ListMessages(1001, limit: 2);
        Assert.Equal(2, capped.Count);
        Assert.Equal("第5封", capped[0].Title);
        Assert.Equal("第4封", capped[1].Title);
    }

    [Fact]
    public void UnreadOnly_Filters_And_CountTracksReads()
    {
        var id1 = _db.SendToMember(1001, "announce", "甲", "a");
        _db.SendToMember(1001, "announce", "乙", "b");
        Assert.Equal(2, _db.CountUnread(1001));

        Assert.True(_db.MarkRead(id1, 1001));
        Assert.Equal(1, _db.CountUnread(1001));
        Assert.Single(_db.ListMessages(1001, unreadOnly: true));
        Assert.Equal(2, _db.ListMessages(1001).Count);

        // 重复标读幂等：第二次不再改动。
        Assert.False(_db.MarkRead(id1, 1001));

        Assert.Equal(1, _db.MarkAllRead(1001));
        Assert.Equal(0, _db.CountUnread(1001));
        Assert.Empty(_db.ListMessages(1001, unreadOnly: true));
    }

    [Fact]
    public void MarkRead_CrossSeat_IsSilentlyRejected()
    {
        var id = _db.SendToMember(1001, "announce", "只给1001", "secret");

        // 越席摸信：2002 标 1001 的信 = 空手而归，原信仍是未读。
        Assert.False(_db.MarkRead(id, 2002));
        Assert.Equal(1, _db.CountUnread(1001));
        Assert.Null(_db.ListMessages(1001)[0].ReadAtUtc);
        Assert.Equal(0, _db.CountUnread(2002));
    }

    [Fact]
    public void Broadcast_ReachesOnlyActiveSeats()
    {
        var seatA = AdmitSeat(3001, "甲");
        var seatB = AdmitSeat(3002, "乙");
        var seatC = AdmitSeat(3003, "丙");
        _db.SetMemberStatus(seatC, MemberStatus.Banned);
        _db.CreateMemberInvite(null); // 待邀空席：不该收到

        var sent = _db.BroadcastToActive("announce", "全站通知", "今晚守站值班");

        Assert.Equal(2, sent);
        Assert.Equal("全站通知", _db.ListMessages(seatA)[0].Title);
        Assert.Equal("announce", _db.ListMessages(seatB)[0].Kind);
        // 3003 进站时有欢迎信，但封禁后不该收到公告
        Assert.DoesNotContain(_db.ListMessages(seatC), m => m.Kind == "announce");
    }

    [Fact]
    public void Welcome_SentOnce_OnFirstAdmit_WithStationName()
    {
        _db.SetSetting("station-name", "破烂小站");
        var seat = AdmitSeat(4001, "首客");

        var welcome = Assert.Single(_db.ListMessages(seat), m => m.Kind == "welcome");
        Assert.Equal("欢迎来到小破分站", welcome.Title);
        Assert.Contains("破烂小站", welcome.Body);
        Assert.Equal("/me", welcome.Link);

        // 重登（老席位再扫码）不重发欢迎信。
        MemberEndpoints.AdmitMember(_db, _gate, Identity(4001, "首客"), null, "SESSDATA=b");
        Assert.Single(_db.ListMessages(seat), m => m.Kind == "welcome");
    }

    [Fact]
    public void Welcome_FallsBackToDefaultName_WhenSettingBlank()
    {
        var seat = AdmitSeat(4002, "无名客");
        Assert.Contains("小破分站", Assert.Single(_db.ListMessages(seat), m => m.Kind == "welcome").Body);
    }

    [Fact]
    public void Arrival_Receipt_SkipsOrphan_SendsToSubmitter()
    {
        var orphanId = SeedVideo("BVorphan", "站长代下");
        MemberMail.NotifyVideoArrived(_db, null, orphanId);
        Assert.Empty(_db.ListMessages(5001)); // 无归属行静默跳过

        var videoId = SeedVideo("BV1me", "我的投稿");
        _db.SetVideoSubmitterMember(videoId, 5001);
        MemberMail.NotifyVideoArrived(_db, null, videoId);

        var receipt = Assert.Single(_db.ListMessages(5001));
        Assert.Equal("complete", receipt.Kind);
        Assert.Equal("投稿已入档", receipt.Title);
        Assert.Contains("我的投稿", receipt.Body);
        Assert.Equal("/watch/BV1me", receipt.Link);
    }

    [Fact]
    public void Arrival_Receipt_FallsBackToBvid_WhenTitleMissing()
    {
        var videoId = SeedVideo("BVnoname");
        _db.SetVideoSubmitterMember(videoId, 5002);
        MemberMail.NotifyVideoArrived(_db, null, videoId);
        Assert.Contains("BVnoname", Assert.Single(_db.ListMessages(5002)).Body);
    }

    [Fact]
    public void Failure_Receipt_CarriesFirstLineOfError()
    {
        var videoId = SeedVideo("BVfail", "翻车库");
        _db.SetVideoSubmitterMember(videoId, 5003);
        MemberMail.NotifyVideoFailed(_db, null, videoId, "解析挂了：链接失效\n堆栈第2行\n堆栈第3行");

        var receipt = Assert.Single(_db.ListMessages(5003));
        Assert.Equal("fail", receipt.Kind);
        Assert.Equal("投稿打了个趔趄", receipt.Title);
        Assert.Contains("解析挂了：链接失效", receipt.Body);
        Assert.DoesNotContain("堆栈第2行", receipt.Body);
        Assert.Null(receipt.Link);
    }

    [Fact]
    public void Failure_Receipt_SkipsOrphan()
    {
        var videoId = SeedVideo("BVnobody");
        MemberMail.NotifyVideoFailed(_db, null, videoId, "没人知道该通知谁");
        Assert.Empty(_db.ListMessages(5004));
    }

    [Fact]
    public void MailSend_Failure_NeverThrows_OnDeadTarget()
    {
        // 送信员对坏对象（不存在的视频 id）也必须安静——热路径不许把异常漏给 worker。
        MemberMail.NotifyVideoArrived(_db, null, 999999);
        MemberMail.NotifyVideoFailed(_db, null, 999999, "查无此行");
        MemberMail.NotifyWelcome(_db, null, 7777); // 席位不存在也照落（信箱不验收件人存在性）
        Assert.Single(_db.ListMessages(7777));
    }

    private long SeedVideo(string bvid, string? title = null)
    {
        var outcome = _db.AddVideosBulk(new[] { new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, title) });
        return Assert.Single(outcome.InsertedIds);
    }
}
