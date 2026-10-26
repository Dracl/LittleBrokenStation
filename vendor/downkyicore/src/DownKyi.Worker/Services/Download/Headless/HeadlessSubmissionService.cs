using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download.Headless;

/// <summary>
/// Submits a single ordinary Bilibili video to the download runtime without any UI: it resolves the input,
/// prepares every page, plans the requested content, builds the task drafts, reviews duplicates and hands
/// the tasks to <see cref="DownloadTaskAdmissionService"/>, which persists them and pushes them into the
/// download queue.
/// </summary>
/// <remarks>
/// The flow mirrors the desktop add-to-download pipeline
/// (src/DownKyi.Desktop/Services/Media/ContentDownloadCoordinator.cs:119 plus
/// src/DownKyi.Desktop/Services/Download/AddToDownloadService.cs:157) with the dialogs replaced by
/// <see cref="HeadlessAppDialogService"/>. Only <see cref="PlayStreamType.Video"/> is supported; bangumi and
/// cheese inputs are reported as <see cref="HeadlessSubmissionStatus.Unsupported"/>.
/// <para>
/// The constructor is internal on purpose: <see cref="DownloadTaskAdmissionService"/> and
/// <see cref="DownloadMovieMetadataBuilder"/> are internal to this assembly, and a public constructor cannot
/// expose them. The host resolves this service through the factory registered by
/// <c>HeadlessDownloadRegistration.AddDownloadModule</c>.
/// </para>
/// </remarks>
public sealed class HeadlessSubmissionService
{
    private readonly ISettingsStore _settingsStore;
    private readonly DownloadTaskAdmissionService _admission;
    private readonly DownloadMovieMetadataBuilder _metadataBuilder;
    private readonly HeadlessVideoPreparer _preparer;
    private readonly ILogger<HeadlessSubmissionService> _logger;

    internal HeadlessSubmissionService(
        ISettingsStore settingsStore,
        IBilibiliApiClient client,
        IWbiKeyProvider wbiKeyProvider,
        DownloadTaskAdmissionService admission,
        DownloadMovieMetadataBuilder metadataBuilder,
        ILogger<HeadlessSubmissionService> logger)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(wbiKeyProvider);
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _metadataBuilder = metadataBuilder ?? throw new ArgumentNullException(nameof(metadataBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _preparer = new HeadlessVideoPreparer(client, wbiKeyProvider, logger);
    }

    /// <summary>
    /// Adds one ordinary video (all of its pages) to the download queue.
    /// </summary>
    /// <param name="videoUrl">
    /// A Bilibili video URL (https://www.bilibili.com/video/BV…), a bare BV id or a bare av id.
    /// </param>
    /// <param name="options">Per-submission overrides; see <see cref="HeadlessSubmissionOptions"/>.</param>
    /// <param name="cancellationToken">Cancels resolution and admission.</param>
    /// <returns>The submission outcome, including the ids of the tasks that reached the queue.</returns>
    // LBS fork: CA1054 does not apply - the parameter is a free-form Bilibili share text (a bare "BV1xx…"
    // or "av123…" id is as valid as a https URL), and VideoInputResolver/ParseEntrance consume it as a
    // string, so System.Uri would reject the two id forms the station legitimately submits.
#pragma warning disable CA1054 // 类型成员是 URL，应改用 Uri
    public async Task<HeadlessSubmissionResult> SubmitAsync(
        string videoUrl,
        HeadlessSubmissionOptions options,
        CancellationToken cancellationToken = default)
    {
#pragma warning restore CA1054 // 类型成员是 URL，应改用 Uri
        ArgumentException.ThrowIfNullOrWhiteSpace(videoUrl);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // LBS fork: input classification through src/DownKyi.Application/Media/VideoInputResolver.cs:5.
        var inputKind = VideoInputResolver.Resolve(videoUrl);
        if (inputKind != VideoInputKind.Video)
        {
            return HeadlessSubmissionResult.Unsupported(
                $"headless 仅支持 B 站普通视频；输入被识别为 {inputKind}。");
        }

        var settings = _settingsStore.Current;
        ArgumentNullException.ThrowIfNull(settings);
        var effectiveSettings = ApplyOverrides(settings, options);
        var requestedContent = options.Content ?? DownloadContentSelection.All;

        var directory = string.IsNullOrWhiteSpace(options.Directory)
            ? effectiveSettings.Video.SaveVideoRootPath
            : options.Directory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return HeadlessSubmissionResult.Failed(
                "未指定下载目录，且设置中的 Video.SaveVideoRootPath 为空。");
        }

        // The desktop path creates the target directory when the user confirms it
        // (src/DownKyi.Desktop/Services/Download/AddToDownloadService.cs:151); headless does the same so an
        // unset station folder does not fail every page during admission.
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            _logger.LogWarningMessage(
                $"Headless submission could not prepare the download directory \"{directory}\".",
                exception);
            return HeadlessSubmissionResult.Failed(
                $"无法创建下载目录 \"{directory}\"：{exception.Message}");
        }

