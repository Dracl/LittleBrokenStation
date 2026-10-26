using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Models;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.BiliApi.Video.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using Microsoft.Extensions.Logging;
using VideoPage = DownKyi.Presentation.VideoPage;

namespace DownKyi.Services.Download.Headless;

/// <summary>
/// Outcome of the headless preparation step: either a projected video with its sections, or the reason the
/// input is not an ordinary Bilibili video.
/// </summary>
internal sealed record HeadlessVideoPreparation(
    VideoInfoView? Video,
    IReadOnlyList<VideoSection>? Sections,
    string? UnsupportedReason)
{
    internal static HeadlessVideoPreparation Supported(
        VideoInfoView video,
        IReadOnlyList<VideoSection> sections) => new(video, sections, null);

    internal static HeadlessVideoPreparation Unsupported(string reason) => new(null, null, reason);
}

/// <summary>
/// Resolves one ordinary Bilibili video from a URL/AV/BV input and brings every page to the state the
/// download runtime expects before <see cref="PreparedDownload.Create"/> runs: playback availability, the
/// automatically selected video/audio quality and the duration.
/// </summary>
/// <remarks>
/// This is the headless replacement of the desktop <c>VideoInfoService</c> + <c>DownloadPreparationService</c>
/// pair, both of which are internal to DownKyi.Desktop and therefore invisible to this assembly. It only
/// covers <see cref="PlayStreamType.Video"/>; bangumi and cheese keep their desktop-only services.
/// No finalized stream address is resolved here - that stays a download-time concern of
/// <c>DownloadPlaybackResolver</c> (src/DownKyi.Worker/Services/Download/DownloadPlaybackResolver.cs:64,
/// <c>GetVideoFinalizedPlaybackAsync</c>).
/// </remarks>
internal sealed class HeadlessVideoPreparer
{
    private readonly IBilibiliApiClient _client;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly ILogger _logger;

    internal HeadlessVideoPreparer(
        IBilibiliApiClient client,
        IWbiKeyProvider wbiKeyProvider,
        ILogger logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal async Task<HeadlessVideoPreparation> PrepareAsync(
        string videoUrl,
        ApplicationSettings settings,
        DownloadContentSelection requestedContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoUrl);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(requestedContent);
        cancellationToken.ThrowIfCancellationRequested();

        // LBS fork: input parsing ported from src/DownKyi.Desktop/Services/VideoInfoService.cs:58
        // (CreateAsync), reduced to the ordinary-video branches.
        if (!TryResolveIdentity(videoUrl, out var bvid, out var avid))
        {
            return HeadlessVideoPreparation.Unsupported(
                "输入的 B 站地址中不含可解析的 AV/BV 标识，无法按普通视频解析。");
        }

        var videoView = await LoadVideoViewAsync(bvid, avid, cancellationToken).ConfigureAwait(false);
        if (videoView == null)
        {
            return HeadlessVideoPreparation.Unsupported("未能获取视频信息（VideoView 为空）。");
        }

        // bangumi / cheese 特征：普通视频接口也可能返回番剧分区（tid），headless 一律不支持。
        var streamType = ResolvePlayStreamType(videoView.Tid);
        if (streamType != PlayStreamType.Video)
        {
            return HeadlessVideoPreparation.Unsupported(
                $"headless 仅支持普通视频；当前视频分区 tid={videoView.Tid.ToString(
                    CultureInfo.InvariantCulture)} 属于 {streamType}。");
        }

        var video = GetVideoView(videoView, settings, cancellationToken);
        var sections = GetVideoSections(videoView, noUgc: true, settings, cancellationToken);
        if (sections == null)
        {
            // LBS fork: same fallback as src/DownKyi.Desktop/Services/Download/DownloadPreparationService.cs:110
            // - a video without ugc sections gets one synthetic "default" section holding the plain pages
            //   (src/DownKyi.Desktop/Services/Download/DownloadPreparationService.cs:113).
            _logger.LogDebugMessage("Video sections do not exist.");
            sections =
            [
                new VideoSection
                {
                    Id = 0,
                    Title = "default",
                    IsSelected = true,
                    VideoPages = GetVideoPages(videoView, settings, cancellationToken) ?? [],
                },
            ];
        }

