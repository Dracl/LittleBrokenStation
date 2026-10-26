using System;
using System.Collections.Generic;

// LBS fork: Title/CoverUrl 携带的是 B 站接口原样返回的字符串（CDN 地址/标题），
// 宿主只负责原样落库供前端直链使用，不做请求也不改写，转 Uri 反而会引入规范化风险。
#pragma warning disable CA1054, CA1056

namespace DownKyi.Services.Download.Headless;

/// <summary>
/// Outcome of one headless submission.
/// </summary>
public enum HeadlessSubmissionStatus
{
    /// <summary>At least one task was persisted and pushed into the download queue.</summary>
    Enqueued,

    /// <summary>Nothing was added because every candidate page already exists as an active or finished task.</summary>
    SkippedDuplicate,

    /// <summary>The input is not an ordinary Bilibili video (bangumi, cheese, unknown) or has no usable input.</summary>
    Unsupported,

    /// <summary>Resolution, planning or task admission failed.</summary>
    Failed,

    /// <summary>The video was understood, but planning produced no downloadable page.</summary>
    NoNewTasks
}

/// <summary>
/// Result of <see cref="HeadlessSubmissionService.SubmitAsync"/>.
/// </summary>
/// <param name="Status">Overall outcome.</param>
/// <param name="TaskIds">
/// <c>DownloadingItem.DownloadBase.Id</c> of every task that reached the queue, in submission order.
/// These are the ids the host can watch through the download stores.
/// </param>
/// <param name="SkipReasons">
/// Human readable reasons for pages that were not added (planner stop reason, skipped pages, duplicate
/// pages, admission errors). Never <c>null</c>.
/// </param>
/// <param name="EnqueuedCount">Number of queued tasks (always <c>TaskIds.Count</c>).</param>
/// <param name="SkippedCount">
/// Pages that were understood but intentionally not added: planner-skipped pages plus skipped duplicates.
/// </param>
/// <param name="FailedCount">Pages whose admission threw.</param>
public sealed record HeadlessSubmissionResult(
    HeadlessSubmissionStatus Status,
    IReadOnlyList<string> TaskIds,
    IReadOnlyList<string> SkipReasons,
    int EnqueuedCount,
    int SkippedCount,
    int FailedCount,
    // LBS fork: 把解析阶段已经拿到的元数据顺路带出，宿主不必二次请求 B 站回填补库。
    string? Title = null,
    string? CoverUrl = null,
    HeadlessVideoSubmission? Metadata = null)
{
    /// <summary>Creates a result for tasks that reached the queue.</summary>
    public static HeadlessSubmissionResult Enqueued(
        IReadOnlyList<string> taskIds,
        IReadOnlyList<string> skipReasons,
        int skippedCount,
        int failedCount,
        string? title = null,
        string? coverUrl = null,
        HeadlessVideoSubmission? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        ArgumentNullException.ThrowIfNull(skipReasons);
        return new HeadlessSubmissionResult(
            HeadlessSubmissionStatus.Enqueued,
            taskIds,
            skipReasons,
            taskIds.Count,
            skippedCount,
            failedCount,
            title,
            coverUrl,
            metadata);
    }

    /// <summary>Creates a result for a submission where only duplicates were found.</summary>
    public static HeadlessSubmissionResult SkippedDuplicate(
        IReadOnlyList<string> skipReasons,
        int skippedCount)
    {
        ArgumentNullException.ThrowIfNull(skipReasons);
        return new HeadlessSubmissionResult(
            HeadlessSubmissionStatus.SkippedDuplicate,
            Array.Empty<string>(),
            skipReasons,
            EnqueuedCount: 0,
            SkippedCount: skippedCount,
            FailedCount: 0);
    }

    /// <summary>Creates a result for an input the headless path does not handle.</summary>
    public static HeadlessSubmissionResult Unsupported(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new HeadlessSubmissionResult(
            HeadlessSubmissionStatus.Unsupported,
            Array.Empty<string>(),
            new[] { reason },
            EnqueuedCount: 0,
            SkippedCount: 0,
            FailedCount: 0);
    }

    /// <summary>Creates a result for a failed submission.</summary>
    public static HeadlessSubmissionResult Failed(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new HeadlessSubmissionResult(
            HeadlessSubmissionStatus.Failed,
            Array.Empty<string>(),
            new[] { reason },
            EnqueuedCount: 0,
            SkippedCount: 0,
            FailedCount: 1);
    }

    /// <summary>Creates a result for a submission that produced no downloadable page.</summary>
    public static HeadlessSubmissionResult NoNewTasks(
        IReadOnlyList<string> skipReasons,
        int skippedCount)
    {
        ArgumentNullException.ThrowIfNull(skipReasons);
        return new HeadlessSubmissionResult(
            HeadlessSubmissionStatus.NoNewTasks,
            Array.Empty<string>(),
            skipReasons,
            EnqueuedCount: 0,
            SkippedCount: skippedCount,
            FailedCount: 0);
    }
}
