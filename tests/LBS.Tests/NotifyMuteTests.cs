using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 通知静音（11-03 站长：「有些人嫌烦」）：席位自关到站播报/周报/投稿回执三路定时货。
/// 钉六件事——①广播跳过关该路的席位；②不带频道的播送（站长公告）关不掉；
/// ③单发静音不落库且频道互不牵连；④库层去污（白名单/大小写/去重/脏值/空清单/假席位）；
/// ⑤投稿回执走 MemberMail 真路径照样被闸；⑥播报员端到端——静音席不收信、水位照盖（静音≠卡账）。
/// 夹具照 ArrivalReportTests 抄：真收编路径造 active 席位，RunTick 纯函数直捣。
/// 端点薄皮不测（无 WebApplicationFactory 先例，见 DmTests 说明），白名单闸在库层同把尺子。
/// </summary>
public sealed class NotifyMuteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public NotifyMuteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-mutes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
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

    /// <summary>走真收编路径造 active 席位（首位自动继位站长，也是 active，收得到播送）。</summary>
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

    /// <summary>造一枚到站的货：submitter 传 null 就是爬工货（无归属），传席位 id 就是成员自投。</summary>
    private long Arrive(string bvid, string? title = null, long? submitter = null)
    {
        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, title ?? bvid, null, null, VideoVisibility.Public),
        });
        var id = outcome.InsertedIds[0];
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("测试UP", 123, "音乐>翻唱", "2026-05-20 18:00:00", "简介", "1万"),
            Array.Empty<PageDraft>());
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title ?? bvid, null);
        _db.MarkArrived(id);
        if (submitter is not long s)
        {
            return id;
        }

        _db.SetVideoSubmitterMember(id, s);
        return id;
    }

    private void Exec(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void 广播跳过关该路的席位()
    {
        var owner = AdmitSeat(1001, "站长");
        var seat = AdmitSeat(1002, "阿席");
        Assert.True(_db.SetNotifyMutes(seat, new[] { NotifyMutes.Arrival }));

        var sent = _db.BroadcastToActive(MemberMail.KindArrival, "分站到货 · 1 件新货", "正文", null, null, NotifyMutes.Arrival);

        Assert.Equal(1, sent); // 只送达没关的站长
        Assert.Single(_db.ListMessages(owner), m => m.Kind == MemberMail.KindArrival);
        Assert.DoesNotContain(_db.ListMessages(seat), m => m.Kind == MemberMail.KindArrival);
    }

    [Fact]
    public void 三路全关也拦不住不带频道的播送_站长公告关不掉()
    {
        AdmitSeat(1001, "站长");
        var seat = AdmitSeat(1002, "阿席");
        _db.SetNotifyMutes(seat, new[] { NotifyMutes.Arrival, NotifyMutes.Weekly, NotifyMutes.Submit });

        // 站长公告那路压根不传频道（见 MemberMessageEndpoints 的 announce）——全员静音也照达每席。
        var sent = _db.BroadcastToActive(MemberMail.KindAnnounce, "全站大事件", "正文");

        Assert.Equal(2, sent);
    }

    [Fact]
    public void 单发静音不落库_频道之间不牵连()
    {
        var seat = AdmitSeat(1001, "站长"); // 首位即站长，单席位也够钉单发路
        _db.SetNotifyMutes(seat, new[] { NotifyMutes.Submit });

        var muted = _db.SendToMember(seat, MemberMail.KindComplete, "投稿已入档", "正文", "/video/BVmute00001",
            muteChannel: NotifyMutes.Submit);
        Assert.Equal(0, muted); // 没落库：id 从 1 起，0＝静默收手
        // 首席位自带一封欢迎信，只能按 kind 数——欢迎信不在可关之列，不配当反例。
        Assert.DoesNotContain(_db.ListMessages(seat), m => m.Kind == MemberMail.KindComplete);

        // 同席位没关 arrival——到站信照进，闸只认自己那一路。
        var passed = _db.SendToMember(seat, MemberMail.KindArrival, "分站到货 · 1 件新货", "正文", null, null, null,
            muteChannel: NotifyMutes.Arrival);
        Assert.True(passed > 0);

        // 不带频道的信（欢迎信那类）不受任何静音影响。
        var open = _db.SendToMember(seat, MemberMail.KindWelcome, "欢迎来到小破分站", "正文", "/me");
        Assert.True(open > 0);
    }

    [Fact]
    public void 库层去污_白名单外不收_脏值忽略_空清单落NULL()
    {
        var seat = AdmitSeat(1001, "站长");

        // 白名单 + 修边小写去重：一次写齐。
        Assert.True(_db.SetNotifyMutes(seat, new[] { " ARRIVAL ", "arrival", "weekly", "hacker" }));
        Assert.Equal(new[] { NotifyMutes.Arrival, NotifyMutes.Weekly }, _db.GetNotifyMutes(seat));

        // 库里直接抹出脏值（历史手改事故）：读取时未知频道无声忽略，known 的照认。
        Exec($"UPDATE members SET notify_mutes = 'arrival,bogus,,' WHERE id = {seat};");
        Assert.Equal(new[] { NotifyMutes.Arrival }, _db.GetNotifyMutes(seat));

        // 空清单落 NULL（不留空串小尾巴），读回全开。
        Assert.True(_db.SetNotifyMutes(seat, Array.Empty<string>()));
        using (var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile}"))
        {
            connection.Open();
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT notify_mutes IS NULL FROM members;";
            Assert.Equal(1L, Convert.ToInt64(check.ExecuteScalar()));
        }

        Assert.Empty(_db.GetNotifyMutes(seat));
        Assert.False(_db.SetNotifyMutes(99999, new[] { NotifyMutes.Arrival })); // 席位不在，招呼没处打
    }

    [Fact]
    public void 投稿回执走送信员真路径_关了submit就哑火()
    {
        var seat = AdmitSeat(1001, "站长");
        var first = Arrive("BVmute00010", "没关之前的稿", submitter: seat);
        MemberMail.NotifyVideoArrived(_db, null, first);
        Assert.Single(_db.ListMessages(seat), m => m.Kind == MemberMail.KindComplete);

        _db.SetNotifyMutes(seat, new[] { NotifyMutes.Submit });
        var second = Arrive("BVmute00011", "关路之后的稿", submitter: seat);
        MemberMail.NotifyVideoArrived(_db, null, second);
        Assert.Single(_db.ListMessages(seat), m => m.Kind == MemberMail.KindComplete); // 没添新账
    }

    [Fact]
    public void 播报员端到端_静音席不收信_水位照盖()
    {
        var owner = AdmitSeat(1001, "站长");
        var seat = AdmitSeat(1002, "阿席");
        Assert.Null(ArrivalReportWorker.RunTick(_db, null)); // 空仓过首岗（水位垫到 0）

        var videoId = Arrive("BVmute00020", "静音试验货");
        _db.SetNotifyMutes(seat, new[] { NotifyMutes.Arrival });

        var sent = ArrivalReportWorker.RunTick(_db, null);

        Assert.Equal(1, sent); // 送达 1 席：站长收信，阿席那路被自家闸拦下
        Assert.NotNull(_db.ListMessages(owner).FirstOrDefault(m => m.Kind == MemberMail.KindArrival));
        Assert.DoesNotContain(_db.ListMessages(seat), m => m.Kind == MemberMail.KindArrival);
        // 静音≠卡账：水位照盖，下批新货照常推进，别让关了通知的人把播报员也拖停。
        Assert.Equal(videoId.ToString(), _db.GetSetting(ArrivalReportWorker.WatermarkKey));
    }
}
