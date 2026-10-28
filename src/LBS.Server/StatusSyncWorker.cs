using System.Text.Json;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 状态同步器：把引擎侧 task 的真实进展翻译回站内 videos.status。
/// 判定规则：task 还在途表里看 Phase；Failed 直接记失败原因。
/// task 从在途表消失即视为完成（引擎完成后搬进历史库；M1 没有删任务的入口，
/// 不存在「被删除误判为完成」的窗口，若 M3 加了删除功能需改用历史库精确判定）。
/// </summary>
public sealed class StatusSyncWorker(
    StationDb db,
    IDownloadTaskLookup lookup,
    DataPaths paths,
    CommentArchiveService commentArchive,
    LbsUpInfoFetcher upInfo,
    LbsCollectPolicy policy,
    DownloadPauseBoard board,
    ILogger<StatusSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // 停滞探测：连续巡视字节数与相位都不挪腾超阈值的在途任务，疑为崩溃遗留，销 last_error 提示站长可续下。
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMinutes(5);
    internal const string StallHint = "引擎任务停滞（疑崩溃遗留），点首页这张卡的闪电续下";

    private readonly Dictionary<long, StallWatch> _stalled = new();

    // 钉子户名片补采：单稿 UP 站内无现货可蹭、Arrived 后又永不再入站重采——每 24 轮（约两分钟）挑两条零散补采，
    // 节奏慢到不扰 B 站；没采集号时 TryFetchUpAsync 自己空转，不用这里把关。
    private const int UpCardRescueEveryPasses = 24;
    private const int UpCardRescueBatch = 2;
    private int _passesSinceUpCardRescue;

    private sealed record StallWatch(long Bytes, DownloadPhase Phase, DateTimeOffset StuckSince, bool Flagged);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("状态同步员上岗，每 {Seconds} 秒巡视一次下载中的馆藏", Interval.TotalSeconds);

        // 开局对账：头像文件换过 sha1 命名，早年 up-{mid}.jpg 那代老账全部丢链——先从同站现货里蹭一波（零出门），
        // 免得 UpCard 永远 404 顶灰占位；真正没现货可蹭的留给到站重采的老路。
        var borrowed = db.BackfillUpCardsFromShared();
        if (borrowed > 0)
        {
            logger.LogInformation("UP 名片开局蹭库：{Count} 条老账改了同站现货", borrowed);
        }

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "状态同步巡视出错，下一轮再试");
            }
        }
    }

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        await SyncMainVideosAsync(cancellationToken);
        await SyncCodecVersionsAsync(cancellationToken);
        await RescueStuckUpCardsAsync(cancellationToken);
    }

    /// <summary>钉子户补采：把脸没落盘又无现货可蹭的行零散挑几条出门重采（重采前 TryFetchUpAsync 自己会再蹭一次库）。</summary>
    private async Task RescueStuckUpCardsAsync(CancellationToken cancellationToken)
    {
        if (++_passesSinceUpCardRescue < UpCardRescueEveryPasses)
        {
            return;
        }

        _passesSinceUpCardRescue = 0;
        foreach (var videoId in db.ListUpCardsBeyondSalvage(UpCardRescueBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryFetchUpAsync(videoId);
        }
    }

    private async Task SyncMainVideosAsync(CancellationToken cancellationToken)
    {        foreach (var (videoId, taskIdsJson) in db.GetDownloadingVideos())
        {
            _seenThisPass.Add(videoId);
            var taskIds = ParseTaskIds(taskIdsJson);
            if (taskIds.Count == 0)
            {
                // 没有引擎任务却挂着下载中，属于脏数据，直接判到站比卡死诚实
                db.MarkArrived(videoId);
                MemberMail.NotifyVideoArrived(db, logger, videoId);
                _stalled.Remove(videoId);
                continue;
            }

            VideoStatus? target = null;
            string? failure = null;
            DownloadTask? inFlight = null;
            foreach (var taskId in taskIds)
            {
                var task = await lookup.FindAsync(new DownloadTaskId(taskId), cancellationToken);
                if (task is null)
                {
                    continue; // 已搬去历史库 = 完成
                }

                if (task.Phase == DownloadPhase.Failed)
                {
                    target = VideoStatus.Failed;
                    failure = task.Failure is null
                        ? "引擎未提供失败原因"
                        : $"{task.Failure.Code}：{task.Failure.Message}";
                    break;
                }

                inFlight = task;
                target = null; // 还有在途任务，维持 Downloading
                break;
            }

            // 循环里 null 有两种含义（全完成/在途），用显式标记区分
            if (target == VideoStatus.Failed)
            {
                db.MarkFailed(videoId, failure!);
                MemberMail.NotifyVideoFailed(db, logger, videoId, failure);
                _stalled.Remove(videoId);
                logger.LogWarning("馆藏 {VideoId} 引擎侧失败：{Failure}", videoId, failure);
            }
            else if (AllGone(taskIds, lookup, cancellationToken).GetAwaiter().GetResult())
            {
                db.MarkArrived(videoId);
                MemberMail.NotifyVideoArrived(db, logger, videoId);
                _stalled.Remove(videoId);
                var (archived, missingVideo) = db.FinalizeArtifacts(videoId, paths.Videos);
                logger.LogInformation(
                    "馆藏 {VideoId} 已到站（引擎任务 {Count} 个全部完成），归档产物 {Archived} 个{Missing}",
                    videoId,
                    taskIds.Count,
                    archived,
                    missingVideo > 0 ? $"，缺正片 {missingVideo} 个分 P" : string.Empty);

                // 评论归档异步补一刀，翻十几页可能耗几十秒，不能卡住同步员的巡视节奏；
                // CommentArchiveService 内部把异常都包成 Outcome，这里只兵来将挡一下取消。
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await TryFetchUpAsync(videoId);
                            var outcome = await commentArchive.TryArchiveAsync(videoId, force: false, CancellationToken.None);
                            if (!outcome.Ok)
                            {
                                logger.LogWarning("馆藏 {VideoId} 评论归档没成：{Message}", videoId, outcome.Message);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "馆藏 {VideoId} 评论归档抛出异常（已忽略）", videoId);
                        }
                    },
                    CancellationToken.None);
            }
            else if (inFlight is not null)
            {
                TrackStall(videoId, inFlight);
            }
        }

        // 本轮没见到的馆藏（已删除/改状态）从停滞表里销名，免得字典长毛。
        foreach (var staleId in _stalled.Keys.Where(id => !_seenThisPass.Contains(id)).ToList())
        {
            _stalled.Remove(staleId);
        }
        _seenThisPass.Clear();
    }

    private readonly HashSet<long> _seenThisPass = new();

    private void TrackStall(long videoId, DownloadTask task)
    {
        if (board.IsPaused)
        {
            // 暂停板落着的时候不动才是正常形态，不记停滞、顺手销旧账。
            _stalled.Remove(videoId);
            return;
        }

        var bytes = task.Progress.DownloadedBytes ?? 0;
        var now = DateTimeOffset.UtcNow;
        if (_stalled.TryGetValue(videoId, out var seen) && seen.Bytes == bytes && seen.Phase == task.Phase)
        {
            if (!seen.Flagged && now - seen.StuckSince >= StallThreshold)
            {
                db.SetLastError(videoId, StallHint);
                _stalled[videoId] = seen with { Flagged = true };
                logger.LogWarning("馆藏 {VideoId} 下载 {Minutes} 分钟没有进展（相位 {Phase}，{Bytes} 字节），疑为崩溃遗留，可在首页续下",
                    videoId, StallThreshold.TotalMinutes, task.Phase, bytes);
            }

            return;
        }

        if (seen is { Flagged: true })
        {
            db.SetLastError(videoId, null); // 又动弹了，撤停滞提示
        }

        _stalled[videoId] = new StallWatch(bytes, task.Phase, now, false);
    }

    /// <summary>
    /// 编码版本巡视：任务全消失即扫盘定档（按提交时登记的基准路径认文件），
    /// 引擎报失败或扫不到文件都记 Failed，播放页可以重新点单。
    /// </summary>
    private async Task SyncCodecVersionsAsync(CancellationToken cancellationToken)
    {
        foreach (var (rowId, videoId, pageIdx, codec, taskIdsJson, basePath) in db.GetDownloadingCodecVersions())
        {
            var taskIds = ParseTaskIds(taskIdsJson);
            if (taskIds.Count == 0)
            {
                continue; // 提交 worker 还没轮到这条，挂着等
            }

            var allGone = true;
            string? failure = null;
            foreach (var taskId in taskIds)
            {
                var task = await lookup.FindAsync(new DownloadTaskId(taskId), cancellationToken);
                if (task is null)
                {
                    continue; // 已搬历史库 = 完成
                }

                if (task.Phase == DownloadPhase.Failed)
                {
                    failure = task.Failure is null ? "引擎未提供失败原因" : $"{task.Failure.Code}：{task.Failure.Message}";
                }

                allGone = false; // 还在途
                break;
            }

            if (failure is not null)
            {
                db.MarkCodecFailed(rowId, failure);
                logger.LogWarning("编码版本 {VideoId}/P{Page}/{Codec} 引擎失败：{Failure}", videoId, pageIdx, codec, failure);
                continue;
            }

            if (!allGone)
            {
                continue;
            }

            if (basePath is null)
            {
                db.MarkCodecFailed(rowId, "任务完成但没有登记基准路径");
                continue;
            }

            if (db.FinalizeCodecVersion(rowId, basePath, paths.Videos))
            {
                logger.LogInformation("编码版本 {VideoId}/P{Page}/{Codec} 已入库", videoId, pageIdx, codec);
            }
            else
            {
                db.MarkCodecFailed(rowId, $"引擎报完成，但没扫到 {basePath} 开头的视频文件");
                logger.LogWarning("编码版本 {VideoId}/P{Page}/{Codec} 扫盘无文件：{Base}", videoId, pageIdx, codec, basePath);
            }
        }
    }

    private static async Task<bool> AllGone(
        IReadOnlyList<string> taskIds,
        IDownloadTaskLookup lookup,
        CancellationToken cancellationToken)
    {
        foreach (var taskId in taskIds)
        {
            if (await lookup.FindAsync(new DownloadTaskId(taskId), cancellationToken) is not null)
            {
                return false;
            }
        }

        return true;
    }

    internal static IReadOnlyList<string> ParseTaskIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// UP 主名片采集：到站时顺手采一次头像/简介/粉丝。失败也销账（up_face 置空串），
    /// 免得每次打开观看页都去扰 B 站；要重采删 video_meta 对应行即可。
    /// 除了 NULL，陈旧值（/avatars/ 下文件已不存在）也算没采好，会再出去采一趟。
    /// </summary>
    private async Task TryFetchUpAsync(long videoId)
    {
        var (mid, face) = db.GetUpCardState(videoId);
        if (mid is null || !LbsUpInfoFetcher.NeedsRefetch(face, paths.Avatars))
        {
            return;
        }

        // 同站先蹭：同一位 UP 的别稿已有化好的名片就直接抄一份，不扰 B 站也不占采集号；
        // 没现货可蹭才继续下面的出门流程。
        if (db.FindSharedUpCardByMid(mid.Value, videoId) is { } shared)
        {
            db.SetUpInfo(videoId, shared.Face, shared.Sign, shared.Fans);
            logger.LogInformation("馆藏 {VideoId} UP 名片同站蹭成：{Face}", videoId, shared.Face);
            return;
        }

        // 站里没号就不出门：UP 名片与互动数据全指着采集号，空着出去只会被 B 站 -101 打回，
        // 还会把设备的指纹拖进风控。柜台那块采集号状态会标灰，站长看得到原因。
        if (!policy.HasAccount())
        {
            logger.LogDebug("UP 名片本轮跳过 videoId={VideoId}：站里没有可用的采集号", videoId);
            return;
        }

        // 同趟的名片+互动数据共用一个采集号的上下文（分站已不按视频分主副号）。
        using var _owner = policy.BeginForVideo(videoId);
        var profile = await upInfo.FetchAsync(mid.Value, CancellationToken.None);
        if (profile is null)
        {
            db.SetUpInfo(videoId, string.Empty, null, null);
        }
        else
        {
            db.SetUpInfo(videoId, profile.FaceRel, profile.Sign, profile.Fans);
            logger.LogInformation(
                "馆藏 {VideoId} UP 主名片入库：{Name}·{Fans} 粉丝",
                videoId,
                profile.Name ?? "?",
                profile.Fans?.ToString() ?? "?");
        }

        // 互动数据同趟顺路采：失败不在这里硬试，观看页懒补自会接手。
        var brief = db.GetVideoBrief(videoId);
        if (brief is not null)
        {
            var stat = await upInfo.FetchStatAsync(brief.Value.Bvid, CancellationToken.None);
            if (stat is not null)
            {
                db.SetVideoStat(videoId, stat.Like, stat.Coin, stat.Favorite, stat.Share, stat.Reply);
                logger.LogInformation("馆藏 {VideoId} 互动数据入库：赞{Like} 币{Coin} 藏{Fav}", videoId, stat.Like, stat.Coin, stat.Favorite);
            }
        }
    }
}
