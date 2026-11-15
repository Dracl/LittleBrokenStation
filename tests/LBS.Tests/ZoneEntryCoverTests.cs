using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 分区总览入口卡的底图一摄（StationDb.GetZoneEntryCover）：
/// 冠军分区按「次数」选 —— watch.play / watch.heartbeat 的行数，访客也数，与周报「最多人看」的去重人头有意两套口径
/// （那边数观众人数，这边数热度）；中选那条 = 冠军分区内次数最高且有封面的；只认在站的货；
/// 快照存 settings，按本地日（UTC+8）钉住，同日反复取都同一张，过零点才重算。
/// </summary>
public sealed class ZoneEntryCoverTests : IDisposable
{
    // Day1 本地 10-29 14:00，Day2 本地 10-30 14:00（+08），中间跨一个本地零点
    private static readonly DateTimeOffset Day1 = new(2026, 10, 29, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 10, 30, 6, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;

    public ZoneEntryCoverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-zone-entry-" + Guid.NewGuid().ToString("N"));
        var videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(videosDir);
        _paths = new DataPaths(_tempDir, videosDir, _tempDir, _tempDir, _tempDir);
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

    private void Execute(string sql, params (string Name, object Value)[] args)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>造一条在站的货并给它落户口（走站长改派槽，避开 B 站档案那层）。</summary>
    private long Arrive(string bvid, string zone)
    {
        var id = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.MarkArrived(id);
        Execute("UPDATE videos SET site_zone = $z WHERE id = $i;", ("$z", zone), ("$i", id));
        return id;
    }

    /// <summary>给一条货配上封面产物（形状照 ListZoneVideos 认的那张：artifacts kind='cover'）。</summary>
    private void GiveCover(string bvid) =>
        Execute(
            "INSERT INTO artifacts (video_id, page_idx, kind, rel_path) SELECT id, NULL, 'cover', $p FROM videos WHERE bvid = $b;",
            ("$p", $"covers/{bvid}.jpg"),
            ("$b", bvid));

    /// <summary>往台账塞 n 枪观看（play 与 heartbeat 交替，形状照 VideoPage.vue 的上报实况）。mid 给空 = 访客。</summary>
    private void Watch(string bvid, int times, long? mid = null)
    {
        for (var i = 0; i < times; i++)
        {
            var beat = i % 2 == 1;
            _db.InsertEventsBatch(new[]
            {
                new EventDraft(
                    mid is null ? EventActors.Guest : EventActors.Member,
                    mid,
                    beat ? "playback.heartbeat" : "playback.play",
                    "video",
                    bvid,
                    beat ? """{"t":42}""" : null,
                    mid is null ? "9.9.9.9" : null,
                    Day1.ToString("O"),
                    mid is null ? null : 1),
            });
        }
    }

    [Fact]
    public void 冠军分区按总次数选_区内再挑最火那条的封面()
    {
        // 音乐区只有一条，单条 50 枪最猛；动画区三条摊成 52 枪，按分区总量反超
        Arrive("BVhot50", "音乐>翻唱");
        GiveCover("BVhot50");
        Watch("BVhot50", 50, 940001);

        Arrive("BVaniTop", "动画>原创");
        GiveCover("BVaniTop");
        Watch("BVaniTop", 22, 940001);

        Arrive("BVaniB", "动画");
        GiveCover("BVaniB");
        Watch("BVaniB", 15, 940001);

        Arrive("BVaniC", "动画>手书");
        GiveCover("BVaniC");
        Watch("BVaniC", 15, 940001);

        var hit = _db.GetZoneEntryCover(Day1);

        Assert.Equal("动画", hit.Zone);
        Assert.Equal("BVaniTop", hit.Bvid);
        Assert.Equal("/media/covers/BVaniTop.jpg", hit.Cover);
    }

    [Fact]
    public void 访客的播放也算次数_不去重人头与周榜两口径()
    {
        // 动画区全靠访客刷（mid 为 NULL，周榜那种去重口径一条都不算它）；音乐区是一个成员看的
        Arrive("BVguest", "动画");
        GiveCover("BVguest");
        Watch("BVguest", 30); // 访客

        Arrive("BVmember", "音乐");
        GiveCover("BVmember");
        Watch("BVmember", 10, 940001); // 成员一人

        var hit = _db.GetZoneEntryCover(Day1);

        Assert.Equal("动画", hit.Zone);
        Assert.Equal("BVguest", hit.Bvid);
    }

    [Fact]
    public void 快照按本地日钉住_同日加火不跳图_过零点才重算()
    {
        Arrive("BVA", "音乐");
        GiveCover("BVA");
        Watch("BVA", 10, 940001);

        Assert.Equal("BVA", _db.GetZoneEntryCover(Day1).Bvid);

        // 动画区后来居上（99 枪），但同日不许跳图
        Arrive("BVB", "动画");
        GiveCover("BVB");
        Watch("BVB", 99, 940001);

        Assert.Equal("BVA", _db.GetZoneEntryCover(Day1).Bvid);
        Assert.Equal("BVA", _db.GetZoneEntryCover(Day1.AddHours(9)).Bvid); // 本地 23:00，还在今天

        Assert.Equal("BVB", _db.GetZoneEntryCover(Day1.AddHours(11)).Bvid); // 本地次日 01:00，重算
        Assert.Equal("BVB", _db.GetSetting(StationDb.ZoneEntryVideoKey));
        Assert.Equal(Day2.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd"), _db.GetSetting(StationDb.ZoneEntryDayKey));
        Assert.Equal("BVB", _db.GetZoneEntryCover(Day2).Bvid); // 次日之内也钉住
    }

    [Fact]
    public void 快照那条下架了_同日也重算并把新结果盖回快照()
    {
        Arrive("BVA", "音乐");
        GiveCover("BVA");
        Watch("BVA", 10, 940001);

        Arrive("BVB", "音乐");
        GiveCover("BVB");
        Watch("BVB", 5, 940001);

        Assert.Equal("BVA", _db.GetZoneEntryCover(Day1).Bvid);

        Execute("UPDATE videos SET status = 'Pending' WHERE bvid = 'BVA';"); // 不算在站了

        var after = _db.GetZoneEntryCover(Day1);
        Assert.Equal("BVB", after.Bvid);
        Assert.Equal("BVB", _db.GetSetting(StationDb.ZoneEntryVideoKey));
    }

    [Fact]
    public void 冠军区整区无封面_交空回落且不写快照()
    {
        Arrive("BVnocover", "动画"); // 冠军区，但整区一张封面都没有
        Watch("BVnocover", 30, 940001);

        Arrive("BVpic", "音乐");
        GiveCover("BVpic");
        Watch("BVpic", 10, 940001);

        var hit = _db.GetZoneEntryCover(Day1);

        Assert.Equal("动画", hit.Zone); // 冠军照报，只是没图
        Assert.Null(hit.Bvid);
        Assert.Null(hit.Cover);
        Assert.Null(_db.GetSetting(StationDb.ZoneEntryVideoKey)); // 没中选就别写快照，免得明天继续空
    }

    [Fact]
    public void 没在站的货不参与_哪怕它次数最高()
    {
        Arrive("BVA", "音乐");
        GiveCover("BVA");
        Watch("BVA", 5, 940001);

        var ghostId = _db.AddVideo("https://www.bilibili.com/video/BVghost", "BVghost").VideoId; // 没 MarkArrived
        Execute("UPDATE videos SET site_zone = '动画' WHERE id = $i;", ("$i", ghostId));
        GiveCover("BVghost");
        Watch("BVghost", 90, 940001);

        var hit = _db.GetZoneEntryCover(Day1);

        Assert.Equal("音乐", hit.Zone);
        Assert.Equal("BVA", hit.Bvid);
    }
}
