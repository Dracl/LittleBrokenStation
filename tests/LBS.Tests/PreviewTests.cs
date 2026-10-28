using System.Text.Json;
using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace LBS.Tests;

/// <summary>
/// 悬停预览：台账 + 设定 + 卡阵下发 + 删档清预览。
/// 钉的是「谁能进名单、行怎么变、名字长什么样」这三件最容易漂的事；
/// ffmpeg 真转码不在这里测（CI 不保证有 ffmpeg），那一段靠手工实测验收。
/// </summary>
public sealed class PreviewTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly string _previewsDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;

    public PreviewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-preview-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        _previewsDir = Path.Combine(_tempDir, "previews");
        Directory.CreateDirectory(_videosDir);
        Directory.CreateDirectory(_previewsDir);
        _paths = new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
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

    /// <summary>造一条到站馆藏：磁盘上有正片、台账有 local_video_path，返回 videoId。</summary>
    private long SeedArrived(string bvid, bool withVideo = true)
    {
        var videoId = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        var dir = Path.Combine(_videosDir, bvid);
        Directory.CreateDirectory(dir);
        var basePath = Path.Combine(dir, $"1-{bvid}-1080P-AVC");
        if (withVideo)
        {
            File.WriteAllText(basePath + ".mp4", "video-bytes");
        }

        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP主小明", 123, "音乐", "2023-05-20 18:00:00", "简介文本", "1234万"),
            new[] { new PageDraft("task-" + bvid, 1, "正片", 456, "04:13", basePath) });
        _db.FinalizeArtifacts(videoId, _videosDir);
        _db.MarkArrived(videoId);
        return videoId;
    }

    [Fact]
    public void 预览建表幂等_空库总账给零()
    {
        // 建表幂等：老库开机再 Initialize 一次不该炸，也不该凭空长出账。
        _db.Initialize();
        _db.Initialize();

        Assert.Empty(_db.ListVideosNeedingPreview());
        Assert.Null(_db.GetPreview(1));
        var stats = _db.PreviewStats(inflight: 3);
        Assert.Equal(new PreviewStats(0, 0, 3, 0, 0, 0), stats);
    }

    [Fact]
    public void 预览名单_排已成交与三次失败的_认新到站优先()
    {
        var fresh = SeedArrived("BVFresh0001");
        var settled = SeedArrived("BVSettled001");
        var retryable = SeedArrived("BVRetry00001");
        var givenUp = SeedArrived("BVGivenUp001");
        SeedArrived("BVNoFile001", withVideo: false); // 台账说到了、盘上没成片 → 没料可转，不入列
        var notArrived = _db.AddVideo("u", "BVPending001").VideoId; // 还在排队 → 不算

        _db.RegisterPreviewArrived(settled, 1, "BVSettled001.mp4", 640, 360, 45.0, 1024);
        _db.MarkPreviewFailed(retryable, "ffmpeg 没答应");
        _db.MarkPreviewFailed(givenUp, "一");
        _db.MarkPreviewFailed(givenUp, "二");
        _db.MarkPreviewFailed(givenUp, "三");

        var queue = _db.ListVideosNeedingPreview();
        Assert.Equal(new[] { retryable, fresh }, queue.Select(c => c.VideoId).ToArray()); // 新到站优先，已成交与三次失败的都不在
        var first = Assert.Single(queue, c => c.VideoId == fresh);
        Assert.Equal("BVFresh0001", first.Bvid);
        Assert.Equal(1, first.PageIdx);
        Assert.EndsWith(".mp4", first.LocalVideoRel);
        Assert.StartsWith("BVFresh0001/", first.LocalVideoRel.Replace('\\', '/')); // rel 相对 data/videos

        // 每轮封顶照吃 limit
        Assert.Single(_db.ListVideosNeedingPreview(1));
        Assert.True(notArrived > 0);
    }

    [Fact]
    public void 预览转正与失败记账_Arrived行不许被失败涂掉()
    {
        var videoId = SeedArrived("BVUpsert0001");

        // 没行 → 插 Failed/attempts=1；再失败只加次数与换错话。
        _db.MarkPreviewFailed(videoId, "ffmpeg 没装");
        var failed = _db.GetPreview(videoId)!;
        Assert.Equal(StationDb.PreviewStatus.Failed, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("ffmpeg 没装", failed.LastError);
        _db.MarkPreviewFailed(videoId, "ffprobe 超时");
        Assert.Equal(2, _db.GetPreview(videoId)!.Attempts);
        Assert.Equal("ffprobe 超时", _db.GetPreview(videoId)!.LastError);

        // 成功转正：整行覆盖，状态归 Arrived、次数归零、错话抹净。
        _db.RegisterPreviewArrived(videoId, 1, "BVUpsert0001.mp4", 640, 360, 44.9, 102_400);
        var arrived = _db.GetPreview(videoId)!;
        Assert.Equal(StationDb.PreviewStatus.Arrived, arrived.Status);
        Assert.Equal(0, arrived.Attempts);
        Assert.Null(arrived.LastError);
        Assert.Equal("BVUpsert0001.mp4", arrived.RelPath);
        Assert.Equal((640, 360), (arrived.Width, arrived.Height));
        Assert.Equal(102_400, arrived.Bytes);

        // 转码偶尔失手不许把已经能播的预览涂成失败。
        _db.MarkPreviewFailed(videoId, "半路崩了");
        var stillArrived = _db.GetPreview(videoId)!;
        Assert.Equal(StationDb.PreviewStatus.Arrived, stillArrived.Status);
        Assert.Equal(0, stillArrived.Attempts);
        Assert.Null(stillArrived.LastError);

        // 重新生成一次 = 换张新底片，整行覆盖。
        _db.RegisterPreviewArrived(videoId, 2, "BVUpsert0001.mp4", 854, 480, 59.5, 204_800);
        var reworked = _db.GetPreview(videoId)!;
        Assert.Equal(2, reworked.PageIdx);
        Assert.Equal((854, 480), (reworked.Width, reworked.Height));
        Assert.Equal(204_800, reworked.Bytes);
    }

    [Fact]
    public void 预览重试_只动失败行_可按bvid点名()
    {
        var a = SeedArrived("BVResetA0001");
        var b = SeedArrived("BVResetB0001");
        var done = SeedArrived("BVResetC0001");
        _db.RegisterPreviewArrived(done, 1, "BVResetC0001.mp4", 640, 360, 45.0, 1);
        _db.MarkPreviewFailed(a, "错甲");
        _db.MarkPreviewFailed(a, "错甲");
        _db.MarkPreviewFailed(b, "错乙");

        // 点名：只清被点到的，Arrived 行原样不动（rel 与体积都还在）。
        Assert.Equal(1, _db.ResetPreviewFailures(["BVResetA0001"]));
        Assert.Equal(0, _db.GetPreview(a)!.Attempts);
        Assert.Equal("错甲", _db.GetPreview(a)!.LastError); // 错话留着当病历
        Assert.Equal(1, _db.GetPreview(b)!.Attempts);
        Assert.Equal("BVResetC0001.mp4", _db.GetPreview(done)!.RelPath);

        // 给空名单：一个不动（绝不误伤成「重置全部」）。
        Assert.Equal(0, _db.ResetPreviewFailures([]));
        Assert.Equal(0, _db.ResetPreviewFailures(["   "]));

        // 不带名单：全部 Failed 清成可重试，Arrived 行不遭殃。
        Assert.Equal(1, _db.ResetPreviewFailures(null));
        Assert.Equal(0, _db.GetPreview(b)!.Attempts);
        Assert.Equal(0, _db.ResetPreviewFailures(null));
        Assert.Equal(StationDb.PreviewStatus.Arrived, _db.GetPreview(done)!.Status);

        // 清成可重试后重新进名单。
        Assert.Contains(_db.ListVideosNeedingPreview(), c => c.VideoId == b);
    }

    [Fact]
    public void 预览设定_白名单与夹取()
    {
        var settings = NewSettings();
        Assert.True(settings.Enabled);
        Assert.Equal(360, settings.Res);
        Assert.Equal(45, settings.Seconds);
        Assert.Equal(40, settings.Batch);
        Assert.Equal(1, settings.Concurrency);
        Assert.True(settings.YieldOnDownload);

        // 清晰度只认白名单：不在名单里整单打回，KV 也不该被写脏。
        Assert.False(settings.Update(null, 999, null, null, null, null));
        Assert.Equal(360, NewSettings().Res);
        Assert.Null(_db.GetSetting(PreviewSettings.KeyRes)); // 整单打回，KV 也没被写脏

        // 范围量越界就地夹回边界（并发封顶 32，拿 99 去撞才能验到夹回）。
        Assert.True(settings.Update(false, 720, 999, 1, 99, false));
        var reloaded = NewSettings();
        Assert.False(reloaded.Enabled);
        Assert.Equal(720, reloaded.Res);
        Assert.Equal(PreviewSettings.SecondsMax, reloaded.Seconds);
        Assert.Equal(PreviewSettings.BatchMin, reloaded.Batch);
        Assert.Equal(PreviewSettings.ConcurrencyMax, reloaded.Concurrency);
        Assert.False(reloaded.YieldOnDownload);

        Assert.True(settings.Update(true, 480, 3, null, null, true));
        var clamped = NewSettings();
        Assert.Equal(480, clamped.Res);
        Assert.Equal(PreviewSettings.SecondsMin, clamped.Seconds);

        // 库里存了野值：认不出的退回默认，能认的夹回边界，总之不跟着发疯。
        _db.SetSetting(PreviewSettings.KeyRes, "abc");
        _db.SetSetting(PreviewSettings.KeySeconds, "not-a-number");
        _db.SetSetting(PreviewSettings.KeyBatch, "-7");
        _db.SetSetting(PreviewSettings.KeyConcurrency, "99");
        var sane = NewSettings();
        Assert.Equal(360, sane.Res);
        Assert.Equal(45, sane.Seconds);
        Assert.Equal(PreviewSettings.BatchMin, sane.Batch);
        Assert.Equal(PreviewSettings.ConcurrencyMax, sane.Concurrency);
    }

    [Fact]
    public void 预览件名_只可能是bvid加点mp4()
    {
        var derived = PreviewBackfillWorker.DerivePreviewRel("BV1GJ411x7h7");
        Assert.NotNull(derived);
        var rel = derived!;
        Assert.Equal("BV1GJ411x7h7.mp4", rel);
        Assert.DoesNotContain('/', rel);
        Assert.DoesNotContain('\\', rel);
        Assert.DoesNotContain(".part", rel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", rel, StringComparison.Ordinal);
        Assert.EndsWith(".mp4", rel);

        // 认不出的名字一律不当文件名使：路径花活、空白、怪字符全拒。
        Assert.Null(PreviewBackfillWorker.DerivePreviewRel("BV/../etc/passwd"));
        Assert.Null(PreviewBackfillWorker.DerivePreviewRel("BV..\\..\\evil"));
        Assert.Null(PreviewBackfillWorker.DerivePreviewRel("BV 1GJ"));
        Assert.Null(PreviewBackfillWorker.DerivePreviewRel("BV1GJ.mp4.part"));
        Assert.Null(PreviewBackfillWorker.DerivePreviewRel("正片标题"));
        Assert.Null(PreviewBackfillWorker.DerivePreviewRel("   "));
        Assert.Null(PreviewBackfillWorker.DerivePreviewRel("BV" + new string('x', 70)));
    }

    [Fact]
    public void 卡阵下发预览地址_与封面同形_搜索同源()
    {
        var with = SeedArrived("BVCard00001");
        var failed = SeedArrived("BVCard00002");
        var never = SeedArrived("BVCard00003");
        _db.RegisterPreviewArrived(with, 1, "BVCard00001.mp4", 640, 360, 45.0, 2048);
        _db.MarkPreviewFailed(failed, "ffmpeg 没答应");

        var rows = _db.ListVideos();
        Assert.Equal("/previews/BVCard00001.mp4", Assert.Single(rows, r => r.Id == with).PreviewUrl);
        Assert.Null(Assert.Single(rows, r => r.Id == failed).PreviewUrl); // Failed 行不许漏给前端
        Assert.Null(Assert.Single(rows, r => r.Id == never).PreviewUrl);

        // 搜索与列表同源同形（ReadEntries 共用，不给搜索页留第二条腿）。
        Assert.Equal("/previews/BVCard00001.mp4", Assert.Single(_db.SearchVideos("BVCard00001", null), r => r.Id == with).PreviewUrl);

        // 预览绝不进 artifacts：进了就会被缺件回炉当缺产物、把整片误判回炉重下。
        Assert.Null(_db.GetArtifactRel(with, "preview"));
        Assert.DoesNotContain(_db.GetArtifactRelPaths(with), rel => rel.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !rel.Contains('/'));
    }

    [Fact]
    public void 预览总账_体积走磁盘实扫_不数半成品()
    {
        var a = SeedArrived("BVStats0001");
        var b = SeedArrived("BVStats0002");
        var c = SeedArrived("BVStats0003");
        File.WriteAllBytes(Path.Combine(_previewsDir, "BVStats0001.mp4"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(_previewsDir, "BVStats0002.mp4.part"), new byte[999]); // 在途件不算账面体积
        _db.RegisterPreviewArrived(a, 1, "BVStats0001.mp4", 640, 360, 45.0, 4096);
        _db.MarkPreviewFailed(b, "转崩了");

        var stats = _db.PreviewStats(inflight: 2);
        Assert.Equal(1, stats.Covered);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(2, stats.Inflight);
        Assert.Equal(4096, stats.Bytes);
        Assert.Equal(4096, stats.AvgBytes);
        Assert.Equal(2, stats.Pending); // b 还能重试、c 没试过

        // 清空：行全销，文件归调用方删（StationDb 只管账）。
        Assert.Equal(2, _db.DeleteAllPreviews());
        Assert.Null(_db.GetPreview(a));
        Assert.Null(_db.GetPreview(b));
    }

    [Fact]
    public void 删档顺带清预览_回收站不收派生物()
    {
        var videoId = SeedArrived("BVRecycle001");
        _db.RegisterPreviewArrived(videoId, 1, "BVRecycle001.mp4", 640, 360, 45.0, 2048);
        var previewFile = Path.Combine(_previewsDir, "BVRecycle001.mp4");
        File.WriteAllBytes(previewFile, new byte[2048]);

        var outcome = _db.MoveVideoToRecycle(videoId, _videosDir, Path.Combine(_tempDir, "recycle"), "站长", 0);
        Assert.True(outcome.Ok);

        // 预览可再生：文件与行就地抹掉，不跟着搬进回收站（恢复后补员员自己再转一段）。
        Assert.False(File.Exists(previewFile));
        Assert.Null(_db.GetPreview(videoId));
        Assert.Empty(Directory.EnumerateFiles(_previewsDir));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_tempDir, "recycle"), "*", SearchOption.AllDirectories),
            f => f.EndsWith("BVRecycle001.mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 逐件耗时_只存真测的那一手_不拿产物时长冒充()
    {
        var timed = SeedArrived("BVCost00001");
        var blind = SeedArrived("BVCost00002");

        // 量到耗时的那手：2.34 秒存进去就得取回 2.34，与产物时长 45 各待各的列。
        _db.RegisterPreviewArrived(timed, 1, "BVCost00001.mp4", 640, 360, 45.0, 1_130_000, 2.34);
        var row = _db.GetPreview(timed)!;
        Assert.Equal(2.34, row.CostSeconds!.Value, 2);
        Assert.Equal(45.0, row.Seconds!.Value, 2);

        // 没量到（老路径 / 老行）就留 null，绝不拿 45 顶上。
        _db.RegisterPreviewArrived(blind, 1, "BVCost00002.mp4", 640, 360, 45.0, 1_000_000);
        Assert.Null(_db.GetPreview(blind)!.CostSeconds);

        // 单件均值只吃带耗时的行：样本只算 timed 那一条。
        var unit = _db.PreviewUnit()!;
        Assert.Equal(1, unit.Count);
        Assert.Equal(2.34, unit.CostSeconds, 2);
        Assert.Equal(1_130_000, unit.Bytes);

        // 流水牌上逐件也能看到真耗时（老行那行就是 null）。
        var feed = _db.ListRecentPreviews();
        Assert.Equal(2.34, Assert.Single(feed, f => f.Bvid == "BVCost00001").CostSeconds!.Value, 2);
        Assert.Null(Assert.Single(feed, f => f.Bvid == "BVCost00002").CostSeconds);
    }

    [Fact]
    public void 单件均值_没一条实测就回零个样本_不许编()
    {
        var a = SeedArrived("BVUnit00001");
        var b = SeedArrived("BVUnit00002");
        var c = SeedArrived("BVUnit00003");

        // 全没耗时（等于升级前的老库）：整块回 null，前端退回推算轨。
        _db.RegisterPreviewArrived(a, 1, "BVUnit00001.mp4", 640, 360, 45.0, 1024);
        _db.RegisterPreviewArrived(b, 1, "BVUnit00002.mp4", 640, 360, 45.0, 2048);
        Assert.Null(_db.PreviewUnit());

        // 三条实测：均值 2.0 秒 / 2000 字节，条数 3。
        _db.RegisterPreviewArrived(a, 1, "BVUnit00001.mp4", 640, 360, 45.0, 1000, 1.0);
        _db.RegisterPreviewArrived(b, 1, "BVUnit00002.mp4", 640, 360, 45.0, 2000, 2.0);
        _db.RegisterPreviewArrived(c, 1, "BVUnit00003.mp4", 640, 360, 45.0, 3000, 3.0);
        var unit = _db.PreviewUnit()!;
        Assert.Equal(3, unit.Count);
        Assert.Equal(2.0, unit.CostSeconds, 2);
        Assert.Equal(2000, unit.Bytes);
        Assert.Equal(45.0, unit.Seconds, 1);

        // 失败的行不算样本：只数 Arrived。
        _db.MarkPreviewFailed(SeedArrived("BVUnit00004"), "ffmpeg 没应");
        Assert.Equal(3, _db.PreviewUnit()!.Count);
    }

    [Fact]
    public void 点名补件_无视三次上限与已有成片_没料的不响()
    {
        var settled = SeedArrived("BVForce0001"); // 已有 Arrived 行
        var givenUp = SeedArrived("BVGivenForced"); // 连吃三次被搁下
        SeedArrived("BVForceNoFile", withVideo: false); // 盘上没成片
        _db.RegisterPreviewArrived(settled, 1, "BVForce0001.mp4", 640, 360, 45.0, 2048, 1.5);
        _db.MarkPreviewFailed(givenUp, "一");
        _db.MarkPreviewFailed(givenUp, "二");
        _db.MarkPreviewFailed(givenUp, "三");

        // 常规名单：两者都不在（已成交 + 已搁下）。
        Assert.DoesNotContain(_db.ListVideosNeedingPreview(), c => c.VideoId == settled || c.VideoId == givenUp);

        // 点名：两个都排进来（不重新计数 attempts、也不管已有成片）；没成片的与不存在的 bvid 直接跳过。
        var queue = _db.ListPreviewCandidatesByBvids(["BVForce0001", "BVGivenForced", "BVForceNoFile", "BVNotExist000"]);
        Assert.Equal(new[] { givenUp, settled }, queue.Select(c => c.VideoId).ToArray()); // 新到站优先
        Assert.All(queue, c => Assert.EndsWith(".mp4", c.LocalVideoRel));

        // 空名单 / 全空白：一个也不动，不得退化成「补全部」。
        Assert.Empty(_db.ListPreviewCandidatesByBvids([]));
        Assert.Empty(_db.ListPreviewCandidatesByBvids(["", "  "]));
    }

    [Fact]
    public void 设定入参_前端发的那个包要能绑上字段()
    {
        // 前端 savePvSettings 发的那一串字段名就是这条线的契约：名字对不上不报错也不警告，
        // 只会当成「这一项没改」——「下载中让路」开关拔不动就是这么来的（曾写成 yield）。
        var wire = """{"enabled":false,"res":480,"seconds":60,"batch":20,"concurrency":2,"yieldOnDownload":false}""";
        var req = JsonSerializer.Deserialize<PreviewEndpoints.PreviewSettingsRequest>(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(req);
        Assert.False(req!.Enabled);
        Assert.Equal(480, req.Res);
        Assert.Equal(60, req.Seconds);
        Assert.Equal(20, req.Batch);
        Assert.Equal(2, req.Concurrency);
        Assert.False(req.YieldOnDownload);

        // 旧字段名 yield 认不出来：改名必须前后端一起改，这条断言就是那根提醒线。
        var legacy = JsonSerializer.Deserialize<PreviewEndpoints.PreviewSettingsRequest>("""{"yield":false}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Null(legacy!.YieldOnDownload);
    }

    private PreviewSettings NewSettings() => new(_db, NullLogger<PreviewSettings>.Instance);
}
