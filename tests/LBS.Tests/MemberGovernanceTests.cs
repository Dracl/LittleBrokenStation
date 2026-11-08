using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 成员治理四件（11-08 站长拍板）：除名与召回、邀请码事后查与作废、素净席位自改名号、公告按批撤回。
/// 钉的都是判据本体与库层事实（本仓无 HTTP harness，与 MemberAdmissionTests / StationAccountTests 同一姿势）：
/// ① 除名是白名单判据——扫码入场被拒、手上活票即刻作废，而不是「只挡 banned」那种加一个新状态就漏一次的写法；
/// ② 邀请码一抖两条兑现路（扫码与账密）同时断，且只断码不毁席位；
/// ③ 自改的名号是「用户意图」，补绑 B 站号与每次重登都不许把它盖掉（名与脸同进同退）；
/// ④ 公告撤回打戳不删行，成员端四条读路径（清单/未读/标读/一键标读）一起干净，且捐得回来。
/// </summary>
public sealed class MemberGovernanceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public MemberGovernanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-govern-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
        _gate = new MemberGate(_db);
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

    private static BilibiliIdentity Identity(long mid, string name = "小张") => new(mid, name, "https://face/y.jpg");

    private long CrownOwner(long mid = 9001L)
    {
        MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, "站长"), invite: null, "SESSDATA=owner");
        return _db.GetMemberByMid(mid)!.Id;
    }

    /// <summary>造一个素净席位（账密注册，无 mid），返回席位 id。</summary>
    private long AddPlainSeat(string username, string passwordHash = "hash-x")
    {
        var (id, _) = _db.CreateMemberInvite("测试席");
        Assert.NotNull(_db.ActivateMemberByRegistration(id, username, passwordHash));
        return id;
    }

    // ── 除名与召回 ────────────────────────────────────────────

    [Fact]
    public void 除名后扫码进不来()
    {
        CrownOwner(9001L);
        var (friendId, code) = _db.CreateMemberInvite("给朋友");
        Assert.NotNull(MemberEndpoints.AdmitMember(_db, _gate, Identity(2002L, "朋友"), invite: code, "SESSDATA=b").Token);

        _db.SetMemberStatus(friendId, MemberStatus.Exiled);

        // 判据必须是白名单：这里若写成 != Banned，除名的人扫一次码就走完重登分支拿回新票。
        var again = MemberEndpoints.AdmitMember(_db, _gate, Identity(2002L, "朋友"), invite: null, "SESSDATA=c");
        Assert.Null(again.Token);
        Assert.Equal(403, again.Status);
        Assert.Contains("除名", again.Error);
    }

    [Fact]
    public void 除名即刻作废手上的活票()
    {
        CrownOwner();
        var seat = AddPlainSeat("朋友甲");
        var (token, _) = _gate.IssueSession(seat);
        Assert.NotNull(_gate.ResolveMemberByToken(token));

        _db.SetMemberStatus(seat, MemberStatus.Exiled);

        // SetMemberStatus 里那条判据写成 status == Banned 时，这一格会红：30 天的票还活着，除名只除了新登录。
        Assert.Null(_gate.ResolveMemberByToken(token));
    }

    [Fact]
    public void 召回复原又能进门()
    {
        CrownOwner();
        var (seatId, code) = _db.CreateMemberInvite(null);
        Assert.NotNull(MemberEndpoints.AdmitMember(_db, _gate, Identity(2002L, "朋友"), invite: code, "SESSDATA=b").Token);

        _db.SetMemberStatus(seatId, MemberStatus.Exiled);
        Assert.Null(MemberEndpoints.AdmitMember(_db, _gate, Identity(2002L, "朋友"), invite: null, "SESSDATA=c").Token);

        _db.SetMemberStatus(seatId, MemberStatus.Active);
        Assert.NotNull(MemberEndpoints.AdmitMember(_db, _gate, Identity(2002L, "朋友"), invite: null, "SESSDATA=d").Token);
    }

    [Fact]
    public void 除名席的会话票新建即失效()
    {
        // 除名后就算有人手滑给他发一张票，验票那一头也不认（MemberGate.Resolve 只认 Active）。
        CrownOwner();
        var seat = AddPlainSeat("朋友乙");
        _db.SetMemberStatus(seat, MemberStatus.Exiled);
        var (token, _) = _gate.IssueSession(seat);
        Assert.Null(_gate.ResolveMemberByToken(token));
    }

    // ── 邀请码：事后查与作废 ──────────────────────────────────

    [Fact]
    public void 邀请码清单只列未兑现的()
    {
        CrownOwner();
        var (a, codeA) = _db.CreateMemberInvite("给阿明");
        var (_, codeB) = _db.CreateMemberInvite(null);

        var pending = _db.ListPendingInvites();
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, i => i.Code == codeA && i.Note == "给阿明");
        Assert.Contains(pending, i => i.Code == codeB && i.Note is null);

        // 兑现一枚（账密注册）后它从清单里消失：席位已转 active，不再是「还能进人的码」。
        Assert.NotNull(_db.ActivateMemberByRegistration(a, "aming", "hash-a"));
        pending = _db.ListPendingInvites();
        Assert.Single(pending);
        Assert.Equal(codeB, pending[0].Code);
    }

    [Fact]
    public void 撤销邀请码两条兑现路同时断()
    {
        CrownOwner();
        var (seatId, code) = _db.CreateMemberInvite("发错了");

        Assert.True(_db.RevokeMemberInvite(seatId));
        Assert.Null(_db.FindInvitedByCode(code));
        // 扫码与账密注册都只经 FindInvitedByCode 认码：这里 null 就等于两条路一起断，不必两边各改一遍。
        var stranger = MemberEndpoints.AdmitMember(_db, _gate, Identity(3003L, "生人"), invite: code, "SESSDATA=e");
        Assert.Null(stranger.Token);
        Assert.Contains("邀请码", stranger.Error);
    }

    [Fact]
    public void 撤销不毁席位也不许重复撤()
    {
        CrownOwner();
        var (seatId, _) = _db.CreateMemberInvite("发错了");
        Assert.True(_db.RevokeMemberInvite(seatId));

        // 席位行留着（note 还在）：想重新发一枚码给同一位朋友，座还在。
        var seat = _db.GetMemberById(seatId);
        Assert.NotNull(seat);
        Assert.Equal("发错了", seat!.Note);

        // 再撤一次不算事：码早就不在了。
        Assert.False(_db.RevokeMemberInvite(seatId));
        // 已兑现的席位也撤不动（它本来就没有码）。
        var plain = AddPlainSeat("阿丙");
        Assert.False(_db.RevokeMemberInvite(plain));
    }

    // ── 素净席位自改名号 ──────────────────────────────────────

    [Fact]
    public void 素净席位自改名号并在重登刷新后保住()
    {
        CrownOwner();
        var seat = AddPlainSeat("阿梨");
        Assert.True(_db.SetMemberNickname(seat, "阿梨酱"));
        Assert.Equal("阿梨酱", _db.GetMemberById(seat)!.Nickname);

        // 素净席位本就没有 mid，TouchMemberLogin（WHERE mid = $mid）碰不到它；这里拿同一张嘴扫一下，
        // 钉的是「改名不会被任何刷新路径抢走」，而不是只靠运气。
        _db.TouchMemberLogin(7007L, "别人的同名 B 站号", "https://face/b.jpg");
        Assert.Equal("阿梨酱", _db.GetMemberById(seat)!.Nickname);
    }

    [Fact]
    public void 绑了号的席位也改得动而且重登不盖()
    {
        // 锁真正吃重力的就是这条路：绑了 mid 的席位每次扫码重登都走 TouchMemberLogin，
        // COALESCE($name, nickname) 只挡 null 不挡「用户自己改过」——不锁就会「改了名、下次登录又变回去」。
        CrownOwner();
        var (seatId, code) = _db.CreateMemberInvite("给朋友");
        Assert.NotNull(MemberEndpoints.AdmitMember(_db, _gate, Identity(7008L, "B站原名"), invite: code, "SESSDATA=b").Token);
        Assert.Equal("B站原名", _db.GetMemberById(seatId)!.Nickname);

        Assert.True(_db.SetMemberNickname(seatId, "我想叫阿西"));
        _db.TouchMemberLogin(7008L, "B站改过的名", "https://face/c.jpg");
        Assert.Equal("我想叫阿西", _db.GetMemberById(seatId)!.Nickname);
    }

    [Fact]
    public void 没自改过的席位照旧跟B站走()
    {
        // 锁只认「用户自己改过」这一件事：没改过的席位，B 站昵称与头像照常刷进来，别把老行为改坏了。
        CrownOwner();
        var (seatId, code) = _db.CreateMemberInvite("给朋友");
        Assert.NotNull(MemberEndpoints.AdmitMember(_db, _gate, Identity(7009L, "B站昵称"), invite: code, "SESSDATA=b").Token);
        _db.TouchMemberLogin(7009L, "B站改过名", "https://face/e.jpg");
        var seat = _db.GetMemberById(seatId)!;
        Assert.Equal("B站改过名", seat.Nickname);
        Assert.Equal("https://face/e.jpg", seat.FaceUrl);
    }

    [Fact]
    public void 待邀席位不许提前挂名()
    {
        CrownOwner();
        var (invited, _) = _db.CreateMemberInvite("还没兑现");
        // 那还是张没认主的空席：本人还没到场，名字先不接（接了就是站长替别人起名）。
        Assert.False(_db.SetMemberNickname(invited, "影子名"));
        Assert.Null(_db.GetMemberById(invited)!.Nickname);
        // 查无此席也不报错不报成
        Assert.False(_db.SetMemberNickname(424242L, "幽灵名"));
    }

    // ── 公告按批撤回 ──────────────────────────────────────────

    [Fact]
    public void 公告撤回后成员端四条读路径一起干净且捐得回来()
    {
        CrownOwner();
        var readerA = AddPlainSeat("读者甲");
        var readerB = AddPlainSeat("读者乙");

        // 站长自己也是 active 席位，公告照发给他（BroadcastToActive 不区分角色）：三席全送达才是对的。
        var sent = _db.BroadcastToActive(MemberMail.KindAnnounce, "站长公告", "今晚停机半小时", batchId: "batch-1");
        Assert.Equal(3, sent);
        Assert.Single(_db.ListMessages(readerA, 50));
        Assert.Equal(1, _db.CountUnread(readerA));

        var batches = _db.ListAnnounceBatches(MemberMail.KindAnnounce);
        Assert.Single(batches);
        Assert.Equal("batch-1", batches[0].BatchId);
        Assert.Equal(3, batches[0].Sent);
        Assert.False(batches[0].Revoked);

        Assert.Equal(3, _db.RevokeAnnounceBatch("batch-1"));
        // 清单、未读铃铛、标读、一键标读四条读路径都带 revoked_at_utc IS NULL：撤回后成员端一片干净。
        Assert.Empty(_db.ListMessages(readerA, 50));
        Assert.Equal(0, _db.CountUnread(readerA));
        Assert.False(_db.MarkRead(1L, readerA));
        Assert.Equal(0, _db.MarkAllRead(readerB));
        Assert.True(_db.ListAnnounceBatches(MemberMail.KindAnnounce)[0].Revoked);

        // 反悔的反悔：捐回来，当初没读的还是没读。
        Assert.Equal(3, _db.RestoreAnnounceBatch("batch-1"));
        Assert.Single(_db.ListMessages(readerA, 50));
        Assert.Equal(1, _db.CountUnread(readerA));
    }

    [Fact]
    public void 撤回只影响这一批不殃及别批与别的信()
    {
        CrownOwner();
        var reader = AddPlainSeat("读者丙");
        _db.BroadcastToActive(MemberMail.KindAnnounce, "第一批", "正文一", batchId: "batch-a");
        _db.BroadcastToActive(MemberMail.KindAnnounce, "第二批", "正文二", batchId: "batch-b");
        Assert.Equal(2, _db.ListMessages(reader, 50).Count);

        Assert.Equal(2, _db.RevokeAnnounceBatch("batch-a"));
        var left = Assert.Single(_db.ListMessages(reader, 50));
        Assert.Equal("第二批", left.Title);
        // 撤回同一批第二次不算事（那批已经收回了）。
        Assert.Equal(0, _db.RevokeAnnounceBatch("batch-a"));
        // 没有批次号的历史公告（11-08 之前发的）列不出来，也不该被误撤。
        _db.SendToMember(reader, MemberMail.KindWelcome, "欢迎", "欢迎进站");
        Assert.Equal(0, _db.RevokeAnnounceBatch("batch-never-exists"));
        Assert.Equal(2, _db.ListMessages(reader, 50).Count);
    }

    [Fact]
    public void 到站播报与周报没批次号也不受影响()
    {
        // 它们调 BroadcastToActive 时不传 batchId（不是站长公告，本来就不该被撤回）：写入要照常。
        CrownOwner();
        var reader = AddPlainSeat("读者丁");
        var sent = _db.BroadcastToActive(MemberMail.KindArrival, "新货进栈", "《某片》到站了",
            msgType: "video_receipt", payloadJson: "{}", muteChannel: "arrival");
        // 站长本人也是 active 席位，而且是没静音的那一个（静音是席位自己关的，站长没关）。
        Assert.Equal(2, sent);
        var msg = Assert.Single(_db.ListMessages(reader, 50));
        Assert.Equal(MemberMail.KindArrival, msg.Kind);
        Assert.Empty(_db.ListAnnounceBatches(MemberMail.KindAnnounce));
    }

    private string InviteOf()
    {
        var (_, code) = _db.CreateMemberInvite(null);
        return code;
    }
}
