using System.Threading.Channels;
using DownKyi.Application.FileName;
using DownKyi.Application.Settings;
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
    DownloadPauseBoard board,
    MediaReconcileGate mediaGate,
    ILogger<DownloadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 崩溃恢复：上次进程挂掉前排队的 Pending 全部重新入队
        var recovered = db.GetPendingVideos();
        foreach (var candidate in recovered)
        {
            await queue.EnqueueAsync(candidate.Id, stoppingToken);
        }

        // 导入/缺件回炉：只在开机换过库的那次点名磁盘实物（闸门见 MediaReconcileGate），
        // 普通开机一律跳过——仓挂载瞬断时点名会把满仓健康件误拉重下。
        var returned = mediaGate.ReconcileOnBoot
            ? MissingMediaReconciler.Reconcile(db, paths, logger)
            : Array.Empty<long>();
        foreach (var videoId in returned)
        {
            await queue.EnqueueAsync(videoId, stoppingToken);
        }

        logger.LogInformation("后台提交 Worker 到站，接管 {Recovered} 条待提交视频，缺件回炉 {Returned} 条（换库开机={Swapped}）", recovered.Count, returned.Count, mediaGate.ReconcileOnBoot);

        var mainLoop = Task.Run(() => ConsumeMainAsync(stoppingToken), stoppingToken);
        var codecLoop = Task.Run(() => ConsumeCodecAsync(stoppingToken), stoppingToken);
        await Task.WhenAll(mainLoop, codecLoop);
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

            // 加档跟随主版本的归属号；副号失败同样有主号重跑兑底。
            var codecOwner = db.GetVideoOwner(videoId) is { } storedOwner ? (LbsOwner)storedOwner : LbsOwner.Main;
            var result = await router.RunAsAsync(codecOwner, () => submission.SubmitAsync(
                $"https://www.bilibili.com/video/{brief.Value.Bvid}",
                options,
                stoppingToken));
            if (codecOwner == LbsOwner.Alt && IsFailure(result))
            {
                logger.LogWarning("副号加档失败 videoId={VideoId} codec={Codec}（{Reasons}），改主号重跑一次", videoId, codec, JoinReasons(result));
                var retry = await router.RunAsAsync(LbsOwner.Main, () => submission.SubmitAsync(
                    $"https://www.bilibili.com/video/{brief.Value.Bvid}",
                    options,
                    stoppingToken));
                if (!IsFailure(retry))
                {
                    db.SetVideoOwner(videoId, (int)LbsOwner.Main);
                    result = retry;
                }
            }

            if (result.Status != HeadlessSubmissionStatus.Enqueued)
            {
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
            // 按视频分号：首次提交轮转定档（主、副、主、副……），重提交沿档；无副号恒主。
            // 提交链同步 await 到底，环境号顺着调用链流进上游客户端，playurl/view info 都挂归属号的 cookie。
            var assigned = db.GetVideoOwner(videoId);
            var owner = assigned is { } stored ? (LbsOwner)stored : router.AssignNext();
            if (assigned is null)
            {
                db.SetVideoOwner(videoId, (int)owner);
            }

            logger.LogInformation("提交进站 videoId={VideoId} 归属 {Owner}", videoId, owner);
            var options = LbsSubmissionProfile.ForStation(paths, settingsStore.Current);

            // 投稿人自己的号优先：成员池有活号就用他的 cookie 跑提交链（playurl/视频信息全挂他的登录态），
            // 出池（没号/全失效）回落站级主备槽；归属台账仍按主/副轮转，不动存量机制。
            var memberCred = db.GetVideoSubmitter(videoId) is long submitterMid
                ? db.PickMemberAccountForDownload(submitterMid)
                : null;
            var result = memberCred is null
                ? await router.RunAsAsync(owner, () => submission.SubmitAsync(candidate.Url, options, stoppingToken))
                : await router.RunAsMemberAsync(memberCred.Cookie,
                    () => router.RunAsAsync(owner, () => submission.SubmitAsync(candidate.Url, options, stoppingToken)));
            if (memberCred is not null)
            {
                // cookie 只进不出：日志只报号码，不报内容。
                logger.LogInformation("提交进站 videoId={VideoId} 走投稿人自有号 mid={Mid}", videoId, memberCred.AccountMid);
            }

            // 副号提交失败（风控/画质权限等单号问题）：主号重跑一次，成功则改判归主，下载不断档。
            if (owner == LbsOwner.Alt && IsFailure(result))
            {
                logger.LogWarning("副号提交失败 videoId={VideoId}（{Reasons}），改主号重跑一次", videoId, JoinReasons(result));
                var retry = await router.RunAsAsync(LbsOwner.Main, () => submission.SubmitAsync(candidate.Url, options, stoppingToken));
                if (!IsFailure(retry))
                {
                    db.SetVideoOwner(videoId, (int)LbsOwner.Main);
                    result = retry;
                }
            }
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
