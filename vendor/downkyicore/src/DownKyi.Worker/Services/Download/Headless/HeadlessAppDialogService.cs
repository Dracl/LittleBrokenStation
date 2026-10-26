using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download.Headless;

// LBS fork: headless decision provider fed to DownloadContentConflictResolver
// (src/DownKyi.Worker/Services/Download/DownloadContentConflictResolver.cs:113), which is the only
// IAppDialogService consumer of the download runtime. It replaces the desktop content-conflict dialog:
// there is no UI here, so every request is answered with a fixed policy and one log line per downgrade.
//
// Station policy (decided for the archive use case - store as much as possible):
//   * the main video itself is not obtainable for the page  -> SkipPage;
//   * every other downgrade that still leaves downloadable content (audio dropped / lower video or audio
//     quality / danmaku, subtitle or cover dropped)  -> UseAvailableContent with ApplyToAll = true;
//   * no downloadable content at all                   -> SkipPage.
// Because DownloadContentConflictResolver.ResolveAsync (:128) already returns NoAvailableContent before
// asking, and DownloadContentConflict (PreparedDownload-side) keeps a "compatible" selection whenever
// something is downloadable, HasAvailableContent + "video still reachable" is exactly the
// "can we archive anything of this page" test - see Decide for the mapping.
internal sealed class HeadlessAppDialogService : IAppDialogService
{
    private readonly ILogger _logger;

    internal HeadlessAppDialogService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<AppDialogResult> ShowAsync(
        AppDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Dialog != AppDialog.DownloadContentConflict || !TryGetPrompt(request, out var prompt))
        {
            // Defensive: the headless submission path never opens another dialog. Accepting with the
            // available content keeps DownloadContentConflictDialogContract.ShowAsync (:122) from throwing
            // instead of turning an unexpected prompt into a hard failure.
            _logger.LogWarningMessage(
                $"Headless dialog request {request.Dialog} is not a download content conflict; "
                + "accepted with the available content.");
            return Task.FromResult(Accept(DownloadContentConflictAction.UseAvailableContent));
        }

