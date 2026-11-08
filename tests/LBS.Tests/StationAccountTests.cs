using System.Text.RegularExpressions;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// M5.4 站内账户体系：PBKDF2 铸钥与验钥、注册（凭码/无主继位/重名含大小写/坏名弱码）、
/// 账密登录的库侧原语、IP 失败黑板节流、改密与站长代重置、素净席位投稿闸门，
/// 以及 mid→member_id 回填迁移（KV 立碑守卫 + 幂等重跑）。
/// 端点 lambda 只皮一层，裁决全收在 ValidateCredentials / ResolveSeatForRegister /
/// SubmissionGate.Allows / IpFailBoard 这几张可直测的嘴里。
/// </summary>
public sealed class StationAccountTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public StationAccountTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-acct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
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
        }
    }

    private static BilibiliIdentity Identity(long mid, string name = "小张") => new(mid, name, "https://face/y.jpg");

    /// <summary>扫码收编一个 active 席位，返回站内席位 id。</summary>
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

    /// <summary>走账密注册全流程（与端点同一条嘴），返回新席位 id。有主时自动取码凭码进站。</summary>
    private long Register(string username, string password = "mima-8wei+")
    {
        Assert.Null(MemberAccountEndpoints.ValidateCredentials(username, password));
        Assert.Null(_db.GetMemberByUsername(username));
        string? code = null;
        if (_db.HasOwner())
        {
            (_, code) = _db.CreateMemberInvite(null);
        }

        var (seat, error) = MemberAccountEndpoints.ResolveSeatForRegister(_db, code);
        Assert.Null(error);
        var activated = _db.ActivateMemberByRegistration(seat!.Value, username, MemberPasswords.Hash(password));
        Assert.NotNull(activated);
        return activated!.Id;
    }

    // ── PBKDF2 铸钥 ──────────────────────────────────────────────

    [Fact]
    public void Hash_HasCanonicalShape_And_VerifyRoundtrips()
    {
        var hash = MemberPasswords.Hash("正确的马");
        Assert.Matches(new Regex(@"^pbkdf2\$210000\$[0-9a-f]{32}\$[0-9a-f]{64}$"), hash);
        // 同字不同盐：两次铸出的钥形不同而互认
        Assert.NotEqual(hash, MemberPasswords.Hash("正确的马"));
        Assert.True(MemberPasswords.Verify(hash, "正确的马"));
        Assert.False(MemberPasswords.Verify(hash, "错误的马"));
        Assert.False(MemberPasswords.Verify(hash, null));
        Assert.False(MemberPasswords.Verify(hash, ""));
        Assert.False(MemberPasswords.Verify(null, "正确的马"));
        // 串体被篡改（换盐/截 hash）一律拒认，且不炸
        Assert.False(MemberPasswords.Verify(hash[..^1], "正确的马"));
        Assert.False(MemberPasswords.Verify("pbkdf2$1000$deadbeef$beef", "正确的马"));
        Assert.False(MemberPasswords.Verify("plain-text-thing", "plain-text-thing"));
    }

    [Fact]
    public void MeetsPolicy_EightIsTheLine()
    {
        Assert.False(MemberPasswords.MeetsPolicy(null));
        Assert.False(MemberPasswords.MeetsPolicy(""));
        Assert.False(MemberPasswords.MeetsPolicy("1234567"));
        Assert.True(MemberPasswords.MeetsPolicy("12345678"));
    }

    // ── 注册 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("a")]              // 太短
    [InlineData("一二三四五六七八九十一二三四五六七八九十一二三四五六")] // 25 字超长
    [InlineData("bad name")]       // 空格
    [InlineData("bad.name")]       // 点号
    [InlineData("全角！name")]     // 全角叹号
    [InlineData("")]               // 空
    public void ValidateCredentials_RejectsBadNames(string username)
    {
        var error = MemberAccountEndpoints.ValidateCredentials(username, "mima-8wei+");
        Assert.NotNull(error);
        Assert.Equal(400, error!.Value.Status);
        Assert.Contains("用户名", error.Value.Message);
    }

    [Theory]
    [InlineData("ok_user", null)]
    [InlineData("ok_user", "1234567")]
    [InlineData("ok_user", "")]
    public void ValidateCredentials_RejectsWeakPasswords(string username, string? password)
    {
        var error = MemberAccountEndpoints.ValidateCredentials(username, password!);
        Assert.NotNull(error);
        Assert.Contains("8 位", error!.Value.Message);
    }

    [Theory]
    [InlineData("阿破")]
    [InlineData("_under")]
    [InlineData("A1")]
    [InlineData("Station_Name_99")]
    public void ValidateCredentials_AcceptsLegalNames(string username)
    {
        Assert.Null(MemberAccountEndpoints.ValidateCredentials(username, "mima-8wei+"));
    }

    [Fact]
    public void Register_WithoutOwner_FirstSeatCrownsOwner()
    {
        Assert.False(_db.HasOwner());
        var seatId = Register("开站第一人");

        var member = _db.GetMemberById(seatId)!;
        Assert.Equal(MemberRoles.Owner, member.Role);
        Assert.Equal(MemberStatus.Active, member.Status);
        Assert.Equal("开站第一人", member.Username);
        Assert.Null(member.Mid); // 素净席位：无 B 站号也成席
        Assert.True(_db.HasMemberPassword(seatId));
        Assert.True(_db.VerifyMemberPassword(seatId, "mima-8wei+"));
    }

    [Fact]
    public void Register_WithOwner_RequiresGoodCode()
    {
        AdmitSeat(9001, "站长"); // 扫码立主

        var (noCodeSeat, noCodeError) = MemberAccountEndpoints.ResolveSeatForRegister(_db, null);
        Assert.Null(noCodeSeat);
        Assert.Equal(403, noCodeError!.Value.Status);
        Assert.Contains("邀请码", noCodeError.Value.Message);

        var (badSeat, badError) = MemberAccountEndpoints.ResolveSeatForRegister(_db, "nosuchcode");
        Assert.Null(badSeat);
        Assert.Equal(403, badError!.Value.Status);

        // 好码：兑现落席
        var (_, code) = _db.CreateMemberInvite("访客");
        var (seat, error) = MemberAccountEndpoints.ResolveSeatForRegister(_db, $"  {code.ToLowerInvariant()}  ");
        Assert.Null(error);
        Assert.NotNull(seat);

        var activated = _db.ActivateMemberByRegistration(seat!.Value, "凭码来客", MemberPasswords.Hash("mima-8wei+"))!;
        Assert.Equal(MemberStatus.Active, activated.Status);
        Assert.Null(activated.Mid);
        // 码用过即废：同码再兑不落席
        var (againSeat, againError) = MemberAccountEndpoints.ResolveSeatForRegister(_db, code);
        Assert.Equal(403, againError!.Value.Status);
        Assert.Null(againSeat);
    }

    [Fact]
    public void Register_Username_IsCaseInsensitiveUnique()
    {
        Register("Guard_Tian_01");

        Assert.NotNull(_db.GetMemberByUsername("guard_tian_01")); // 大小写换个遍也认得出是同一把名
        var (seat, error) = MemberAccountEndpoints.ResolveSeatForRegister(_db, null); // 有主无码 → 403
        Assert.Equal(403, error!.Value.Status);

        // 直接绕开门禁去撞库：NOCASE 唯一索引亲自动手（返 null 交上层报「被人占了」）
        AdmitSeat(9002, "垫背站长");
        var (dupSeat, _) = _db.CreateMemberInvite(null);
        Assert.Null(_db.ActivateMemberByRegistration(dupSeat, "GUARD_TIAN_01", MemberPasswords.Hash("mima-8wei+")));
    }

    // ── 账密登录 ────────────────────────────────────────────────

    [Fact]
    public void LoginPrimitives_RightWrongBannedAndPasswordless()
    {
        AdmitSeat(9101, "站长");
        var seat = Register("查票的");

        Assert.NotNull(_db.GetMemberByUsername("查票的"));
        Assert.True(_db.VerifyMemberPassword(seat, "mima-8wei+"));
        Assert.False(_db.VerifyMemberPassword(seat, "错一位都不行"));
        Assert.Null(_db.GetMemberByUsername("没这人"));

        // 被封的席位：登录裁决层（端点 lambda）看的就是这个状态
        _db.SetMemberStatus(seat, MemberStatus.Banned);
        var fallen = _db.GetMemberByUsername("查票的")!;
        Assert.NotEqual(MemberStatus.Active, fallen.Status);
        _db.SetMemberStatus(seat, MemberStatus.Active);
        Assert.Equal(MemberStatus.Active, _db.GetMemberByUsername("查票的")!.Status);

        // 纯扫码客没设过密码：HasMemberPassword 关 → 密码门不开
        var scanSeat = AdmitSeat(9102, "扫码客");
        Assert.False(_db.HasMemberPassword(scanSeat));
        Assert.False(_db.VerifyMemberPassword(scanSeat, ""));
        Assert.False(_db.VerifyMemberPassword(scanSeat, "anything"));
    }

    /// <summary>
    /// 假表：时间只认测试推它走。黑板这类靠时钟的东西拿 Thread.Sleep 赌调度，机器一忙就自己抖
    /// （同窗内连错两笔，中间被调度抢走 40ms 就把累加判成重开）。ApiThrottleTests 的 ManualTimeProvider 先例同形。
    /// </summary>
    private sealed class BoardClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static MemberAccountEndpoints.IpFailBoard Board(BoardClock clock, TimeSpan window, params TimeSpan[] tiers) =>
        new(window, tiers, clock);

    [Fact]
    public void IpFailBoard_阶梯冷却_错一次就上锁且只封作案IP()
    {
        var clock = new BoardClock();
        var board = Board(clock, TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

        Assert.False(board.Snapshot("1.2.3.4").Blocked);
        board.RegisterFailure("1.2.3.4");

        var first = board.Snapshot("1.2.3.4");
        Assert.True(first.Blocked); // 错一次就上锁（扫码不经此闸，站长扫得进来）
        Assert.Equal(1, first.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), first.RetryAfter);

        // 冷却中再撞门：既不延长刑期，也不把计数吹大（否则脚本能把封禁无限往后推）
        board.RegisterFailure("1.2.3.4");
        var again = board.Snapshot("1.2.3.4");
        Assert.Equal(1, again.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), again.RetryAfter);

        // 只封作案的 IP，别家照进
        Assert.False(board.Snapshot("5.6.7.8").Blocked);
    }

    [Fact]
    public void IpFailBoard_阶梯随失败次数加长到封顶档()
    {
        var clock = new BoardClock();
        var board = Board(clock, TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

        board.RegisterFailure("slow");
        Assert.Equal(TimeSpan.FromMinutes(1), board.Snapshot("slow").RetryAfter);

        clock.Advance(TimeSpan.FromMinutes(1)); // 第一档过完，才能吃第二枪
        Assert.False(board.Snapshot("slow").Blocked);
        board.RegisterFailure("slow");
        Assert.Equal(2, board.Snapshot("slow").Count);
        Assert.Equal(TimeSpan.FromMinutes(5), board.Snapshot("slow").RetryAfter);

        clock.Advance(TimeSpan.FromMinutes(5)); // 第二档过完
        board.RegisterFailure("slow");
        Assert.Equal(3, board.Snapshot("slow").Count);
        Assert.Equal(TimeSpan.FromMinutes(30), board.Snapshot("slow").RetryAfter);

        clock.Advance(TimeSpan.FromMinutes(30)); // 第三档过完：第四枪起封顶，不再往上加
        board.RegisterFailure("slow");
        var fourth = board.Snapshot("slow");
        Assert.Equal(4, fourth.Count); // 计数照加
        Assert.Equal(TimeSpan.FromMinutes(30), fourth.RetryAfter); // 刑期封顶
    }

    [Fact]
    public void IpFailBoard_窗口一过账目作废_成功登录销账()
    {
        var clock = new BoardClock();
        var board = Board(clock, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));

        board.RegisterFailure("9.9.9.9");
        Assert.Equal(1, board.Snapshot("9.9.9.9").Count);

        // 锁中撞门：不计次也不加刑（否则脚本能把封禁无限往后推，也能靠堆计数跳档）。
        // 想上阶梯就得等锁过完再吃下一枪——阶梯的长法钉在「阶梯随失败次数加长到封顶档」那格。
        board.RegisterFailure("9.9.9.9");
        Assert.Equal(1, board.Snapshot("9.9.9.9").Count);

        clock.Advance(TimeSpan.FromMinutes(1)); // 锁过完，第二枪才吃得上，同窗内才累加
        board.RegisterFailure("9.9.9.9");
        Assert.Equal(2, board.Snapshot("9.9.9.9").Count);

        board.Clear("9.9.9.9"); // 登进去了就销账
        Assert.False(board.Snapshot("9.9.9.9").Blocked);
        Assert.Equal(0, board.Snapshot("9.9.9.9").Count);

        board.RegisterFailure("9.9.9.9");
        Assert.Equal(1, board.Snapshot("9.9.9.9").Count); // 销账后从 1 重开

        clock.Advance(TimeSpan.FromMinutes(11)); // 窗口一过（要 strictly 超出窗口，不是恰平），旧账整段作废
        var fresh = board.Snapshot("9.9.9.9");
        Assert.False(fresh.Blocked);
        Assert.Equal(0, fresh.Count);

        board.RegisterFailure("9.9.9.9");
        Assert.Equal(1, board.Snapshot("9.9.9.9").Count); // 不背着上一轮的旧账
    }

    // ── 改密 / 代重置 ───────────────────────────────────────────

    [Fact]
    public void ChangePassword_FirstTimeNeedsNoOld_ThenOldMustMatch()
    {
        AdmitSeat(9201, "站长");
        var seat = Register("换钥人");

        // 首设（素净席位注册即有密码，这里模拟扫码客从无到有）：先摘干净
        _db.SetMemberPassword(seat, "");
        Assert.False(_db.HasMemberPassword(seat));

        // 端点条件：HasMemberPassword 假 → 不问旧钥直接设
        _db.SetMemberPassword(seat, MemberPasswords.Hash("新钥匙-8位+"));
        Assert.True(_db.VerifyMemberPassword(seat, "新钥匙-8位+"));
        Assert.False(_db.VerifyMemberPassword(seat, "mima-8wei+"));

        // 有了钥匙再换：旧钥验不过就免谈（端点条件直译）
        Assert.True(_db.HasMemberPassword(seat));
        Assert.False(_db.VerifyMemberPassword(seat, "旧钥记岔了"));
        Assert.True(_db.VerifyMemberPassword(seat, "新钥匙-8位+"));
    }

    [Fact]
    public void AdminReset_OverwritesAndUnlocksLogin()
    {
        AdmitSeat(9301, "站长");
        var seat = Register("忘钥人");
        Assert.True(_db.VerifyMemberPassword(seat, "mima-8wei+"));

        // 站长代重置（admin-reset 端点就这一行 SetMemberPassword）
        _db.SetMemberPassword(seat, MemberPasswords.Hash("站长新给的-8位"));
        Assert.False(_db.VerifyMemberPassword(seat, "mima-8wei+"));
        Assert.True(_db.VerifyMemberPassword(seat, "站长新给的-8位"));
    }

    // ── 投稿闸门 ────────────────────────────────────────────────

    [Fact]
    public void SubmissionGate_PlainSeatBlocked_UntilAccountBound_OwnerExempt()
    {
        var ownerSeat = AdmitSeat(9401, "站长"); // 站长不绑投稿号也畅通
        Assert.True(SubmissionGate.Allows(_db, _db.GetMemberById(ownerSeat)!));

        // 扫码收编自带名下号，素净席位只能由账密注册产生
        var plain = Register("素净客");
        var plainMember = _db.GetMemberById(plain)!;
        Assert.False(SubmissionGate.Allows(_db, plainMember)); // 素净席位：投稿闸落着

        _db.UpsertMemberAccount(plain, 94020, "投稿小号", null, "SESSDATA=alt");
        Assert.True(SubmissionGate.Allows(_db, _db.GetMemberById(plain)!)); // 绑了号就放行

        // 号全标失效 → 闸再落回
        foreach (var account in _db.ListMemberAccounts(plain))
        {
            _db.MarkMemberAccountExpired(plain, account.AccountMid, true);
        }

        Assert.False(SubmissionGate.Allows(_db, _db.GetMemberById(plain)!));
    }

    // ── 迁移回填 ────────────────────────────────────────────────

    [Fact]
    public void Migration_BackfillsMidToMemberId_GuardedAndIdempotent()
    {
        // 造 legacy 现场：直接写旧列（to_mid/from_mid/actor_mid/submitter_mid），新列留 NULL。
        var (seatA, seatB) = SeedLegacyWorld();

        // 摘碑重跑迁移（模拟老库首启）
        _db.RemoveSetting("m54-migrated");
        _db.Initialize();

        using (var conn = OpenRaw())
        {
            Assert.Equal(seatA, Scalar(conn,
                "SELECT to_member_id FROM messages WHERE to_mid = 9501 AND kind = 'legacy';"));
            Assert.Equal(seatA, Scalar(conn,
                "SELECT from_member_id FROM dm_messages WHERE body = '老私信' LIMIT 1;"));
            Assert.Equal(seatB, Scalar(conn,
                "SELECT to_member_id FROM dm_messages WHERE body = '老私信' LIMIT 1;"));
            Assert.Equal(seatB, Scalar(conn,
                "SELECT submitter_member_id FROM videos WHERE bvid = 'BVlegacy' LIMIT 1;"));
            Assert.Equal(seatA, Scalar(conn,
                "SELECT actor_member_id FROM events WHERE event = 'legacy.act' LIMIT 1;"));
            // 访客事件 actor_mid=0 不是任何人：不挂身份
            Assert.Equal(DBNull.Value, Scalar(conn,
                "SELECT actor_member_id FROM events WHERE event = 'legacy.guest' LIMIT 1;"));
        }

        // 幂等重跑：碑在 → 回填不再动刀（把已回填的行改脏，重跑不改回也不重复）
        using (var conn = OpenRaw())
        {
            Exec(conn, "UPDATE messages SET to_member_id = -7 WHERE to_mid = 9501 AND kind = 'legacy';");
        }

        _db.Initialize();
        using (var check = OpenRaw())
        {
            Assert.Equal(-7L, Scalar(check,
                "SELECT to_member_id FROM messages WHERE to_mid = 9501 AND kind = 'legacy';"));
        }
    }

    /// <summary>两席位 + 各表 legacy 行；返回两位席位的站内 id（回填该指过去的人）。</summary>
    private (long, long) SeedLegacyWorld()
    {
        var seatA = AdmitSeat(9501, "老甲");
        var seatB = AdmitSeat(9502, "老乙");
        // 把 M5.4 自动回填的成果擦掉、碑摘掉，还原「老库未迁」现场
        _db.RemoveSetting("m54-migrated");
        using var conn = OpenRaw();
        Exec(conn, "UPDATE messages SET to_member_id = NULL WHERE to_mid = 9501;");
        Exec(conn, "UPDATE dm_messages SET from_member_id = NULL, to_member_id = NULL;");
        Exec(conn, "UPDATE videos SET submitter_member_id = NULL;");
        Exec(conn, "UPDATE events SET actor_member_id = NULL;");

        Exec(conn, """
            INSERT INTO messages (to_mid, from_mid, kind, title, body, created_at_utc)
            VALUES (9501, NULL, 'legacy', '老公告', '旧时的信', '2025-01-01T00:00:00+00:00');
            """);
        Exec(conn, """
            INSERT INTO dm_messages (from_mid, to_mid, body, created_at_utc)
            VALUES (9501, 9502, '老私信', '2025-01-01T00:00:00+00:00');
            """);
        Exec(conn, """
            INSERT INTO videos (url, bvid, title, status, created_at_utc, submitter_mid)
            VALUES ('https://biv/1', 'BVlegacy', '老投稿', 'arrived', '2025-01-01T00:00:00+00:00', 9502);
            """);
        Exec(conn, """
            INSERT INTO events (ts_utc, actor_type, actor_mid, event)
            VALUES ('2025-01-01T00:00:00+00:00', 'member', 9501, 'legacy.act');
            INSERT INTO events (ts_utc, actor_type, actor_mid, event)
            VALUES ('2025-01-01T00:00:01+00:00', 'guest', 0, 'legacy.guest');
            """);
        return (seatA, seatB);
    }

    private SqliteConnection OpenRaw()
    {
        var conn = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
