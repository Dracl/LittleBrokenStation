using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// UP 历史脸档案（周巡脸员 <see cref="UpFaceSweepWorker"/> 的四件套）：
/// ▸ <c>up_faces</c> 表的 UNIQUE(mid, face_rel) 语义 —— 新件立行（首见=再见）、同件重复只挪 last_seen 不添行、
///   不同件各占一行（这才是「历史」二字的全部含义），清单按再见时刻新者在前；
/// ▸ 巡脸名单 = 馆藏稿子作者 ∪ 盯梢名单（去重、脏值不进）；
/// ▸ 该不该巡（<see cref="UpFaceSweepWorker.NeedsSweep"/>）比的是<b>周</b>不是天，周一那条周边界单独钉一格；
/// ▸ 盖不盖水位（<see cref="UpFaceSweepWorker.ShouldStampWatermark"/>）：整轮空手不盖、带回过就盖、名单空也盖；
/// ▸ 存件口径（<see cref="LbsUpInfoFetcher.StoreFaceAsync"/>，巡脸的下半场，取数与存件分家才测得到）：
///   没脸不记、件已在盘不重下（同 URL 二次调用连下载都不发）、0 字节壳归缺件、下载炸了连账都不记。
///
/// 库是真库（临时目录里的 station.db），脸是真落在临时 <c>data/up_faces</c> 的假件——只有「敲 B 站」那一发换成假下载器。
/// </summary>
public sealed class UpFaceSweepTests : IDisposable
{
    /// <summary>本周的周一（2026-11-06 是周五）：周边界那几格全靠它当锚。</summary>
    private static readonly DateTime WeekMonday = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;
    private int _downloads;

    public UpFaceSweepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-upfaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // 五槽全指回同一座临时根：UpFaces 是 Root/up_faces，顺带验一回「派生件不跟视频库混住」的布局。
        _paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
        _db.Initialize();
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

    // ── up_faces 台账 ──────────────────────────────────────────────────────

    [Fact]
    public void 新件立行_首见与再见同一刻()
    {
        _db.UpsertUpFace(1001, "/up_faces/aaa.jpg", "2026-11-02T03:00:00.0000000Z");

        var row = Assert.Single(_db.ListUpFaces(1001));
        Assert.Equal("/up_faces/aaa.jpg", row.FaceRel);
        Assert.Equal("2026-11-02T03:00:00.0000000Z", row.FirstSeen);
        Assert.Equal(row.FirstSeen, row.LastSeen);
    }

    [Fact]
    public void 同件重复_只挪last_seen不添行也不改首见()
    {
        _db.UpsertUpFace(1001, "/up_faces/aaa.jpg", "2026-11-02T03:00:00.0000000Z");
        _db.UpsertUpFace(1001, "/up_faces/aaa.jpg", "2026-11-09T03:00:00.0000000Z"); // 下一周又见到同一张脸

        var row = Assert.Single(_db.ListUpFaces(1001)); // UNIQUE(mid, face_rel) 挡重：巡十轮也还是一行
        Assert.Equal("2026-11-02T03:00:00.0000000Z", row.FirstSeen); // 首见是「第一次撞见」，后头的巡检不许回头改它
        Assert.Equal("2026-11-09T03:00:00.0000000Z", row.LastSeen);
    }

    [Fact]
    public void 换脸的两张各占一行_清单按last_seen新的在前()
    {
        _db.UpsertUpFace(1001, "/up_faces/old.jpg", "2026-10-05T00:00:00.0000000Z");
        _db.UpsertUpFace(1001, "/up_faces/new.jpg", "2026-11-02T00:00:00.0000000Z");
        _db.UpsertUpFace(1001, "/up_faces/old.jpg", "2026-11-01T00:00:00.0000000Z"); // 老脸又被撞见一次，仍不如新脸近

        var rows = _db.ListUpFaces(1001);
        Assert.Equal(2, rows.Count);
        Assert.Equal("/up_faces/new.jpg", rows[0].FaceRel);
        Assert.Equal("/up_faces/old.jpg", rows[1].FaceRel);
    }

