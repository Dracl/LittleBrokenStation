using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>
/// M5.1 地基：成员席位（邀请制流转）、账号池（主力轮转与失效旗）、
/// 会话（发/验/吊销）、事件台账（批量落库、过滤查询、记录器冲刷）。
/// </summary>
public class StationMemberEventTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public StationMemberEventTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-member-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
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
            // 临时目录删不掉不碍事，交给系统清
        }
    }

    // ── 席位与邀请码 ────────────────────────────────────────────────────────

    [Fact]
    public void InviteLifecycle_BindTurnsActiveAndBurnsCode()
    {
        var (id, code) = _db.CreateMemberInvite("小王的席位");

        // 邀请码认主：大小写空格都宽容
        var invited = _db.FindInvitedByCode("  " + code.ToLowerInvariant() + " ");
        Assert.NotNull(invited);
        Assert.Equal(MemberStatus.Invited, invited!.Status);
        Assert.Null(invited.Mid);

        var bound = _db.BindMember(id, 12345L, "小王", "https://face/x.jpg");
        Assert.NotNull(bound);
        Assert.Equal(MemberStatus.Active, bound!.Status);
        Assert.Equal(12345L, bound.Mid);
        Assert.Equal("小王", bound.Nickname);
        Assert.NotNull(bound.BoundAtUtc);

        // 码是一次性的：兑现后就作废
        Assert.Null(_db.FindInvitedByCode(code));
    }

    [Fact]
    public void Bind_ExistingMid_YieldsToRelogin()
    {
        var (firstId, _) = _db.CreateMemberInvite(null);
        _db.BindMember(firstId, 777L, "先来", null);

        var (secondId, _) = _db.CreateMemberInvite("想抢同一个 B 站号");
        var bound = _db.BindMember(secondId, 777L, "后到", null);

        // mid 已被占：绑定失败交上层走重登，不制造双席位
        Assert.Null(bound);
        Assert.Null(_db.GetMemberById(secondId)!.Mid);
    }

    [Fact]
    public void Ban_RevokeSessionsAtOnce()
    {
        var (id, code) = _db.CreateMemberInvite(null);
        _db.BindMember(id, 888L, "将被封的", null);
        _db.IssueMemberSession(id, "token-hash-a", DateTimeOffset.UtcNow.AddDays(30));
        Assert.NotNull(_db.ResolveMemberSession("token-hash-a"));

        _db.SetMemberStatus(id, MemberStatus.Banned);

        Assert.Null(_db.ResolveMemberSession("token-hash-a"));
        Assert.Equal(MemberStatus.Banned, _db.GetMemberById(id)!.Status);
    }

    [Fact]
    public void TouchLogin_RefreshesButKeepsNulls()
    {
        var (id, _) = _db.CreateMemberInvite(null);
        _db.BindMember(id, 999L, "原名", "face-old");
        var beforeLogin = _db.GetMemberByMid(999L)!.LastLoginAtUtc;

        _db.TouchMemberLogin(999L, null, "face-new");

        var after = _db.GetMemberByMid(999L)!;
        Assert.Equal("原名", after.Nickname); // 传 null 保旧名
        Assert.Equal("face-new", after.FaceUrl);
        Assert.True(string.CompareOrdinal(after.LastLoginAtUtc, beforeLogin) >= 0);
    }

    [Fact]
    public void DeleteMember_CascadesAccountsAndSessions()
    {
        var (id, _) = _db.CreateMemberInvite(null);
        _db.BindMember(id, 555L, "删掉的", null);
        _db.UpsertMemberAccount(id, 555L, "删掉的", null, "SESSDATA=abc");
        _db.IssueMemberSession(id, "tok-del", DateTimeOffset.UtcNow.AddDays(1));

        _db.DeleteMember(id);

        Assert.Null(_db.GetMemberById(id));
        Assert.Empty(_db.ListMemberAccounts(id));
        Assert.Null(_db.ResolveMemberSession("tok-del"));
    }

    // ── 账号池 ──────────────────────────────────────────────────────────────

    [Fact]
    public void AccountPool_FirstAccountBecomesPrimary_AndSurvivesExpiry()
    {
        var (id, _) = _db.CreateMemberInvite(null);

        _db.UpsertMemberAccount(id, 101L, "主力号", null, "SESSDATA=main");
        _db.UpsertMemberAccount(id, 102L, "备用号", null, "SESSDATA=backup");

        var accounts = _db.ListMemberAccounts(id);
        Assert.Equal(2, accounts.Count);
        Assert.True(accounts.Single(a => a.AccountMid == 101L).IsPrimary);
        Assert.False(accounts.Single(a => a.AccountMid == 102L).IsPrimary);

        // 主力失效：旗子立起、主力位让贤，取号退到备用
        _db.MarkMemberAccountExpired(id, 101L, true);
        var picked = _db.PickMemberAccountForDownload(id);
        Assert.NotNull(picked);
        Assert.Equal(102L, picked!.AccountMid);
        Assert.Equal("SESSDATA=backup", picked.Cookie);

        // 全部失效：取不到号，交调用方回落
        _db.MarkMemberAccountExpired(id, 102L, true);
        Assert.Null(_db.PickMemberAccountForDownload(id));
    }

    [Fact]
    public void ReLogin_RefreshesCookieAndClearsExpiryFlag()
    {
        var (id, _) = _db.CreateMemberInvite(null);
        _db.UpsertMemberAccount(id, 201L, "续命的", null, "SESSDATA=old");
        _db.MarkMemberAccountExpired(id, 201L, true);

        _db.UpsertMemberAccount(id, 201L, null, null, "SESSDATA=new");

        var account = Assert.Single(_db.ListMemberAccounts(id));
        Assert.False(account.Expired);
        Assert.Equal("续命的", account.AccountName); // 名字传 null 保旧
        Assert.Equal("SESSDATA=new", _db.PickMemberAccountForDownload(id)!.Cookie);
    }

    [Fact]
    public void SetPrimary_SwapsWithinPoolOnly()
    {
        var (alice, _) = _db.CreateMemberInvite(null);
        var (bob, _) = _db.CreateMemberInvite(null);
        _db.UpsertMemberAccount(alice, 301L, null, null, "a1");
        _db.UpsertMemberAccount(bob, 401L, null, null, "b1");
        _db.UpsertMemberAccount(alice, 302L, null, null, "a2");

        var second = _db.ListMemberAccounts(alice).Single(a => a.AccountMid == 302L);
        _db.SetMemberAccountPrimary(second.Id);

        var after = _db.ListMemberAccounts(alice);
        Assert.True(after.Single(a => a.AccountMid == 302L).IsPrimary);
        Assert.False(after.Single(a => a.AccountMid == 301L).IsPrimary);
        // 别人的主力不受牵连
        Assert.True(_db.ListMemberAccounts(bob).Single().IsPrimary);
    }

    [Fact]
    public void PickAccount_EmptyPool_ReturnsNull()
    {
        var (id, _) = _db.CreateMemberInvite(null);
        Assert.Null(_db.PickMemberAccountForDownload(id));
    }

    // ── 会话 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Session_RoundtripAndExpiry()
    {
        var (id, _) = _db.CreateMemberInvite(null);
        _db.IssueMemberSession(id, "tok-live", DateTimeOffset.UtcNow.AddDays(30));
        _db.IssueMemberSession(id, "tok-dead", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(id, _db.ResolveMemberSession("tok-live"));
        Assert.Null(_db.ResolveMemberSession("tok-dead"));
        Assert.Null(_db.ResolveMemberSession("tok-unknown"));
    }

    // ── 事件台账 ────────────────────────────────────────────────────────────

    [Fact]
    public void Events_BatchInsertAndFilteredQueries()
    {
        var drafts = new List<EventDraft>();
        for (var i = 0; i < 300; i++)
        {
            var isWatch = i % 3 == 0;
            drafts.Add(new EventDraft(
                i % 7 == 0 ? EventActors.Guest : EventActors.Member,
                i % 7 == 0 ? null : 42L,
                isWatch ? EventNames.PlaybackOpen : EventNames.SearchQuery,
                isWatch ? "video" : "query",
                isWatch ? $"BV{i}" : "idm",
                null, "1.2.3.4",
                DateTimeOffset.UtcNow.AddMinutes(i).ToString("O")));
        }

        _db.InsertEventsBatch(drafts);
        Assert.Equal(300, _db.CountEvents());

        var plays = _db.QueryEvents(eventPrefix: "playback.");
        Assert.Equal(100, plays.Count);
        Assert.All(plays, e => Assert.Equal(EventNames.PlaybackOpen, e.Event));

        var byMember = _db.QueryEvents(actorMid: 42L);
        Assert.Equal(300 - 43, byMember.Count); // 300 里 7 的倍数下标是 guest

        var limited = _db.QueryEvents(limit: 10);
        Assert.Equal(10, limited.Count);
        // 新→旧排列
        Assert.True(string.CompareOrdinal(limited[0].TsUtc, limited[9].TsUtc) >= 0);
    }

    [Fact]
    public void Events_EmptyBatchIsNoop()
    {
        _db.InsertEventsBatch([]);
        Assert.Equal(0, _db.CountEvents());
    }

    [Fact]
    public async Task EventRecorder_BurstThenShutdown_FlushesEverything()
    {
        var recorder = new EventRecorder(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<EventRecorder>.Instance);
        using var cts = new CancellationTokenSource();
        var consume = Task.Run(() => recorder.ConsumeAsync(cts.Token));

        for (var i = 0; i < 50; i++)
        {
            recorder.Record(EventActors.Member, 7L, EventNames.PlaybackHeartbeat, "video", "BV1x", null, "9.9.9.9");
        }

        recorder.RecordWithPayload(EventActors.System, null, EventNames.VideoArrived, "video", "BV1x",
            new { bytes = 123, title = "带 payload 的" });

        // 留一点正常消费时间，再收站触发冲刷
        await Task.Delay(300);
        cts.Cancel();
        await consume;

        Assert.Equal(51, _db.CountEvents());
        var arrived = _db.QueryEvents(eventPrefix: "video.arrived");
        var row = Assert.Single(arrived);
        Assert.Contains("123", row.Payload);
    }

    [Fact]
    public void Submitter_Roundtrip()
    {
        var videoId = _db.AddVideo("https://www.bilibili.com/video/BV1xx411c7mD", "BV1xx411c7mD").VideoId;
        Assert.True(videoId > 0);
        Assert.Null(_db.GetVideoSubmitterMember(videoId));

        _db.SetVideoSubmitterMember(videoId, 4321L);
        Assert.Equal(4321L, _db.GetVideoSubmitterMember(videoId));
    }

    [Fact]
    public void IngestStats_CountsVideos_AndMeasuresArtifactBytesFromDisk()
    {
        // 占用字节的真源是产物登记的磁盘实数（旧版在这里查了个不存在的列，真机才暴雷）。
        var videoId = _db.AddVideo("https://www.bilibili.com/video/BV1ingest01", "BV1ingest01").VideoId;
        _db.SetVideoSubmitterMember(videoId, 5566L);

        var dir = Path.Combine(_tempDir, "UP测试", "入站测试 - 2026");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "p1.mp4"), new byte[300]);
        _db.RegisterCommentsArtifact(videoId, Path.Combine("UP测试", "入站测试 - 2026", "p1.mp4"));

        var stats = Assert.Single(_db.ListMemberIngestStats());
        Assert.Equal(5566L, stats.SubmitterMemberId);
        Assert.Equal(1, stats.VideoCount);
        Assert.Equal(300L, stats.TotalBytes);
    }
}
