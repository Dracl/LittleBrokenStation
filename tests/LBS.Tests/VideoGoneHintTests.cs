using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>
/// 「片子已下架」的三处死活标志：私信分享卡、投稿归档回执、站长室举报柜的 bvid 链。
/// 这三处的数据都是定格快照（发出去那一刻抄进 dm_messages / messages / danmaku_reports），
/// 片子进回收站后卡面不会变，所以死活必须由读路径现算一枚，谓词跟站内其余地方一致：videos 有行且 status='Arrived'。
/// </summary>
public sealed class VideoGoneHintTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly string _recycleDir;
    private readonly StationDb _db;

    public VideoGoneHintTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-gone-" + Guid.NewGuid().ToString("N"));
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

    private long SeedArrived(string bvid)
    {
        var videoId = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.MarkArrived(videoId);
        return videoId;
    }

    private long SeedMember(string name)
    {
        var (id, _) = _db.CreateMemberInvite(name);
        return id;
    }

    [Fact]
    public void 私信分享卡_片子在架是活_进了回收站就标死()
    {
        var bvid = "BV1gone01";
        var videoId = SeedArrived(bvid);
        var me = SeedMember("分享的人");
        var peer = SeedMember("收卡的人");
        _db.SendDm(me, peer, "这片子好", "video", bvid, """{"type":"video","bvid":"BV1gone01","title":"好片"}""");

        var alive = Assert.Single(_db.ListDmConversation(me, peer));
        Assert.Equal(true, alive.VideoInStation);

        Assert.True(_db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0).Ok);

        // 卡上的标题与封面是定格快照，不会跟着变；变的只有这枚死活。
        var gone = Assert.Single(_db.ListDmConversation(me, peer));
        Assert.Equal(false, gone.VideoInStation);
        Assert.NotNull(gone.DynJson);
    }

    [Fact]
    public void 动态型分享不掺和_它比的是动态号不是BV()
    {
        var me = SeedMember("发动态卡的人");
        var peer = SeedMember("收卡的人");
        _db.SendDm(me, peer, "看这条动态", "dynamic", "118800123456789", """{"type":"dynamic","id":"118800123456789"}""");

        // dynamic 型的 dynamic_id 存的是真动态 id，拿去比 bvid 会一律标成下架，kind 闸门必须挡住。
        var row = Assert.Single(_db.ListDmConversation(me, peer));
        Assert.Null(row.VideoInStation);
    }

    [Fact]
    public void 归档回执_片在架给链接_片没了标下架_公告信不参与()
    {
        var bvid = "BV1gone02";
        var videoId = SeedArrived(bvid);
        var member = SeedMember("投稿席位");
        _db.SendToMember(member, "complete", "投稿已入档", "正片弹幕字幕封面都已上架", $"/watch/{bvid}");
        _db.SendToMember(member, "announce", "全站播送", "今晚加班", null);

        var receipt = Assert.Single(_db.ListMessages(member), m => m.Kind == "complete");
        Assert.Equal(true, receipt.VideoInStation);
        var notice = Assert.Single(_db.ListMessages(member), m => m.Kind == "announce");
        Assert.Null(notice.VideoInStation); // 不携片链的信不参与判定，前端不该给它挂提示

        Assert.True(_db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0).Ok);

        var gone = Assert.Single(_db.ListMessages(member), m => m.Kind == "complete");
        Assert.Equal(false, gone.VideoInStation);
        Assert.Equal($"/watch/{bvid}", gone.Link); // 链接原样留着，只是标成点不开
    }

    [Fact]
    public void 举报柜的BV链_按在架与否给死活_快照列一个字不改()
    {
        var bvid = "BV1gone03";
        var videoId = SeedArrived(bvid);
        var reporter = SeedMember("举报席位");
        var dmId = _db.AddLocalDanmaku(videoId, 1, 3.5, "这条弹幕有问题", "dm-sender-1", false);
        var snapshot = _db.GetLocalDanmakuSnapshot(dmId);
        Assert.NotNull(snapshot);
        var (created, _) = _db.AddDanmakuReport(snapshot!, bvid, "路人甲", reporter, "路人甲", "骂人");
        Assert.True(created);

        var alive = Assert.Single(_db.ListDanmakuReports(true));
        Assert.True(alive.VideoInStation);

        Assert.True(_db.MoveVideoToRecycle(videoId, _videosDir, _recycleDir, "站长", 0).Ok);

        var gone = Assert.Single(_db.ListDanmakuReports(true));
        Assert.False(gone.VideoInStation);
        // 单子上的字（弹幕文本、举报理由、BV 号）是定格快照，删片不许拖它下水。
        Assert.Equal("这条弹幕有问题", gone.Text);
        Assert.Equal(bvid, gone.Bvid);
        Assert.Equal("骂人", gone.Reason);
    }
}
