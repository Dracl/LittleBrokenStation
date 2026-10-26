using System;
using System.Collections.Generic;

namespace DownKyi.Services.Download.Headless;

/// <summary>
/// One admitted page captured at submission time.
/// </summary>
/// <param name="TaskId">DownloadingItem.DownloadBase.Id — the engine task id for this page.</param>
/// <param name="Page">Page number inside the video (1-based).</param>
/// <param name="Title">Page part title.</param>
/// <param name="Cid">Page cid.</param>
/// <param name="DurationText">Human readable duration, as the engine stores it.</param>
/// <param name="OutputBasePath">
/// The final on-disk output base path (no extension) chosen by admission. Published files are this path
/// plus extensions (.mp4/.ass/.Cover.jpg/...), so the host can locate artifacts without guessing names.
/// </param>
public sealed record HeadlessPageSubmission(
    string TaskId,
    int Page,
    string Title,
    long Cid,
    string? DurationText,
    string OutputBasePath);

/// <summary>
/// LBS fork: archive metadata captured during prepare, carried out of the submission so the host never
/// needs a second round-trip to Bilibili to fill its own catalog. All values are display text taken from
/// the already-fetched VideoView projection.
/// </summary>
public sealed record HeadlessVideoSubmission(
    string? UpName,
    long UpMid,
    string? Zone,
    string? PublishText,
    string? Description,
    string? ViewCountText,
    IReadOnlyList<HeadlessPageSubmission> Pages);
