using System.Text.Json;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
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
    IDownloadTaskStateService state,
    IDownloadTaskQueue taskQueue,
    DataPaths paths,
    CommentArchiveService commentArchive,
    LbsUpInfoFetcher upInfo,
    LbsCollectPolicy policy,
    DownloadPauseBoard board,
    DownloadQueue queue,
    MediaStreamProber streams,
    ILogger<StatusSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // 判重让路的加档行：回炉前先拿 90 秒租约（比巡视间隔宽出十几倍）抢地皮，
    // 租约期内 retry_at_utc 在未来，下一轮巡视看不见它，同一条就不会被连投两次。
    private static readonly TimeSpan CodecRetryLease = TimeSpan.FromSeconds(90);

    // 停滞探测：连续巡视字节数与相位都不挪腾超阈值的在途任务，疑为崩溃遗留，销 last_error 提示站长可续下。
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMinutes(5);
    internal const string StallHint = "引擎任务停滞（疑崩溃遗留），点首页这张卡的闪电续下";

    private readonly Dictionary<long, StallWatch> _stalled = new();

    // 加档停滞看门狗：主版本有停滞探测，加档行一直没人管——引擎任务卡在非 Failed 相位就是一天天地等。
    // 相位与字节数都不挪腾超 30 分钟即疑为崩溃遗留：销掉引擎任务回炉 Failed，站长首页重点；
    // 一直没分到任务的行同样超时就销，重启遗留的盲区一并补掉。
    private static readonly TimeSpan CodecStallThreshold = TimeSpan.FromMinutes(30);
    private readonly Dictionary<long, CodecStallWatch> _codecStalled = new();
    private readonly HashSet<long> _codecSeenThisPass = new();

    private sealed record CodecStallWatch(string Signature, DateTimeOffset StuckSince);

    // 钉子户名片补采：单稿 UP 站内无现货可蹭、Arrived 后又永不再入站重采——每 24 轮（约两分钟）挑两条零散补采，
    // 节奏慢到不扰 B 站；没采集号时 TryFetchUpAsync 自己空转，不用这里把关。
    private const int UpCardRescueEveryPasses = 24;
    private const int UpCardRescueBatch = 2;
    private int _passesSinceUpCardRescue;

    // UP 认证的存量回填（11-05）：这一栏是后加的采集项，而脸早已本地化的老 UP 在 NeedsRefetch 那里算「能用」，
    // 补采轮永远不登他们的门 —— 不专门跑一轮，功能对老用户等于不存在。照 m54-migrated 的先例在 settings 立碑：
    // 跑完一轮才盖哨兵（没号跳过不算跑过，下次开机接着补），之后永不再扰 B 站。
    // 哨兵原名 up-head-official-migrated（头图与认证同轮）；头图已于 11-06 废除，老站的旧碑由 NeedsUpOfficialBackfill 搬过来，不重跑。
    internal const string UpOfficialMigratedKey = "up-official-migrated";
    internal const string LegacyUpOfficialMigratedKey = "up-head-official-migrated";
    private static readonly TimeSpan UpOfficialBackfillDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan UpOfficialBackfillStartupDelay = TimeSpan.FromSeconds(20);

    private sealed record StallWatch(long Bytes, DownloadPhase Phase, DateTimeOffset StuckSince, bool Flagged);

    // 真值暂记簿：引擎实发的画质/音频档（Metadata.Resolution/.AudioCodec，替换完可用性降级后的真相，
    // id 116=1080P 60帧、30251/31251=Hi-Res 系）只活在在途任务身上——搬进历史库后 FindAsync 就摸不到了，
    // 只能趁在途拄下来。到站/定档那一刻落库（quality_name 进清晰度菜单、audio_quality_id 进 Hi-Res 徽记判据）；
    // 重启窗口里错过的在途期没得拄，退回 ffprobe 实测链，不阻塞归档。
    private sealed record QualityStamp(int? QualityId, string? QualityName, int? AudioQualityId);

    private static QualityStamp StampOf(DownloadTask task) => new(
        task.Metadata.Resolution?.Id,
        string.IsNullOrWhiteSpace(task.Metadata.Resolution?.Name) ? null : task.Metadata.Resolution!.Name,
        task.Metadata.AudioCodec?.Id);

    // 主版本按 videoId 记、加档按 rowId 记：两张表主键会串号，分家省事。
    private readonly Dictionary<long, QualityStamp> _qualitySeen = new();
    private readonly Dictionary<long, QualityStamp> _codecQualitySeen = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("状态同步员上岗，每 {Seconds} 秒巡视一次下载中的馆藏", Interval.TotalSeconds);

        // 开局对账：头像文件换过 sha1 命名，早年 up-{mid}.jpg 那代老账全部丢链——先从同站现货里蹭一波（零出门），
        // 免得 UpCard 永远 404 顶灰占位；真正没现货可蹭的留给到站重采的老路。
        // 包壳：开局对账炸了不能拖着整个同步员下岗，本轮不蹭就是了，下一轮/到站重采都有出口。
        try
        {
            var borrowed = db.BackfillUpCardsFromShared();
            if (borrowed > 0)
            {
                logger.LogInformation("UP 名片开局蹭库：{Count} 条老账改了同站现货", borrowed);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UP 名片开局蹭库没跑成（不影响巡视，到站重采会补）");
        }

        // 认证的存量欠账：开局补一轮。它要逐位出网（一位一枪 + 300ms 歇），几百位能跑到几分钟，
        // 所以不排在巡视主循环前面（不能让下载对账等它），也不与开机的补探/复活抢那几秒问 —— 20 秒后开跑。
        _ = Task.Run(() => BackfillUpOfficialAsync(stoppingToken), CancellationToken.None);

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

    /// <summary>哨兵还没盖（或上一轮因没号根本没跑）才算欠账。抽成静态小口是为了让这条判据能被测到。
    /// 老站升级不重跑：头图时代那块碑（<c>up-head-official-migrated</c>）里有值就把值搬到新碑、拆旧碑 ——
    /// 当年那一轮已经把认证采进库了，不能因为改名让老用户重跑一遍扰 B 站。</summary>
    internal static bool NeedsUpOfficialBackfill(StationDb db)
    {
        if (db.GetSetting(UpOfficialMigratedKey) is not null)
        {
            return false;
        }

        var legacy = db.GetSetting(LegacyUpOfficialMigratedKey);
        if (legacy is null)
        {
            return true;
        }

        db.SetSetting(UpOfficialMigratedKey, legacy);
        db.RemoveSetting(LegacyUpOfficialMigratedKey);
        return false;
    }

    /// <summary>
    /// 一次性存量回填：把 <see cref="StationDb.ListMidsLackingOfficial"/> 那份名单逐位走现有的
    /// <see cref="LbsUpInfoFetcher.FetchAsync"/>（认证就在这趟里），采到了就走
    /// <see cref="StationDb.SetUpOfficial"/> 只刷那一列（名片其余栏一个字节不碰）。
    /// 门闸与节奏照封面补员：没采集号整轮跳过（<b>不盖哨兵</b>，下次开机再试），位与位之间 300ms，
    /// 整轮挂在 <see cref="LbsCollectPolicy.RunForActivityAsync"/> 的出号上下文里（LbsQueryClient 第一行就 RequireCookie）。
    /// 采不到只记账不报错：无认证是常态不是故障——<b>「出门了但没带回货」照盖</b>，不然每次开机都扰一遍 B 站（11-05 深夜交叉验证的教训：filled==0 就不盖会让无认证普通 UP 永不收敛，每次开机 3N 发出网）。
    /// 盖不盖由 <see cref="ShouldStampOfficialSentinel"/> 裁决（抽成纯函数供测格钉死）：整轮一张名片都没采成不盖（接口不认这趟，盖了是装「干完活」）。
    /// 风控计数那道门闸已随头图一同退役：banner 链路没了之后这轮不再打 acc/info，card 口的 -799 也无计数器可记。
    /// </summary>
    private async Task BackfillUpOfficialAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(UpOfficialBackfillStartupDelay, cancellationToken);
            if (!NeedsUpOfficialBackfill(db))
            {
                return;
            }

            var mids = db.ListMidsLackingOfficial();
            if (mids.Count == 0)
            {
                db.SetSetting(UpOfficialMigratedKey, DateTimeOffset.UtcNow.ToString("O"));
                return;
            }

            // 号池空了不装忙（封面补员同款姿势）：先问一句再出号，也不把哨兵盖上一了百了。
            if (!policy.HasAccount())
            {
                logger.LogInformation("UP 认证存量回填：站里没可用的采集号，这 {Total} 位先欠着（哨兵不盖，下次开机再试）", mids.Count);
                return;
            }

            await policy.RunForActivityAsync(() => FillUpOfficialAsync(mids, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关门收兵：哨兵没盖，下次开机接着补。
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UP 认证存量回填没跑成（不致命），下次开机再试");
        }
    }

    private async Task FillUpOfficialAsync(IReadOnlyList<long> mids, CancellationToken cancellationToken)
    {
        var filled = 0;
        var attempted = 0; // 本轮名片正经采到的位数：「真没有」与「接口全不认」的唯一分辨凭证（11-05 深夜交叉验证补）
        foreach (var mid in mids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = await upInfo.FetchAsync(mid, cancellationToken);
            if (profile is not null)
            {
                attempted++;
            }

            // 只写拿到货的那几位：认证是 null（无认证的人）时连窄口都不必开（COALESCE 下写 null 本就是空操作）；
            // 算进「补了 X 位」就是把空手记成战功。整轮一行 UPDATE 没发也不报错 —— 无认证是常态。
            if (profile?.Official is not null && db.SetUpOfficial(mid, profile.Official) > 0)
            {
                filled++;
            }

            await Task.Delay(UpOfficialBackfillDelay, cancellationToken);
        }

        // 盖戳判据抽成纯函数（真值表由测格钉死）：整轮零张名片不盖（card 接口不认这趟，盖了永锁缺账）；
        // 有名片就盖——无认证没货是常态，不盖就是每次开机 3N 发出网永不收敛。
        if (!ShouldStampOfficialSentinel(attempted))
        {
            logger.LogWarning("UP 认证存量回填：补了 {Filled} 位 / 共 {Total} 位，名片只成了 {Attempted} 张——判为接口不认，这轮不盖哨兵，下次开机再试",
                filled, mids.Count, attempted);
            return;
        }

        // 跑完整轮才立碑（与 m54-migrated 同款：干完活再盖戳，中途挂掉下次还能重跑）。
        db.SetSetting(UpOfficialMigratedKey, DateTimeOffset.UtcNow.ToString("O"));
        logger.LogInformation("UP 认证存量回填：补了 {Filled} 位 / 共 {Total} 位", filled, mids.Count);
    }

    /// <summary>哨兵盖戳判据（纯函数供测格钉死，NeedsUpOfficialBackfill 同族）：
    /// 一张名片都没采成（attempted==0）不盖——card 接口整轮不认，盖了是把「没采到」焊成「干完活」；
    /// 有名片就盖——非主播/无认证没货是常态，不盖就是每次开机重扰 B 站永不收敛。
    /// 旧签名里的 throttleHits 参数已随 banner 链路退役：没了两发 acc/info，风控计数恒为 0，留着只会把判据说模糊。</summary>
    internal static bool ShouldStampOfficialSentinel(int attempted) => attempted > 0;

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        await SyncMainVideosAsync(cancellationToken);
        await SyncCodecVersionsAsync(cancellationToken);
        await RequeueDeferredCodecsAsync(cancellationToken);
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
                // 没有引擎任务却挂着下载中，属于脏数据，直接判到站比卡死诚实。
                // 守卫照走：读快照与落笔之间若复活专员已把行退回 Pending，这一盖就该丢。
                if (!db.MarkArrived(videoId, VideoStatus.Downloading))
                {
                    logger.LogInformation("馆藏 {VideoId} 判到站时发现状态已被抢先改走，本轮不通知不归档", videoId);
                    continue;
                }

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

            // 趁在途拄实发规格（每轮都拄最新的，多任务稿件最后见到的那份为准——同稿一档，谁拄都一样）。
            if (inFlight is not null)
            {
                _qualitySeen[videoId] = StampOf(inFlight);
            }

            // 循环里 null 有两种含义（全完成/在途），用显式标记区分
            if (target == VideoStatus.Failed)
            {
                if (db.MarkFailed(videoId, failure!, VideoStatus.Downloading))
                {
                    MemberMail.NotifyVideoFailed(db, logger, videoId, failure);
                    _stalled.Remove(videoId);
                    _qualitySeen.Remove(videoId);
                    logger.LogWarning("馆藏 {VideoId} 引擎侧失败：{Failure}", videoId, failure);
                }
                else
                {
                    logger.LogInformation("馆藏 {VideoId} 判失败时发现状态已被抢先改走，不盖不改账", videoId);
                }
            }
            else if (AllGone(taskIds, lookup, cancellationToken).GetAwaiter().GetResult())
            {
                // 守卫：巡视读的是本轮开头的快照，读到与落笔之间复活专员可能已把行退回 Pending
                // （硬重投先删引擎行再 ResetToPending）。这一盖若盖在刚退回的行上，盘上没文件、引擎没任务，
                // 馆藏会卡在假到站再也起不来——落笔要求当前仍是 Downloading，抢输了就整段跳过。
                if (!db.MarkArrived(videoId, VideoStatus.Downloading))
                {
                    logger.LogInformation("馆藏 {VideoId} 到站盖章被抢先（多半是重投交错），本轮不归档不通知", videoId);
                    continue;
                }

                MemberMail.NotifyVideoArrived(db, logger, videoId);
                _stalled.Remove(videoId);
                // 引擎实发规格趁热盖章（在途拄的那份）：清晰度菜单直接照报「1080P 60帧」，
                // Hi-Res 徽记拿 audio_quality_id 当硬判据。拄过即销账，不占内存。
                if (_qualitySeen.TryGetValue(videoId, out var stamp))
                {
                    db.SetPageQuality(videoId, stamp.QualityId, stamp.QualityName, stamp.AudioQualityId);
                }

                _qualitySeen.Remove(videoId);
                var (archived, missingVideo) = db.FinalizeArtifacts(videoId, paths.Videos);
                // 引擎首下的封面是引擎自己落的、不登记台账，行刚被上面那句扫出来——缩略只能切在这之后。
                // （补员与重拉那两条路在 CoverFileFetcher 里就地切片，两边合起来才盖住全部来路。）
                await CoverThumbs.TryBuildAsync(db, paths, videoId, cancellationToken);
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
                            // 分轨码率趁热探：正片刚归档完、还在页缓存里，一趟 ffprobe 把编码名 + 双轨 kbps 一次写掉。
                            // 到站那一刻不探就得等开片，而前端不现探，错过这个事件点就一直没码率。
                            streams.ProbeArrivedVideo(videoId);
                            await TryFetchUpAsync(videoId);
                            // 互动数据与创作团队单独走一趟：同站名下的稿 UP 名片会提前 return，
                            // 挂在它后面那些稿就永远采不到 staff。
                            await TryFetchStatAsync(videoId);
                            var outcome = await commentArchive.TryArchiveAsync(videoId, force: false, cancellationToken);
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
                    cancellationToken);
            }
            else if (inFlight is not null)
            {
                TrackStall(videoId, inFlight);
            }
        }

        // 本轮没见到的馆藏（已删除/改状态）从停滞表与真值暂记簿里销名，免得字典长毛。
        foreach (var staleId in _stalled.Keys.Where(id => !_seenThisPass.Contains(id)).ToList())
        {
            _stalled.Remove(staleId);
        }

        foreach (var staleId in _qualitySeen.Keys.Where(id => !_seenThisPass.Contains(id)).ToList())
        {
            _qualitySeen.Remove(staleId);
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
            _codecSeenThisPass.Add(rowId);
            var taskIds = ParseTaskIds(taskIdsJson);
            if (taskIds.Count == 0)
            {
                // 提交 worker 还没轮到这条，挂着等；但一直挂超 30 分钟就是提交途中进程重启的遗留——
                // 这种行从此没人捾，看门狗判它回炉，站长首页重点加档即可。
                if (!TrackCodecStall(rowId, "no-task"))
                {
                    db.MarkCodecFailed(rowId, "加档迟迟没分到任务，看门狗超 30 分钟收摊，可回到那支片子的播放器里重新点单加档");
                    logger.LogWarning("编码加档 {VideoId}/P{Page}/{Codec} 迟迟未提交，看门狗收摊", videoId, pageIdx, codec);
                }
                continue;
            }

            var allGone = true;
            string? failure = null;
            DownloadTask? inFlight = null;
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

                inFlight = task;
                allGone = false; // 还在途
                break;
            }

            if (failure is not null)
            {
                // 判 Failed 顺手把引擎账也销了：光判行不行——引擎判重不看相位，死任务会被当成
                // 「未完成的同名任务」把同视频的重试点单永远拦在让路循环（锁R&B 事故现场）。
                _codecQualitySeen.Remove(rowId);
                await RetireCodecTaskAsync(rowId, videoId, pageIdx, codec, taskIds, failure, cancellationToken);
                continue;
            }

            // 趁在途拄实发规格（与主版本同一套路，定档那一刻盖章）。
            if (inFlight is not null)
            {
                _codecQualitySeen[rowId] = StampOf(inFlight);
            }

            if (!allGone)
            {
                // 旧版盲区：任务只要在途表里就永远算「还在途」——相位与字节都不挪腾超 30 分钟，
                // 看门狗把引擎任务撤销并判 Failed，不让僵尸行占住同名去重的名额。
                if (inFlight is not null && !TrackCodecStall(rowId, $"{inFlight.Phase}:{inFlight.Progress.DownloadedBytes ?? 0}"))
                {
                    await RetireCodecTaskAsync(rowId, videoId, pageIdx, codec, taskIds,
                        "引擎任务 30 分钟没有进展（疑崩溃遗留），看门狗收摊，可回到那支片子的播放器里重新点单加档", cancellationToken);
                }

                continue;
            }

            _codecStalled.Remove(rowId); // 任务全消失了，停滞账本清账（接下来要么定档要么记失败）

            if (basePath is null)
            {
                db.MarkCodecFailed(rowId, "任务完成但没有登记基准路径");
                continue;
            }

            if (db.FinalizeCodecVersion(rowId, basePath, paths.Videos))
            {
                logger.LogInformation("编码版本 {VideoId}/P{Page}/{Codec} 已入库", videoId, pageIdx, codec);
                // 定档那一刻盖引擎实发规格的章（在途拄的那份），盖完销账。
                if (_codecQualitySeen.TryGetValue(rowId, out var stamp))
                {
                    db.SetCodecQuality(rowId, stamp.QualityId, stamp.QualityName, stamp.AudioQualityId);
                }

                _codecQualitySeen.Remove(rowId);
                // 定档那一刻文件还热在页缓存里，趁热探分轨码率；起进程不许卡 5 秒一轮的巡视，甩后台。
                _ = Task.Run(() => streams.ProbeCodecFile(rowId, videoId, pageIdx, codec), cancellationToken);
            }
            else
            {
                db.MarkCodecFailed(rowId, $"引擎报完成，但没扫到 {basePath} 开头的视频文件");
                logger.LogWarning("编码版本 {VideoId}/P{Page}/{Codec} 扫盘无文件：{Base}", videoId, pageIdx, codec, basePath);
            }
        }

        // 本轮没见到的加档行（定档/失败/被删）从停滞表与真值暂记簿销名，免得字典长毛。
        foreach (var rowId in _codecStalled.Keys.Where(id => !_codecSeenThisPass.Contains(id)).ToList())
        {
            _codecStalled.Remove(rowId);
        }

        foreach (var rowId in _codecQualitySeen.Keys.Where(id => !_codecSeenThisPass.Contains(id)).ToList())
        {
            _codecQualitySeen.Remove(rowId);
        }
        _codecSeenThisPass.Clear();
    }

    /// <summary>加档停滞记号：签名（相位:字节 或 no-task）换了就重计时；超阈值返回 false 让调用方收摊。暂停板落着不算停滞。</summary>
    private bool TrackCodecStall(long rowId, string signature)
    {
        if (board.IsPaused)
        {
            _codecStalled.Remove(rowId);
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (_codecStalled.TryGetValue(rowId, out var seen))
        {
            if (seen.Signature == signature)
            {
                return now - seen.StuckSince < CodecStallThreshold;
            }

            _codecStalled[rowId] = new CodecStallWatch(signature, now);
            return true;
        }

        _codecStalled[rowId] = new CodecStallWatch(signature, now);
        return true;
    }

    /// <summary>销引擎账编排：逐枚撤销任务（打断队列→挂起→销账，对齐删档那套）再把加档行判 Failed；
    /// 引擎销不动也不拦收摊——账先销，残存任务由下一轮看门狗、开机清扫或引擎自己收尾。
    /// Failed 回炉与看门狗收摊共用这条道：死任务绝不能留在在途表里占同名去重的名额。</summary>
    private async Task RetireCodecTaskAsync(
        long rowId,
        long videoId,
        int pageIdx,
        string codec,
        IReadOnlyList<string> taskIds,
        string reason,
        CancellationToken cancellationToken)
    {
        foreach (var taskId in taskIds)
        {
            if (await lookup.FindAsync(new DownloadTaskId(taskId), cancellationToken) is null)
            {
                continue;
            }

            try
            {
                await taskQueue.CancelAsync(new DownloadTaskId(taskId));
            }
            catch (Exception)
            {
                // 本就没在跑，取消不掉无妨
            }

            try
            {
                await state.CancelAsync(new DownloadTaskId(taskId), cancellationToken);
                await state.DeleteAsync(new DownloadTaskId(taskId), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "编码加档的引擎任务 {TaskId} 销不动，先销加档行", taskId);
            }
        }

        db.MarkCodecFailed(rowId, reason);
        _codecStalled.Remove(rowId);
        logger.LogWarning("编码加档 {VideoId}/P{Page}/{Codec} 判 Failed 并销引擎账：{Reason}", videoId, pageIdx, codec, reason);
    }

    /// <summary>
    /// 判重让路的加档行到期回炉：先抢租约（抢不到 = 已被别的轮次接走）再投回加档队列。
    /// 按 (稿件, 编码) 去重只投一次——提交本来就是整稿整编码一把梭，逐分 P 入队会撞自己的在途任务。
    /// 暂停板与逐件间隔仍由 SubmitCodecAsync 那条路照管，这里不另开闸。
    /// </summary>
    private async Task RequeueDeferredCodecsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var batch in db.GetDueCodecRetries(now).GroupBy(row => (row.VideoId, row.Codec)))
        {
            var claimed = 0;
            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (db.TakeCodecRetryLease(row.Id, now, now + CodecRetryLease))
                {
                    claimed++;
                }
            }

            if (claimed == 0)
            {
                continue;
            }

            var head = batch.First();
            logger.LogInformation("编码加档 {VideoId}/{Codec} 让路到期，回炉重投（{Rows} 个分 P）", head.VideoId, head.Codec, claimed);
            await queue.EnqueueCodecAsync(head.VideoId, head.Codec, cancellationToken);
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
    /// UP 主名片采集：到站时顺手采一次头像/简介/粉丝，顺带 B 站官方认证（就一张 card）。
    /// 失败也销账（up_face 置空串），
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
            // 只拄名片那三栏：official 不传就是 NULL（SetUpInfo 对 null 的现口径）。
            // 被抄的这行本来就没脸（所以才会走蹭），它的认证大概也一并没；真丢了由另一行或补采轮兜着。
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
            // 失败只销 face 那一笔（NeedsRefetch 只看它）；official 不传就是 NULL，重采时再长回来。
            db.SetUpInfo(videoId, string.Empty, null, null);
        }
        else
        {
            db.SetUpInfo(videoId, profile.FaceRel, profile.Sign, profile.Fans, profile.Official);
            logger.LogInformation(
                "馆藏 {VideoId} UP 主名片入库：{Name}·{Fans} 粉丝{Official}",
                videoId,
                profile.Name ?? "?",
                profile.Fans?.ToString() ?? "?",
                profile.Official is null ? "" : $"·{profile.Official}");
        }

        // 互动数据与创作团队不再挂在本方法尾上：它们自己有一趟（TryFetchStatAsync），
        // 不至于因为 UP 名片同站蹭成而跟着没采。
    }

    /// <summary>
    /// 互动数据与创作团队：到站时单独走一趟，一次 view 同时拿五维真数与联合创作名单（B 站「创作团队 N 人」那份）。
    /// 采不到不销账，观看页懒补（BackfillStat）自会接手；要重采删 video_meta 对应行即可。
    /// </summary>
    private async Task TryFetchStatAsync(long videoId)
    {
        if (!db.ShouldBackfillStat(videoId))
        {
            return;
        }

        // 同 UP 名片那条规矩：没号就不出门，空手出去只会被 -101 打回，还把设备指纹递进风控。
        if (!policy.HasAccount())
        {
            logger.LogDebug("互动数据与创作团队本轮跳过 videoId={VideoId}：站里没有可用的采集号", videoId);
            return;
        }

        var brief = db.GetVideoBrief(videoId);
        if (brief is null)
        {
            return;
        }

        using var _owner = policy.BeginForVideo(videoId);
        var stat = await upInfo.FetchStatAsync(brief.Value.Bvid, CancellationToken.None);
        if (stat is null)
        {
            return; // 不销账：让观看页那一发懒补接手
        }

        db.SetVideoStat(videoId, stat.Like, stat.Coin, stat.Favorite, stat.Share, stat.Reply);
        db.SetVideoStaff(videoId, WatchStaffCodec.ToJson(StaffMap.ToWatch(stat.Staff)));
        logger.LogInformation(
            "馆藏 {VideoId} 互动数据入库：赞{Like} 币{Coin} 藏{Fav}（创作团队 {Team} 人）",
            videoId,
            stat.Like,
            stat.Coin,
            stat.Favorite,
            stat.Staff?.Count ?? 0);
    }
}
