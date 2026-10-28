using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace LBS.Tests;

/// <summary>
/// 站级登录态退役之后的取号规矩：全站唯一一处「不是自己的号办自己的事」就是采集号
/// （评论/弹幕归档、UP 名片、动态、收藏导入、编码加档），它优先取站长席位的活号，
/// 其次借任一活跳席位的号；号池空了必须当场判无号，绝不把匿名请求发去 B 站冒充数据。
/// 投稿链不吃采集号（那条链只认投稿人自己的号），两路各走各的，这里也一并钉住。
/// </summary>
public class CollectAccountTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;
    private readonly LbsAccountRouter _router = new();

    public CollectAccountTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-collect-" + Guid.NewGuid().ToString("N"));
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
            // 临时目录删不掉不碍事
        }
    }

    private static BilibiliIdentity Identity(long mid, string name) => new(mid, name, "https://face/x.jpg");

    /// <summary>无主之站首扫免码，第一个扫码进来的就是站长。</summary>
    private long CrownOwner(long mid = 1001L)
    {
        MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, "站长"), invite: null, "SESSDATA=owner");
        return _db.GetMemberByMid(mid)!.Id;
    }

    private long AdmitMemberWithAccount(long mid)
    {
        var (_, code) = _db.CreateMemberInvite("测试席");
        MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, "成员" + mid), invite: code, $"SESSDATA={mid}");
        return _db.GetMemberByMid(mid)!.Id;
    }

    // ── 取号规矩 ────────────────────────────────────────────────────────────

    [Fact]
    public void PickAccountForCollect_PrefersOwnerSeat()
    {
        var owner = CrownOwner();
        var member = AdmitMemberWithAccount(2002L);

        var account = _db.PickAccountForCollect();

        Assert.NotNull(account);
        Assert.Equal(owner, account!.MemberId);
        Assert.Equal(1001L, account.AccountMid);
        Assert.Equal("SESSDATA=owner", account.Cookie); // 站里的活先拿站长自己的号扛，别先动成员的
        Assert.NotEqual(member, account.MemberId);
    }

    [Fact]
    public void PickAccountForCollect_LendsOtherSeatWhenOwnerAccountExpired()
    {
        var owner = CrownOwner();
        var member = AdmitMemberWithAccount(2002L);
        _db.MarkMemberAccountExpired(owner, 1001L, expired: true);

        var account = _db.PickAccountForCollect();

        Assert.NotNull(account);
        Assert.Equal(member, account!.MemberId); // 站长号失效了就借成员的活号，采集不断档
        Assert.Equal("SESSDATA=2002", account.Cookie);
    }

    [Fact]
    public void PickAccountForCollect_IgnoresEmptyPoolAndBannedSeat()
    {
        var owner = CrownOwner();
        var member = AdmitMemberWithAccount(2002L);
        _db.MarkMemberAccountExpired(owner, 1001L, expired: true);
        _db.MarkMemberAccountExpired(member, 2002L, expired: true);

        Assert.Null(_db.PickAccountForCollect()); // 号池全失效 → 无号，不许拿死号硬上

        _db.UpsertMemberAccount(member, 2002L, "复活", null, "SESSDATA=revived");
        Assert.NotNull(_db.PickAccountForCollect()); // 重新扫码自动摘旗，采集自己会恢复

        _db.SetMemberStatus(member, MemberStatus.Banned);
        Assert.Null(_db.PickAccountForCollect()); // 席位封了，它的号也不该替站里干活
    }

    [Fact]
    public void PickAccountForCollect_PrefersPrimaryWithinOwnerSeat()
    {
        var owner = CrownOwner();
        _db.UpsertMemberAccount(owner, 1002L, "站长的二号", null, "SESSDATA=owner-second");
        _db.SetMemberAccountPrimary(_db.ListMemberAccounts(owner).First(a => a.AccountMid == 1002L).Id);

        var account = _db.PickAccountForCollect();

        Assert.Equal("SESSDATA=owner-second", account!.Cookie); // 同席位多号时认主力，站长能自己挑
    }

    [Fact]
    public void AdminStats_CountsOnlyLiveAccountsOnActiveSeats()
    {
        CrownOwner();
        var member = AdmitMemberWithAccount(2002L);
        AdmitMemberWithAccount(2003L);

        Assert.Equal(3, _db.AdminStats().LiveAccounts);

        _db.MarkMemberAccountExpired(_db.GetMemberByMid(2003L)!.Id, 2003L, expired: true);
        _db.SetMemberStatus(member, MemberStatus.Banned);

        // 副号退役后这个位置报的是「成员活号」：失效的与封席的都不得算进来（归零就该在柜台标灰）
        Assert.Equal(1, _db.AdminStats().LiveAccounts);
    }

    // ── 采集策略与调用链 ──────────────────────────────────────────────────────

    [Fact]
    public async Task CollectPolicy_PinsCollectAccountOnTheCallChain()
    {
        CrownOwner();
        var policy = new LbsCollectPolicy(_db, _router, NullLogger<LbsCollectPolicy>.Instance);

        var seen = await policy.RunForVideoAsync(1L, () => Task.FromResult(new
        {
            Cookie = _router.CurrentCookie,
            Use = _router.CurrentAccount?.Use,
            Mid = _router.CurrentAccount?.AccountMid,
        }));

        Assert.Equal("SESSDATA=owner", seen.Cookie);
        Assert.Equal(LbsAccountUse.Collect, seen.Use);
        Assert.Equal(1001L, seen.Mid);
        Assert.Null(_router.CurrentCookie); // 出了上下文就清空，别把号漏给下一条链
    }

    [Fact]
    public async Task CollectPolicy_WithoutAnyAccount_RefusesAndStampsStall()
    {
        var policy = new LbsCollectPolicy(_db, _router, NullLogger<LbsCollectPolicy>.Instance);
        Assert.False(policy.HasAccount());

        var ex = await Assert.ThrowsAsync<LbsNoAccountException>(() => policy.RunForActivityAsync(() => Task.FromResult(1)));

        Assert.Contains("采集号", ex.Message); // 话要写给站长看：去哪儿绑号能恢复
        Assert.NotNull(policy.LastNoAccountUtc); // 柜台靠这个报「停摆多久了」
    }

    [Fact]
    public async Task CollectPolicy_BeginScope_KeepsSameAccountForWholeRound()
    {
        CrownOwner();
        var policy = new LbsCollectPolicy(_db, _router, NullLogger<LbsCollectPolicy>.Instance);

        using (policy.BeginForVideo(7L))
        {
            Assert.Equal("SESSDATA=owner", _router.CurrentCookie);
            // 同一轮里成员改了号也不改口：一趟换号容易被 B 站当成漂移
            _db.UpsertMemberAccount(_db.GetMemberByMid(1001L)!.Id, 1099L, "中途插进来的号", null, "SESSDATA=late");
            Assert.Equal("SESSDATA=owner", _router.CurrentCookie);
        }

        Assert.Null(_router.CurrentCookie);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MemberScope_KeepsSubmitterApartFromCollect()
    {
        CrownOwner();
        var policy = new LbsCollectPolicy(_db, _router, NullLogger<LbsCollectPolicy>.Instance);
        var submitter = AdmitMemberWithAccount(2002L);
        var cred = _db.PickMemberAccountForDownload(submitter)!;

        // 投稿链走投稿人自己的号，用途标必须与采集号分开（日志与状态栏据此报「谁的号在干活」）
        var insideMember = await _router.RunAsMemberAsync(cred.Cookie, cred.AccountMid,
            () => Task.FromResult(new { _router.CurrentCookie, Use = _router.CurrentAccount?.Use }));
        Assert.Equal(cred.Cookie, insideMember.CurrentCookie);
        Assert.Equal(LbsAccountUse.Submitter, insideMember.Use);

        var insideCollect = await policy.RunForVideoAsync(1L,
            () => Task.FromResult(new { _router.CurrentCookie, Use = _router.CurrentAccount?.Use }));
        Assert.Equal("SESSDATA=owner", insideCollect.CurrentCookie);
        Assert.Equal(LbsAccountUse.Collect, insideCollect.Use);
    }

    [Fact]
    public void RoutedCookieProvider_WithoutAccountContext_RefusesAnonymous()
    {
        var provider = new LbsRoutedCookieProvider(_router);

        // 站级主号单槽已拆：链上没号就是没号，绝不能悄悄发匿名请求出去
        Assert.Throws<LbsNoAccountException>(() => provider.GetCookieHeader());
    }
}
