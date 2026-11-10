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
        var sub = Assert.Single(page.SubtitleUrls); // 老单轨时代只有无名默认副本：照列，label 交 null 吃前端笨名
        Assert.Equal("/media/测试标题/1-正片-1080P-AVC.srt", sub.Url);
        Assert.Null(sub.Label);
        Assert.Equal("/media/测试标题/1-正片-1080P-AVC.Cover.jpg", data.CoverUrl); // 本地封面优先

        // 重扫安全：不重复归档
        var (again, _) = _db.FinalizeArtifacts(videoId, _videosDir);
        Assert.Equal(0, again);
    }

    [Fact]
    public void 多轨字幕扫盘_真名落库_无名默认副本不撞面板()
    {
        // BV1Q44v6RE2V 实机形态：接口回三轨，引擎写盘带 _{lan_doc} 尾，另备一枚无名默认副本。
        // 早年扫盘只认全等 .srt，两枚闸叠着只剩一轨无名（站长 11-11 实机报的这单）。
        var videoId = AddVideo("BV1Q44v6RE2V");
        var dir = Path.Combine(_videosDir, "多轨测试");
        Directory.CreateDirectory(dir);
        var @base = Path.Combine(dir, "1-正片-1080P-AVC");
        File.WriteAllText(@base + ".mp4", "video");
        File.WriteAllText(@base + ".srt", "default-copy"); // 给外部播放器自动挂载的无名默认副本
        File.WriteAllText(@base + "_中文.srt", "sub-zh");
        File.WriteAllText(@base + "_English_1234567.srt", "sub-en"); // 同语言撞车追 trackId 尾的形状
        File.WriteAllText(@base + "_日本語.srt", "sub-ja");
        File.WriteAllText(@base + ".comments.json", "[]"); // 中缀 json 不是字幕轨，误吞就是脏账

        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时间", "简介", "播放"),
            new[] { new PageDraft("task-1", 1, "正片", 456, "03:30", @base) });

        var (archived, missing) = _db.FinalizeArtifacts(videoId, _videosDir);
        Assert.Equal(0, missing);
        Assert.Equal(5, archived); // mp4 + 无名副本 + 三枚真名轨；.comments.json 不登

        var page = Assert.Single(_db.GetWatchData("BV1Q44v6RE2V")!.Pages);
        Assert.Equal(3, page.SubtitleUrls.Count); // 有带名轨就只列带名轨，无名副本退场不撞车
        Assert.Equal(
            new[] { "English", "中文", "日本語" },
            page.SubtitleUrls.Select(t => t.Label).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(page.SubtitleUrls, t => Assert.EndsWith(".srt", t.Url));

        // 重扫安全：不重复登记、不抹已有的名
        var (again, _) = _db.FinalizeArtifacts(videoId, _videosDir);
        Assert.Equal(0, again);
        page = Assert.Single(_db.GetWatchData("BV1Q44v6RE2V")!.Pages);
        Assert.Equal(3, page.SubtitleUrls.Count);
    }

    [Fact]
    public void 字幕轨名解析_只认轨尾_脏形交空不吃亏()
    {
        // 认名的边界全挂在扫盘这一趟：撞车尾剔纯数字；脏形（纯数字尾、超长尾）认不出交 null。
        // 另一枚闸也一并钉死：有带名轨时无名轨不上台（SubtitleTracksOf 的去重规则）。
        var videoId = AddVideo("BV1TrackLabel1");
        var dir = Path.Combine(_videosDir, "轨名边界");
        Directory.CreateDirectory(dir);
        var @base = Path.Combine(dir, "1-正片-1080P-AVC");
        File.WriteAllText(@base + ".mp4", "video");
        File.WriteAllText(@base + "_中文_998877.srt", "撞车尾");
        File.WriteAllText(@base + "_999.srt", "纯数字尾");

        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时间", "简介", "播放"),
            new[] { new PageDraft("task-1", 1, "正片", 456, "03:30", @base) });
        _db.FinalizeArtifacts(videoId, _videosDir);

        var page = Assert.Single(_db.GetWatchData("BV1TrackLabel1")!.Pages);
        var zh = Assert.Single(page.SubtitleUrls); // 脏形无名被带名轨挤下台（默认副本同一条退场规则）
        Assert.Equal("中文", zh.Label); // 撞车尾剔纯数字 trackId，只留真名
    }

    [Fact]
    public void 字幕全无名_脏形也照列_吃笨名兜底()
    {
        // 纯数字尾与 41 字超长尾都不像名：认不出交 null，但不得被拒登——
        // 宁可屏上摆「字幕 1/2」笨名，也不能把真存在的轨藏进盘里看不见。
        var videoId = AddVideo("BV1TrackDirty1");
        var dir = Path.Combine(_videosDir, "脏形轨");
        Directory.CreateDirectory(dir);
        var @base = Path.Combine(dir, "1-正片-1080P-AVC");
        File.WriteAllText(@base + ".mp4", "video");
        File.WriteAllText(@base + "_一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十一.vtt", "超长尾"); // 41 字，不像名
        File.WriteAllText(@base + "_999.srt", "纯数字尾");

        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时间", "简介", "播放"),
            new[] { new PageDraft("task-1", 1, "正片", 456, "03:30", @base) });
        _db.FinalizeArtifacts(videoId, _videosDir);

        var page = Assert.Single(_db.GetWatchData("BV1TrackDirty1")!.Pages);
        Assert.Equal(2, page.SubtitleUrls.Count); // 全无名 → 照列全量，前端吃「字幕 N」笨名
        Assert.All(page.SubtitleUrls, t => Assert.Null(t.Label));
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
