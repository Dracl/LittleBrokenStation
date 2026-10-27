using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>StationDb 用临时库文件测，测完即删。</summary>
public sealed class StationDbTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public StationDbTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
    }

    [Fact]
    public void 初始化幂等()
    {
        _db.Initialize();
        _db.Initialize();
        Assert.Empty(_db.ListVideos());
    }

    [Fact]
    public void WAL模式已启用()
    {
        // 侧文件（-wal/-shm）只是代理证据：最后一个连接干净关闭时会被 checkpoint 删掉，
        // 早年靠连接池养着才一直灵；数据交换引入 ClearAllPools 后这份运气不可靠了——
        // 直接问 PRAGMA journal_mode，这才是要测的事（WAL 是文件头属性，持久）。
        _db.AddVideo("https://www.bilibili.com/video/BV1GJ411x7h7", "BV1GJ411x7h7");
        _db.AddVideosBulk(new[] { new NewVideo("u", "BV1xx411c7mD", "t") });
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar()?.ToString());
    }

    [Fact]
    public void 单条入库_重复拒绝()
    {
        var url = "https://www.bilibili.com/video/BV1GJ411x7h7";
        var first = _db.AddVideo(url, "BV1GJ411x7h7");
        Assert.Equal(AddVideoResult.Inserted, first.Result);
        Assert.True(first.VideoId > 0);

        var second = _db.AddVideo(url + "?p=2", "BV1GJ411x7h7");
        Assert.Equal(AddVideoResult.Duplicate, second.Result);
        Assert.Equal(0, second.VideoId);
    }

    [Fact]
    public void 批量入库_库内与批内去重()
    {
        _db.AddVideo("u0", "BV1GJ411x7h7");

        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo("u1", "BV1GJ411x7h7", "撞库内"),
            new NewVideo("u2", "BV1xx411c7mD", "新1"),
            new NewVideo("u3", "BV1xx411c7mD", "批内重复"),
            new NewVideo("u4", "BV1qt411w7BE", "新2"),
        });

        Assert.Equal(2, outcome.Added);
        Assert.Equal(2, outcome.Duplicated);
        Assert.Equal(2, outcome.InsertedIds.Count);
        Assert.All(outcome.InsertedIds, id => Assert.True(id > 0));
    }

    [Fact]
    public void 列表按新到旧_标题可空()
    {
        _db.AddVideosBulk(new[]
        {
            new NewVideo("u1", "BV1GJ411x7h7", null),
            new NewVideo("u2", "BV1xx411c7mD", "有标题"),
        });

        var list = _db.ListVideos();
        Assert.Equal(2, list.Count);
        Assert.Equal("BV1xx411c7mD", list[0].Bvid);
        Assert.Equal("有标题", list[0].Title);
        Assert.Equal(VideoStatus.Pending, list[0].Status);
        Assert.Null(list[1].Title);
    }

    /// <summary>造一条走完提交流程的已到站视频，搜索测试用。</summary>
    private long 已到站(string bvid, string? title, string? up = null, string? zone = null, string? views = null)
    {
        var id = _db.AddVideo("https://www.bilibili.com/video/" + bvid, bvid).VideoId;
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title, null);
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft(up, up is null ? null : 100, zone, null, null, views),
            new[] { new PageDraft("task-" + bvid, 1, title ?? bvid, 1, "04:13", Path.Combine(_tempDir, "v", bvid + ".mp4")) });
        _db.MarkArrived(id);
        return id;
    }

    [Fact]
    public void 搜索_标题UP与BV号都能命中()
    {
        已到站("BV1Ax411d7jD", "流离之境", up: "七禾いえ", zone: "动画>综合");
        已到站("BV1Z2ny63E86", "另一首歌", up: "其他人", zone: "音乐");

        Assert.Single(_db.SearchVideos("流离", null));
        Assert.Single(_db.SearchVideos("七禾", null));
        Assert.Single(_db.SearchVideos("音乐", null));
        var byBvid = _db.SearchVideos("bv1ax", null);
        Assert.Single(byBvid); // SQLite LIKE 对 ASCII 大小写不敏感，BV 号随手大小写也能搜到
        Assert.Equal("BV1Ax411d7jD", byBvid[0].Bvid);
    }

    [Fact]
    public void 搜索_只看到已到站()
    {
        已到站("BV1Ax411d7jD", "已到站的歌");
        _db.AddVideo("u", "BV1xx411c7mD"); // Pending
        var failedId = _db.AddVideo("u", "BV1qt411w7BE").VideoId;
        _db.MarkSubmitted(failedId, new[] { "t" }, "失败的歌", null);
        _db.MarkFailed(failedId, "boom");

        Assert.Single(_db.SearchVideos("的歌", null));
        Assert.Empty(_db.SearchVideos("失败", null));
        Assert.Empty(_db.SearchVideos("BV1xx", null));
    }

    [Fact]
    public void 搜索_多词之间取交集()
    {
        已到站("BV1Ax411d7jD", "流离之境", up: "七禾いえ");
        已到站("BV1Z2ny63E86", "流离之外", up: "其他人");

        Assert.Single(_db.SearchVideos("流离 七禾", null));
        Assert.Equal(2, _db.SearchVideos("流离", null).Count);
        Assert.Empty(_db.SearchVideos("流离 不存在", null));
    }

    [Fact]
    public void 搜索_通配符按字面处理()
    {
        已到站("BV1Ax411d7jD", "100%原创");
        已到站("BV1Z2ny63E86", "百分之五十");

        Assert.Single(_db.SearchVideos("100%", null));
        Assert.Empty(_db.SearchVideos("100_x", null)); // _ 也是字面量，不是任意单字符
        Assert.Single(_db.SearchVideos("%", null));    // 不会被当成全表匹配
    }

    [Fact]
    public void 搜索_按播放数降序()
    {
        已到站("BV1Ax411d7jD", "冷门", views: "900");
        已到站("BV1Z2ny63E86", "爆款", views: "10000");
        已到站("BV1qt411w7BE", "中等", views: "4582");

        // 三支共享 BV1 前缀，一词命中全集，比较两种排序的首位。
        var hot = _db.SearchVideos("BV1", "plays");
        Assert.Equal(new[] { "爆款", "中等", "冷门" }, hot.Select(v => v.Title));

        var latest = _db.SearchVideos("BV1", null);
        Assert.Equal("中等", latest[0].Title); // 最后入库的排第一
    }

    [Fact]
    public void 搜索_万播放不能输给五千()
    {
        已到站("BV1Ax411d7jD", "小热门", views: "5000");
        已到站("BV1Z2ny63E86", "大爆款", views: "4618.3万");
        已到站("BV1qt411w7BE", "顶流", views: "2.1亿");

        var hot = _db.SearchVideos("BV1", "plays");
        Assert.Equal(new[] { "顶流", "大爆款", "小热门" }, hot.Select(v => v.Title));
    }

    [Fact]
    public void 搜索_空查询与长词限制()
    {
        已到站("BV1Ax411d7jD", "随便");
        Assert.Empty(_db.SearchVideos("   ", null));
        Assert.Single(_db.SearchVideos("随便", null));
    }

    [Fact]
    public void 列表_弹幕条数数Dialogue行()
    {
        var id = _db.AddVideo("u", "BV1Ax411d7jD").VideoId;
        _db.MarkSubmitted(id, new[] { "t1", "t2" }, "多P视频", null);
        var vDir = Path.Combine(_tempDir, "v");
        Directory.CreateDirectory(vDir);
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("up", 1, "分区", null, null, "100"),
            new[]
            {
                new PageDraft("t1", 1, "P1", 1, "01:00", Path.Combine(vDir, "vid.P1.mp4")),
                new PageDraft("t2", 2, "P2", 2, "01:00", Path.Combine(vDir, "vid.P2.mp4")),
            });
        File.WriteAllText(Path.Combine(vDir, "vid.P1.mp4.ass"), "[Script Info]\nDialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,首条\n; 注释行\nDialogue: 0,0:00:02.00,0:00:06.00,Default,,0,0,0,,次条\n");
        File.WriteAllText(Path.Combine(vDir, "vid.P2.mp4.ass"), "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,第三条\n");

        var (archived, _) = _db.FinalizeArtifacts(id, _tempDir);
        Assert.True(archived >= 2); // 两份 ass 都该扫进档案

        var entry = _db.ListVideos().Single(v => v.Bvid == "BV1Ax411d7jD");
        Assert.Equal(3, entry.DanmakuCount); // 两个 P 的 Dialogue 行求和，注释行不算

        // 没弹幕档案的给 null，前端整段不显，不给假 0
        _db.AddVideo("u2", "BV1Z2ny63E86");
        Assert.Null(_db.ListVideos().Single(v => v.Bvid == "BV1Z2ny63E86").DanmakuCount);
    }

    [Fact]
    public void 编码版本_生命周期()
    {
        var id = 已到站("BV1Ax411d7jD", "多版视频", views: "100");
        var basePath = Path.Combine(_tempDir, "v", "vid.P1");

        // 登记：大小写归一，重复拒
        Assert.True(_db.AddCodecVersion(id, 1, "AV1"));
        Assert.False(_db.AddCodecVersion(id, 1, "av1"));
        Assert.False(_db.AddCodecVersion(id, 1, "vp9"));

        var rows = _db.GetCodecVersions(id);
        Assert.Single(rows);
        Assert.Equal("av1", rows[0].Codec);
        Assert.Equal("Downloading", rows[0].Status);

        // 提交回写
        _db.MarkCodecSubmitted(rows[0].Id, new[] { "task-9" }, basePath);
        var inFlight = _db.GetDownloadingCodecVersions();
        Assert.Single(inFlight);
        Assert.Contains("task-9", inFlight[0].TaskIdsJson);

        // 扫盘定档：基准路径前缀 + 视频后缀命中
        Directory.CreateDirectory(Path.Combine(_tempDir, "v"));
        File.WriteAllText(Path.Combine(_tempDir, "v", "vid.P1-AV1.mp4"), "fake-video");
        Assert.True(_db.FinalizeCodecVersion(rows[0].Id, basePath, _tempDir));

        rows = _db.GetCodecVersions(id);
        Assert.Equal("Arrived", rows[0].Status);
        Assert.Equal("v/vid.P1-AV1.mp4", rows[0].RelPath);

        // 失败的允许重跑
        Assert.True(_db.AddCodecVersion(id, 1, "hevc"));
        var hevc = _db.GetCodecVersions(id).Single(r => r.Codec == "hevc");
        _db.MarkCodecFailed(hevc.Id, "引擎拒了");
        Assert.True(_db.AddCodecVersion(id, 1, "hevc"));
        Assert.Equal("Downloading", _db.GetCodecVersions(id).Single(r => r.Codec == "hevc").Status);

        // 销账交回 rel_path 供删文件
        Assert.Equal("v/vid.P1-AV1.mp4", _db.TakeCodecVersionForDelete(id, 1, "av1"));
        Assert.Null(_db.TakeCodecVersionForDelete(id, 1, "av1"));

        // 整站连坐：删视频时表行一并清
        _db.DeleteVideoCascade(id, _tempDir);
        Assert.Empty(_db.GetCodecVersions(id));
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
            // 临时目录清理失败不影响测试结论
        }
    }
}
