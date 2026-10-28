using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>
/// 回收站：删馆藏改成「文件搬走 + 六张表的行连主键封存」，站长可恢复、可单件销毁、可清空。
/// 这里钉的是最要紧的几条：搬走必须真搬走（用户侧查无此片）、恢复必须原样回来（连站内弹幕的 id 都不许变）、
/// 撞号要让路（不能把同名馆藏搅浑）、销毁必须真没了。
/// </summary>
public sealed class RecycleBinTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly string _recycleDir;
    private readonly StationDb _db;

    public RecycleBinTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-recycle-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        _recycleDir = Path.Combine(_tempDir, "recycle");
        Directory.CreateDirectory(_videosDir);
        _db = new StationDb(new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir));
        _db.Initialize();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    private long AddVideo(string bvid) =>
        _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;

    /// <summary>造一条到站馆藏：正片 + ass + 封面 + 弹幕清单四个文件，一个分 P，一条站内弹幕。</summary>
    private long SeedArrived(string bvid, out string basePath)
    {
        var videoId = AddVideo(bvid);
        var dir = Path.Combine(_videosDir, bvid);
        Directory.CreateDirectory(dir);
        basePath = Path.Combine(dir, "1-正片-1080P-AVC");
        File.WriteAllText(basePath + ".mp4", "video-bytes");
        File.WriteAllText(basePath + ".ass",
            "Dialogue: 0,0:00:01.00,0:00:06.00,默认,,0,0,0,,老弹幕一\nDialogue: 0,0:00:02.00,0:00:07.00,默认,,0,0,0,,老弹幕二");
        File.WriteAllText(basePath + ".Cover.jpg", "cover-bytes");
        File.WriteAllText(basePath + ".danmaku.json", """{"schema":1,"legacyDialogue":0,"danmakus":[]}""");

        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP主小明", 123, "音乐", "2023-05-20 18:00:00", "简介文本", "1234万"),
            new[] { new PageDraft("task-" + bvid, 1, "正片", 456, "04:13", basePath) });
        var (archived, missing) = _db.FinalizeArtifacts(videoId, _videosDir);
        Assert.Equal(0, missing);
        // 正片 / ass / 封面三个会被台账收走；.danmaku.json 不在扫盘分类的名单里（产物表里没有它），
        // 正好留给回收站的前缀补扫去接——收站时要连它一起捣走。
        Assert.Equal(3, archived);
        _db.AddLocalDanmaku(videoId, 1, 3.5, "站内朋友发的弹幕", "dm-1", false);
        return videoId;
    }

    [Fact]
    public void 收进回收站_文件搬走且用户侧查无此片()
    {
        var videoId = SeedArrived("BV1recycle01", out var basePath);
        Assert.NotNull(_db.GetVideoBrief(videoId));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(basePath)!));

        var moved = _db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长小花", commentCount: 7);

        Assert.True(moved.Ok, moved.Message);
        Assert.Equal(4, moved.Files);

        // 台账主行没了：列表、搜索、相关推荐、观看页都查不到它。
        Assert.Null(_db.GetVideoBrief(videoId));
        Assert.Null(_db.GetWatchData("BV1recycle01"));
        Assert.Empty(_db.ListLocalDanmaku(videoId));
        Assert.Empty(_db.GetDanmakuPageTargets(videoId));

        // 文件真从 videos 里搬走了，连正片目录的空壳一并清掉。
        Assert.False(Directory.Exists(Path.GetDirectoryName(basePath)!));
        Assert.False(File.Exists(basePath + ".mp4"));

        var entry = Assert.Single(_db.ListRecycle());
        Assert.Equal(videoId, entry.VideoId);
        Assert.Equal("BV1recycle01", entry.Bvid);
        Assert.Equal("站长小花", entry.DeletedBy);
        Assert.Equal(4, entry.FileCount);
        Assert.Equal(1, entry.PageCount);
        Assert.Equal(1, entry.DanmakuCount);
        Assert.Equal(7, entry.CommentCount);
        Assert.True(entry.Bytes > 0);

        // 文件躺在回收站的分间里，不在 /media 静态区。
        var payload = Path.Combine(_recycleDir, entry.Id.ToString(), "files", "BV1recycle01", "1-正片-1080P-AVC.mp4");
        Assert.True(File.Exists(payload));
    }

    [Fact]
    public void 回收站往返_行与文件原样回来_站内弹幕id不变()
    {
        var videoId = SeedArrived("BV1recycle02", out var basePath);
        var danmakuId = Assert.Single(_db.ListLocalDanmaku(videoId)).Id;
        var moved = _db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0);
        Assert.True(moved.Ok, moved.Message);
        var entry = Assert.Single(_db.ListRecycle());

        var restored = _db.RestoreFromRecycle(entry.Id, _videosDir, _recycleDir);

        Assert.True(restored.Ok, restored.Message);
        Assert.Equal(4, restored.Files);
        Assert.Empty(_db.ListRecycle());

        // 主行按原编号塞回，观看链路重新亮起来。
        var brief = _db.GetVideoBrief(videoId);
        Assert.NotNull(brief);
        Assert.Equal("BV1recycle02", brief.Value.Bvid);
        var data = _db.GetWatchData("BV1recycle02");
        Assert.NotNull(data);
        Assert.Equal("/media/BV1recycle02/1-正片-1080P-AVC.mp4", Assert.Single(data.Pages).VideoUrl);

        // 站内弹幕的 id 都没变：朋友发的那条还是同一条，删前删后一个样。
        var back = Assert.Single(_db.ListLocalDanmaku(videoId));
        Assert.Equal(danmakuId, back.Id);
        Assert.Equal("站内朋友发的弹幕", back.Text);

        // 文件回到原位，回收站分间清空。
        Assert.True(File.Exists(basePath + ".mp4"));
        Assert.Equal("video-bytes", File.ReadAllText(basePath + ".mp4"));
        Assert.True(File.Exists(basePath + ".ass"));
        Assert.False(Directory.Exists(Path.Combine(_recycleDir, entry.Id.ToString())));
    }

    [Fact]
    public void 同名馆藏已经回来_恢复就让路()
    {
        var videoId = SeedArrived("BV1recycle03", out _);
        var moved = _db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0);
        Assert.True(moved.Ok, moved.Message);
        var entry = Assert.Single(_db.ListRecycle());

        // 有人按同一个 BV 号重新投过一次：这时恢复会把两份搅成一锅，让路才是对的。
        AddVideo("BV1recycle03");

        var restored = _db.RestoreFromRecycle(entry.Id, _videosDir, _recycleDir);

        Assert.False(restored.Ok);
        Assert.Contains("已经回到分站", restored.Message);
        var still = Assert.Single(_db.ListRecycle());
        Assert.Equal(entry.Id, still.Id);
    }

    [Fact]
    public void 单件销毁_文件与清单一起没了()
    {
        var videoId = SeedArrived("BV1recycle04", out _);
        _db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0);
        var entry = Assert.Single(_db.ListRecycle());

        Assert.True(_db.PurgeRecycle(entry.Id, _recycleDir));
        Assert.Empty(_db.ListRecycle());
        Assert.False(Directory.Exists(Path.Combine(_recycleDir, entry.Id.ToString())));

        // 没了就是没了，捞不回来。
        var restored = _db.RestoreFromRecycle(entry.Id, _videosDir, _recycleDir);
        Assert.False(restored.Ok);
        Assert.False(_db.PurgeRecycle(entry.Id, _recycleDir));
    }

    [Fact]
    public void 清空回收站_件数如实回报()
    {
        var first = SeedArrived("BV1recycle05", out _);
        var second = SeedArrived("BV1recycle06", out _);
        _db.MoveVideoToRecycle(first, _videosDir, _recycleDir, "站长", 0);
        _db.MoveVideoToRecycle(second, _videosDir, _recycleDir, "站长", 0);
        Assert.Equal(2, _db.ListRecycle().Count);

        Assert.Equal(2, _db.PurgeAllRecycle(_recycleDir));
        Assert.Empty(_db.ListRecycle());
        Assert.Empty(Directory.GetDirectories(_recycleDir));

        // 再按一次不炸，只说清掉了 0 件。
        Assert.Equal(0, _db.PurgeAllRecycle(_recycleDir));
    }

    [Fact]
    public void 封面隔着站长的门递出去()
    {
        var videoId = SeedArrived("BV1recycle07", out _);
        _db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0);
        var entry = Assert.Single(_db.ListRecycle());

        var cover = _db.ReadRecycleCover(entry.Id, _recycleDir);

        Assert.NotNull(cover);
        Assert.Equal("cover-bytes", System.Text.Encoding.UTF8.GetString(cover));
        Assert.Null(_db.ReadRecycleCover(999_999, _recycleDir));
    }

    [Fact]
    public void 馆藏不存在时收站不动任何文件()
    {
        var moved = _db.MoveVideoToRecycle(4242, _videosDir, _recycleDir, "站长", 0);

        Assert.False(moved.Ok);
        Assert.Empty(_db.ListRecycle());
        Assert.False(Directory.Exists(_recycleDir));
    }
}