    [Fact]
    public void 同一个件名挂在两位UP名下_各算一行不互相顶掉()
    {
        // 判重键是 (mid, face_rel) 不是 face_rel：撞脸的两个人（同图同人换号 / 抄头像）各有各的档案。
        _db.UpsertUpFace(1001, "/up_faces/same.jpg", "2026-11-02T00:00:00.0000000Z");
        _db.UpsertUpFace(1002, "/up_faces/same.jpg", "2026-11-02T00:00:00.0000000Z");

        Assert.Single(_db.ListUpFaces(1001));
        Assert.Single(_db.ListUpFaces(1002));
        Assert.Empty(_db.ListUpFaces(1003)); // 没档案的位回空清单，不是 null
    }

    // ── 巡脸名单 ──────────────────────────────────────────────────────────

    [Fact]
    public void 名单取馆藏作者与盯梢名单的并集_去重且脏值不进()
    {
        SeedVideoUpMid(2001); // 馆藏里只有的
        SeedVideoUpMid(2002); // 两条稿同一个作者 → 并集里只算一位
        SeedVideoUpMid(2002);
        SeedVideoUpMid(null); // 没抽到作者的稿：那不是任何人
        SeedVideoUpMid(0); // 脏值
        SeedVideoUpMid(-7); // 脏值
        Assert.True(_db.AddDynamicTarget(2003)); // 只被盯、稿子里还没有的
        Assert.True(_db.AddDynamicTarget(2001)); // 两条腿都有 → 去重
        Assert.True(_db.AddDynamicTarget(0)); // 脏值
        Assert.True(_db.AddDynamicTarget(-9)); // 脏值

        Assert.Equal(new[] { 2001L, 2002L, 2003L }, _db.ListSweepableMids());
    }

    [Fact]
    public void 空站名单是空清单_不是null() => Assert.Empty(_db.ListSweepableMids());

