using System.Threading.Channels;
using DownKyi.Application.Downloads;
using DownKyi.Application.FileName;
using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using DownKyi.Services.Download.Headless;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 进站下载队列：内存通道，进程重启不保留（重启后由 DownloadWorker 从库里的 Pending 重建）。
/// </summary>
public sealed class DownloadQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    private readonly Channel<(long VideoId, string Codec)> _codecChannel = Channel.CreateUnbounded<(long, string)>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    public ValueTask EnqueueAsync(long videoId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(videoId, cancellationToken);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask EnqueueCodecAsync(long videoId, string codec, CancellationToken cancellationToken = default) =>
        _codecChannel.Writer.WriteAsync((videoId, codec), cancellationToken);

    public IAsyncEnumerable<(long VideoId, string Codec)> ReadCodecsAsync(CancellationToken cancellationToken) =>
        _codecChannel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// 后台提交 Worker：把站内 Pending 记录交给下载引擎（headless 提交链：解析→准备→规划→判重→入队）。
/// 真正的字节下载由引擎 runtime 自己调度，这里只负责「提交+记账」。
/// </summary>
public sealed class DownloadWorker(
    DownloadQueue queue,
    StationDb db,
    DataPaths paths,
    ISettingsStore settingsStore,
    HeadlessSubmissionService submission,
    LbsAccountRouter router,
    LbsCollectPolicy collect,
    DownloadPauseBoard board,
    MediaReconcileGate mediaGate,
    IDownloadTaskLookup lookup,
    IDownloadTaskStateService state,
    IDownloadTaskQueue taskQueue,
    ILogger<DownloadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 崩溃恢复：上次进程挂掉前排队的 Pending 全部重新入队。
        // 以下开机四段全包壳：它们都在 ExecuteAsync 首个 await 之前，一个库瞬拒就能把宿主启动顶死/让 worker 静默退役，
        // 而这些恰是最容易岔的（仓挂载瞬断、引擎 Storage 被残进程占着）。宁可降级开站，余行交给复活/看门狗。
        var recovered = 0;
        try
        {
            foreach (var candidate in db.GetPendingVideos())
            {
                await queue.EnqueueAsync(candidate.Id, stoppingToken);
                recovered++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "开机 Pending 重投没跑完（继续开站，余行交给复活/看门狗）");
        }

        // 导入/缺件回炉：只在开机换过库的那次点名磁盘实物（闸门见 MediaReconcileGate），
        // 普通开机一律跳过——仓挂载瞬断时点名会把满仓健康件误拉重下。
        IReadOnlyList<long> returned = Array.Empty<long>();
        try
        {
            returned = mediaGate.ReconcileOnBoot
                ? MissingMediaReconciler.Reconcile(db, paths, logger)
                : Array.Empty<long>();
            foreach (var videoId in returned)
            {
                await queue.EnqueueAsync(videoId, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "缺件回炉点名没跑完（继续开站，下次换库开机再点）");
        }

        // 加档开机重投：加档行活在内存 channel 里，进程挂掉时遗留的 Downloading 行从此没人捾——
        // 同步员只对空任务行挂着等，回炉只认租约到期行。开机把不让路的行全部重新入队走一遍提交链：
        // 没任务的行直接重提；有任务的行会被引擎判重让路排队，看门狗与回炉都有出口，不留永久僵尸。
        var codecRecoveredCount = 0;
        try
        {
            var codecRecovered = db.GetCodecQueueRows()
                .Where(r => !r.Waiting)
                .Select(r => (r.VideoId, r.Codec))
                .Distinct()
                .ToList();
            foreach (var (videoId, codec) in codecRecovered)
            {
                await queue.EnqueueCodecAsync(videoId, codec, stoppingToken);
                codecRecoveredCount++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "加档开机重投没跑完（继续开站，租约到期行自有回炉出口）");
        }

        // 引擎孤儿任务清扫：引擎判重不看相位，躺在在途表里的死任务会把同视频的重试点单拦在让路循环
        // （锁R&B 事故：死任务的行重试点单时清了旧任务号，尸首成了无主孤魂，台账照不到）。
        // 开机点名引擎全部在途任务：站里台账没署名的就是孤儿，逐枚打断队列→挂起→销账。
        // 摆在重投之前：此时绝无在途提交，不会误杀刚交账的新任务。
        var swept = 0;
        try
        {
            swept = await SweepOrphanEngineTasksAsync(
                db.GetReferencedEngineTaskIds(), lookup, state, taskQueue, logger, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "开机孤儿引擎任务清扫没跑完（留给下轮开机/看门狗）");
        }

        logger.LogInformation("后台提交 Worker 到站，接管 {Recovered} 条待提交视频，加档重投 {CodecRecovered} 组，缺件回炉 {Returned} 条，Failed 残账销 {Swept} 枚（换库开机={Swapped}）", recovered, codecRecoveredCount, returned.Count, swept, mediaGate.ReconcileOnBoot);

        var mainLoop = Task.Run(() => ConsumeMainAsync(stoppingToken), stoppingToken);
        var codecLoop = Task.Run(() => ConsumeCodecAsync(stoppingToken), stoppingToken);
        await Task.WhenAll(mainLoop, codecLoop);
    }

    /// <summary>
    /// 开机孤儿清扫的可直接测边界（台账原始列 + 引擎三件套进，销掉枚数出）：
    /// 台账署过名的任务一枚不许碰，其余在途任务逐枚打断队列→挂起→销账。
    /// 台账交来的是 <c>engine_task_ids</c> 原文（每格一枚 JSON 数组，videos 与 video_codecs 的并集），
    /// 解析只认 <see cref="StatusSyncWorker.ParseTaskIds"/>：它认转义，坏 JSON 只当没引用。
    /// 把清单手拼成一串不是合法 JSON 的字符再 Parse，只会恒得到空集 = 开机把在途任务当孤儿销光。
    /// </summary>
    internal static async Task<int> SweepOrphanEngineTasksAsync(
        IEnumerable<string?> ledgerTaskIdsJson,
        IDownloadTaskLookup lookup,
        IDownloadTaskStateService state,
        IDownloadTaskQueue taskQueue,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var referenced = ledgerTaskIdsJson
            .SelectMany(json => StatusSyncWorker.ParseTaskIds(json))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var swept = 0;
        foreach (var orphan in await lookup.GetUnfinishedAsync(cancellationToken))
        {
            if (referenced.Contains(orphan.Id.Value))
            {
                continue;
            }

            try
            {
                await taskQueue.CancelAsync(orphan.Id);
            }
            catch (Exception)
            {
                // 本就没在跑，取消不掉无妨
            }

            try
            {
                await state.CancelAsync(orphan.Id, cancellationToken);
                await state.DeleteAsync(orphan.Id, cancellationToken);
                swept++;
                logger.LogWarning("开机销掉无主引擎任务 {TaskId}：台账无署名，留着会拦同视频重单", orphan.Id.Value);
            }
            catch (Exception)
            {
                // 销不动留给下轮开机/看门狗，不拦开站
            }
        }

        return swept;
    }

    private async Task ConsumeMainAsync(CancellationToken stoppingToken)
    {
        await foreach (var videoId in queue.ReadAllAsync(stoppingToken))
        {
            await SubmitAsync(videoId, stoppingToken);
            // 两件任务之间按站长定的间隔歇脚（极速 5~15s，选中间隔 ±10% 抖动），
            // 提交链每发都要碰 playurl/view 接口，接力赛式连投在风控眼里就是机器。
            await board.WaitBetweenTasksAsync(stoppingToken);
        }
    }

    private async Task ConsumeCodecAsync(CancellationToken stoppingToken)
    {
        await foreach (var (videoId, codec) in queue.ReadCodecsAsync(stoppingToken))
        {
            await SubmitCodecAsync(videoId, codec, stoppingToken);
            await board.WaitBetweenTasksAsync(stoppingToken);
        }
    }

    /// <summary>编码 id：与 PlaybackQualityCatalog 的 codec 目录对齐（7=AVC、12=HEVC、13=AV1）。</summary>
    private static int? CodecIdOf(string codec) => codec switch
    {
        "avc" => 7,
        "hevc" => 12,
        "av1" => 13,
        _ => null,
    };

    /// <summary>
    /// 编码版本加档：整视频全部已归档分 P 重提一遍，指定编码 + 文件名追加编码段（与主版本文件永不相撞），
    /// RedownloadCompleted 让已完成记录不再拦路。每个分 P 一行任务，完成后由同步员扫盘定档。
    /// </summary>
    private async Task SubmitCodecAsync(long videoId, string codec, CancellationToken stoppingToken)
    {
        // 加档同样受暂停板管。
        await board.WaitIfPausedAsync(stoppingToken);

        // 本轮真要提交了：销掉排队标记（站长室那块拿它显示「排队中」），retry_count 留着算退避。
        db.ClearCodecRetry(videoId, codec);

        var codecId = CodecIdOf(codec);
        if (codecId is null)
        {
            return;
        }

        var brief = db.GetVideoBrief(videoId);
        if (brief is null || brief.Value.Status != VideoStatus.Arrived)
        {
            // 主版本还没到站，版本队列项作废（行保持 Downloading 也没人盯，直接判失败免得挂账）
            FailCodecRows(videoId, codec, "主版本尚未到站，加档作废");
            return;
        }

        logger.LogInformation("提交编码加档 videoId={VideoId} codec={Codec}", videoId, codec);
        try
        {
            var settings = settingsStore.Current;
            var options = LbsSubmissionProfile.ForStation(paths, settings) with
            {
                VideoCodec = codecId,
                RedownloadCompleted = true,
                // 命名尾部追加「-编码」段：版本文件与主版本同目录不同名，互不覆盖。
                FileNameParts = [.. settings.Video.FileNameParts, FileNamePart.Hyphen, FileNamePart.Blank, FileNamePart.VideoCodec],
            };

            // 加档是站里的活（成员重下不需要重投）：吃采集号，不再按视频分主副号。
            var result = await collect.RunForVideoAsync(videoId, () => submission.SubmitAsync(
                $"https://www.bilibili.com/video/{brief.Value.Bvid}",
                options,
                stoppingToken));

            if (result.Status != HeadlessSubmissionStatus.Enqueued)
            {
                // 引擎判重键不含编码（DuplicateKey.From(cid, NeedDownloadContent)）：同稿并发加两个编码，
                // 后到的必被判「已存在未完成的同名任务」。这不是失败，是让路——留在途挂个到期时间，
                // 等同步员到期回炉（前头那档一下完，重试就能真入队），而不是一票否决让站长重点。
                if (IsInFlightDuplicate(result))
                {
                    DeferCodecRows(videoId, codec, JoinReasons(result));
                    return;
                }

                FailCodecRows(videoId, codec, JoinReasons(result));
                return;
            }

            // 把每个分 P 的任务 id/基准路径归回 video_codecs 行；没分到任务的行判失败（可重试点）。
            var pendingRows = db.GetCodecVersions(videoId)
                .Where(row => row.Status == "Downloading" && string.Equals(row.Codec, codec, StringComparison.Ordinal))
                .ToDictionary(row => row.PageIdx, row => row.Id);
            if (result.Metadata is { } metadata)
            {
                foreach (var page in metadata.Pages)
                {
                    if (pendingRows.Remove(page.Page, out var rowId))
                    {
                        db.MarkCodecSubmitted(rowId, new[] { page.TaskId }, page.OutputBasePath);
                    }
                }
            }

            foreach (var rowId in pendingRows.Values)
            {
                db.MarkCodecFailed(rowId, "引擎未为该分 P 产生任务");
            }

            logger.LogInformation("编码加档 {VideoId}/{Codec} 已交给引擎，任务 {Count} 个", videoId, codec, result.TaskIds.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LbsNoAccountException ex)
        {
            // 没号不是事故：写明原因挂在那一行，成员绑好号点重试就能接着跑。
            FailCodecRows(videoId, codec, ex.Message);
            logger.LogWarning("编码加档判无号 videoId={VideoId} codec={Codec}", videoId, codec);
        }
        catch (Exception ex)
        {
            FailCodecRows(videoId, codec, ex.Message);
            logger.LogWarning(ex, "编码加档提交异常 videoId={VideoId} codec={Codec}", videoId, codec);
        }
    }

    private void FailCodecRows(long videoId, string codec, string reason)
    {
        foreach (var row in db.GetCodecVersions(videoId)
                     .Where(row => row.Status == "Downloading" && string.Equals(row.Codec, codec, StringComparison.Ordinal)))
        {
            db.MarkCodecFailed(row.Id, reason);
        }
    }

    // 撞车让路的退避：30s × 已经让过的趟数，5 分钟封顶；连让 40 趟（前头那档不会走了）才按原路判失败。
    private const int CodecRetryGiveUp = 40;
    private const double CodecRetryStepSec = 30;
    private const double CodecRetryCapSec = 300;

    /// <summary>引擎的「在途重复工」拒绝：SkipReasons 里那句「已存在未完成的同名任务」，该让路不该判死。</summary>
    private static bool IsInFlightDuplicate(HeadlessSubmissionResult result) =>
        result.SkipReasons.Any(reason => reason.Contains("未完成的同名任务", StringComparison.Ordinal));

    /// <summary>整批加档行让路：逐行按让路次数递增退避，踩满上限才标失败（带上引擎给的原因）。</summary>
    private void DeferCodecRows(long videoId, string codec, string reason)
    {
        foreach (var row in db.GetCodecVersions(videoId)
                     .Where(row => row.Status == "Downloading" && string.Equals(row.Codec, codec, StringComparison.Ordinal)))
        {
            if (row.RetryCount >= CodecRetryGiveUp)
            {
                db.MarkCodecFailed(row.Id, $"{reason}（已让路重试 {row.RetryCount} 次仍未放行，放弃）");
                logger.LogWarning("编码加档 {VideoId}/P{Page}/{Codec} 让路 {Retries} 次仍被判重，判失败：{Reason}",
                    videoId, row.PageIdx, codec, row.RetryCount, reason);
                continue;
            }

            var backoff = TimeSpan.FromSeconds(Math.Min(CodecRetryStepSec * (row.RetryCount + 1), CodecRetryCapSec));
            db.MarkCodecDeferred(row.Id, DateTimeOffset.UtcNow + backoff, reason);
            logger.LogInformation("编码加档 {VideoId}/P{Page}/{Codec} 撞车让路，{Backoff} 秒后回炉（第 {Retries} 次）：{Reason}",
                videoId, row.PageIdx, codec, (int)backoff.TotalSeconds, row.RetryCount + 1, reason);
        }
    }

    private async Task SubmitAsync(long videoId, CancellationToken stoppingToken)
    {
        // 暂停板落下时先闸住：排队可以等，提交不能偷跑。
        await board.WaitIfPausedAsync(stoppingToken);

        var candidate = db.GetSubmissionCandidate(videoId);
        if (candidate is null)
        {
            return; // 已被处理过或记录不存在，重复入队的正常现象
        }

        logger.LogInformation("提交进站任务 videoId={VideoId} url={Url}", videoId, candidate.Url);
        try
        {
            var options = LbsSubmissionProfile.ForStation(paths, settingsStore.Current) with
            {
                // 站长按过「销档重下」才置位：引擎的已完成记录只对这馆让路（判重键 = (cid, 需要下载的内容)，
                // 不销它的话重投会在门口被原样打回 Skipped）。平时递 false，保持原有「下过就不重下」行为。
                RedownloadCompleted = candidate.ForceRedownload,
            };

            // 谁的稿谁出号：投稿人自己的活号是唯一来源，分站已无站级兑底登录态。
            // 投稿口那道闸门（SubmissionGate）已经挡过一次「素净席位」，这里兑的是
            // 「过了闸门之后号被 B 站踢下线」这种中途失效，以及无主史前稿的重试。
            var submitterMemberId = db.GetVideoSubmitterMember(videoId);
            var memberCred = submitterMemberId is long submitterId
                ? db.PickMemberAccountForDownload(submitterId)
                : null;
            if (memberCred is null)
            {
                var why = submitterMemberId is null
                    ? "这条稿子没有投稿人席位，也就没有可借的 B 站号；请成员重新投稿，或由站长给这稿补上归属"
                    : "投稿人的 B 站号全失效了：请他到「个人空间」重新扫码，再回首页把这稿重投一次";
                db.MarkFailed(videoId, why);
                MemberMail.NotifyVideoFailed(db, logger, videoId, why);
                logger.LogWarning("提交进站 videoId={VideoId} 判无号：{Why}", videoId, why);
                return;
            }

            // cookie 只进这条调用链：日志只报号码，不报内容。
            logger.LogInformation("提交进站 videoId={VideoId} 走投稿人自有号 mid={Mid}", videoId, memberCred.AccountMid);
            var result = await router.RunAsMemberAsync(memberCred.Cookie, memberCred.AccountMid,
                () => submission.SubmitAsync(candidate.Url, options, stoppingToken));
            switch (result.Status)
            {
                case HeadlessSubmissionStatus.Enqueued:
                    db.MarkSubmitted(videoId, result.TaskIds, result.Title, result.CoverUrl);
                    if (result.Metadata is { } metadata)
                    {
                        db.SaveSubmissionMetadata(
                            videoId,
                            new VideoMetaDraft(
                                metadata.UpName,
                                metadata.UpMid,
                                metadata.Zone,
                                metadata.PublishText,
                                metadata.Description,
                                metadata.ViewCountText),
                            metadata.Pages
                                .Select(static page => new PageDraft(
                                    page.TaskId,
                                    page.Page,
                                    page.Title,
                                    page.Cid,
                                    page.DurationText,
                                    page.OutputBasePath))
                                .ToList());
                    }

                    logger.LogInformation("视频 {VideoId} 已交给引擎调度，引擎任务 {Count} 个，档案已入库", videoId, result.TaskIds.Count);
                    break;
                case HeadlessSubmissionStatus.SkippedDuplicate:
                case HeadlessSubmissionStatus.NoNewTasks:
                case HeadlessSubmissionStatus.Unsupported:
                    db.MarkSkipped(videoId, JoinReasons(result));
                    logger.LogInformation("视频 {VideoId} 无需下载：{Reasons}", videoId, JoinReasons(result));
                    break;
                default:
                    db.MarkFailed(videoId, JoinReasons(result));
                    MemberMail.NotifyVideoFailed(db, logger, videoId, JoinReasons(result));
                    logger.LogWarning("视频 {VideoId} 提交失败：{Reasons}", videoId, JoinReasons(result));
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 进程关闭，记录保持 Pending，下次启动自动恢复
            throw;
        }
        catch (Exception ex)
        {
            db.MarkFailed(videoId, ex.Message);
            MemberMail.NotifyVideoFailed(db, logger, videoId, ex.Message);
            logger.LogWarning(ex, "视频 {VideoId} 提交异常", videoId);
        }
    }

    private static string JoinReasons(HeadlessSubmissionResult result) =>
        result.SkipReasons.Count > 0 ? string.Join("；", result.SkipReasons) : result.Status.ToString();

    /// <summary>引擎给出的非成功结论里，只有真失败才值得换号重跑；判重/不支持换号也没意义。</summary>
    private static bool IsFailure(HeadlessSubmissionResult result) => result.Status switch
    {
        HeadlessSubmissionStatus.Enqueued
            or HeadlessSubmissionStatus.SkippedDuplicate
            or HeadlessSubmissionStatus.NoNewTasks
            or HeadlessSubmissionStatus.Unsupported => false,
        _ => true,
    };

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("后台提交 Worker 离站");
        return base.StopAsync(cancellationToken);
    }
}
