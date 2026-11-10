using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Services.Download;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LBS.Tests;

/// <summary>
/// 开机孤儿引擎任务清扫与全站暂停板这一对「引擎任务账」：
///  1) 清扫的「有人署名」必须真从台账解析出来。曾把清单手拼成一串不是 JSON 的字符再 Parse，
///     恒得到空集 = 开机把引擎里所有在途任务当孤儿销光（主任务与加档任务一起没主）；
///     任务号带转义、大小写不一致、两张表重复署名，都不许认错主人，也不许把真孤儿留下。
///  2) 暂停/继续要连 video_codecs 里的在途加档一起管住（原来只遍历 videos 的 Downloading 行，
///     暂停板落下加档照样跑），同一枚任务号只动一次，已定档/已失败/没挂起的不碰。
/// </summary>
public sealed class EngineTaskSweepAndPauseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public EngineTaskSweepAndPauseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-sweep-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "videos"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "downkyi"));
        _db = new StationDb(new DataPaths(
            _tempDir,
            Path.Combine(_tempDir, "videos"),
            Path.Combine(_tempDir, "downkyi"),
            _tempDir,
            _tempDir));
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

    // ─── 开机孤儿清扫 ────────────────────────────────────────────────

    [Fact]
    public async Task 开机清扫_主与加档署过名的任务不销_真孤儿才销()
    {
        var videoId = SeedDownloading("BV1sweepmain", "task-main-1", "task-main-2");
        SeedCodecDownloading(videoId, 1, "hevc", "task-codec-1");

        // 台账两表都照到（少了 video_codecs 那一格，加档那侧就成了「没主的人」）
        Assert.Equal(2, _db.GetReferencedEngineTaskIds().Count);

        var lookup = new FakeLookup(
            EngineTask("task-main-1"),
            EngineTask("task-main-2"),
            EngineTask("task-codec-1"),
            EngineTask("task-orphan"));
        var state = new FakeState();
        var queue = new FakeQueue();

        var swept = await DownloadWorker.SweepOrphanEngineTasksAsync(
            _db.GetReferencedEngineTaskIds(), lookup, state, queue, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, swept);
        Assert.Equal(new[] { "task-orphan" }, state.Cancelled);
        Assert.Equal(new[] { "task-orphan" }, state.Deleted);
        Assert.Equal(new[] { "task-orphan" }, queue.Cancelled);
    }

    [Fact]
    public async Task 开机清扫_任务号转义与大小写都不认错主人()
    {
        // 真会走进 JSON 的脏字符：引号、反斜杠、斜杠、空格、中文。
        // 手拼清单时这些字符会把整串解析打回空集，署过名的任务于是全被当孤儿销掉。
        const string tricky = "task-\"quote\"\\back\\slash 中文-空格";
        var videoId = SeedDownloading("BV1sweepesc", tricky, "TASK-Mixed-Case");
        // 同一枚任务在加档行里再署一次（大小写不同、一行里还重复两次）：去重后仍算有人署名，也只该留一枚
        SeedCodecDownloading(videoId, 1, "av1", "task-mixed-case", "task-mixed-case");

        var lookup = new FakeLookup(
            EngineTask(tricky),
            EngineTask("task-mixed-case"),
            EngineTask("task-orphan-2"));
        var state = new FakeState();
        var queue = new FakeQueue();

        var swept = await DownloadWorker.SweepOrphanEngineTasksAsync(
            _db.GetReferencedEngineTaskIds(), lookup, state, queue, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, swept);
        Assert.Equal(new[] { "task-orphan-2" }, state.Deleted);
    }

    [Fact]
    public async Task 开机清扫_台账坏JSON等于没署名_孤儿照销不炸()
    {
        var lookup = new FakeLookup(EngineTask("task-x"), EngineTask("task-y"));
        var state = new FakeState();
        var queue = new FakeQueue();

        var swept = await DownloadWorker.SweepOrphanEngineTasksAsync(
            new string?[] { null, "", "   ", "[]", "not json", "[\"unterminated" },
            lookup,
            state,
            queue,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(2, swept);
        Assert.Equal(new[] { "task-x", "task-y" }, state.Deleted);
        Assert.Equal(new[] { "task-x", "task-y" }, queue.Cancelled);
    }

    [Fact]
    public async Task 开机清扫_销不动的留给下轮_不拦后面的孤儿()
    {
        var videoId = SeedDownloading("BV1sweepstuck", "task-main-1");
        SeedCodecDownloading(videoId, 1, "hevc", "task-codec-1");

        var lookup = new FakeLookup(
            EngineTask("task-main-1"),
            EngineTask("task-codec-1"),
            EngineTask("task-orphan-a"),
            EngineTask("task-orphan-b"));
        var state = new FakeState();
        state.DeleteExplosions.Add("task-orphan-a");
        var queue = new FakeQueue();

        var swept = await DownloadWorker.SweepOrphanEngineTasksAsync(
            _db.GetReferencedEngineTaskIds(), lookup, state, queue, NullLogger.Instance, CancellationToken.None);

        // 一枚销不动不算事故：它不计数，但后面的孤儿照销，署过名的仍一枚没动
        Assert.Equal(1, swept);
        Assert.Equal(new[] { "task-orphan-a", "task-orphan-b" }, state.Cancelled);
        Assert.Equal(new[] { "task-orphan-b" }, state.Deleted);
        Assert.DoesNotContain("task-main-1", state.Deleted);
        Assert.DoesNotContain("task-codec-1", state.Deleted);
    }

    [Fact]
    public async Task 开机清扫_引擎没有在途任务_一枚也不销()
    {
        SeedDownloading("BV1sweepempty", "task-main-1");

        var state = new FakeState();
        var swept = await DownloadWorker.SweepOrphanEngineTasksAsync(
            _db.GetReferencedEngineTaskIds(), new FakeLookup(), state, new FakeQueue(), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, swept);
        Assert.Empty(state.Deleted);
    }

    // ─── 全站暂停板 ──────────────────────────────────────────────────

    [Fact]
    public async Task 暂停_主任务与加档一起挂起_已定档与已失败的行不碰()
    {
        var videoId = SeedDownloading("BV1pause", "task-main-1", "task-main-2");
        SeedCodecDownloading(videoId, 1, "hevc", "task-codec-1");

        // 已定档的加档行：任务号仍留在行里（定档不清账），但活干完了，不该再被挂起
        var doneRow = SeedCodecDownloading(videoId, 2, "av1", "task-codec-done");
        _db.MarkCodecArrived(doneRow, $"{videoId}/P2- AV1.mp4");

        // 已判失败的加档行同理：留给站长重试点单，暂停板不管
        var failedRow = SeedCodecDownloading(videoId, 3, "avc", "task-codec-failed");
        _db.MarkCodecFailed(failedRow, "引擎未为该分 P 产生任务");

        // 还没分到任务的在途加档行（判重让路中）：空清单不炸也不计数
        Assert.True(_db.AddCodecVersion(videoId, 4, "hevc"));

        // 已到站的主版本：不在 Downloading 行里，任务号不该被牵连
        var arrived = _db.AddVideo("https://www.bilibili.com/video/BV1arrived", "BV1arrived").VideoId;
        _db.MarkSubmitted(arrived, ["task-arrived"], "片名", null);
        Assert.True(_db.MarkArrived(arrived));

        var state = new FakeState();
        var board = Board(new FakeLookup(), state, new FakeQueue());

        var message = await board.PauseAllAsync(CancellationToken.None);

        Assert.Equal(new[] { "task-codec-1", "task-main-1", "task-main-2" }, Ordered(state.Paused));
        Assert.DoesNotContain("task-codec-done", state.Paused);
        Assert.DoesNotContain("task-codec-failed", state.Paused);
        Assert.DoesNotContain("task-arrived", state.Paused);
        Assert.Equal("1", _db.GetSetting("download-paused"));
        Assert.True(board.IsPaused);
        Assert.Contains("3 个在途任务已挂起", message);
    }

    [Fact]
    public async Task 继续_主任务与加档都唤醒_没挂起的不打扰且每枚只排一次()
    {
        var videoId = SeedDownloading(
            "BV1resume", "task-main-paused", "task-main-running", "task-gone", "task-shared");
        SeedCodecDownloading(videoId, 1, "hevc", "task-codec-paused", "task-shared");

        // 已定档加档行的任务即便在引擎里还挂着，也不归暂停板唤醒
        var doneRow = SeedCodecDownloading(videoId, 2, "av1", "task-codec-done");
        _db.MarkCodecArrived(doneRow, $"{videoId}/P2- AV1.mp4");

        var lookup = new FakeLookup(
            EngineTask("task-main-paused", DownloadPhase.Paused),
            EngineTask("task-main-running", DownloadPhase.Downloading),
            EngineTask("task-shared", DownloadPhase.Pausing),
            EngineTask("task-codec-paused", DownloadPhase.Paused),
            EngineTask("task-codec-done", DownloadPhase.Paused));
        var state = new FakeState();
        var queue = new FakeQueue();
        var board = Board(lookup, state, queue);

        var message = await board.ResumeAllAsync(CancellationToken.None);

        // task-shared 在主行与加档行都署了名：只唤醒一次、只重排一次；task-gone 已搬历史库
        Assert.Equal(new[] { "task-codec-paused", "task-main-paused", "task-shared" }, Ordered(state.Resumed));
        Assert.Equal(new[] { "task-codec-paused", "task-main-paused", "task-shared" }, Ordered(queue.Enqueued));
        Assert.DoesNotContain("task-main-running", state.Resumed);
        Assert.DoesNotContain("task-codec-done", state.Resumed);
        Assert.Equal("0", _db.GetSetting("download-paused"));
        Assert.False(board.IsPaused);
        Assert.Contains("3 个挂起任务重新排队", message);
    }

    [Fact]
    public async Task 暂停与继续_同一枚任务号只动一次_大小写脏行也算同一枚()
    {
        // 脏台账：同一枚任务号在主行与加档行都署了名，另一枚两边只差大小写
        var videoId = SeedDownloading("BV1dedupe", "task-shared", "TASK-Casey");
        SeedCodecDownloading(videoId, 1, "hevc", "task-shared", "task-casey");

        var state = new FakeState();
        var queue = new FakeQueue();
        // 引擎里只有主行那份写法：去重后只会拿它去点名（大小写不同的那份算同一枚，不重复动）
        var board = Board(
            new FakeLookup(
                EngineTask("task-shared", DownloadPhase.Paused),
                EngineTask("TASK-Casey", DownloadPhase.Paused)),
            state,
            queue);

        await board.PauseAllAsync(CancellationToken.None);
        Assert.Equal(2, state.Paused.Count);
        Assert.Single(state.Paused, static id => id == "task-shared");
        Assert.Single(state.Paused, static id => string.Equals(id, "task-casey", StringComparison.OrdinalIgnoreCase));

        await board.ResumeAllAsync(CancellationToken.None);
        Assert.Equal(2, state.Resumed.Count);
        Assert.Equal(state.Resumed.Count, queue.Enqueued.Count);
    }

    // ─── 造账与假引擎 ────────────────────────────────────────────────

    private long SeedDownloading(string bvid, params string[] taskIds)
    {
        var videoId = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.MarkSubmitted(videoId, taskIds, "片名", null);
        return videoId;
    }

    /// <summary>造一枚在途加档行并登记引擎任务号，返回行 id。</summary>
    private long SeedCodecDownloading(long videoId, int pageIdx, string codec, params string[] taskIds)
    {
        Assert.True(_db.AddCodecVersion(videoId, pageIdx, codec));
        var row = _db.GetDownloadingCodecVersions()
            .Single(r => r.VideoId == videoId && r.PageIdx == pageIdx && r.Codec == codec);
        _db.MarkCodecSubmitted(row.Id, taskIds, $"{videoId}/P{pageIdx}-{codec}");
        return row.Id;
    }

    private DownloadPauseBoard Board(IDownloadTaskLookup lookup, IDownloadTaskStateService state, IDownloadTaskQueue queue) =>
        new(_db, lookup, state, queue, NullLogger<DownloadPauseBoard>.Instance);

    private static string[] Ordered(IEnumerable<string> ids) =>
        ids.OrderBy(static s => s, StringComparer.Ordinal).ToArray();

    private static DownloadTask EngineTask(string id, DownloadPhase phase = DownloadPhase.Downloading)
    {
        return DownloadTask.Restore(
            new DownloadTaskId(id),
            new DownloadTaskMetadata(
                new DownloadMediaIdentity("BV1sweep", 1, 1, 0, 1, 1),
                "片名",
                "P1",
                "00:01",
                "avc1",
                new DownloadQuality(80, "1080P"),
                new DownloadQuality(30280, "AAC"),
                string.Empty,
                string.Empty,
                0),
            new DownloadPlan(DownloadContentSelection.None, [], 0, nfoRequest: null),
            new DownloadOutput(id, null),
            phase,
            DownloadProgress.None,
            DownloadTransferState.Empty,
            failure: null,
            completion: null,
            version: 1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1));
    }

    private static Exception NotSupported() => new NotSupportedException("这块测试用不到这个引擎动作");

    private sealed class FakeLookup : IDownloadTaskLookup
    {
        private readonly Dictionary<string, DownloadTask> _tasks;

        public FakeLookup(params DownloadTask[] tasks) =>
            _tasks = tasks.ToDictionary(static t => t.Id.Value, StringComparer.Ordinal);

        public Task<DownloadTask?> FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken) =>
            Task.FromResult(_tasks.TryGetValue(taskId.Value, out var task) ? task : null);

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>(
                _tasks.Keys.OrderBy(static s => s, StringComparer.Ordinal).Select(id => _tasks[id]).ToList());

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidAsync(long cid, CancellationToken cancellationToken) =>
            throw NotSupported();

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidsAsync(IReadOnlyCollection<long> cids, CancellationToken cancellationToken) =>
            throw NotSupported();
    }

    private sealed class FakeState : IDownloadTaskStateService
    {
        public List<string> Paused { get; } = [];
        public List<string> Resumed { get; } = [];
        public List<string> Cancelled { get; } = [];
        public List<string> Deleted { get; } = [];

        /// <summary>点名几枚「销不动」的任务：清扫要绕过去继续销后面的，不整轮趴下。</summary>
        public HashSet<string> DeleteExplosions { get; } = [];

        public Task<OperationResult<DownloadTask>> PauseAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            Paused.Add(taskId.Value);
            return Task.FromResult(Succeeded(taskId.Value));
        }

        public Task<OperationResult<DownloadTask>> ResumeAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            Resumed.Add(taskId.Value);
            return Task.FromResult(Succeeded(taskId.Value));
        }

        public Task<OperationResult<DownloadTask>> CancelAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            Cancelled.Add(taskId.Value);
            return Task.FromResult(Succeeded(taskId.Value));
        }

        public Task<OperationResult<DownloadTask>> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            if (DeleteExplosions.Contains(taskId.Value))
            {
                throw new InvalidOperationException("引擎台账锁着，这枚销不动");
            }

            Deleted.Add(taskId.Value);
            return Task.FromResult(Succeeded(taskId.Value));
        }

        private static OperationResult<DownloadTask> Succeeded(string id) =>
            OperationResult.Success(EngineTask(id, DownloadPhase.Paused));

        Task<DownloadTask?> IDownloadTaskSchedulerService.FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.StartAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.ConfirmPausedAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.RetryAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.RecoverInterruptedAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.ReconcileInterruptedAsync(DownloadTaskId taskId, long snapshotVersion, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.FailAsync(DownloadTaskId taskId, DownloadFailure failure, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.FailIfRunnableAsync(DownloadTaskId taskId, DownloadFailure failure, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.FailIfDispatchableAsync(DownloadTaskId taskId, DownloadFailure failure, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.ConfirmPausedIfPausingAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.DiscardUnpublishedArtifactsAndFailAsync(DownloadTaskId taskId, DownloadFailure failure, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.CompleteAsync(DownloadTaskId taskId, DownloadCompletion completion, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.RecordTransferFileAsync(DownloadTaskId taskId, string key, string filePath, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.ClaimTransferFileAsync(DownloadTaskId taskId, string key, string filePath, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.ClaimTransferFilesAsync(DownloadTaskId taskId, IReadOnlyCollection<KeyValuePair<string, string>> files, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.InvalidateCompletedFileAsync(DownloadTaskId taskId, string key, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.InvalidateCompletedFilesAsync(DownloadTaskId taskId, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.CompleteTransferFileAsync(DownloadTaskId taskId, string key, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.RecordPublishedArtifactAsync(DownloadTaskId taskId, DownloadPublishingArtifact publishing, string path, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.BeginPublishingArtifactAsync(DownloadTaskId taskId, DownloadPublishingArtifact publishing, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.ClearPublishingArtifactAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.SetBackendIdentityAsync(DownloadTaskId taskId, string? backendIdentity, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.UpdateActivityAsync(DownloadTaskId taskId, string? activeContent, string? statusText, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.UpdateProgressAsync(DownloadTaskId taskId, DownloadProgress progress, CancellationToken cancellationToken) => throw NotSupported();
        Task<OperationResult<DownloadTask>> IDownloadTaskStateService.UpdateOutputFileSizeAsync(DownloadTaskId taskId, string? fileSizeText, CancellationToken cancellationToken) => throw NotSupported();
    }

    private sealed class FakeQueue : IDownloadTaskQueue
    {
        public List<string> Enqueued { get; } = [];
        public List<string> Cancelled { get; } = [];

        public Task EnqueueAsync(DownloadTaskId taskId, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(taskId.Value);
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId)
        {
            Cancelled.Add(taskId.Value);
            return Task.FromResult(true);
        }
    }
}