        HeadlessVideoPreparation preparation;
        try
        {
            preparation = await _preparer.PrepareAsync(
                videoUrl,
                effectiveSettings,
                requestedContent,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BilibiliApiResponseException exception)
        {
            _logger.LogWarningMessage(
                $"Headless submission could not resolve \"{videoUrl}\"; "
                + $"operation={exception.Operation}; code={exception.Code}.",
                exception);
            return HeadlessSubmissionResult.Failed(
                $"解析视频信息失败：{exception.Operation} (code={exception.Code}) {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarningMessage(
                $"Headless submission could not reach Bilibili for \"{videoUrl}\".",
                exception);
            return HeadlessSubmissionResult.Failed($"访问 B 站接口失败：{exception.Message}");
        }

        if (preparation.Video is not { } video || preparation.Sections is not { } sections)
        {
            var reason = preparation.UnsupportedReason
                ?? "headless 未能把该输入解析为可下载的普通视频。";
            _logger.LogInformationMessage($"Headless submission rejected \"{videoUrl}\": {reason}");
            return HeadlessSubmissionResult.Unsupported(reason);
        }

        // LBS fork: src/DownKyi.Worker/Services/Download/PreparedDownload.cs:249 (Create).
        var prepared = PreparedDownload.Create(video, sections);

        // LBS fork: src/DownKyi.Worker/Services/Download/DownloadActionPlanner.cs:22 (ctor) and :28 (PlanAsync),
        // fed by the headless dialog so no prompt can block the station. isAll = true: the whole video is
        // submitted, page selection is not a headless concept.
        var planner = new DownloadActionPlanner(
            new DownloadContentConflictResolver(new HeadlessAppDialogService(_logger)));
        var finalized = await planner.PlanAsync(
            requestedContent,
            prepared,
            isAll: true,
            new DownloadContentConflictChoices(),
            cancellationToken).ConfigureAwait(false);

        if (finalized.StopReason is { } stopReason)
        {
            var stopDescription =
                $"计划阶段未产生可下载页面：{Describe(stopReason)}（候选 {finalized.CandidateCount} 页，"
                + $"跳过 {finalized.SkippedCount} 页）。";
            _logger.LogInformationMessage(
                $"Headless submission of \"{videoUrl}\" stopped: {stopDescription}");
            return HeadlessSubmissionResult.NoNewTasks(
                [stopDescription],
                finalized.SkippedCount);
        }

        var pending = CreatePendingDownloads(directory, finalized, effectiveSettings);
        if (pending.Count == 0)
        {
            return HeadlessSubmissionResult.NoNewTasks(["该视频没有可提交的下载页面。"], finalized.SkippedCount);
        }

        // LBS fork: src/DownKyi.Worker/Services/Download/DownloadTaskAdmissionService.cs:126.
        var review = await _admission.ReviewDuplicatesAsync(
            pending.Select(static download => download.Item).ToArray(),
            cancellationToken).ConfigureAwait(false);

        var taskIds = new List<string>();
        var skipReasons = new List<string>();
        // LBS fork: archive snapshots of admitted pages, carried out on the result so the station can
        // fill its catalog (pages + final output paths) without a second Bilibili round-trip.
        var pageSnapshots = new List<HeadlessPageSubmission>();
        var skippedDuplicates = 0;
        var failedCount = 0;

        for (var index = 0; index < pending.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pendingDownload = pending[index];
            var item = pendingDownload.Item;
            var displayName = GetDisplayName(item);
            var id = item.DownloadBase.Id;
            var isActiveDuplicate = review.ActiveDuplicateIds.Contains(id);
            var isCompletedDuplicate = review.CompletedDuplicateIds.Contains(id);

            if (isActiveDuplicate || (isCompletedDuplicate && !options.RedownloadCompleted))
            {
                // Default headless policy: never re-download finished work, just report it.
                skippedDuplicates++;
                var reason = isActiveDuplicate
                    ? $"已存在未完成的同名任务，跳过：{displayName}"
                    : $"已完成下载记录中已存在，跳过：{displayName}";
                skipReasons.Add(reason);
                _logger.LogInformationMessage($"Headless submission skipped a duplicate: {reason}");
                continue;
            }

            if (effectiveSettings.Video.Content.GenerateMovieMetadata
                && item.DownloadBase.NeedDownloadContent.Video)
            {
                item.Metadata = await _metadataBuilder.BuildAsync(
                    pendingDownload.Video,
                    pendingDownload.Page,
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                // LBS fork: src/DownKyi.Worker/Services/Download/DownloadTaskAdmissionService.cs:53 - the only
                // place that persists a task and enqueues it; this service never touches the queue directly.
                var admitted = await _admission.AdmitAsync(
                    item,
                    effectiveSettings.Basic.RepeatFileAutoAddNumberSuffix,
                    cancellationToken,
                    redownloadCompleted: isCompletedDuplicate).ConfigureAwait(false);
                if (!admitted)
                {
                    skippedDuplicates++;
                    skipReasons.Add($"任务被下载运行时判为重复，跳过：{displayName}");
                    _logger.LogInformationMessage(
                        $"Headless submission skipped a task rejected by admission: {displayName}");
                    continue;
                }

                taskIds.Add(id);
                pageSnapshots.Add(new HeadlessPageSubmission(
                    id,
                    item.DownloadBase.Page,
                    string.IsNullOrWhiteSpace(item.DownloadBase.Name) ? item.DownloadBase.MainTitle : item.DownloadBase.Name,
                    item.DownloadBase.Cid,
                    item.DownloadBase.Duration,
                    item.DownloadBase.FilePath));
            }
            catch (DownloadRuntimeUnavailableException exception)
            {
                var remaining = pending.Count - index;
                failedCount += remaining;
                _logger.LogWarningMessage(
                    "Headless submission stopped because the download runtime is unavailable.",
                    exception);
                skipReasons.Add($"下载运行时不可用，剩余 {remaining} 个任务未提交。");
                break;
            }
            catch (IOException exception)
            {
                failedCount++;
                _logger.LogWarningMessage(
                    $"Headless submission could not admit the task \"{displayName}\".",
                    exception);
                skipReasons.Add($"写入任务失败，跳过：{displayName}（{exception.Message}）");
            }
        }

        var skippedCount = finalized.SkippedCount + skippedDuplicates;
        if (taskIds.Count > 0)
        {
            _logger.LogInformationMessage(
                $"Headless submission queued {taskIds.Count} task(s) for \"{videoUrl}\" "
                + $"({skippedCount} skipped, {failedCount} failed).");
            return HeadlessSubmissionResult.Enqueued(
                taskIds,
                skipReasons,
                skippedCount,
                failedCount,
                video.Title,
                video.CoverUrl,
                new HeadlessVideoSubmission(
                    video.UpName,
                    video.UpperMid,
                    video.VideoZone,
                    video.CreateTime,
                    video.Description,
                    video.PlayNumber,
                    pageSnapshots));
        }

        if (failedCount > 0)
        {
            return new HeadlessSubmissionResult(
                HeadlessSubmissionStatus.Failed,
                Array.Empty<string>(),
                skipReasons,
                EnqueuedCount: 0,
                SkippedCount: skippedCount,
                FailedCount: failedCount);
        }

        if (skippedDuplicates > 0)
        {
            return HeadlessSubmissionResult.SkippedDuplicate(skipReasons, skippedCount);
        }

        return HeadlessSubmissionResult.NoNewTasks(skipReasons, skippedCount);
    }

    /// <summary>
    /// Builds the per-submission view of the settings: station overrides win over the stored values, and
    /// everything else (file name layout, ordering, danmaku format, NFO switch) keeps the stored value.
    /// </summary>
    private ApplicationSettings ApplyOverrides(ApplicationSettings settings, HeadlessSubmissionOptions options)
    {
        var video = settings.Video;
        var quality = options.VideoQuality ?? video.Quality;
        var audioQuality = video.AudioQuality;
        if (!string.IsNullOrWhiteSpace(options.AudioQualityFormat))
        {
            var preference = PlaybackQualityCatalog.GetAudioPreferences()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Name,
                    options.AudioQualityFormat,
                    StringComparison.Ordinal));
            if (preference == null)
            {
                _logger.LogWarningMessage(
                    $"Unknown audio quality name \"{options.AudioQualityFormat}\"; "
                    + "keeping the configured audio quality.");
            }
            else
            {
                audioQuality = preference.Id;
            }
        }