        return prompt.Mode switch
        {
            DownloadContentConflictPromptMode.QualitySubstitutionBatch =>
                Task.FromResult(HandleQualitySubstitutionBatch(prompt)),
            _ => Task.FromResult(HandleSingleConflict(prompt)),
        };
    }

    // LBS fork: mirrors DownloadContentConflictResolver.ResolveQualitySubstitutionBatchAsync (:158). The
    // batch decision must carry ApplyToAll = true (:172 throws otherwise), and UseAvailableContent is the
    // "download the highest quality that is actually available" semantics the station asks for - SkipPage
    // here would silently drop every page whose preferred quality is not offered. Each collected item gets
    // its own downgrade log line, because a batch covers a whole video.
    private AppDialogResult HandleQualitySubstitutionBatch(DownloadContentConflictPrompt prompt)
    {
        foreach (var item in prompt.Items)
        {
            Decide(item.PageName, item.Conflict);
        }

        // A quality-substitution batch is built from items that are IsQualitySubstitutionOnly and
        // HasAvailableContent (DownloadActionPlanner.cs:203), so Decide can only answer UseAvailableContent
        // here; the batch as a whole is decided once, not per page.
        return Accept(DownloadContentConflictAction.UseAvailableContent);
    }

    // LBS fork: mirrors DownloadContentConflictResolver.ResolveActionAsync (:191). Not a pure quality
    // substitution means the runtime can only offer a reduced selection for this page; the station keeps the
    // reduced selection unless the main video itself is gone.
    private AppDialogResult HandleSingleConflict(DownloadContentConflictPrompt prompt) =>
        Accept(Decide(prompt.PageName, prompt.Conflict));

    // The single decision rule of the headless dialog. Returns the action to hand back to the resolver and
    // writes one log line per downgrade so the station operator can see what was not stored.
    private DownloadContentConflictAction Decide(
        string pageName,
        DownloadContentConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageName);

        if (!conflict.HasAvailableContent)
        {
            _logger.LogInformationMessage(
                $"Headless conflict decision: skip page \"{pageName}\" - nothing downloadable "
                + $"({Describe(conflict)}).");
            return DownloadContentConflictAction.SkipPage;
        }

        // 只有「正片本身拿不到」才放弃这一页：此时只剩音频/封面等边角内容，归档价值为负
        // (会产出一堆没有正片的目录)。
        if (conflict.RequestedContent.Video && !conflict.AvailableContent.Video)
        {
            _logger.LogInformationMessage(
                $"Headless conflict decision: skip page \"{pageName}\" - the main video itself is not "
                + $"available ({Describe(conflict)}).");
            return DownloadContentConflictAction.SkipPage;
        }

        var downgrades = CollectDowngrades(conflict);
        if (downgrades.Count == 0)
        {
            // Defensive: a conflict always carries something to report, but never answer without a log.
            _logger.LogInformationMessage(
                $"Headless conflict decision: use available content for \"{pageName}\" "
                + $"({Describe(conflict)}).");
            return DownloadContentConflictAction.UseAvailableContent;
        }

        foreach (var downgrade in downgrades)
        {
            _logger.LogInformationMessage(
                $"Headless conflict decision: use available content for \"{pageName}\" - {downgrade} "
                + $"({Describe(conflict)}).");
        }

        return DownloadContentConflictAction.UseAvailableContent;
    }

    // One line per downgrade kind. Note that the conflict contract can only express media-kind trades
    // (DownloadMediaCapabilities.TryGetCompatibleContent, src/DownKyi.Worker/Services/Download/
    // PreparedDownload.cs:33, only ever clears audio/video) plus quality substitutions, so the danmaku /
    // subtitle / cover branches stay defensive for future conflict kinds instead of firing today.
    private static List<string> CollectDowngrades(DownloadContentConflict conflict)
    {
        var requested = conflict.RequestedContent;
        var available = conflict.AvailableContent;
        var downgrades = new List<string>();

        if (requested.Audio && !available.Audio)
        {
            downgrades.Add("audio is not available, downgraded to video only");
        }

        if (requested.Danmaku && !available.Danmaku)
        {
            downgrades.Add("danmaku is not available, dropped");
        }

        if (requested.HasSubtitleAction && !available.HasSubtitleAction)
        {
            downgrades.Add("subtitles are not available, dropped");
        }

        if (requested.Cover && !available.Cover)
        {
            downgrades.Add("cover is not available, dropped");
        }

        AddQualityDowngrade(downgrades, "video", conflict.QualitySubstitutions.Video);
        AddQualityDowngrade(downgrades, "audio", conflict.QualitySubstitutions.Audio);
        return downgrades;
    }

    private static void AddQualityDowngrade(
        List<string> downgrades,
        string kind,
        DownloadQualitySubstitution? substitution)
    {
        ArgumentNullException.ThrowIfNull(downgrades);
        if (substitution != null)
        {
            downgrades.Add(
                $"{kind} quality {substitution.RequestedName}({substitution.RequestedQuality.ToString(
                    CultureInfo.InvariantCulture)}) is not available, using {substitution.SelectedName}({
                    substitution.SelectedQuality.ToString(CultureInfo.InvariantCulture)})");
        }
    }

    private static AppDialogResult Accept(DownloadContentConflictAction action) => new(
        AppDialogOutcome.Accepted,
        DownloadContentConflictDialogContract.Encode(
            new DownloadContentConflictDecision(action, ApplyToAll: true)));

    private static bool TryGetPrompt(
        AppDialogRequest request,
        out DownloadContentConflictPrompt prompt)
    {
        prompt = null!;
        if (request.Parameters is null)
        {
            return false;
        }

        if (!request.Parameters.TryGetValue(
                DownloadContentConflictDialogContract.PromptParameter, out var value))
        {
            return false;
        }

        if (value is not DownloadContentConflictPrompt candidate)
        {
            return false;
        }

        prompt = candidate;
        return true;
    }

    private static string Describe(DownloadContentConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return "requested=["
            + DescribeSelection(conflict.RequestedContent)
            + "], available=["
            + DescribeSelection(conflict.AvailableContent)
            + $"], modes={conflict.AvailableMedia.SupportedModes}, substitutions=[{DescribeSubstitutions(
                conflict.QualitySubstitutions)}]";
    }

    private static string DescribeSubstitutions(DownloadQualitySubstitutions substitutions)
    {
        ArgumentNullException.ThrowIfNull(substitutions);
        return string.Join(
            ", ",
            new[]
            {
                DescribeSubstitution("video", substitutions.Video),
                DescribeSubstitution("audio", substitutions.Audio),
            }.Where(static text => text.Length > 0));
    }

    private static string DescribeSubstitution(string kind, DownloadQualitySubstitution? substitution)
    {
        return substitution == null
            ? string.Empty
            : $"{kind} {substitution.RequestedName}({substitution.RequestedQuality.ToString(
                CultureInfo.InvariantCulture)})->{substitution.SelectedName}({substitution.SelectedQuality.ToString(
                CultureInfo.InvariantCulture)})";
    }

    private static string DescribeSelection(DownloadContentSelection content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return string.Join(
            ", ",
            new[]
            {
                content.Audio ? "audio" : null,
                content.Video ? "video" : null,
                content.Danmaku ? "danmaku" : null,
                content.HasSubtitleAction ? "subtitle" : null,
                content.Cover ? "cover" : null,
            }.OfType<string>());
    }
}
