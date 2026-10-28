using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LBS.Tests;

/// <summary>
/// 站内分区：有效分区 = 站长改派优先、B 站归档 zone 兜底；只数已入站的货；
/// 顶级段聚合与改派校验（TryNormalizeZone 与端点同一张嘴）。
/// </summary>
public sealed class ZoneTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;

    public ZoneTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-zones-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
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

    /// <summary>造一条到货：bvid + B 站归档分区（null = 没档案），直达 Arrived。</summary>
    private long Arrive(string bvid, string? zone)
    {
        var videoId = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        if (zone is not null)
        {
            _db.SaveSubmissionMetadata(
                videoId,
                new VideoMetaDraft("测试UP", 123, zone, "2026-05-20 18:00:00", "简介", "1万"),
                Array.Empty<PageDraft>());
        }

        _db.MarkArrived(videoId);
        return videoId;
    }

    [Fact]
    public void 顶级段聚合_计数倒序_同名并流()
    {
        Arrive("BVZONE000001", "音乐>翻唱");
        Arrive("BVZONE000002", "音乐>演奏");
        Arrive("BVZONE000003", "动画>综合");
        Arrive("BVZONE000004", "音乐"); // 没有 '>' 的单段也算户口

        var zones = _db.ListZones();
        Assert.Equal(2, zones.Count);
        Assert.Equal("音乐", zones[0].Name);
        Assert.Equal(3, zones[0].Count);
        Assert.Equal("动画", zones[1].Name);
        Assert.Equal(1, zones[1].Count);
    }

    [Fact]
    public void 未入站与无户口都不进分区()
    {
        Arrive("BVZONE000005", "音乐>翻唱");
        Arrive("BVZONE000006", null); // 到货但没档案 → 没户口
        _db.AddVideo("https://www.bilibili.com/video/BVZONE000007", "BVZONE000007"); // 还在排队

        var zones = _db.ListZones();
        Assert.Single(zones);
        Assert.Equal("音乐", zones[0].Name);
        Assert.Equal(1, zones[0].Count);
    }

    [Fact]
    public void 改派优先于继承_清空后回继承()
    {
        var id = Arrive("BVZONE000008", "音乐>翻唱");

        Assert.True(_db.SetSiteZone(id, "鬼畜>调教"));
        var zones = _db.ListZones();
        Assert.Single(zones);
        Assert.Equal("鬼畜", zones[0].Name);

        var items = _db.ListZoneVideos("鬼畜");
        Assert.Single(items);
        Assert.Equal("鬼畜>调教", items[0].Zone);

        // 清空改派：户口回跟 B 站档案走
        Assert.True(_db.SetSiteZone(id, null));
        Assert.Equal("音乐", Assert.Single(_db.ListZones()).Name);
    }

    [Fact]
    public void 分区页只认顶级段_列表带全名()
    {
        Arrive("BVZONE000009", "音乐>翻唱");
        Arrive("BVZONE000010", "音乐>演奏");
        Arrive("BVZONE000011", "动画>综合");

        var items = _db.ListZoneVideos("音乐");
        Assert.Equal(2, items.Count);
        Assert.All(items, it => Assert.StartsWith("音乐>", it.Zone)); // 卡上标全名，页按顶级摆

        Assert.Empty(_db.ListZoneVideos("不存在"));
        Assert.Empty(_db.ListZoneVideos("  ")); // 空白 name 空手而归，不 404
    }

    [Fact]
    public void 改派校验_只认父大于子一层()
    {
        Assert.True(StationDb.TryNormalizeZone("  音乐 > 翻唱  ", out var norm, out _));
        Assert.Equal("音乐>翻唱", norm); // 两侧空白掰掉，段间空白也掰掉

        Assert.True(StationDb.TryNormalizeZone("  ", out var cleared, out _));
        Assert.Null(cleared); // 空白 = 清空回继承，放行

        Assert.False(StationDb.TryNormalizeZone("a>b>c", out _, out _));
        Assert.False(StationDb.TryNormalizeZone(">翻唱", out _, out _));
        Assert.False(StationDb.TryNormalizeZone("音乐>", out _, out _));
        Assert.False(StationDb.TryNormalizeZone(new string('长', StationDb.MaxZoneNameLength + 1), out _, out _));
    }

    [Fact]
    public void 改派不存在的稿回false()
    {
        Assert.False(_db.SetSiteZone(424242, "音乐>翻唱"));
    }
}
