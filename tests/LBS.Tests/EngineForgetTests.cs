using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 删档与重投的引擎对账三件套：
///  1) 取料口径 GetEngineTraceForVideo——任务号三处并集（到站那一刻 videos.engine_task_ids 已被清空，单靠它必漏）、cid 只从 pages 取；
///  2) 按 cid 销引擎历史台账（判重键 = (cid, 需要下载的内容)，不销则删完重投被判「已下过」，新行落到无人认领的 Skipped）；
///  3) Skipped 出口那枚重下旗的一次性生命周期（只能活一次提交，不能搭下一趟顺风车）。
/// </summary>
public sealed class EngineForgetTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly string _downkyiDir;
    private readonly StationDb _db;

    public EngineForgetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-forget-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        _downkyiDir = Path.Combine(_tempDir, "downkyi");
        Directory.CreateDirectory(_videosDir);
        Directory.CreateDirectory(Path.Combine(_downkyiDir, "Storage"));
        _db = new StationDb(new DataPaths(_tempDir, _videosDir, _downkyiDir, _tempDir, _tempDir));
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

    private long SeedSubmitted(string bvid = "BV1forget1")
    {
        var videoId = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.MarkSubmitted(videoId, ["task-main-1", "task-main-2"], "片名", null);
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP主", 12345, "音乐>翻唱", "2026-01-01", "简介", "1 播放"),
            [
                new PageDraft("task-main-1", 1, "P1", 101, "01:00", $"{bvid}/{bvid}-P1"),
                new PageDraft("task-main-2", 2, "P2", 102, "02:00", $"{bvid}/{bvid}-P2"),
            ]);
        return videoId;
    }

    /// <summary>造一枚引擎自己的 Data.db，只放这张判重台账用得着的两列（真实 schema 还有一堆展示列）。</summary>
    private void SeedEngineHistory(params (string Id, long Cid)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_downkyiDir, "Storage", "Data.db")}");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS download_history (
                id  TEXT PRIMARY KEY,
                cid INTEGER NOT NULL DEFAULT 0,
                main_title TEXT
            );
            """;
        create.ExecuteNonQuery();

        foreach (var (id, cid) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO download_history (id, cid, main_title) VALUES ($id, $cid, $title);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$cid", cid);
            insert.Parameters.AddWithValue("$title", "某片");
            insert.ExecuteNonQuery();
        }
    }

    private long CountEngineHistory()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_downkyiDir, "Storage", "Data.db")}");
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(1) FROM download_history;";
        return (long)(count.ExecuteScalar() ?? 0L);
    }

    [Fact]
    public void 对账料_到站清了任务号也从分P与加档行取齐()
    {
        var videoId = SeedSubmitted();
        Assert.Empty(_db.GetCodecVersions(videoId)); // 本馆还没加档行

        // 加档：另起一行在途加档，它的引擎任务号过去从来没人安顿。
        Assert.True(_db.AddCodecVersion(videoId, 1, "hevc"));
        var codec = Assert.Single(_db.GetDownloadingCodecVersions(), row => row.VideoId == videoId);
        _db.MarkCodecSubmitted(codec.Id, ["task-codec-1"], "BV1forget1/BV1forget1-P1- HEVC.mp4");

        // 到站那一刻 videos.engine_task_ids 被 MarkArrived 清成 NULL——单看这一列必定漏掉全部任务号。
        _db.MarkArrived(videoId);

        var trace = _db.GetEngineTraceForVideo(videoId);
        Assert.Equal(
            new[] { "task-codec-1", "task-main-1", "task-main-2" },
            trace.TaskIds.OrderBy(static s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(new long[] { 101, 102 }, trace.Cids.OrderBy(static c => c).ToArray());
    }

    [Fact]
    public void 对账料_脏JSON与空表都不炸()
    {
        var videoId = _db.AddVideo("https://www.bilibili.com/video/BV1forget2", "BV1forget2").VideoId;

        var empty = _db.GetEngineTraceForVideo(videoId);
        Assert.Empty(empty.TaskIds);
        Assert.Empty(empty.Cids);

        _db.MarkSubmitted(videoId, [], null, null);
        var after = _db.GetEngineTraceForVideo(videoId);
        Assert.Empty(after.TaskIds);
    }

    [Fact]
    public void 引擎历史按cid销_只销这一部_别人的行不动()
    {
        SeedEngineHistory(("h1", 101), ("h2", 101), ("h3", 102), ("h4", 202), ("h5", 0));

        // 202 是别部片子的 cid，0 是旧库迁移没填 cid 的行（拿它去删会把别人的历史一并捣掉），都不许碰。
        var deleted = LbsEnginePurge.PurgeEngineHistoryByCids(_downkyiDir, new long[] { 101, 102, 0, -7 });

        Assert.Equal(3, deleted);
        Assert.Equal(2, CountEngineHistory());
    }

    [Fact]
    public void 引擎历史销空_没台账没文件都只回零()
    {
        Assert.Equal(0, LbsEnginePurge.PurgeEngineHistoryByCids(_downkyiDir, Array.Empty<long>()));
        Assert.Equal(0, LbsEnginePurge.PurgeEngineHistoryByCids(
            Path.Combine(_tempDir, "并不存在"), new long[] { 101 }));

        SeedEngineHistory(("h1", 101));
        Assert.Equal(0, LbsEnginePurge.PurgeEngineHistoryByCids(_downkyiDir, new long[] { 999 }));
    }

    [Fact]
    public void 重下旗_挂上就认_走过一趟状态机就清零()
    {
        var videoId = _db.AddVideo("https://www.bilibili.com/video/BV1forget3", "BV1forget3").VideoId;
        Assert.False(_db.GetSubmissionCandidate(videoId)!.ForceRedownload);

        _db.MarkForceRedownload(videoId);
        Assert.True(_db.GetSubmissionCandidate(videoId)!.ForceRedownload);

        // 回炉走 ExecuteUpdate（状态机唯一改状态入口）：旗必须随之清零，否则下一趟重投会白重下一遍。
        _db.ResetToPending(videoId);
        Assert.False(_db.GetSubmissionCandidate(videoId)!.ForceRedownload);
    }

    [Fact]
    public void 重下旗_提交成功也清零()
    {
        var videoId = _db.AddVideo("https://www.bilibili.com/video/BV1forget4", "BV1forget4").VideoId;
        _db.MarkForceRedownload(videoId);
        Assert.True(_db.GetSubmissionCandidate(videoId)!.ForceRedownload);

        // MarkSubmitted 走自己的 UPDATE，也得把旗销掉：这枚旗只许被一次提交消费。
        _db.MarkSubmitted(videoId, ["task-x"], "片名", null);
        Assert.Null(_db.GetSubmissionCandidate(videoId)); // 已不在 Pending 态，取不到候选
        _db.MarkFailed(videoId, "网络抖了一下");
        _db.ResetToPending(videoId);
        Assert.False(_db.GetSubmissionCandidate(videoId)!.ForceRedownload);
    }
}
