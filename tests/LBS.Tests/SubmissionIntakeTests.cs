using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text;

namespace LBS.Tests;

/// <summary>
/// 投稿受理的顺序钉（11-02 那一夜拿数据换来的）：闸门拒收之后台账必须一行不增。
///
/// 老写法是 AddVideosBulk 先落台账、再查席位、再过投稿闸门，且拒收不回滚 —— 于是「席位当时还没绑 B 站号」
/// 被拒一次，就把那条 BV 号永久占成无标题、无归属、永不下載的空壳，补绑号后重投只吃 409「已经到过了」
/// （现场：BV1Fz4y1Q7kB / id=468，以及卡住的 467；10-26 那批 151 条无归属稿同出一门）。
/// 端点层无 WebApplicationFactory 先例（见 DmTests 的说明），那两道脸与落库全在 SubmissionIntake 里，这里直测它。
/// </summary>
public sealed class SubmissionIntakeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public SubmissionIntakeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-intake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _db = new StationDb(new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir));
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

    private static NewVideo Video(string bvid) => new($"https://www.bilibili.com/video/{bvid}", bvid, null);

    private long CrownOwner(long mid = 7001L)
    {
        MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, "站长"), invite: null, "SESSDATA=owner");
        return _db.GetMemberByMid(mid)!.Id;
    }

    /// 素净席位：账密进站那种，有席位、无 B 站号 —— 能逛能聊能私信，唯独不能投稿。
    private StationMember PlainSeat(string note = "素净席")
    {
        var (id, _) = _db.CreateMemberInvite(note);
        _db.SetMemberStatus(id, MemberStatus.Active);
        return _db.GetMemberById(id)!;
    }

    /// 那张脸真跑一遍，只看它落下的状态码与话术关键字（不猜 Results.Json 的内部类型）。
    /// DefaultHttpContext 自带的服务容器是空的，而 Results.Json 开口要 IOptions&lt;JsonOptions&gt;，现搭一个最小容器顶上。
    private static (int StatusCode, string Body) Execute(IResult result)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging(); // Results.Json 写账时要 ILoggerFactory，最小容器也得给它带上
        services.AddSingleton<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>(
            new OptionsWrapper<Microsoft.AspNetCore.Http.Json.JsonOptions>(new Microsoft.AspNetCore.Http.Json.JsonOptions()));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        using var body = new MemoryStream();
        http.Response.Body = body;
        result.ExecuteAsync(http).GetAwaiter().GetResult();
        return (http.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public void 素净席位投稿_403且台账一行不增()
    {
        var seat = PlainSeat();

        var denial = SubmissionIntake.Deny(_db, seat);

        Assert.NotNull(denial);
        var (status, text) = Execute(denial!);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("no-bilibili-account", text);
        // ↓ 老写法翻车就在这一格：拒收之后台账里已经躺着一条无主空壳，BV 号从此投不进来。
        Assert.Null(_db.GetVideoIdByBvid("BV1ghost00001"));
    }

    [Fact]
    public void 匿名投稿_401且台账一行不增()
    {
        var denial = SubmissionIntake.Deny(_db, member: null);

        Assert.NotNull(denial);
        var (status, text) = Execute(denial!);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Contains("member-required", text);
        Assert.Null(_db.GetVideoIdByBvid("BV1ghost00002"));
    }

    [Fact]
    public void 被拒后补绑号_同一条BV再投能成且当场定归属()
    {
        var seat = PlainSeat();
        const string bvid = "BV1after0001";
        Assert.NotNull(SubmissionIntake.Deny(_db, seat)); // 先撞一次闸门（老写法在这里留下幽灵行）

        _db.UpsertMemberAccount(seat.Id, 8100L, "投稿小号", null, "SESSDATA=sub");
        Assert.Null(SubmissionIntake.Deny(_db, seat)); // 补绑即放行，不必重开席位

        var intake = SubmissionIntake.Accept(_db, seat, new[] { Video(bvid) });

        Assert.Equal(1, intake.Added);
        var videoId = Assert.Single(intake.InsertedIds);
        // 归属与行同笔落：不留「先插后补」的空档，也就不会有人排队排在一条没主的稿上。
        Assert.Equal(seat.Id, _db.GetVideoSubmitterMember(videoId));
    }

    [Fact]
    public void 重投同一条BV_第二次Added为零且抢不走已有归属()
    {
        var alice = PlainSeat("甲席");
        var bob = PlainSeat("乙席");
        _db.UpsertMemberAccount(alice.Id, 8201L, "甲的号", null, "SESSDATA=a");
        _db.UpsertMemberAccount(bob.Id, 8202L, "乙的号", null, "SESSDATA=b");
        const string bvid = "BV1dup000001";

        var first = SubmissionIntake.Accept(_db, alice, new[] { Video(bvid) });
        var second = SubmissionIntake.Accept(_db, bob, new[] { Video(bvid) });

        Assert.Equal(1, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Empty(second.InsertedIds); // 端点据此报 409「已经到过了」/ 批量报「来过 1 条」
        Assert.Equal(alice.Id, _db.GetVideoSubmitterMember(Assert.Single(first.InsertedIds)));
    }

    [Fact]
    public void 站长不受投稿闸_池子空也落得了台账()
    {
        var owner = CrownOwner();
        var seat = _db.GetMemberById(owner)!;
        // CrownOwner 扫码进站顺手存了那枚票；这格要的是「一个号都没有」，先把池子扫干净。
        foreach (var acc in _db.ListMemberAccounts(owner))
        {
            _db.DeleteMemberAccount(acc.Id);
        }

        Assert.Empty(_db.ListMemberAccounts(owner)); // 前提：真的一个号都没有

        Assert.Null(SubmissionIntake.Deny(_db, seat)); // 站长拿引擎登录态投稿，不受此闸

        var intake = SubmissionIntake.Accept(_db, seat, new[] { Video("BV1owner0001") });
        Assert.Equal(1, intake.Added);
        Assert.Equal(owner, _db.GetVideoSubmitterMember(Assert.Single(intake.InsertedIds)));
    }
}