        // An empty override list would produce a meaningless path, so it is treated as "no override".
        var fileNameParts = options.FileNameParts is { Count: > 0 } overrides
            ? [.. overrides]
            : video.FileNameParts;

        // LBS fork: codec versioning - an explicit codec wins over the configured preference so the
        // station can pull the same page in another encoding without touching the station-wide setting.
        var codecs = options.VideoCodec ?? video.VideoCodecs;

        if (quality == video.Quality
            && audioQuality == video.AudioQuality
            && codecs == video.VideoCodecs
            && fileNameParts == video.FileNameParts)
        {
            return settings;
        }

        return settings with
        {
            Video = video with
            {
                Quality = quality,
                AudioQuality = audioQuality,
                VideoCodecs = codecs,
                FileNameParts = fileNameParts,
            },
        };
    }

    // LBS fork: ported from
    // src/DownKyi.Desktop/Services/Download/AddToDownloadService.cs:291 (CreatePendingDownloads), without the
    // bangumi main-cover ownership block - headless only accepts ordinary videos. The draft factory already
    // enters tasks as DownloadStatus.NotStarted
    // (src/DownKyi.Worker/Services/Download/DownloadTaskDraftFactory.cs:110), which is what
    // DownloadTaskProjectionMapper.CreateNewTask (:25) requires, so the status needs no extra assignment.
    private static List<PendingDownload> CreatePendingDownloads(
        string directory,
        FinalizedDownload finalized,
        ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(finalized);
        ArgumentNullException.ThrowIfNull(settings);
        var pending = new List<PendingDownload>();
        foreach (var finalizedSection in finalized.Sections)
        {
            foreach (var finalizedPage in finalizedSection.Pages)
            {
                var item = DownloadTaskDraftFactory.Create(
                    directory,
                    finalized.Video,
                    finalizedSection.Section,
                    finalized.Sections.Count,
                    finalizedPage.Page,
                    finalizedPage.VideoQuality,
                    finalizedPage.AudioQualityFormat,
                    finalizedPage.AvailableMedia,
                    settings,
                    finalizedPage.FinalizedContent);
                pending.Add(new PendingDownload(item, finalized.Video, finalizedPage.Page));
            }
        }

        return pending;
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/Download/AddToDownloadService.cs:340.
    private static string GetDisplayName(DownloadingItem item) =>
        string.Equals(item.DownloadBase.MainTitle, item.DownloadBase.Name, StringComparison.Ordinal)
            ? item.DownloadBase.Name
            : $"{item.DownloadBase.MainTitle} - {item.DownloadBase.Name}";

    private static string Describe(DownloadPlanningStopReason stopReason) => stopReason switch
    {
        DownloadPlanningStopReason.NoContentRequested => "未选择任何需要下载的内容",
        DownloadPlanningStopReason.NoPagesSelected => "没有选中的分P",
        DownloadPlanningStopReason.NoAvailableContent => "没有可下载的媒体内容",
        DownloadPlanningStopReason.SkippedByUser => "所有分P都因内容冲突被跳过",
        _ => stopReason.ToString(),
    };

    // LBS fork: mirrors src/DownKyi.Desktop/Services/Download/AddToDownloadService.cs:345.
    private sealed record PendingDownload(DownloadingItem Item, VideoInfoView Video, VideoPage Page);
}
