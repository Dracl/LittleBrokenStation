using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 站内弹幕举报的最小闭环（校验与落库直测，仿 DmTests 直驱 ValidateSend / DanmakuFaceTests 造库造席位）：
/// 举报即定格——弹幕正文、被举报人、举报人、bvid、弹幕发送时间全在落单那刻抄成快照，弹幕被删、席位改名都不回头改这张单；
/// 同席位对同一条弹幕只许举报一次（幂等回旧单，不炸前端）；举报自己（同席位或同浏览器凭证）拒；理由超 50 字拒；
/// 处置只此一次——delete 真删弹幕并盖 deleted、dismiss 只盖 dismissed，两者都落 handled_at_utc，抢第二次的吃 409。
/// 举报不预隐藏也在这里钉住：pending 期间 danmaku_local 一个字段都没动，弹幕照旧查得到。
/// </summary>
public sealed class DanmakuReportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly StationDb _db;

    public DanmakuReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-dmreport-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(_videosDir);
        _db = new StationDb(new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir));
        _db.Initialize();
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

    private long AddVideo(string bvid) =>
        _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;

    /// <summary>造一个在席席位（昵称就是举报单上那个名字快照的来源）。</summary>
    private long Seat(string name, long? mid = null)
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        var bound = _db.BindMember(id, mid ?? 920_000 + id, name, null);
        Assert.NotNull(bound);
        return id;
    }

    [Fact]
    public void 举报落库带快照_弹幕删了席位走了这张单照样看得全()
    {
        var videoId = AddVideo("BVreport01");
        var sender = Seat("嘴上没把门的", 930_001);
        var reporter = Seat("看不惯的", 930_002);
        var dmId = _db.AddLocalDanmaku(videoId, 2, 12.5, "这条弹幕有问题", "dm-sender-1", false, sender);

        var (error, outcome, reportId) = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, "  骂人了  ", null);
        Assert.Null(error);
        Assert.Equal(DanmakuReportEndpoints.ReportOutcome.Created, outcome);
        Assert.True(reportId > 0);

        var row = Assert.Single(_db.ListDanmakuReports(true));
        Assert.Equal(reportId, row.Id);
        Assert.Equal(dmId, row.DmId);
        Assert.Equal(videoId, row.VideoId);
        Assert.Equal("BVreport01", row.Bvid); // 链接快照从 videos 表现取
        Assert.Equal(2, row.PageIdx);
        Assert.Equal(12.5, row.TSec);
        Assert.Equal("这条弹幕有问题", row.Text);
        Assert.Equal(sender, row.SenderMemberId);
        Assert.Equal("dm-sender-1", row.SenderDmid);
        Assert.Equal("嘴上没把门的", row.SenderName);
        Assert.Equal(reporter, row.ReporterMemberId);
        Assert.Equal("看不惯的", row.ReporterName);
        Assert.Equal("骂人了", row.Reason); // trim 后落库
        Assert.Equal("pending", row.Status);
        Assert.Null(row.HandledAtUtc);
        Assert.NotNull(row.DmCreatedAtUtc); // 弹幕自己的发送时间也定格：删了还报得出

        // 举报不预隐藏：pending 期间弹幕本身一个字段没动，照常查得到（站长按处置才让它消失）。
        Assert.NotNull(_db.GetLocalDanmakuSnapshot(dmId));
        Assert.Contains(_db.ListLocalDanmaku(videoId), d => d.Id == dmId);

        // 弹幕被删 + 席位改名：这张单上的字一个都不跟着变（读列表直读快照列，不 JOIN 别处）。
        Assert.True(_db.DeleteLocalDanmaku(dmId));
        _db.TouchMemberLogin(930_001, "改名了", null);
        var after = Assert.Single(_db.ListDanmakuReports(true));
        Assert.Equal("这条弹幕有问题", after.Text);
        Assert.Equal("嘴上没把门的", after.SenderName);
        Assert.Equal("看不惯的", after.ReporterName);
        Assert.Equal(row.DmCreatedAtUtc, after.DmCreatedAtUtc);
    }

    [Fact]
    public void 没挂席位的匿名弹幕_名字快照为空也照样能举报()
    {
        var videoId = AddVideo("BVreport02");
        var reporter = Seat("看不惯的");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "老库里无凭证的一条", null, true);

        Assert.Null(DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, null, null).Error);

        var row = Assert.Single(_db.ListDanmakuReports(true));
        Assert.Null(row.SenderMemberId);
        Assert.Null(row.SenderDmid);
        Assert.Null(row.SenderName);
        Assert.Null(row.Reason); // 理由可选：没填就是 null，前端显「—」
    }

    [Fact]
    public void 同席位重复举报幂等_换个席位另算一单()
    {
        var videoId = AddVideo("BVreport03");
        var sender = Seat("发弹幕的");
        var reporter = Seat("看不惯的");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "就这一条", "dm-dup", false, sender);

        var first = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, "头一单", null);
        Assert.Null(first.Error);
        Assert.Equal(DanmakuReportEndpoints.ReportOutcome.Created, first.Outcome);

        var again = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, "换个说法再来一单", null);
        Assert.Null(again.Error); // 幂等：不报错炸前端
        Assert.Equal(DanmakuReportEndpoints.ReportOutcome.Duplicate, again.Outcome);
        Assert.Equal(first.ReportId, again.ReportId);
        Assert.Equal(1L, _db.CountDanmakuReports());
        // 旧单的快照不改口：第二次填的理由不会盖掉第一单
        Assert.Equal("头一单", Assert.Single(_db.ListDanmakuReports(true)).Reason);

        var other = Seat("另一个看不惯的");
        var third = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, other, null, null);
        Assert.Null(third.Error);
        Assert.Equal(DanmakuReportEndpoints.ReportOutcome.Created, third.Outcome);
        Assert.Equal(2L, _db.CountDanmakuReports());
    }

    [Fact]
    public void 举报自己的弹幕_一律拒()
    {
        var videoId = AddVideo("BVreport04");
        var me = Seat("自己人", 930_010);
        var other = Seat("别人", 930_011);

        // 挂着席位的行：席位 id 相同就是自己。
        var mine = _db.AddLocalDanmaku(videoId, 0, 1, "我自己发的", "dm-mine", false, me);
        var self = DanmakuReportEndpoints.ValidateReport(_db, videoId, mine, me, null, null);
        Assert.Equal(StatusCodes.Status400BadRequest, self.Error!.Value.Status);
        Assert.Equal(0L, _db.CountDanmakuReports());

        // 没挂席名的匿名行：拿浏览器凭证认——同一张 cookie 也算自己。
        var anon = _db.AddLocalDanmaku(videoId, 0, 2, "匿名发的", "dm-anon-mine", false);
        Assert.Equal(StatusCodes.Status400BadRequest,
            DanmakuReportEndpoints.ValidateReport(_db, videoId, anon, other, null, "dm-anon-mine").Error!.Value.Status);
        // 别人的凭证举报它：放行。
        Assert.Null(DanmakuReportEndpoints.ValidateReport(_db, videoId, anon, other, null, "dm-someone-else").Error);
        // 两边都不能是空串：弹幕无凭证 + 举报人没 cookie 不能互认成一人。
        var ghost = _db.AddLocalDanmaku(videoId, 0, 3, "无凭证老行", null, false);
        Assert.Null(DanmakuReportEndpoints.ValidateReport(_db, videoId, ghost, other, null, null).Error);
        Assert.Equal(2L, _db.CountDanmakuReports());
    }

    [Fact]
    public void 举报理由超五十字拒_整字落库不截断()
    {
        var videoId = AddVideo("BVreport05");
        var sender = Seat("发弹幕的");
        var reporter = Seat("看不惯的");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "话多的弹幕", "dm-long", false, sender);

        var tooLong = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, new string('字', DanmakuReportEndpoints.MaxReasonLength + 1), null);
        Assert.Equal(StatusCodes.Status400BadRequest, tooLong.Error!.Value.Status);
        Assert.False(string.IsNullOrEmpty(tooLong.Error!.Value.Message));
        Assert.Equal(0L, _db.CountDanmakuReports()); // 拒了就不该落单

        var edge = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, new string('字', DanmakuReportEndpoints.MaxReasonLength), null);
        Assert.Null(edge.Error);
        Assert.Equal(new string('字', DanmakuReportEndpoints.MaxReasonLength), Assert.Single(_db.ListDanmakuReports(true)).Reason);
    }

    [Fact]
    public void 弹幕不存在或不在这个视频下_报404()
    {
        var videoId = AddVideo("BVreport06");
        var otherVideo = AddVideo("BVreport07");
        var reporter = Seat("看不惯的");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "在这儿呢", "dm-here", false);

        Assert.Equal(StatusCodes.Status404NotFound,
            DanmakuReportEndpoints.ValidateReport(_db, videoId, 999_999, reporter, null, null).Error!.Value.Status);
        Assert.Equal(StatusCodes.Status404NotFound,
            DanmakuReportEndpoints.ValidateReport(_db, otherVideo, dmId, reporter, null, null).Error!.Value.Status);
        Assert.Equal(0L, _db.CountDanmakuReports());
    }

    [Fact]
    public void 删除处置_弹幕真删_状态翻转_重复处置吃409()
    {
        var videoId = AddVideo("BVreport08");
        var sender = Seat("发弹幕的");
        var reporter = Seat("看不惯的");
        var dmId = _db.AddLocalDanmaku(videoId, 1, 3.5, "该删的一条", "dm-del", false, sender);
        var (_, _, reportId) = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, "骂人", null);

        var (status, message) = DanmakuReportEndpoints.HandleReport(_db, reportId, "Delete"); // 大小写与空格都认
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(string.IsNullOrEmpty(message));
        Assert.Null(_db.GetLocalDanmakuSnapshot(dmId)); // 真删，不是打标记
        Assert.DoesNotContain(_db.ListLocalDanmaku(videoId), d => d.Id == dmId);

        var row = Assert.Single(_db.ListDanmakuReports(false));
        Assert.Equal("deleted", row.Status);
        Assert.NotNull(row.HandledAtUtc);
        Assert.Equal("该删的一条", row.Text); // 弹幕没了，举报卡照样摆得下正文
        Assert.Equal(0L, _db.CountDanmakuReports()); // 待处理归零
        Assert.Empty(_db.ListDanmakuReports(true));

        // 重复处置（换个动作也不行）：409，且状态不被第二次盖掉。
        Assert.Equal(StatusCodes.Status409Conflict, DanmakuReportEndpoints.HandleReport(_db, reportId, "dismiss").Status);
        Assert.Equal("deleted", Assert.Single(_db.ListDanmakuReports(false)).Status);
    }

    [Fact]
    public void 删除处置时弹幕已被发送者自删_跳过不算错()
    {
        var videoId = AddVideo("BVreport09");
        var sender = Seat("发弹幕的");
        var reporter = Seat("看不惯的");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "自己先删了", "dm-gone", false, sender);
        var (_, _, reportId) = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, null, null);

        Assert.True(_db.DeleteLocalDanmaku(dmId)); // 发送者自删在前

        Assert.Equal(StatusCodes.Status200OK, DanmakuReportEndpoints.HandleReport(_db, reportId, "delete").Status);
        var row = Assert.Single(_db.ListDanmakuReports(false));
        Assert.Equal("deleted", row.Status);
        Assert.NotNull(row.HandledAtUtc);
    }

    [Fact]
    public void 忽略处置_只盖章不删弹幕()
    {
        var videoId = AddVideo("BVreport10");
        var sender = Seat("发弹幕的");
        var reporter = Seat("看不惯的");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "站长觉得没问题", "dm-keep", false, sender);
        var (_, _, reportId) = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, "看着像广告", null);

        Assert.Equal(StatusCodes.Status200OK, DanmakuReportEndpoints.HandleReport(_db, reportId, "dismiss").Status);
        Assert.NotNull(_db.GetLocalDanmakuSnapshot(dmId)); // 弹幕还在，照旧飘
        Assert.Contains(_db.ListLocalDanmaku(videoId), d => d.Id == dmId);

        var row = Assert.Single(_db.ListDanmakuReports(false));
        Assert.Equal("dismissed", row.Status);
        Assert.NotNull(row.HandledAtUtc);
        Assert.Equal(0L, _db.CountDanmakuReports());
        Assert.Equal(StatusCodes.Status409Conflict, DanmakuReportEndpoints.HandleReport(_db, reportId, "delete").Status);
        Assert.NotNull(_db.GetLocalDanmakuSnapshot(dmId)); // 忽略过的手再想删也删不动了
    }

    [Fact]
    public void 处置的坏动作与查无此单_不改状态()
    {
        var videoId = AddVideo("BVreport11");
        var reporter = Seat("看不惯的");
        var dmId = _db.AddLocalDanmaku(videoId, 0, 1, "等着发落的", "dm-wait", false);
        var (_, _, reportId) = DanmakuReportEndpoints.ValidateReport(_db, videoId, dmId, reporter, null, null);

        Assert.Equal(StatusCodes.Status400BadRequest, DanmakuReportEndpoints.HandleReport(_db, reportId, "nuke").Status);
        Assert.Equal(StatusCodes.Status400BadRequest, DanmakuReportEndpoints.HandleReport(_db, reportId, null).Status);
        Assert.Equal(StatusCodes.Status404NotFound, DanmakuReportEndpoints.HandleReport(_db, 987_654, "delete").Status);

        var row = Assert.Single(_db.ListDanmakuReports(true));
        Assert.Equal("pending", row.Status);
        Assert.Null(row.HandledAtUtc);
        Assert.NotNull(_db.GetLocalDanmakuSnapshot(dmId));
    }

    [Fact]
    public void 列表按举报时间倒序_pending只看待处理的()
    {
        var videoId = AddVideo("BVreport12");
        var reporter = Seat("连举报三单的");
        var first = DanmakuReportEndpoints.ValidateReport(_db, videoId,
            _db.AddLocalDanmaku(videoId, 0, 1, "第一条", "dm-o1", false), reporter, null, null).ReportId;
        var second = DanmakuReportEndpoints.ValidateReport(_db, videoId,
            _db.AddLocalDanmaku(videoId, 0, 2, "第二条", "dm-o2", false), reporter, null, null).ReportId;
        var third = DanmakuReportEndpoints.ValidateReport(_db, videoId,
            _db.AddLocalDanmaku(videoId, 0, 3, "第三条", "dm-o3", false), reporter, null, null).ReportId;

        var all = _db.ListDanmakuReports(false);
        Assert.Equal(new List<long> { third, second, first }, all.Select(r => r.Id).ToList());
        Assert.True(string.CompareOrdinal(all[0].CreatedAtUtc, all[1].CreatedAtUtc) >= 0); // 新单在前
        Assert.Equal(3L, _db.CountDanmakuReports());
        Assert.Equal(3L, _db.CountDanmakuReports(null)); // status 给 null 数全部

        Assert.Equal(StatusCodes.Status200OK, DanmakuReportEndpoints.HandleReport(_db, third, "delete").Status);
        Assert.Equal(new List<long> { second, first }, _db.ListDanmakuReports(true).Select(r => r.Id).ToList());
        Assert.Equal(2L, _db.CountDanmakuReports()); // h3 徽章只数 pending
        Assert.Equal(1L, _db.CountDanmakuReports("deleted"));
        Assert.Equal(3, _db.ListDanmakuReports(false).Count); // 全部里那张结掉的单还在
    }
}