    // ── 该不该巡（真值表） ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026-11-022")] // 位数不对 = 认不出
    [InlineData("2026/11/02")] // 换了分隔符也认不出
    [InlineData("last-week")] // 手贱改过 settings：宁多巡一轮，不漏巡一整周
    public void 水位缺失或认不出_该巡(string? watermark) =>
        Assert.True(UpFaceSweepWorker.NeedsSweep(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc), watermark));

    [Fact]
    public void 同一周内_不巡()
    {
        // 2026-11-06 是周五，与水位那一周同桶：怎么醒都不出门（换脸不是按小时发生的事）。
        Assert.Equal(DayOfWeek.Monday, WeekMonday.DayOfWeek); // 锚自己先验一遍，免得整组测试跟着假日历白跑
        Assert.False(UpFaceSweepWorker.NeedsSweep(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc), "2026-11-02"));
    }

    [Fact]
    public void 跨进新的一周_该巡() =>
        Assert.True(UpFaceSweepWorker.NeedsSweep(new DateTime(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc), "2026-11-02"));

    [Fact]
    public void 周边界_周日夜里还算旧周_周一零点整算新周()
    {
        // 周界钉在 UTC 日历日的周一 00:00：差一分钟也不许提前出门 / 拖后一周。
        Assert.False(UpFaceSweepWorker.NeedsSweep(new DateTime(2026, 11, 8, 23, 59, 59, DateTimeKind.Utc), "2026-11-02"));
        Assert.True(UpFaceSweepWorker.NeedsSweep(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), "2026-11-02"));
    }

    [Fact]
    public void 水位盖的是本周周一_周日与周一各归相邻两周()
    {
        Assert.Equal("2026-11-02", UpFaceSweepWorker.WeekStamp(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc))); // 周一本身
        Assert.Equal("2026-11-02", UpFaceSweepWorker.WeekStamp(new DateTime(2026, 11, 6, 7, 0, 0, DateTimeKind.Utc))); // 周五
        Assert.Equal("2026-11-02", UpFaceSweepWorker.WeekStamp(new DateTime(2026, 11, 8, 23, 59, 59, DateTimeKind.Utc))); // 周日（本周边最后一秒）
        Assert.Equal("2026-11-09", UpFaceSweepWorker.WeekStamp(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc))); // 下周一
        Assert.Equal("2026-10-26", UpFaceSweepWorker.WeekStamp(new DateTime(2026, 11, 1, 23, 59, 59, DateTimeKind.Utc))); // 周日属于上一周（那周的周一是 10-26）
    }

    // ── 盖不盖水位 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(5, 0, false)] // 点了 5 位一张脸都没带回：判接口不认这趟，十分钟后再试
    [InlineData(5, 1, true)] // 带回过哪怕一张就盖：不换脸的老 UP 是常态，不盖就是永不收敛
    [InlineData(5, 5, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, 0, true)] // 空名单：那周的账本就是「没人可巡」
    public void 盖戳判据(int attempted, int filled, bool expected) =>
        Assert.Equal(expected, UpFaceSweepWorker.ShouldStampWatermark(attempted, filled));

    // ── 存件口径（巡脸的下半场） ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task B站没给脸_不记不报也不发下载(string? faceUrl)
    {
        var rel = await LbsUpInfoFetcher.StoreFaceAsync(3001, faceUrl, _paths, _db, StoreOk, "2026-11-02T00:00:00.0000000Z", default);

        Assert.Null(rel);
        Assert.Equal(0, _downloads);
        Assert.Empty(_db.ListUpFaces(3001));
    }

    [Fact]
    public async Task 件名沿用头像命名_件住data_up_faces()
    {
        const string url = "https://i0.hdslb.com/bfs/face/8fb3072cf2578e29187f.jpg";
        var rel = await LbsUpInfoFetcher.StoreFaceAsync(3002, url, _paths, _db, StoreOk, "2026-11-02T00:00:00.0000000Z", default);

        var name = LbsCommentArchiver.AvatarFileName(url);
        Assert.Equal("/up_faces/" + name, rel);
        Assert.Equal(1, _downloads);
        Assert.True(File.Exists(Path.Combine(_paths.UpFaces, name))); // 落盘区是 DataPaths.UpFaces，不跟 avatars 混住
        Assert.Equal(new[] { "/up_faces/" + name }, _db.ListUpFaces(3002).Select(r => r.FaceRel).ToArray());
    }

    [Fact]
    public async Task 同一URL二次调用_不重下只刷last_seen()
    {
        const string url = "https://i0.hdslb.com/bfs/face/unchanged-face.jpg";
        Assert.NotNull(await LbsUpInfoFetcher.StoreFaceAsync(3003, url, _paths, _db, StoreOk, "2026-11-02T00:00:00.0000000Z", default));
        Assert.Equal(1, _downloads);

        // 把件的时间戳钉到很久以前：第二趟真去重下了，这个戳必然挪动。
        var dest = Path.Combine(_paths.UpFaces, LbsCommentArchiver.AvatarFileName(url));
        File.SetLastWriteTimeUtc(dest, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var again = await LbsUpInfoFetcher.StoreFaceAsync(3003, url, _paths, _db, StoreOk, "2026-11-09T00:00:00.0000000Z", default);

        Assert.Equal("/up_faces/" + LbsCommentArchiver.AvatarFileName(url), again); // URL 未变、件已在盘：照样交件、照样记账
        Assert.Equal(1, _downloads); // 下载一次都没多发
        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(dest)); // 件没被重写过

        var row = Assert.Single(_db.ListUpFaces(3003));
        Assert.Equal("2026-11-02T00:00:00.0000000Z", row.FirstSeen);
        Assert.Equal("2026-11-09T00:00:00.0000000Z", row.LastSeen);
    }

    [Fact]
    public async Task 零字节壳当缺件_下一轮重下才记账()
    {
        const string url = "https://i0.hdslb.com/bfs/face/half-baked.jpg";
        var dest = Path.Combine(_paths.UpFaces, LbsCommentArchiver.AvatarFileName(url));

        Assert.Null(await LbsUpInfoFetcher.StoreFaceAsync(3004, url, _paths, _db, StoreEmpty, "2026-11-02T00:00:00.0000000Z", default));
        Assert.True(File.Exists(dest)); // 下载器一进去就把文件建出来了（DownloadFileAsync 的 FileMode.Create 同款现场）
        Assert.Equal(0, new FileInfo(dest).Length);
        Assert.Empty(_db.ListUpFaces(3004)); // 空壳不算有件，账也不记

        Assert.NotNull(await LbsUpInfoFetcher.StoreFaceAsync(3004, url, _paths, _db, StoreOk, "2026-11-09T00:00:00.0000000Z", default));
        Assert.Equal(2, _downloads); // 0 字节当缺件 → 这一轮真去重下
        Assert.Single(_db.ListUpFaces(3004));
    }

    [Fact]
    public async Task 下载炸了_回null连账都不记()
    {
        var rel = await LbsUpInfoFetcher.StoreFaceAsync(
            3005, "https://i0.hdslb.com/bfs/face/boom.jpg", _paths, _db, StoreThrows, "2026-11-02T00:00:00.0000000Z", default);

        Assert.Null(rel);
        Assert.Equal(1, _downloads);
        Assert.Empty(_db.ListUpFaces(3005)); // 台账里不许挂着一张盘上查无的脸
    }

    [Fact]
    public async Task 换脸的那一轮_旧件原样留着新件另存一行()
    {
        // 「历史」的全部意义：换脸不许把旧脸顶掉（up_face 那一列才做「只留最新」的事）。
        Assert.NotNull(await LbsUpInfoFetcher.StoreFaceAsync(
            3006, "https://i0.hdslb.com/bfs/face/first-face.jpg", _paths, _db, StoreOk, "2026-10-05T00:00:00.0000000Z", default));
        Assert.NotNull(await LbsUpInfoFetcher.StoreFaceAsync(
            3006, "https://i0.hdslb.com/bfs/face/second-face.jpg", _paths, _db, StoreOk, "2026-11-02T00:00:00.0000000Z", default));

        Assert.Equal(2, _downloads);
        var rows = _db.ListUpFaces(3006);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(new FileInfo(Path.Combine(_paths.UpFaces, Path.GetFileName(r.FaceRel))).Length > 0));
        Assert.Equal("2026-11-02T00:00:00.0000000Z", rows[0].LastSeen); // 新者在前
    }

    // ── 替身下载器与造数据 ────────────────────────────────────────────────

    /// <summary>正常交件的假下载器：把假字节写进目标件，顺便数着发数（真出网那一发归 SweepFaceAsync，测不到这里）。</summary>
    private Task StoreOk(string url, string dest, CancellationToken token)
    {
        _downloads++;
        File.WriteAllText(dest, "fake-jpeg-bytes");
        return Task.CompletedTask;
    }

    /// <summary>「下载成功却只留下空壳」的假下载器：FileMode.Create 一进去就建文件、半路炸掉就是这副样子。</summary>
    private Task StoreEmpty(string url, string dest, CancellationToken token)
    {
        _downloads++;
        File.WriteAllBytes(dest, Array.Empty<byte>());
        return Task.CompletedTask;
    }

    /// <summary>下载直接抛的假下载器（断网 / 412 风控页的现场）。</summary>
    private Task StoreThrows(string url, string dest, CancellationToken token)
    {
        _downloads++;
        throw new IOException("下载没走通");
    }

    /// <summary>造一条带作者（含 null / 脏值）的稿子，给巡脸名单当 <c>video_meta.up_mid</c> 那条腿。</summary>
    private void SeedVideoUpMid(long? upMid)
    {
        var bvid = "BVface" + Guid.NewGuid().ToString("N")[..6];
        var id = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("测试UP", upMid, "音乐>翻唱", "2026-05-20 18:00", "简介", "1.2万"),
            [new PageDraft("task-" + bvid, 1, "P1", 1000 + id, "12:34", "videos")]);
    }
}
