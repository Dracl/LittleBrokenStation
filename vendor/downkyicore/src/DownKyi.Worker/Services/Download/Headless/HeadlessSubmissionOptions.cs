using System.Collections.Generic;
using DownKyi.Application.FileName;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download.Headless;

/// <summary>
/// Per-submission overrides for <see cref="HeadlessSubmissionService.SubmitAsync"/>.
/// Every <c>null</c> value falls back to the value persisted in the settings store, so an all-null
/// instance means "add this video exactly the way the desktop would add it".
/// </summary>
/// <param name="Content">
/// What to download for every page. <c>null</c> means <see cref="DownloadContentSelection.All"/>
/// (audio + video + danmaku + subtitle + cover), matching the desktop default. A selection without any
/// requested action is rejected by the planner with <c>NoContentRequested</c>.
/// </param>
/// <param name="VideoQuality">
/// Preferred Bilibili video quality id (see <c>PlaybackQualityCatalog.GetResolutions()</c>).
/// <c>null</c> keeps <c>ApplicationSettings.Video.Quality</c>. A station that wants "the highest quality
/// that is actually available" passes <c>PlaybackQualityCatalog.MaximumProbeQuality</c> (127): the quality
/// catalog picks the highest supported stream below it and the headless conflict dialog accepts that
/// substitution automatically instead of asking the user.
/// </param>
/// <param name="AudioQualityFormat">
/// Preferred audio quality by display name (one of <c>PlaybackQualityCatalog.GetAudioQualities()</c> names:
/// 低质量 / 中质量 / 高质量 / Dolby Atmos / Hi-Res无损). <c>null</c> keeps
/// <c>ApplicationSettings.Video.AudioQuality</c>. Unknown names are ignored and logged.
/// </param>
/// <param name="Directory">
/// Absolute root directory for the download output. <c>null</c> (or blank) keeps
/// <c>ApplicationSettings.Video.SaveVideoRootPath</c>; the directory is created when missing.
/// </param>
/// <param name="RedownloadCompleted">
/// When <c>false</c> (default) pages that already exist in the finished-download history are skipped and
/// logged. When <c>true</c> those pages are admitted again with
/// <c>DownloadTaskAdmissionService.AdmitAsync(..., redownloadCompleted: true)</c>. Pages that are still in
/// the unfinished queue are always skipped - re-adding them would double-drive the same output.
/// </param>
/// <param name="FileNameParts">
/// File-name layout override, i.e. <c>ApplicationSettings.Video.FileNameParts</c>. <c>null</c> keeps the
/// configured value; a station policy such as <c>[UpName, Slash, MainTitle, Hyphen, Bvid]</c> puts every
/// task under <c>&lt;root&gt;/&lt;up name&gt;/&lt;title&gt;-&lt;bvid&gt;</c>.
/// <para>
/// Uniqueness is the caller's responsibility: the parts above are identical for every 分P of the same video,
/// so a multi-P submission collapses all pages onto one output base path (admission only appends a numeric
/// suffix when <c>Basic.RepeatFileAutoAddNumberSuffix</c> is on, and that silently turns "P1, P2, P3" into
/// "name, name (1), name (2)"). Add a per-page unique part for multi-P videos -
/// <see cref="FileNamePart.Order"/> works, but it renders the literal placeholder <c>ORDER</c> unless
/// <c>ApplicationSettings.Video.OrderFormat</c> is <c>Natural</c> or <c>LeadingZeros</c>, so
/// <see cref="FileNamePart.Cid"/> is the safer choice.
/// </para>
/// </param>
public sealed record HeadlessSubmissionOptions(
    DownloadContentSelection? Content = null,
    int? VideoQuality = null,
    string? AudioQualityFormat = null,
    string? Directory = null,
    bool RedownloadCompleted = false,
    IReadOnlyList<FileNamePart>? FileNameParts = null,
    // LBS fork: 指定视频编码（PlaybackQualityCatalog 的 codec id：7=AVC、12=HEVC、13=AV1），
    // null 跟随设置里的 Video.VideoCodecs。分站用它为同一支视频拉取多个编码版本。
    int? VideoCodec = null);
