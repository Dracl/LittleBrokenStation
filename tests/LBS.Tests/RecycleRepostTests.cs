using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>
/// 删档之后「同一个 BV 号再来一次」的几种走法：进回收站再投、彻底销毁再投、两件同 BV 交叉恢复、回收站目录被人手动清掉。
/// 这里钉的是账目归属：回收站那份带着六张表的行按 video_id 过日子，重新投的是另一条新行，
/// 抽屉条目、观看进度、站内弹幕都跟着旧行走，新行不继承——这是取舍而非漏洞，钉住免得日后被当成 bug 改坏。
/// </summary>
public sealed class RecycleRepostTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly string _recycleDir;
    private readonly StationDb _db;

    public RecycleRepostTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-repost-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>造一条到站馆藏：正片 / ass / 封面 / 弹幕旁挂四个文件，一个分 P，标到 Arrived。</summary>
    private long SeedArrived(string bvid, out string basePath, string marker = "first")
    {
        var videoId = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        var dir = Path.Combine(_videosDir, bvid);
        Directory.CreateDirectory(dir);
        basePath = Path.Combine(dir, "1-正片-1080P-AVC");
        File.WriteAllText(basePath + ".mp4", $"video-bytes-{marker}-{bvid}");
        File.WriteAllText(basePath + ".ass", "Dialogue: 0,0:00:01.00,0:00:06.00,默认,,0,0,0,,老弹幕一");
        File.WriteAllText(basePath + ".Cover.jpg", "cover-bytes-" + marker);
        File.WriteAllText(basePath + ".danmaku.json", """{"schema":1,"legacyDialogue":0,"danmakus":[]}""");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP主小明", 123, "音乐", "2023-05-20 18:00:00", "简介文本", "1234万"),
            new[] { new PageDraft("task-" + marker + "-" + bvid, 1, "正片", 456, "04:13", basePath) });
        _db.FinalizeArtifacts(videoId, _videosDir);
        _db.MarkArrived(videoId);
        return videoId;
    }

    [Fact]
    public void 删进回收站后再投同一个BV_查重放行且拿到新编号()
    {
        var oldId = SeedArrived("BV1repost01", out _);
        Assert.True(_db.MoveVideoToRecycle(oldId, _videosDir, _recycleDir, "站长", 0).Ok);

        var outcome = _db.AddVideo("https://www.bilibili.com/video/BV1repost01", "BV1repost01");

        // 投稿查重只翻 videos 表，回收站里躺着的那件不算「已经到过」——所以再投一定放行。
        Assert.Equal(AddVideoResult.Inserted, outcome.Result);
        Assert.True(outcome.VideoId > 0);
        Assert.NotEqual(oldId, outcome.VideoId);
        Assert.Equal(outcome.VideoId, _db.GetVideoIdByBvid("BV1repost01"));

        // 旧那件不会自动消失，继续躺在回收站里当备份。
        var entry = Assert.Single(_db.ListRecycle());
        Assert.Equal(oldId, entry.VideoId);
    }

    [Fact]
    public void 再投的那份不继承抽屉条目与观看进度_这些账跟着旧行走()
    {
        var oldId = SeedArrived("BV1repost02", out _);
        var (memberId, _) = _db.CreateMemberInvite("席位二号");
        var collection = _db.CreateCollection("睡前听", null, memberId);
        Assert.True(_db.AddItem(collection, oldId));
        _db.UpsertVideoHistory(memberId, oldId, 1, 30, 240);
        _db.AddLocalDanmaku(oldId, 1, 3.5, "站内朋友发的弹幕", "dm-1", false);

        Assert.True(_db.MoveVideoToRecycle(oldId, _videosDir, _recycleDir, "站长", 0).Ok);
        var newId = _db.AddVideo("https://www.bilibili.com/video/BV1repost02", "BV1repost02").VideoId;

        // 抽屉、进度、站内弹幕都按 video_id 认人：新行是另一条命，一概不继承。
        var detail = _db.GetCollection(collection);
        Assert.NotNull(detail);
        Assert.Empty(detail.Items);
        Assert.Empty(_db.GetVideoHistory(memberId));
        Assert.Empty(_db.ListLocalDanmaku(newId));

        // 旧账没死，只是看不见：把回收站那份恢复回来，抽屉条目与进度会跟着回来（前提是没人占着这个 BV）。
        Assert.Equal(oldId, Assert.Single(_db.ListRecycle()).VideoId);
    }

    [Fact]
    public void 彻底销毁后再投_台账从头长出一行_旧账不漂回来()
    {
        var oldId = SeedArrived("BV1repost03", out var basePath);
        Assert.True(_db.MoveVideoToRecycle(oldId, _videosDir, _recycleDir, "站长", 0).Ok);
        var entry = Assert.Single(_db.ListRecycle());
        Assert.True(_db.PurgeRecycle(entry.Id, _recycleDir));

        var newId = _db.AddVideo("https://www.bilibili.com/video/BV1repost03", "BV1repost03").VideoId;

        Assert.NotEqual(oldId, newId);
        Assert.Equal(newId, _db.GetVideoIdByBvid("BV1repost03"));
        Assert.Empty(_db.ListRecycle());
        // 销毁时文件随分间一起没了，新行得重新下：同 BV 只剩这一行，没有第二份账。
        Assert.False(File.Exists(basePath + ".mp4"));
    }

    [Fact]
    public void 回收站躺着两件同BV_恢复先到的那份_第二件让路()
    {
        var firstId = SeedArrived("BV1repost04", out var basePath, "first");
        Assert.True(_db.MoveVideoToRecycle(firstId, _videosDir, _recycleDir, "站长", 0).Ok);

        // 同一个 BV 重新投一份（正片字节不同，好分辨回来的是哪一份），到站后又删一次。
        var secondId = SeedArrived("BV1repost04", out _, "second");
        Assert.True(_db.MoveVideoToRecycle(secondId, _videosDir, _recycleDir, "站长", 0).Ok);
        Assert.Equal(2, _db.ListRecycle().Count);

        var firstEntry = _db.ListRecycle().Single(e => e.VideoId == firstId);
        var secondEntry = _db.ListRecycle().Single(e => e.VideoId == secondId);

        var restoredFirst = _db.RestoreFromRecycle(firstEntry.Id, _videosDir, _recycleDir);
        Assert.True(restoredFirst.Ok, restoredFirst.Message);
        Assert.Equal("video-bytes-first-BV1repost04", File.ReadAllText(basePath + ".mp4"));

        // 第二件此时恢复就是两份搅一锅，必须让路，并留在回收站当备份。
        var restoredSecond = _db.RestoreFromRecycle(secondEntry.Id, _videosDir, _recycleDir);
        Assert.False(restoredSecond.Ok);
        Assert.Contains("已经回到分站", restoredSecond.Message);
        Assert.Single(_db.ListRecycle());
    }

    [Fact]
    public void 清空回收站不误伤在架的那份同BV新片()
    {
        var oldId = SeedArrived("BV1repost05", out _);
        Assert.True(_db.MoveVideoToRecycle(oldId, _videosDir, _recycleDir, "站长", 0).Ok);
        var newId = SeedArrived("BV1repost05", out var basePath, "second");
        Assert.NotNull(_db.GetPlaybackData("BV1repost05"));

        Assert.Equal(1, _db.PurgeAllRecycle(_recycleDir));

        // 清的是回收站，不是在架馆藏：新片的行、播放链路、盘上文件都得原样在。
        Assert.NotNull(_db.GetVideoBrief(newId));
        Assert.NotNull(_db.GetPlaybackData("BV1repost05"));
        Assert.True(File.Exists(basePath + ".mp4"));
    }

    [Fact]
    public void 回收站分间被人手动清掉_恢复照样放行但带不回文件()
    {
        var videoId = SeedArrived("BV1repost06", out var basePath);
        Assert.True(_db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0).Ok);
        var entry = Assert.Single(_db.ListRecycle());

        // 有人越过分站直接清了 data/recycle：清单还记着文件，盘上却没有了。
        Directory.Delete(Path.Combine(_recycleDir, entry.Id.ToString()), true);

        var restored = _db.RestoreFromRecycle(entry.Id, _videosDir, _recycleDir);

        // 缺件不挡恢复（清单记了但盘上没有就当本来就缺），于是账上有一行馆藏、播放指向不存在的文件。
        Assert.True(restored.Ok, restored.Message);
        Assert.Equal(0, restored.Files);
        Assert.NotNull(_db.GetPlaybackData("BV1repost06"));
        Assert.False(File.Exists(basePath + ".mp4"));
    }
}
