using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// 准入逻辑（扫码成功那一刻的分流）：生面孔无码拒收、凭码认主、
/// 老席位重登换 cookie、封禁号拒之门外。
/// </summary>
public class MemberAdmissionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public MemberAdmissionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-admit-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void StrangerWithoutInvite_IsRejected()
    {
        var outcome = MemberEndpoints.AdmitMember(_db, _gate, Identity(1001L), invite: null, "SESSDATA=a");

        Assert.Null(outcome.Token);
        Assert.Equal(403, outcome.Status);
        Assert.Contains("邀请码", outcome.Error);
        Assert.Null(_db.GetMemberByMid(1001L));
    }

    [Fact]
    public void StrangerWithInvite_BindsSeatAndGetsSession()
    {
        var (id, code) = _db.CreateMemberInvite("小张的席位");

        var outcome = MemberEndpoints.AdmitMember(_db, _gate, Identity(1002L), code, "SESSDATA=b; bili_jct=j");

        Assert.NotNull(outcome.Token);
        Assert.Null(outcome.Error);
        var member = _db.GetMemberByMid(1002L)!;
        Assert.Equal(MemberStatus.Active, member.Status);
        Assert.Equal("小张", member.Nickname);

        // 会话真能验回来
        var account = Assert.Single(_db.ListMemberAccounts(member.Id));
        Assert.Equal(1002L, account.AccountMid);
        Assert.True(account.IsPrimary);
        Assert.Equal(member.Id, _gate.ResolveMemberByToken(outcome.Token!)!.Id);
    }

    [Fact]
    public void ReturningMember_ReloginsAndRefreshesCookie()
    {
        var (_, code) = _db.CreateMemberInvite(null);
        MemberEndpoints.AdmitMember(_db, _gate, Identity(1003L, "旧名"), code, "SESSDATA=old");
        var member = _db.GetMemberByMid(1003L)!;

        var again = MemberEndpoints.AdmitMember(_db, _gate, Identity(1003L, "新名"), invite: null, "SESSDATA=new");

        Assert.NotNull(again.Token); // 老席位重登不再要邀请码
        var account = Assert.Single(_db.ListMemberAccounts(member.Id)); // 同号不换行
        Assert.Equal("SESSDATA=new", _db.PickMemberAccountForDownload(member.Id)!.Cookie);
        Assert.Equal("新名", _db.GetMemberByMid(1003L)!.Nickname);
    }

    [Fact]
    public void BannedMid_IsTurnedAway()
    {
        var (_, code) = _db.CreateMemberInvite(null);
        MemberEndpoints.AdmitMember(_db, _gate, Identity(1004L), code, "SESSDATA=c");
        var member = _db.GetMemberByMid(1004L)!;
        _db.SetMemberStatus(member.Id, MemberStatus.Banned);

        var outcome = MemberEndpoints.AdmitMember(_db, _gate, Identity(1004L), invite: null, "SESSDATA=d");

        Assert.Null(outcome.Token);
        Assert.Equal(403, outcome.Status);
    }

    [Fact]
    public void FirstAdmittedMember_IsCrownedOwner_AndOthersArePlain()
    {
        // 头名即站长：第一位认主者自动加冕，后来的循规蹈矩当用户
        var (_, code) = _db.CreateMemberInvite(null);
        MemberEndpoints.AdmitMember(_db, _gate, Identity(2001L, "建基者"), code, "SESSDATA=a");
        Assert.True(_db.HasOwner());
        Assert.Equal(MemberRoles.Owner, _db.GetMemberByMid(2001L)!.Role);

        var (_, code2) = _db.CreateMemberInvite(null);
        MemberEndpoints.AdmitMember(_db, _gate, Identity(2002L, "后来人"), code2, "SESSDATA=b");
        Assert.Equal(MemberRoles.Member, _db.GetMemberByMid(2002L)!.Role);
    }

    [Fact]
    public void DeadInviteCode_IsRejected()
    {
        _db.CreateMemberInvite("用完的码");
        var outcome = MemberEndpoints.AdmitMember(_db, _gate, Identity(1005L), "NOSUCHCODE", "SESSDATA=e");

        Assert.Null(outcome.Token);
        Assert.Equal(403, outcome.Status);
    }
}
