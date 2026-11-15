using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 观看历史（续播点位）：校验与落库直测，仿 DanmakuReportTests 造库造席位（临时目录 + Initialize + BindMember，Dispose 里 ClearAllPools）。
/// 钉住这几件事：刚开个头（&lt;5 秒）且这一 P 的坑是空的 → 什么都不落（坑各管各，别的 P 有行不算）；本坑已有行则连早于地板的位置也照常覆盖（不把旧点顶成空气）；
/// 一 P 一坑——同 P 再报才盖旧位并翻新 updated_at_utc，不同 P 各记各位互不相犯，列表收成一片一卡展最近那一 P；
/// durationSec 说不清（≤0 / NaN / ∞ / 没给）一律 400 中文，越界位置夹进 [0, duration]；
/// 列表按 updated_at 倒序并 JOIN 出现成片名与封面（本地件优先），片删了历史里就看不见；席位之间互不可见；单删与清点数清楚。
/// </summary>
public sealed class VideoHistoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly StationDb _db;

    public VideoHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-videohistory-" + Guid.NewGuid().ToString("N"));
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
    private VideoHistoryEndpoints.ProgressOutcome Report(long member, long video, int page, double pos, double dur)
    {
        var (error, outcome) = VideoHistoryEndpoints.ValidateProgress(_db, member, video, page, pos, dur);
        Assert.Null(error);
        return outcome;
    }

    [Fact]
    public void 刚开个头不落行_已有行则连早位也覆盖()
    {
        var video = AddVideo("BVhist01", "开头就走的片子");
        var me = Seat("三秒党");

        // 库里没行 + 位置没到 5 秒地板：忽略，不占历史位。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Ignored, Report(me, video, 1, 4.9, 600));
        Assert.Null(_db.GetVideoHistoryOne(me, video));
        Assert.Empty(_db.GetVideoHistory(me));

        // 到了地板才落行。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 1, 5, 600));
        var row = _db.GetVideoHistoryOne(me, video);
        Assert.NotNull(row);
        Assert.Equal(1, row!.PageIdx);
        Assert.Equal(5, row.PositionSec);
        Assert.Equal(600, row.DurationSec);

        // 本坑已有行时 4.9 秒照常被写回去：拖回首片尾也是「他就看到这儿」，不能让旧位留在库里骗人。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 1, 4.9, 600));
        var back = _db.GetVideoHistoryOne(me, video, 1);
        Assert.Equal(4.9, back!.PositionSec);
        Assert.Equal(1, back.PageIdx);

        // 地板按坑独立：P2 是处女坑，4.9 秒不占——P1 有行不算它的旧位。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Ignored, Report(me, video, 2, 4.9, 600));
        Assert.Null(_db.GetVideoHistoryOne(me, video, 2));
    }

    [Fact]
    public void 多P片子一P一坑_各记各位互不相犯()
    {
        var video = AddVideo("BVhist02b", "三分P的长片");
        var me = Seat("跳着看的");

        Report(me, video, 0, 100, 600);
        Report(me, video, 1, 200, 600);
        // 两个 P 各占各坑：共存，谁也盖不掉谁。
        Assert.Equal(100, _db.GetVideoHistoryOne(me, video, 0)!.PositionSec);
        Assert.Equal(200, _db.GetVideoHistoryOne(me, video, 1)!.PositionSec);

        // 同坑再报只盖那一 P，别 P 纹丝不动。
        Report(me, video, 0, 300, 600);
        Assert.Equal(300, _db.GetVideoHistoryOne(me, video, 0)!.PositionSec);
        Assert.Equal(200, _db.GetVideoHistoryOne(me, video, 1)!.PositionSec);

        // 不带 P 的查法取最近更新那一 P（进页默认落点就是最近看的那一集）。
        Thread.Sleep(20); // 同秒内两笔会把「最近」判糊，留点余量
        Report(me, video, 1, 450, 600);
        var latest = _db.GetVideoHistoryOne(me, video);
        Assert.Equal(1, latest!.PageIdx);
        Assert.Equal(450, latest.PositionSec);

        // 列表收成一片一卡：三个坑也只摆一卡，露面的是最近那一 P。
        var only = Assert.Single(_db.GetVideoHistory(me));
        Assert.Equal(video, only.VideoId);
        Assert.Equal(1, only.PageIdx);
    }

    [Fact]
    public void 同坑新位盖旧位并翻新时间_别P自岿然不动()
    {
        var video = AddVideo("BVhist02", "反复看的片子");
        var me = Seat("回锅的");

        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 1, 30, 600));
        var first = _db.GetVideoHistoryOne(me, video, 1);
        Assert.NotNull(first);

        Thread.Sleep(20); // 时间戳精度到 100ns，留点余量让「翻新」看得见摸得着
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 1, 120.5, 600));
        var second = _db.GetVideoHistoryOne(me, video, 1);
        Assert.NotNull(second);

        Assert.Equal(1, second!.PageIdx); // 还是那一 P 的坑
        Assert.Equal(120.5, second.PositionSec);
        Assert.Equal(600, second.DurationSec);
        Assert.True(
            DateTime.Parse(second.UpdatedAtUtc) > DateTime.Parse(first!.UpdatedAtUtc),
            $"updated_at_utc 没翻新：{first.UpdatedAtUtc} → {second.UpdatedAtUtc}");

        // 换个 P 另开坑：老 P 的位原地躺着，谁也不挨着谁。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 3, 45, 600));
        Assert.Equal(120.5, _db.GetVideoHistoryOne(me, video, 1)!.PositionSec);
        Assert.Equal(45, _db.GetVideoHistoryOne(me, video, 3)!.PositionSec);

        // 列表一片只摆一卡：同片几笔不摆成 P 陈列架。
        var only = Assert.Single(_db.GetVideoHistory(me));
        Assert.Equal(video, only.VideoId);
    }

    [Fact]
    public void 过九十五线算看完_盖戳留档不删行()
    {
        var video = AddVideo("BVhist03", "看完的片子");
        var me = Seat("守到片尾的");
        Report(me, video, 1, 300, 600);
        Assert.NotNull(_db.GetVideoHistoryOne(me, video));

        // 94.9% 还在途中：算普通进度。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 1, 600 * 0.949, 600));
        var midway = _db.GetVideoHistoryOne(me, video);
        Assert.NotNull(midway);
        Assert.Equal(600 * 0.949, midway!.PositionSec);

        // 95% 起跳：完结戳落袋，行照常留着（历史记所有，不只没看完的）。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Finished, Report(me, video, 1, 600 * 0.95, 600));
        var done = _db.GetVideoHistoryOne(me, video);
        Assert.NotNull(done);
        Assert.Equal(600 * 0.95, done!.PositionSec);
        Assert.Contains(_db.GetVideoHistory(me), r => r.VideoId == video); // 列表里也得露面

        // 看完后回看中段：换个 P 另开坑各记各位；列表里同片仍只收一卡（最近那 P 露面）。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Saved, Report(me, video, 2, 90, 600));
        var rewatch = _db.GetVideoHistoryOne(me, video, 2);
        Assert.Equal(90, rewatch!.PositionSec);
        Assert.Equal(600 * 0.95, _db.GetVideoHistoryOne(me, video, 1)!.PositionSec); // P1 的完结位原地躺着
        Assert.Single(_db.GetVideoHistory(me)); // 两个坑也只摆一卡

        // 位置比时长还大（前端把缓冲末尾抖出去这种脏数）：夹进时长 → 照样按看完盖戳留档。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Finished, Report(me, video, 2, 9999, 600));
        var clamped = _db.GetVideoHistoryOne(me, video);
        Assert.Equal(600, clamped!.PositionSec);
    }

    [Fact]
    public void 时长报不上来就拒_位置不是数也拒()
    {
        var video = AddVideo("BVhist04", "时长成谜的片子");
        var me = Seat("报数的");

        foreach (var bad in new double?[] { 0, -1, double.NaN, double.PositiveInfinity, null })
        {
            var (error, _) = VideoHistoryEndpoints.ValidateProgress(_db, me, video, 1, 100, bad);
            Assert.Equal(StatusCodes.Status400BadRequest, error!.Value.Status);
            Assert.False(string.IsNullOrEmpty(error.Value.Message));
        }

        var (posBad, _) = VideoHistoryEndpoints.ValidateProgress(_db, me, video, 1, double.NaN, 600);
        Assert.Equal(StatusCodes.Status400BadRequest, posBad!.Value.Status);

        // 拒掉的都不该落库。
        Assert.Null(_db.GetVideoHistoryOne(me, video));
        Assert.Empty(_db.GetVideoHistory(me));

        // 负数位置夹到 0：没到地板又没旧行 → 忽略，不炸。
        Assert.Equal(VideoHistoryEndpoints.ProgressOutcome.Ignored, Report(me, video, 1, -50, 600));
        Assert.Null(_db.GetVideoHistoryOne(me, video));
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

        var list = _db.GetVideoHistory(me);
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
        Assert.Null(_db.GetVideoHistory(me).Single(r => r.VideoId == noCover).LocalCover);

        // 片下架（videos 行被连坐删）：历史行不再露面，也不留在列表里当死行。
        _db.DeleteVideoCascade(gone, _videosDir);
        Assert.Equal(new List<long> { noCover, withCover }, _db.GetVideoHistory(me).Select(r => r.VideoId).ToList());
    }

    [Fact]
    public void 单删只销那一部_清空数得清()
    {
        var a = AddVideo("BVhist08", "甲片");
        var b = AddVideo("BVhist09", "乙片");
        var me = Seat("要清历史的");
        Report(me, a, 1, 100, 600);
        Report(me, a, 3, 80, 600); // 甲片两个坑：单删一把全销，不留半截
        Report(me, b, 1, 200, 600);

        Assert.True(_db.DeleteVideoHistory(me, a));
        Assert.Null(_db.GetVideoHistoryOne(me, a));
        Assert.Null(_db.GetVideoHistoryOne(me, a, 3)); // 这部片的每个 P 都跟着销
        Assert.NotNull(_db.GetVideoHistoryOne(me, b)); // 单删不牵连别部
        Assert.False(_db.DeleteVideoHistory(me, a));    // 再删一次：没这行，如实回 false

        Assert.Equal(1, _db.ClearVideoHistory(me));
        Assert.Empty(_db.GetVideoHistory(me));
        Assert.Equal(0, _db.ClearVideoHistory(me)); // 本来就是空的：0 不是错
    }

    [Fact]
    public void 历史只属于自己_席位之间互不可见()
    {
        var video = AddVideo("BVhist10", "各看各的");
        var mine = Seat("我");
        var other = Seat("别人", 941_777);

        Report(mine, video, 1, 120, 600);
        Assert.Null(_db.GetVideoHistoryOne(other, video)); // 别人报不出我的点位
        Assert.Empty(_db.GetVideoHistory(other));

        // 各看各的：同一部片两个席位各一行，互不覆盖。
        Report(other, video, 2, 400, 600);
        Assert.Equal(120, _db.GetVideoHistoryOne(mine, video)!.PositionSec);
        Assert.Equal(400, _db.GetVideoHistoryOne(other, video)!.PositionSec);

        // 清自己的，动不了别人的。
        _db.ClearVideoHistory(mine);
        Assert.Null(_db.GetVideoHistoryOne(mine, video));
        Assert.Equal(400, _db.GetVideoHistoryOne(other, video)!.PositionSec);
    }

    [Fact]
    public void 老库一行制迁到一P一坑_旧行搬家不丢且形状翻新()
    {
        // 仿 VideoSettingsTests 的装老库路数：先手工种一口旧主键（member_id, video_id）的表与一条存量，
        // 再走 Initialize 的迁移门——老行得原样搬进新坑，主键换成三列，重跑还须幂等。
        var dir = Path.Combine(Path.GetTempPath(), "lbs-videohistory-migrate-" + Guid.NewGuid().ToString("N"));
        var videosDir = Path.Combine(dir, "videos");
        Directory.CreateDirectory(videosDir);
        try
        {
            var paths = new DataPaths(dir, videosDir, dir, dir, dir);
            using (var seed = new SqliteConnection($"Data Source={paths.DatabaseFile}"))
            {
                seed.Open();
                using var cmd = seed.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE watch_history (
                        member_id    INTEGER NOT NULL,
                        video_id     INTEGER NOT NULL,
                        page_idx     INTEGER NOT NULL,
                        position_sec REAL NOT NULL,
                        duration_sec REAL NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        PRIMARY KEY (member_id, video_id)
                    );
                    INSERT INTO watch_history VALUES (7, 42, 2, 88.5, 600, '2026-11-05T00:00:00.0000000+00:00');
                    """;
                cmd.ExecuteNonQuery();
            }

            var db = new StationDb(paths);
            db.Initialize();

            // 老行还在：同一人同一片的坑原封搬过来，四个数一毫不差。
            var moved = db.GetVideoHistoryOne(7, 42, 2);
            Assert.NotNull(moved);
            Assert.Equal(88.5, moved!.PositionSec);
            Assert.Equal(600, moved.DurationSec);

            // 新坑能另开：同片另一 P 落行不受老坑拦截——这正是旧主键做不到的事。
            db.UpsertVideoHistory(7, 42, 5, 120, 600);
            Assert.Equal(120, db.GetVideoHistoryOne(7, 42, 5)!.PositionSec);
            Assert.Equal(88.5, db.GetVideoHistoryOne(7, 42, 2)!.PositionSec);

            // 重跑幂等：再 Initialize 一次不炸不丢。
            db.Initialize();
            Assert.Equal(88.5, db.GetVideoHistoryOne(7, 42, 2)!.PositionSec);

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响结论
            }
        }
    }
}
