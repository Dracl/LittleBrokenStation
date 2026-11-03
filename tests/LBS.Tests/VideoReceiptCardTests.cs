using System.Text.Json;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 入档回执卡面（站长 11-03：「这条通知也做个排版吧，现在太难看了」）：
/// 归档回执从纯文本字坨升级为单件卡——msg_type='video_receipt' + payload_json 定格快照，
/// 载荷形制与到站播报的到货卡同源（一件也是数组），前端同一套行版式接两家。
/// 钉三件事——①回执带得出卡（msgType/payload/link/body 兜底四样齐）；
/// ②submit 静音闸在加卡之后照样生效（新参不挤掉闸）；③查无此件取料回 null（送信员退回纯文本的底气）。
/// 夹具照 ArrivalReportTests 抄。
/// </summary>
public sealed class VideoReceiptCardTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public VideoReceiptCardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
        _gate = new MemberGate(_db);
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

    private static BilibiliIdentity Identity(long mid, string name = "小张") => new(mid, name, "https://face/y.jpg");

    private long AdmitSeat(long mid, string name = "小张")
    {
        if (!_db.HasOwner())
        {
            MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, name), invite: null, "SESSDATA=a");
        }
        else
        {
            var (_, code) = _db.CreateMemberInvite(null);
            MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, name), code, "SESSDATA=a");
        }

        return _db.GetMemberByMid(mid)!.Id;
    }

    /// <summary>成员自投的货到站（有归属行，正是归档回执的发信对象）。</summary>
    private long Arrive(string bvid, string title, long submitter)
    {
        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, title, "https://cover/hot.jpg", null, VideoVisibility.Public),
        });
        var id = outcome.InsertedIds[0];
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("测试UP", 123, "音乐>翻唱", "2026-05-20 18:00:00", "简介", "1万"),
            Array.Empty<PageDraft>());
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title, null);
        _db.MarkArrived(id);
        _db.SetVideoSubmitterMember(id, submitter);
        return id;
    }

    [Fact]
    public void 归档回执带卡面_载荷齐_body照旧兜底()
    {
        var seat = AdmitSeat(1001, "站长");
        var videoId = Arrive("BVrcpt000001", "入档卡面试验稿", submitter: seat);

        MemberMail.NotifyVideoArrived(_db, null, videoId);

        var letter = Assert.Single(_db.ListMessages(seat), m => m.Kind == MemberMail.KindComplete);
        Assert.Equal(MemberMail.MsgTypeVideoReceipt, letter.MsgType);
        Assert.Equal("/watch/BVrcpt000001", letter.Link);
        Assert.Contains("完整入档", letter.Body); // 纯文本兜底照寄：渲染器认不出卡也不白屏

        var arr = JsonDocument.Parse(letter.PayloadJson!).RootElement.EnumerateArray().Single();
        Assert.Equal("BVrcpt000001", arr.GetProperty("bvid").GetString());
        Assert.Equal("入档卡面试验稿", arr.GetProperty("title").GetString());
        Assert.Equal("测试UP", arr.GetProperty("up").GetString());
        Assert.Equal("https://cover/hot.jpg", arr.GetProperty("coverUrl").GetString());
    }

    [Fact]
    public void 加了卡面_submit静音闸照样灵()
    {
        var seat = AdmitSeat(1001, "站长");
        _db.SetNotifyMutes(seat, new[] { NotifyMutes.Submit });
        var videoId = Arrive("BVrcpt000002", "静音后的投稿", submitter: seat);

        MemberMail.NotifyVideoArrived(_db, null, videoId);

        Assert.DoesNotContain(_db.ListMessages(seat), m => m.Kind == MemberMail.KindComplete);
    }

    [Fact]
    public void 查无此件_取料回null_送信员有底气退纯文本()
    {
        Assert.Null(_db.GetArrivalCardByVideoId(999999));
    }
}
