using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 观看历史（续播点位）：校验与落库直测，仿 DanmakuReportTests 造库造席位（临时目录 + Initialize + BindMember，Dispose 里 ClearAllPools）。
/// 钉住这几件事：刚开个头（&lt;5 秒）且库里没行 → 什么都不落；库里已有行则连早于地板的位置也照常覆盖（不把旧点顶成空气）；
/// 一人一片一行——再报只盖旧位并翻新 updated_at_utc；位置过 95% 视为看完，直接删行不把看完的堆进历史；
/// durationSec 说不清（≤0 / NaN / ∞ / 没给）一律 400 中文，越界位置夹进 [0, duration]；
/// 列表按 updated_at 倒序并 JOIN 出现成片名与封面（本地件优先），片删了历史里就看不见；席位之间互不可见；单删与清点数清楚。
/// </summary>
public sealed class WatchHistoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly StationDb _db;

    public WatchHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-watchhistory-" + Guid.NewGuid().ToString("N"));
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

    private long AddVideo(string bvid, string? title = null, string? coverUrl = null)
    {
        var id = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title, coverUrl);
        return id;
    }

    /// <summary>造一个在席席位（历史挂席位 id，与私信/举报同一套身份）。</summary>
    private long Seat(string name, long? mid = null)
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        var bound = _db.BindMember(id, mid ?? 940_000 + id, name, null);
        Assert.NotNull(bound);
        return id;
    }

    /// <summary>报一次位置并断言放行，顺带取出结局。</summary>
    private WatchHistoryEndpoints.ProgressOutcome Report(long member, long video, int page, double pos, double dur)
    {
        var (error, outcome) = WatchHistoryEndpoints.ValidateProgress(_db, member, video, page, pos, dur);
        Assert.Null(error);
        return outcome;
    }

    [Fact]
    public void 刚开个头不落行_已有行则连早位也覆盖()
    {
        var video = AddVideo("BVhist01", "开头就走的片子");
        var me = Seat("三秒党");

        // 库里没行 + 位置没到 5 秒地板：忽略，不占历史位。
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Ignored, Report(me, video, 1, 4.9, 600));
        Assert.Null(_db.GetWatchHistoryOne(me, video));
        Assert.Empty(_db.GetWatchHistory(me));

        // 到了地板才落行。
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 1, 5, 600));
        var row = _db.GetWatchHistoryOne(me, video);
        Assert.NotNull(row);
        Assert.Equal(1, row!.PageIdx);
        Assert.Equal(5, row.PositionSec);
        Assert.Equal(600, row.DurationSec);

        // 已有行时 4.9 秒照常被写回去：拖回首片尾也是「他就看到这儿」，不能让旧位留在库里骗人。
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 2, 4.9, 600));
        var back = _db.GetWatchHistoryOne(me, video);
        Assert.Equal(4.9, back!.PositionSec);
        Assert.Equal(2, back.PageIdx);
    }

    [Fact]
    public void 一人一片一行_新位盖旧位并翻新时间()
    {
        var video = AddVideo("BVhist02", "反复看的片子");
        var me = Seat("回锅的");

        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 1, 30, 600));
        var first = _db.GetWatchHistoryOne(me, video);
        Assert.NotNull(first);

        Thread.Sleep(20); // 时间戳精度到 100ns，留点余量让「翻新」看得见摸得着
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 3, 120.5, 600));
        var second = _db.GetWatchHistoryOne(me, video);
        Assert.NotNull(second);

        Assert.Equal(3, second!.PageIdx); // 分 P 也跟着换
        Assert.Equal(120.5, second.PositionSec);
        Assert.Equal(600, second.DurationSec);
        Assert.True(
            DateTime.Parse(second.UpdatedAtUtc) > DateTime.Parse(first!.UpdatedAtUtc),
            $"updated_at_utc 没翻新：{first.UpdatedAtUtc} → {second.UpdatedAtUtc}");

        // 一人一片只有一行：报两次不攒第二行。
        var only = Assert.Single(_db.GetWatchHistory(me));
        Assert.Equal(video, only.VideoId);
    }

    [Fact]
    public void 过九十五线算看完_直接删行不留历史()
    {
        var video = AddVideo("BVhist03", "看完的片子");
        var me = Seat("守到片尾的");
        Report(me, video, 1, 300, 600);
        Assert.NotNull(_db.GetWatchHistoryOne(me, video));

        // 94.9% 还在：没到线就别删。
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 1, 600 * 0.949, 600));
        Assert.NotNull(_db.GetWatchHistoryOne(me, video));

        // 95% 起跳：删行，历史里查无此片。
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Removed, Report(me, video, 1, 600 * 0.95, 600));
        Assert.Null(_db.GetWatchHistoryOne(me, video));
        Assert.Empty(_db.GetWatchHistory(me));

        // 删完再报个中段位置，行能重新长回来（这人回看没看完的段落，历史该记住）。
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 2, 90, 600));
        Assert.Equal(90, _db.GetWatchHistoryOne(me, video)!.PositionSec);

        // 位置比时长还大（前端把缓冲末尾抖出去这种脏数）：夹进时长 → 照样按看完处理。
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Removed, Report(me, video, 2, 9999, 600));
        Assert.Null(_db.GetWatchHistoryOne(me, video));
    }

    [Fact]
    public void 时长报不上来就拒_位置不是数也拒()
    {
        var video = AddVideo("BVhist04", "时长成谜的片子");
        var me = Seat("报数的");

        foreach (var bad in new double?[] { 0, -1, double.NaN, double.PositiveInfinity, null })
        {
            var (error, _) = WatchHistoryEndpoints.ValidateProgress(_db, me, video, 1, 100, bad);
            Assert.Equal(StatusCodes.Status400BadRequest, error!.Value.Status);
            Assert.False(string.IsNullOrEmpty(error.Value.Message));
        }

        var (posBad, _) = WatchHistoryEndpoints.ValidateProgress(_db, me, video, 1, double.NaN, 600);
        Assert.Equal(StatusCodes.Status400BadRequest, posBad!.Value.Status);

        // 拒掉的都不该落库。
        Assert.Null(_db.GetWatchHistoryOne(me, video));
        Assert.Empty(_db.GetWatchHistory(me));

        // 负数位置夹到 0：没到地板又没旧行 → 忽略，不炸。
        Assert.Equal(WatchHistoryEndpoints.ProgressOutcome.Ignored, Report(me, video, 1, -50, 600));
        Assert.Null(_db.GetWatchHistoryOne(me, video));
    }

    [Fact]
    public void 列表按最近看过排_片名封面现取_本地件优先()
    {
        var withCover = AddVideo("BVhist05", "有本地封面的", "https://i0.hdslb.com/remote.jpg");
        _db.RegisterCoverArtifact(withCover, "BVhist05.Cover.jpg"); // 馆藏本地件（与首页卡阵同口径）
        var noCover = AddVideo("BVhist06", "只有热链封面的", "https://i0.hdslb.com/remote2.jpg");
        var gone = AddVideo("BVhist07", "已经下架的");
        var me = Seat("看了一堆的");

        Report(me, withCover, 1, 100, 600);
        Thread.Sleep(20);
        Report(me, noCover, 2, 200, 600);
        Thread.Sleep(20);
        Report(me, gone, 1, 300, 600);

        var list = _db.GetWatchHistory(me);
        // 新→旧：最后报的那部排最前。
        Assert.Equal(new List<long> { gone, noCover, withCover }, list.Select(r => r.VideoId).ToList());
        Assert.True(
            string.CompareOrdinal(list[0].UpdatedAtUtc, list[1].UpdatedAtUtc) > 0,
            $"倒序没成立：{list[0].UpdatedAtUtc} 不该早于 {list[1].UpdatedAtUtc}");

        // 片名/封面是 JOIN 现取的：与首页卡阵同口径（本地件 /media/ 优先，没归档留 B 站直链给前端热链兑底）。
        var first = list.Single(r => r.VideoId == withCover);
        Assert.Equal("BVhist05", first.Bvid);
        Assert.Equal("有本地封面的", first.Title);
        Assert.Equal("/media/BVhist05.Cover.jpg", first.LocalCover);
        Assert.Equal("https://i0.hdslb.com/remote.jpg", first.CoverUrl);
        Assert.Equal(2, list.Single(r => r.VideoId == noCover).PageIdx);
        Assert.Null(_db.GetWatchHistory(me).Single(r => r.VideoId == noCover).LocalCover);

        // 片下架（videos 行被连坐删）：历史行不再露面，也不留在列表里当死行。
        _db.DeleteVideoCascade(gone, _videosDir);
        Assert.Equal(new List<long> { noCover, withCover }, _db.GetWatchHistory(me).Select(r => r.VideoId).ToList());
    }

    [Fact]
    public void 单删只销那一部_清空数得清()
    {
        var a = AddVideo("BVhist08", "甲片");
        var b = AddVideo("BVhist09", "乙片");
        var me = Seat("要清历史的");
        Report(me, a, 1, 100, 600);
        Report(me, b, 1, 200, 600);

        Assert.True(_db.DeleteWatchHistory(me, a));
        Assert.Null(_db.GetWatchHistoryOne(me, a));
        Assert.NotNull(_db.GetWatchHistoryOne(me, b)); // 单删不牵连别部
        Assert.False(_db.DeleteWatchHistory(me, a));    // 再删一次：没这行，如实回 false

        Assert.Equal(1, _db.ClearWatchHistory(me));
        Assert.Empty(_db.GetWatchHistory(me));
        Assert.Equal(0, _db.ClearWatchHistory(me)); // 本来就是空的：0 不是错
    }

    [Fact]
    public void 历史只属于自己_席位之间互不可见()
    {
        var video = AddVideo("BVhist10", "各看各的");
        var mine = Seat("我");
        var other = Seat("别人", 941_777);

        Report(mine, video, 1, 120, 600);
        Assert.Null(_db.GetWatchHistoryOne(other, video)); // 别人报不出我的点位
        Assert.Empty(_db.GetWatchHistory(other));

        // 各看各的：同一部片两个席位各一行，互不覆盖。
        Report(other, video, 2, 400, 600);
        Assert.Equal(120, _db.GetWatchHistoryOne(mine, video)!.PositionSec);
        Assert.Equal(400, _db.GetWatchHistoryOne(other, video)!.PositionSec);

        // 清自己的，动不了别人的。
        _db.ClearWatchHistory(mine);
        Assert.Null(_db.GetWatchHistoryOne(mine, video));
        Assert.Equal(400, _db.GetWatchHistoryOne(other, video)!.PositionSec);
    }
}
