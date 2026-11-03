using System.Text.Json;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 到站播报员（<see cref="ArrivalReportWorker"/>，11-03 路线图 🔭 首件「到站通知」）：
/// 钉的是五件最容易漂的事——①判据的锋利度（Arrived + 无归属才播；Pending 空报、成员自投抢报都不许）；
/// ②防重发（水位数字比值，同一批只播一次，垃圾水位退回 0 从头播，宁重播不漏播）；
/// ③分批推进（一波 8 件封顶，剩下的下波接着播、水位逐波爬）；
/// ④卡面载荷契约（msg_type/payload_json 字段齐、老信两列 NULL 不碍事）；
/// ⑤纯文本兜底（标题 24 字截短、一件一行不挤坨）。
///
/// 夹具照 StationMessageTests（真收编路径造 active 席位）与 VisibilityGateTests（Arrive 造到站货）抄，
/// RunTick 是纯函数直捣，Timer 壳不测——与周报发报员同一套待客之道。
/// </summary>
public sealed class ArrivalReportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public ArrivalReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-arrival-" + Guid.NewGuid().ToString("N"));
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
    private long Arrive(string bvid, string? title = null, long? submitter = null, bool arrived = true)
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
        if (arrived)
        {
            _db.MarkArrived(id);
        }

        if (submitter is not long s)
        {
            return id;
        }

        _db.SetVideoSubmitterMember(id, s);
        return id;
    }

    private StationDb.InboxMessage? FirstArrival(long memberId) =>
        _db.ListMessages(memberId).FirstOrDefault(m => m.Kind == MemberMail.KindArrival);

    /// <summary>让播报员在空仓时先过完首岗垫底（水位静默盖到 0）：这几例测的是「在岗播报」，
    /// 首岗行为另有一例钉住（见 首岗存量静默垫底）。</summary>
    private void WakeWorker()
    {
        Assert.Null(ArrivalReportWorker.RunTick(_db, null));
    }

    [Fact]
    public void 新货到站播一条_每席一封_水位盖到最大id()
    {
        var owner = AdmitSeat(1001, "站长");
        var seat = AdmitSeat(1002, "阿席");
        WakeWorker();
        Arrive("BVarr0000001", "新货一号");
        Arrive("BVarr0000002", "新货二号");

        var sent = ArrivalReportWorker.RunTick(_db, null);

        // 两席（含继位站长）各收一封，恰好是 active 席数
        Assert.Equal(2, sent);
        var ownerMail = FirstArrival(owner);
        var seatMail = FirstArrival(seat);
        Assert.NotNull(ownerMail);
        Assert.NotNull(seatMail);
        Assert.Equal("分站到货 · 2 件新货", ownerMail!.Title);
        Assert.Equal(ArrivalReportWorker.MsgTypeArrivals, ownerMail.MsgType);
        // 正文兜底是一件一行：两行、带书名号
        Assert.Equal(2, ownerMail.Body.Split('\n').Length);
        Assert.Contains("《新货一号》", ownerMail.Body);
        // 水位盖到本批最大 id（数字比值，不搞字符串花活）
        Assert.Equal(_db.ListArrivalsSince(0, 50).Max(c => c.VideoId).ToString(),
            _db.GetSetting(ArrivalReportWorker.WatermarkKey));
    }

    [Fact]
    public void 同一批不重播_新货再来才再播()
    {
        AdmitSeat(1001);
        WakeWorker();
        Arrive("BVarr0000010", "第一批");

        Assert.NotNull(ArrivalReportWorker.RunTick(_db, null));
        Assert.Null(ArrivalReportWorker.RunTick(_db, null)); // 同一批连巡两次：第二次哑火
        var member = _db.GetMemberByMid(1001)!;
        Assert.Single(_db.ListMessages(member.Id), m => m.Kind == MemberMail.KindArrival);

        Arrive("BVarr0000011", "第二批");
        Assert.NotNull(ArrivalReportWorker.RunTick(_db, null)); // 新货来了才再开腔
        Assert.Equal(2, _db.ListMessages(member.Id).Count(m => m.Kind == MemberMail.KindArrival));
    }

    [Fact]
    public void 判据锋利_Pending与成员自投都不播()
    {
        AdmitSeat(1001);
        var seat = AdmitSeat(1002, "阿席");
        WakeWorker();
        Arrive("BVarr0000020", "还在路上的", arrived: false); // Pending：货没到，播了就是空报
        Arrive("BVarr0000021", "阿席自己投的", submitter: seat); // 自投：回执归 MemberMail，通报不抢报

        Assert.Null(ArrivalReportWorker.RunTick(_db, null));
        Assert.DoesNotContain(_db.ListMessages(seat), m => m.Kind == MemberMail.KindArrival);

        Arrive("BVarr0000022", "爬工的到货"); // 只有无归属到货该播
        Assert.NotNull(ArrivalReportWorker.RunTick(_db, null));
        var mail = FirstArrival(seat);
        Assert.NotNull(mail);
        Assert.Contains("爬工的到货", mail!.Body);
        Assert.DoesNotContain("阿席自己投的", mail.Body);
        Assert.DoesNotContain("还在路上的", mail.Body);
    }

    [Fact]
    public void 超容量分批播_水位逐波爬()
    {
        AdmitSeat(1001);
        WakeWorker();
        for (var i = 1; i <= ArrivalReportWorker.BatchCap + 2; i++)
        {
            Arrive($"BVarr000{i:D4}", $"第{i}件");
        }

        var first = ArrivalReportWorker.RunTick(_db, null);
        Assert.NotNull(first);
        var member = _db.GetMemberByMid(1001)!;
        var firstMail = FirstArrival(member.Id);
        Assert.NotNull(firstMail);
        Assert.Equal($"分站到货 · {ArrivalReportWorker.BatchCap} 件新货", firstMail!.Title); // 一波封顶 8

        var second = ArrivalReportWorker.RunTick(_db, null);
        Assert.NotNull(second);
        var mails = _db.ListMessages(member.Id).Where(m => m.Kind == MemberMail.KindArrival).ToArray();
        Assert.Equal(2, mails.Length);
        Assert.Equal("分站到货 · 2 件新货", mails[0].Title); // 第二波只播剩下的 2 件（ListMessages 新→旧，mails[0] 是第二波）
        Assert.Null(ArrivalReportWorker.RunTick(_db, null)); // 第三波哑火：台账清账，水位已盖齐
    }

    [Fact]
    public void 水位认不出退回从头播_宁重播不漏播()
    {
        AdmitSeat(1001);
        Arrive("BVarr0000030", "该播的货");
        _db.SetSetting(ArrivalReportWorker.WatermarkKey, "不是数字的水位"); // 模拟存量脏值/人工乱改

        Assert.NotNull(ArrivalReportWorker.RunTick(_db, null)); // 读不出按 0：从头播，绝不静默吞货
        Assert.Equal(_db.ListArrivalsSince(0, 50).Max(c => c.VideoId).ToString(),
            _db.GetSetting(ArrivalReportWorker.WatermarkKey)); // 脏水位被真水位覆写，台账自愈
    }

    [Fact]
    public void 卡面载荷契约_字段齐全且可解析_老信两列null不碍事()
    {
        AdmitSeat(1001);
        WakeWorker();
        var id = Arrive("BVarr0000040", "契约之货");
        _db.RegisterCoverArtifact(id, "BVarr0000040/BVarr0000040-1-Cover.jpg"); // 给一枚封面台账，看快照带不带

        Assert.NotNull(ArrivalReportWorker.RunTick(_db, null));
        var mail = FirstArrival(_db.GetMemberByMid(1001)!.Id);
        Assert.NotNull(mail);
        Assert.NotNull(mail!.PayloadJson);

        using var doc = JsonDocument.Parse(mail.PayloadJson!);
        var item = doc.RootElement.EnumerateArray().Single();
        Assert.Equal("BVarr0000040", item.GetProperty("bvid").GetString());
        Assert.Equal("契约之货", item.GetProperty("title").GetString());
        Assert.Equal("测试UP", item.GetProperty("up").GetString());
        Assert.Equal("/media/BVarr0000040/BVarr0000040-1-Cover.jpg", item.GetProperty("cover").GetString());
        Assert.True(item.TryGetProperty("coverThumbs", out _)); // 键必在场（值可 null），前端认键不猜

        // 老信（欢迎信那类）两列 NULL：同页混排不炸，Kind/msgType/payload 各归各
        _db.SendToMember(_db.GetMemberByMid(1001)!.Id, MemberMail.KindWelcome, "欢迎", "正文");
        var welcome = _db.ListMessages(_db.GetMemberByMid(1001)!.Id).First(m => m.Kind == MemberMail.KindWelcome);
        Assert.Null(welcome.MsgType);
        Assert.Null(welcome.PayloadJson);
    }

    [Fact]
    public void 首岗存量静默垫底_只播上岗后的新货() // 防的是：功能上岗那一刻补播二十通报，把站长室炸成到货超市
    {
        AdmitSeat(1001);
        Arrive("BVarr0000060", "上岗前的老货"); // 存量：播报员没见过它，不该补播

        Assert.Null(ArrivalReportWorker.RunTick(_db, null)); // 首岗：没水位 → 静默盖到最大 id，不开口
        var member = _db.GetMemberByMid(1001)!;
        Assert.DoesNotContain(_db.ListMessages(member.Id), m => m.Kind == MemberMail.KindArrival);
        Assert.Equal(_db.GetMaxVideoId().ToString(), _db.GetSetting(ArrivalReportWorker.WatermarkKey));

        Arrive("BVarr0000061", "上岗后的新货"); // 在岗期间到的货才该播
        Assert.NotNull(ArrivalReportWorker.RunTick(_db, null));
        var mail = FirstArrival(member.Id);
        Assert.NotNull(mail);
        Assert.Contains("上岗后的新货", mail!.Body);
        Assert.DoesNotContain("上岗前的老货", mail.Body);
    }

    [Fact]
    public void 长标题截短不顶行_正文一件一行不挤坨()
    {
        AdmitSeat(1001);
        WakeWorker();
        var longTitle = new string('长', 40);
        Arrive("BVarr0000050", longTitle);
        Arrive("BVarr0000051", "短标题");

        Assert.NotNull(ArrivalReportWorker.RunTick(_db, null));
        var mail = FirstArrival(_db.GetMemberByMid(1001)!.Id);
        Assert.NotNull(mail);
        var lines = mail!.Body.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("1. 《", lines[0]);
        Assert.Contains("…", lines[0]); // 超 24 字加省略号
        Assert.True(lines[0].Length <= "1. 《".Length + 25 + "》 ——测试UP".Length); // 顶不了行
        Assert.StartsWith("2. 《短标题》", lines[1]);
    }
}