        // LBS fork: bangumi pages that carry a nested ordinary-video PlaybackAddress are resolved through
        // DownloadPreparationService.ResolvePlaybackServiceAsync
        // (src/DownKyi.Desktop/Services/Download/DownloadPreparationService.cs:147) on desktop. That path is
        // not reachable from this projection (pages are built from VideoView, so PlaybackAddress is always
        // empty) and is deliberately not supported headless yet.
        // TODO(headless): support bangumi pages with a nested ordinary-video PlaybackAddress by porting
        //   src/DownKyi.Desktop/Services/Download/DownloadPreparationService.cs:147-188 together with the
        //   bangumi info service.
        if (sections.Any(section => section.VideoPages.Any(
                page => !string.IsNullOrWhiteSpace(page.PlaybackAddress))))
        {
            return HeadlessVideoPreparation.Unsupported(
                "headless 暂不支持带嵌套 bangumi 播放地址的视频页面。");
        }

        foreach (var section in sections)
        {
            foreach (var page in section.VideoPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // LBS fork: the desktop download path marks every page as selected
                // (src/DownKyi.Desktop/Services/Download/DownloadPreparationService.cs:128); the planner is
                // driven by isAll, but the flag stays consistent with that behaviour.
                page.IsSelected = true;
                if (requestedContent.HasMedia)
                {
                    await PrepareMediaAsync(
                        page,
                        settings,
                        requestedContent,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return HeadlessVideoPreparation.Supported(video, sections);
    }

    // LBS fork: mirrors the AV/BV dispatch of src/DownKyi.Desktop/Services/VideoInfoService.cs:73-92.
    private static bool TryResolveIdentity(string input, out string? bvid, out long avid)
    {
        if (ParseEntrance.IsAvId(input) || ParseEntrance.IsAvUrl(input))
        {
            bvid = null;
            avid = ParseEntrance.GetAvId(input);
            return avid > 0;
        }

        if (ParseEntrance.IsBvId(input) || ParseEntrance.IsBvUrl(input))
        {
            var parsedBvid = ParseEntrance.GetBvId(input);
            bvid = string.IsNullOrWhiteSpace(parsedBvid) ? null : parsedBvid;
            avid = -1;
            return bvid != null;
        }

        bvid = null;
        avid = -1;
        return false;
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/VideoInfoService.cs:99 (LoadVideoViewAsync) and
    // :106 (the WbiRequestExecutor + VideoViewInfoAsync pair).
    private Task<VideoView?> LoadVideoViewAsync(
        string? bvid,
        long avid,
        CancellationToken cancellationToken)
    {
        return WbiRequestExecutor.ExecuteAsync(
            _wbiKeyProvider,
            (keys, unixTimeSeconds) => _client.VideoViewInfoAsync(
                keys,
                unixTimeSeconds,
                bvid,
                avid,
                cancellationToken),
            TimeProvider.System,
            cancellationToken);
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/VideoInfoService.cs:354 (GetVideoView).
    private static VideoInfoView GetVideoView(
        VideoView videoView,
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoView);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        // 查询、保存封面
        var coverUrl = videoView.Pic;

        // 分区
        var videoZone = string.Empty;
        var zoneList = VideoZone.Current.Zones;
        var zone = zoneList.FirstOrDefault(it => it.Id == videoView.Tid);
        if (zone != null)
        {
            var zoneParent = zoneList.FirstOrDefault(it => it.Id == zone.ParentId);
            videoZone = zoneParent != null
                ? zoneParent.Name + ">" + zone.Name
                : zone.Name;
        }
        else
        {
            videoZone = videoView.Tname;
        }

        // 获取用户头像
        var upName = videoView.Owner?.Name ?? string.Empty;
        var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local);
        var dateTime = startTime.AddSeconds(videoView.Pubdate);

        return new VideoInfoView
        {
            CoverUrl = coverUrl ?? string.Empty,
            Title = videoView.Title,
            TypeId = videoView.Tid,
            VideoZone = videoZone,
            CreateTime = dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            PlayNumber = Format.FormatNumber(videoView.Stat.View),
            DanmakuNumber = Format.FormatNumber(videoView.Stat.Danmaku),
            LikeNumber = Format.FormatNumber(videoView.Stat.Like),
            CoinNumber = Format.FormatNumber(videoView.Stat.Coin),
            FavoriteNumber = Format.FormatNumber(videoView.Stat.Favorite),
            ShareNumber = Format.FormatNumber(videoView.Stat.Share),
            ReplyNumber = Format.FormatNumber(videoView.Stat.Reply),
            Description = videoView.Desc,
            UpHeader = videoView.Owner?.Face ?? string.Empty,
            UpperMid = videoView.Owner?.Mid ?? -1,
            UpName = upName,
        };
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/VideoInfoService.cs:207 (GetVideoSections).
    // The headless download path always asks for noUgc = true, exactly like
    // src/DownKyi.Desktop/Services/Download/DownloadPreparationService.cs:107, so one flat section is used;
    // the ugc-season branches are kept so the projection stays identical to the desktop one.
    private List<VideoSection>? GetVideoSections(
        VideoView videoView,
        bool noUgc,
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoView);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (!(videoView.UgcSeason?.Sections?.Count > 0))
        {
            return null;
        }

        var videoSections = new List<VideoSection>();
        if (noUgc)
        {
            videoSections.Add(CreateDefaultVideoSection(videoView, settings, cancellationToken));
            return videoSections;
        }

        var timeFormat = settings.Video.FileNamePartTimeFormat;
        var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local);
        foreach (var section in videoView.UgcSeason!.Sections!)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pages = new List<VideoPage>();
            var order = 0;
            foreach (var episode in section.Episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (episode.Pages?.Count > 1)
                {
                    videoSections.Add(
                        CreateVideoSectionFromEpisode(videoView, section, episode, startTime, timeFormat));
                }
                else
                {
                    order++;
                    pages.Add(GenerateVideoPage(videoView, episode, order, startTime, timeFormat));
                }
            }

            if (pages.Count > 0)
            {
                videoSections.Add(new VideoSection
                {
                    Id = section.Id,
                    Title = section.Title,
                    VideoPages = pages,
                });
            }
        }

        if (videoSections.Count > 0)
        {
            videoSections[0].IsSelected = true;
        }

        return videoSections.Count > 0 ? videoSections : null;
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/VideoInfoService.cs:262.
    private VideoSection CreateDefaultVideoSection(
        VideoView videoView,
        ApplicationSettings settings,
        CancellationToken cancellationToken) => new()
        {
            Id = 0,
            Title = "default",
            IsSelected = true,
            VideoPages = GetVideoPages(videoView, settings, cancellationToken) ?? [],
        };

    // LBS fork: ported from src/DownKyi.Desktop/Services/VideoInfoService.cs:273.
    private VideoSection CreateVideoSectionFromEpisode(
        VideoView videoView,
        UgcSection section,
        UgcEpisode episode,
        DateTime startTime,
        string timeFormat)
    {
        ArgumentNullException.ThrowIfNull(videoView);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(episode);
        var videoSection = new VideoSection
        {
            Id = section.Id,
            Title = episode.Title,
            VideoPages = [],
        };
        var owner = videoView.Owner ?? new VideoOwner { Name = string.Empty, Face = string.Empty, Mid = -1 };
        var order = 1;
        foreach (var page in episode.Pages)
        {
            var dateTime = startTime.AddSeconds(episode.Arc.Ctime);
            videoSection.VideoPages.Add(new VideoPage
            {
                Avid = episode.Aid,
                Bvid = episode.Bvid,
                Cid = page.Cid,
                EpisodeId = -1,
                FirstFrame = episode.Arc.Pic,
                Order = order++,
                Name = page.Part,
                Duration = FormatPageDuration(page.Duration),
                Owner = owner,
                Page = page.Page,
                PublishTime = dateTime.ToString(timeFormat, CultureInfo.CurrentCulture),
                OriginalPublishTime = dateTime,
                LoadTagsAsync = currentToken => LoadTagsAsync(episode.Bvid, page.Cid, currentToken),
            });
        }

        return videoSection;
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/VideoInfoService.cs:308.
    private VideoPage GenerateVideoPage(
        VideoView videoView,
        UgcEpisode episode,
        int order,
        DateTime startTime,
        string timeFormat)
    {
        ArgumentNullException.ThrowIfNull(videoView);
        ArgumentNullException.ThrowIfNull(episode);
        var page = new VideoPage
        {
            Avid = episode.Aid,
            Bvid = episode.Bvid,
            Cid = episode.Cid,
            EpisodeId = -1,
            FirstFrame = episode.Arc.Pic,
            Order = order,
            Name = episode.Title,
            Duration = FormatPageDuration(episode.Page.Duration),
            Owner = videoView.Owner ?? new VideoOwner { Name = string.Empty, Face = string.Empty, Mid = -1 },
            Page = episode.Page.Page,
            LoadTagsAsync = currentToken => LoadTagsAsync(episode.Bvid, episode.Cid, currentToken),
        };
        var dateTime = startTime.AddSeconds(episode.Arc.Ctime);
        page.PublishTime = dateTime.ToString(timeFormat, CultureInfo.CurrentCulture);
        page.OriginalPublishTime = dateTime;
        return page;
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/VideoInfoService.cs:122 (GetVideoPages). The tag
    // closure replaces IVideoTagProvider (an internal DownKyi.Desktop type) with the Core client extension
    // directly, as used by src/DownKyi.Desktop/Services/Video/VideoTagProvider.cs:40.
    private List<VideoPage>? GetVideoPages(
        VideoView videoView,
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoView);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (videoView.Pages is not { Count: > 0 })
        {
            return null;
        }

        var videoPages = new List<VideoPage>();
        var timeFormat = settings.Video.FileNamePartTimeFormat;
        var order = 0;
        foreach (var page in videoView.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order++;

            // 标题
            string name;
            if (videoView.Pages.Count == 1)
            {
                name = videoView.Title;
            }
            else if (string.IsNullOrEmpty(page.Part))
            {
                // 如果page.part为空字符串
                name = $"{videoView.Title}-P{order.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                name = page.Part;
            }

            var videoPage = new VideoPage
            {
                Avid = videoView.Aid,
                Bvid = videoView.Bvid,
                Cid = page.Cid,
                EpisodeId = -1,
                FirstFrame = page.FirstFrame,
                Order = order,
                Name = name,
                Duration = FormatPageDuration(page.Duration),
                Page = page.Page,
                LoadTagsAsync = currentToken => LoadTagsAsync(videoView.Bvid, page.Cid, currentToken),
            };

            // UP主信息
            videoPage.Owner = videoView.Owner ?? new VideoOwner { Name = string.Empty, Face = string.Empty, Mid = -1 };

            // 视频发布时间（当地时区）
            var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local);
            var dateTime = startTime.AddSeconds(videoView.Pubdate);
            videoPage.OriginalPublishTime = dateTime;
            videoPage.PublishTime = dateTime.ToString(timeFormat, CultureInfo.CurrentCulture);

            videoPages.Add(videoPage);
        }

        return videoPages;
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/Download/DownloadPreparationService.cs:212
    // (PrepareMediaAsync). Discovery is asked only when the page has no availability yet or the requested
    // media is not covered by what the webpage already offers.
    private async Task PrepareMediaAsync(
        VideoPage page,
        ApplicationSettings settings,
        DownloadContentSelection requestedContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(requestedContent);

        var request = CreateDiscoveryRequest(requestedContent, settings);
        var currentAvailability = page.PlaybackAvailability;
        if (currentAvailability != null && !request.NeedsApiSupplement(currentAvailability))
        {
            return;
        }

        // LBS fork: the desktop call goes through VideoInfoService.GetVideoStreamAsync
        // (src/DownKyi.Desktop/Services/VideoInfoService.cs:331), which is exactly this Core extension.
        var playUrl = await _client.GetVideoPlaybackDiscoveryAsync(
            _wbiKeyProvider,
            TimeProvider.System,
            page.Avid,
            page.Bvid,
            page.Cid,
            page.Page,
            request,
            cancellationToken).ConfigureAwait(false);
        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings, _logger);
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/Download/DownloadPreparationService.cs:238. The
    // quality comes from the effective settings, i.e. HeadlessSubmissionOptions overrides are already
    // applied by the caller.
    private static PlaybackDiscoveryRequest CreateDiscoveryRequest(
        DownloadContentSelection requestedContent,
        ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        ArgumentNullException.ThrowIfNull(settings);
        var videoSettings = settings.Video;
        return new PlaybackDiscoveryRequest(
            videoSettings.Quality,
            videoSettings.AudioQuality,
            requestedContent.Video,
            requestedContent.Audio);
    }

    // LBS fork: ported from src/DownKyi.Desktop/Services/Video/VideoTagProvider.cs:36, backed by
    // DownKyi.Core/BiliApi/Video/VideoInfo.cs:87 (GetBiliTagInfoAsync).
    private async Task<IReadOnlyList<string>> LoadTagsAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bvid);
        cancellationToken.ThrowIfCancellationRequested();
        var tags = await _client.GetBiliTagInfoAsync(bvid, cid, cancellationToken).ConfigureAwait(false);
        return tags?
            .Where(static tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
            .Select(static tag => tag.TagName)
            .ToArray()
            ?? Array.Empty<string>();
    }

    // LBS fork: VideoPageDuration.FromSeconds
    // (src/DownKyi.Desktop/Services/Video/VideoPageDuration.cs:6) is internal to DownKyi.Desktop.
    private static string FormatPageDuration(long duration) =>
        duration > 0 ? Format.FormatDuration(duration) : "N/A";

    // LBS fork: stream-type heuristic ported from
    // src/DownKyi.Worker/Services/Download/DownloadTaskDraftFactory.cs:198; used here only to reject
    // bangumi/cheese payloads that arrive through the ordinary video interface.
    private static PlayStreamType ResolvePlayStreamType(int typeId) => typeId switch
    {
        -10 => PlayStreamType.Cheese,
        13 or 23 or 177 or 167 or 11 => PlayStreamType.Bangumi,
        _ => PlayStreamType.Video,
    };
}
