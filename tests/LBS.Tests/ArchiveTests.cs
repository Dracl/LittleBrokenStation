using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>M2a 归档数据层：提交档案入库、到站扫盘、观看数据组装。</summary>
public sealed class ArchiveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly StationDb _db;

    public ArchiveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-archive-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(_videosDir);
        var paths = new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
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

    [Fact]
    public void 提交档案入库_观看数据成形()
    {
        var videoId = AddVideo("BV1z341187Y9");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP主小明", 123, "音乐", "2023-05-20 18:00:00", "简介文本", "1234万"),
            new[]
            {
                new PageDraft("task-1", 1, "正片", 456, "03:30", Path.Combine(_videosDir, "测试标题", "1-正片-1080P-AVC")),
                new PageDraft("task-2", 2, "幕后", 457, "01:00", Path.Combine(_videosDir, "测试标题", "2-幕后-1080P-AVC")),
            });

        var data = _db.GetWatchData("BV1z341187Y9");

        Assert.NotNull(data);
        Assert.Equal("UP主小明", data.UpName);
        Assert.Equal("音乐", data.Zone);
        Assert.Equal(2, data.Pages.Count);
        Assert.Equal("幕后", data.Pages[1].Title);
        Assert.Null(data.Pages[0].VideoUrl); // 文件还没到站，不给假路径
        Assert.Null(data.CoverUrl);
    }

    [Fact]
    public void 到站扫盘_产物分类与观看链路()
    {
        var videoId = AddVideo("BV1GJ411x7h7");
        var dir = Path.Combine(_videosDir, "测试标题");
        Directory.CreateDirectory(dir);
        var @base = Path.Combine(dir, "1-正片-1080P-AVC");
        File.WriteAllText(@base + ".mp4", "video");
        File.WriteAllText(@base + ".ass", "danmaku");
        File.WriteAllText(@base + ".Cover.jpg", "cover");
        File.WriteAllText(@base + ".srt", "subtitle");
        File.WriteAllText(@base + ".nfo", "not-ours"); // 不该被归档

        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时间", "简介", "播放"),
            new[] { new PageDraft("task-1", 1, "正片", 456, "03:30", @base) });

        var (archived, missing) = _db.FinalizeArtifacts(videoId, _videosDir);
        Assert.Equal(0, missing);
        Assert.Equal(4, archived);

        var data = _db.GetWatchData("BV1GJ411x7h7");
        Assert.NotNull(data);
        var page = Assert.Single(data.Pages);
        Assert.Equal("/media/测试标题/1-正片-1080P-AVC.mp4", page.VideoUrl);
        Assert.Equal("/media/测试标题/1-正片-1080P-AVC.ass", page.DanmakuUrl);
        Assert.Equal(new[] { "/media/测试标题/1-正片-1080P-AVC.srt" }, page.SubtitleUrls);
        Assert.Equal("/media/测试标题/1-正片-1080P-AVC.Cover.jpg", data.CoverUrl); // 本地封面优先

        // 重扫安全：不重复归档
        var (again, _) = _db.FinalizeArtifacts(videoId, _videosDir);
        Assert.Equal(0, again);
    }

    [Fact]
    public void 缺正片会被点名()
    {
        var videoId = AddVideo("BV1xx411c7mD");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft(null, null, null, null, null, null),
            new[] { new PageDraft("task-1", 1, "丢了的分P", null, null, Path.Combine(_videosDir, "不存在的目录", "1-分P")) });

        var (_, missing) = _db.FinalizeArtifacts(videoId, _videosDir);
        Assert.Equal(1, missing);
    }

    [Fact]
    public void 未入库的BV_观看404()
    {
        Assert.Null(_db.GetWatchData("BV1noT411x7h7"));
    }

    [Fact]
    public void 大小写混合的BV号_精确命中()
    {
        // BV 号是大小写敏感的 base62，库里存什么样子就该用什么查得到（真机抓出的 bug）。
        AddVideo("BV1Ax411d7jD");
        Assert.NotNull(_db.GetWatchData("BV1Ax411d7jD"));
        Assert.Null(_db.GetWatchData("BV1AX411D7JD"));
    }

    [Fact]
    public void 删站连坐_账本文件一把清() 
    {
        var videoId = AddVideo("BV1de411x7h7");
        var dir = Path.Combine(_videosDir, "删我测试");
        Directory.CreateDirectory(dir);
        var @base = Path.Combine(dir, "1-删我-480P-AVC");
        File.WriteAllText(@base + ".mp4", "v");
        File.WriteAllText(@base + ".ass", "d");
        File.WriteAllText(@base + ".Cover.jpg", "c");

        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时", "介", "播"),
            new[] { new PageDraft("task-1", 1, "删我", 1, "00:10", @base) });
        _db.FinalizeArtifacts(videoId, _videosDir);

        var deleted = _db.DeleteVideoCascade(videoId, _videosDir);
        Assert.Equal(3, deleted);
        Assert.False(File.Exists(@base + ".mp4"));
        Assert.False(Directory.Exists(dir)); // 空目录一并清掉
        Assert.Null(_db.GetWatchData("BV1de411x7h7"));
        Assert.Null(_db.GetVideoForDelete(videoId));
    }
}
