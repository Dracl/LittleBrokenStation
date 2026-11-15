using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LBS.Tests;

/// <summary>
/// UP 名片同站蹭脸：头像命名换 sha1 后，早年 /avatars/up-{mid}.jpg 那代老账全体丢件——
/// 展示层（GetPlaybackData）从同一 UP 的别稿蹭现货，开局对账（BackfillUpCardsFromShared）批量落账，
/// 蹭源只认盘上真在的本地件：热链不配当被蹭源，也不该被蹭走。
/// </summary>
public sealed class UpCardSharingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public UpCardSharingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-upcard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _db = new StationDb(new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir));
        _db.Initialize();
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录留给操作系统
        }
    }

    /// <summary>造一条带 mid 的稿件，up_face 直接落给定值（null = 没采过）。脸文件名传进来就顺手把文件写好。</summary>
    private long Seed(string bvid, long mid, string? face, string? sign = null, long? fans = null, string? createFaceFile = null)
    {
        var id = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("七禾いえ", mid, "音乐>翻唱", "2026-05-20 18:00:00", "简介", "1万"),
            Array.Empty<PageDraft>());
        _db.SetUpInfo(id, face, sign, fans);
        if (createFaceFile is not null)
        {
            // 假脸要落在 Avatars 派生目录（root/avatars），与 StationDb.IsLocalFaceOnDisk 的验货口径对齐。
            var avatars = Path.Combine(_tempDir, "avatars");
            Directory.CreateDirectory(avatars);
            File.WriteAllText(Path.Combine(avatars, createFaceFile), "fake-jpeg");
        }

        return id;
    }

    [Fact]
    public void 老账缺件_展示层蹭同站现货()
    {
        Seed("BVSHARE00001", 777, "/avatars/good.jpg", "签名在", 30000, createFaceFile: "good.jpg");
        Seed("BVSHARE00002", 777, "/avatars/up-777.jpg"); // 早年命名，盘上没这文件

        var data = _db.GetPlaybackData("BVSHARE00002");
        Assert.NotNull(data);
        Assert.Equal("/avatars/good.jpg", data.UpFace); // 别稿的现货顶上，UpCard 不再吃 404
        Assert.Equal("签名在", data.UpSign); // 本行签名空着时一并垫上
        Assert.Equal(30000, data.UpFans);
    }

    [Fact]
    public void 无人可蹭维持原样不造假()
    {
        Seed("BVNOSRC00001", 888, "/avatars/up-888.jpg");

        var data = _db.GetPlaybackData("BVNOSRC00001");
        Assert.NotNull(data);
        Assert.Equal("/avatars/up-888.jpg", data.UpFace); // 没现货就原样，前端自会落默认小人
    }

    [Fact]
    public void 热链算能用_也不配当被蹭源()
    {
        Seed("BVHOTL000001", 999, "https://i0.hdslb.com/x.jpg"); // 热链账：站长方针白嫖图床，不蹭
        Seed("BVHOTL000002", 999, null); // 空账：同 mid 只有热链可看，没有本地现货

        Assert.Equal("https://i0.hdslb.com/x.jpg", _db.GetPlaybackData("BVHOTL000001")!.UpFace);
        Assert.Null(_db.GetPlaybackData("BVHOTL000002")!.UpFace); // 热链不配当蹭源，空账继续空着
    }

    [Fact]
    public void 开局对账_只修缺件行_签名不抹账()
    {
        Seed("BVFIX000001", 777, "/avatars/good.jpg", createFaceFile: "good.jpg");
        var stale = Seed("BVFIX000002", 777, "/avatars/up-777.jpg");
        var blank = Seed("BVFIX000003", 777, string.Empty, "本行签名", null); // 失败销账行：脸空串、签名是自己写的
        var noSource = Seed("BVFIX000004", 666, "/avatars/up-666.jpg"); // 这位 UP 站内没现货

        var repaired = _db.BackfillUpCardsFromShared();

        Assert.Equal(2, repaired); // 缺件行 + 空串行，没现货的那位原地不动
        Assert.Equal(("/avatars/good.jpg", (string?)null, (long?)null), (GetFace(stale), GetSign(stale), GetFans(stale)));
        Assert.Equal(("/avatars/good.jpg", "本行签名", (long?)null), (GetFace(blank), GetSign(blank), GetFans(blank))); // COALESCE 只垫空
        Assert.Equal("/avatars/up-666.jpg", GetFace(noSource));
    }

    [Fact]
    public void 候选行脸路不正_不当被蹭源()
    {
        Seed("BVEVIL000001", 555, "/avatars/../evil.jpg"); // 穿目录的脏值，验货直接毙
        var stale = Seed("BVEVIL000002", 555, "/avatars/up-555.jpg");

        Assert.Equal(0, _db.BackfillUpCardsFromShared());
        Assert.Equal("/avatars/up-555.jpg", GetFace(stale));
        Assert.Null(_db.FindSharedUpCardByMid(555, stale));
    }

    [Fact]
    public void 钉子户只点无现货可蹭的单稿行()
    {
        Seed("BVSTUCK0001", 777, "/avatars/good.jpg", createFaceFile: "good.jpg");
        var salvageable = Seed("BVSTUCK0002", 777, "/avatars/up-777.jpg"); // 可蹭：开局对账会修，不算钉子户
        var single = Seed("BVSTUCK0003", 666, "/avatars/up-666.jpg"); // 单稿没现货：只能出门
        var hotlink = Seed("BVSTUCK0004", 555, "https://i0.hdslb.com/y.jpg"); // 热链能用：不出门
        var fresh = Seed("BVSTUCK0005", 444, "/avatars/ok.jpg", createFaceFile: "ok.jpg"); // 现货健在：不出门

        var stuck = _db.ListUpCardsBeyondSalvage(10);

        Assert.Equal([single], stuck); // 只缺这一个；可蹭/热链/健在的全不入选
        Assert.DoesNotContain(salvageable, stuck);
        Assert.DoesNotContain(hotlink, stuck);
        Assert.DoesNotContain(fresh, stuck);
    }

    private (string? Face, string? Sign, long? Fans) CardState(long videoId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT up_face, up_sign, up_fans FROM video_meta WHERE video_id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private string? GetFace(long videoId) => CardState(videoId).Face;
    private string? GetSign(long videoId) => CardState(videoId).Sign;
    private long? GetFans(long videoId) => CardState(videoId).Fans;
}
