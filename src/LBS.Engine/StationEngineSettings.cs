using System.Collections.Immutable;
using DownKyi.Application.FileName;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download.Headless;
using LBS.Infrastructure;

namespace LBS.Engine;

// ─── 站长室「下载设置」的传输形态 ─────────────────────────────────────────────
// 原则：只暴露引擎真实消费的字段（全部有 vendor 内消费点佐证），
// 假参数（HighSpeedDownloadMode、IsTranscodingFlvToMp4、IsCustomResolution、代理组）一律不出现在这里。
// 布尔一律替 AllowStatus 三态（Yes↔true，None/No↔false）；枚举按 int 传输，前端负责中文名。

public sealed record StationEngineSettingsDto(
    StationNetworkDto Network,
    StationVideoDto Video,
    StationDanmakuDto Danmaku,
    StationBasicDto Basic);

public sealed record StationNetworkDto(
    string UserAgent,
    int MaxCurrentDownloads,
    int Split,
    int MaxOverallDownloadLimit,
    int MaxDownloadLimit);

public sealed record StationVideoDto(
    int VideoCodecs,
    int Quality,
    int AudioQuality,
    bool IsTranscodingAacToMp3,
    int FfmpegHardwareAcceleration,
    int FfmpegMaxParallelJobs,
    StationContentDto Content,
    IReadOnlyList<int> FileNameParts,
    string FileNamePartTimeFormat,
    int OrderFormat);

public sealed record StationContentDto(
    bool DownloadAudio,
    bool DownloadVideo,
    bool DownloadDanmaku,
    bool DownloadSubtitle,
    bool DownloadCover,
    bool GenerateMovieMetadata,
    int PreferredSubtitleLanguage,
    int PreferredSubtitleType);

public sealed record StationDanmakuDto(
    int OutputFormat,
    bool TopFilter,
    bool BottomFilter,
    bool ScrollFilter,
    bool RemoveEmojiAndSpecialCharacters,
    IReadOnlyList<string> BlockedKeywords,
    IReadOnlyList<long> BlockedSenderUids,
    int ScreenWidth,
    int ScreenHeight,
    string FontName,
    int FontSize,
    int LineCount,
    int LayoutAlgorithm);

public sealed record StationBasicDto(bool RepeatFileAutoAddNumberSuffix);

/// <summary>设置草稿 → ApplicationSettings 的双向映射与校验，纯函数便于测试。</summary>
public static class StationEngineSettingsMapper
{
    /// <summary>分站默认 UA：B 站 API 请求头与 CDN 下载请求头共用，站长室可改。</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    /// <summary>
    /// 分站默认命名（照站长本尊的 downkyi 配置钉死）：
    /// 「UP主昵称 - UP主ID / 标题 - 发布日 / 标题 - (序号 分P标题) - bvid 」。
    /// 前两段 Slash 天然造出两层目录，序号+bvid 保证多 P 不互撞。
    /// </summary>
    public static readonly ImmutableArray<FileNamePart> DefaultFileNameParts =
    [
        FileNamePart.UpName, FileNamePart.Blank, FileNamePart.Hyphen, FileNamePart.Blank,
        FileNamePart.UpMid, FileNamePart.Slash,
        FileNamePart.MainTitle, FileNamePart.Blank, FileNamePart.Hyphen, FileNamePart.Blank,
        FileNamePart.VideoPublishTime, FileNamePart.Slash,
        FileNamePart.MainTitle, FileNamePart.Blank, FileNamePart.Hyphen, FileNamePart.Blank,
        FileNamePart.OpenParen, FileNamePart.Order, FileNamePart.Blank, FileNamePart.PageTitle, FileNamePart.CloseParen,
        FileNamePart.Hyphen, FileNamePart.Blank, FileNamePart.Bvid, FileNamePart.Blank,
    ];

    public static StationEngineSettingsDto FromSettings(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new StationEngineSettingsDto(
            new StationNetworkDto(
                settings.Network.UserAgent,
                settings.Network.MaxCurrentDownloads,
                settings.Network.Split,
                settings.Network.MaxOverallDownloadLimit,
                settings.Network.MaxDownloadLimit),
            new StationVideoDto(
                settings.Video.VideoCodecs,
                settings.Video.Quality,
                settings.Video.AudioQuality,
                IsYes(settings.Video.IsTranscodingAacToMp3),
                (int)settings.Video.FfmpegHardwareAcceleration,
                settings.Video.FfmpegMaxParallelJobs,
                new StationContentDto(
                    settings.Video.Content.DownloadAudio,
                    settings.Video.Content.DownloadVideo,
                    settings.Video.Content.DownloadDanmaku,
                    settings.Video.Content.DownloadSubtitle,
                    settings.Video.Content.DownloadCover,
                    settings.Video.Content.GenerateMovieMetadata,
                    (int)settings.Video.Content.PreferredSubtitleLanguage,
                    (int)settings.Video.Content.PreferredSubtitleType),
                settings.Video.FileNameParts.Select(part => (int)part).ToArray(),
                settings.Video.FileNamePartTimeFormat,
                (int)settings.Video.OrderFormat),
            new StationDanmakuDto(
                (int)settings.Danmaku.OutputFormat,
                IsYes(settings.Danmaku.TopFilter),
                IsYes(settings.Danmaku.BottomFilter),
                IsYes(settings.Danmaku.ScrollFilter),
                IsYes(settings.Danmaku.RemoveEmojiAndSpecialCharacters),
                settings.Danmaku.BlockedKeywords.ToArray(),
                settings.Danmaku.BlockedSenderUids.ToArray(),
                settings.Danmaku.ScreenWidth,
                settings.Danmaku.ScreenHeight,
                settings.Danmaku.FontName,
                settings.Danmaku.FontSize,
                settings.Danmaku.LineCount,
                (int)settings.Danmaku.LayoutAlgorithm),
            new StationBasicDto(settings.Basic.RepeatFileAutoAddNumberSuffix));
    }

    /// <summary>
    /// 把白名单字段写回 settings（其余字段原样保留）。软性非法值钳制并记 warning，
    /// 硬性非法（内容全关、UA 清洗后为空等）返回 false + error。
    /// </summary>
    public static bool TryApplyDraft(
        ApplicationSettings current,
        StationEngineSettingsDto draft,
        out ApplicationSettings updated,
        out string? error,
        out List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(current);
        updated = current;
        error = null;
        warnings = [];

        if (draft?.Network is not { } network
            || draft.Video is not { } video
            || draft.Danmaku is not { } danmaku
            || draft.Basic is not { } basic)
        {
            error = "设置草稿结构不完整";
            return false;
        }

        if (video.Content is not { } content)
        {
            error = "设置草稿缺少下载内容部分";
            return false;
        }

        if (!content.DownloadVideo && !content.DownloadAudio)
        {
            error = "正片和音频至少要选一个，不然下回来的不是视频站";
            return false;
        }

        var userAgent = SanitizeHeader(network.UserAgent);
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            userAgent = DefaultUserAgent;
            warnings.Add("UserAgent 为空，已换回分站默认 UA");
        }

        var qualityIds = PlaybackQualityCatalog.GetResolutions().Select(item => item.Id).ToHashSet();
        var quality = ClampToKnown(video.Quality, qualityIds, PlaybackQualityCatalog.MaximumProbeQuality, "画质", warnings);

        var audioIds = PlaybackQualityCatalog.GetAudioPreferences().Select(item => item.Id).ToHashSet();
        var audioQuality = ClampToKnown(video.AudioQuality, audioIds, 30280, "音质", warnings);

        var codecIds = PlaybackQualityCatalog.GetCodecIds().Select(item => item.Id).ToHashSet();
        var codecs = ClampToKnown(video.VideoCodecs, codecIds, 7, "优先编码", warnings);

        var fileNameParts = SanitizeFileNameParts(video.FileNameParts, warnings);

        var timeFormat = SanitizeTimeFormat(video.FileNamePartTimeFormat, warnings);

        var keywords = (draft.Danmaku.BlockedKeywords ?? Array.Empty<string>())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keywords.Count > 200)
        {
            keywords = keywords.Take(200).ToList();
            warnings.Add("弹幕关键词黑名单超过 200 条，只保留前 200 条");
        }

        var uids = (draft.Danmaku.BlockedSenderUids ?? Array.Empty<long>())
            .Where(uid => uid > 0)
            .Distinct()
            .ToList();
        if (uids.Count > 200)
        {
            uids = uids.Take(200).ToList();
            warnings.Add("弹幕 UID 黑名单超过 200 条，只保留前 200 条");
        }

        updated = current with
        {
            Network = current.Network with
            {
                UserAgent = userAgent,
                MaxCurrentDownloads = Math.Clamp(network.MaxCurrentDownloads, 1, 16),
                Split = Math.Clamp(network.Split, 1, 32),
                MaxOverallDownloadLimit = ClampLimit(network.MaxOverallDownloadLimit, "全局限速", warnings),
                MaxDownloadLimit = ClampLimit(network.MaxDownloadLimit, "单任务限速", warnings),
            },
            Video = current.Video with
            {
                VideoCodecs = codecs,
                Quality = quality,
                AudioQuality = audioQuality,
                IsTranscodingAacToMp3 = ToAllow(video.IsTranscodingAacToMp3),
                FfmpegHardwareAcceleration = EnumClamp(video.FfmpegHardwareAcceleration, FfmpegHardwareAcceleration.NotSet, 7, "ffmpeg 硬解", warnings),
                FfmpegMaxParallelJobs = Math.Clamp(video.FfmpegMaxParallelJobs, 1, 4),
                Content = current.Video.Content with
                {
                    DownloadAudio = content.DownloadAudio,
                    DownloadVideo = content.DownloadVideo,
                    DownloadDanmaku = content.DownloadDanmaku,
                    DownloadSubtitle = content.DownloadSubtitle,
                    DownloadCover = content.DownloadCover,
                    GenerateMovieMetadata = content.GenerateMovieMetadata,
                    PreferredSubtitleLanguage = EnumClamp(content.PreferredSubtitleLanguage, SubtitleLanguagePreference.Unspecified, 5, "字幕语言", warnings),
                    PreferredSubtitleType = EnumClamp(content.PreferredSubtitleType, SubtitleTypePreference.Standard, 1, "字幕类型", warnings),
                },
                FileNameParts = fileNameParts,
                FileNamePartTimeFormat = timeFormat,
                OrderFormat = video.OrderFormat is 1 or 2 ? (OrderFormat)video.OrderFormat : OrderFormat.Natural,
            },
            Danmaku = current.Danmaku with
            {
                OutputFormat = EnumClamp(danmaku.OutputFormat, DanmakuOutputFormat.Ass, 2, "弹幕输出格式", warnings),
                TopFilter = ToAllow(danmaku.TopFilter),
                BottomFilter = ToAllow(danmaku.BottomFilter),
                ScrollFilter = ToAllow(danmaku.ScrollFilter),
                RemoveEmojiAndSpecialCharacters = ToAllow(danmaku.RemoveEmojiAndSpecialCharacters),
                BlockedKeywords = [.. keywords],
                BlockedSenderUids = [.. uids],
                ScreenWidth = Math.Clamp(danmaku.ScreenWidth, 1, 16384),
                ScreenHeight = Math.Clamp(danmaku.ScreenHeight, 1, 16384),
                FontName = string.IsNullOrWhiteSpace(danmaku.FontName) ? "黑体" : danmaku.FontName.Trim(),
                FontSize = Math.Clamp(danmaku.FontSize, 1, 500),
                LineCount = Math.Clamp(danmaku.LineCount, 0, 1000),
                LayoutAlgorithm = EnumClamp(danmaku.LayoutAlgorithm, DanmakuLayoutAlgorithm.None, 2, "弹幕排版算法", warnings),
            },
            Basic = current.Basic with
            {
                RepeatFileAutoAddNumberSuffix = basic.RepeatFileAutoAddNumberSuffix,
            },
        };
        return true;
    }

    /// <summary>分站提交策略：内容勾选/画质/命名一律吃设置现值（面板所见即所下）。</summary>
    public static HeadlessSubmissionOptions BuildSubmissionOptions(DataPaths paths, ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        var content = settings.Video.Content;
        var selection = new DownloadContentSelection(
            content.DownloadAudio,
            content.DownloadVideo,
            content.DownloadDanmaku,
            content.DownloadSubtitle,
            content.DownloadCover)
        {
            // 分站恒抓全轨（11-11 站长拍板）：语言/类型偏好不再下传——它是提交即定生的筛子，
            // 多语 AI 轨会被筛剩一条（BV1Q44v6RE2V 三轨只进站一条的病灶）。
            // 设置里的偏好值原样留着（面板已标注停用），要恢复筛选只改回这两行。
            PreferredSubtitleLanguage = SubtitleLanguagePreference.Unspecified,
            PreferredSubtitleType = SubtitleTypePreference.Standard,
        };

        return new HeadlessSubmissionOptions
        {
            // 全关属于配置事故（面板校验拦住），真发生就退回全量，至少让视频进站。
            Content = selection.HasAnyRequestedAction ? selection : DownloadContentSelection.All,
            VideoQuality = settings.Video.Quality > 0
                ? settings.Video.Quality
                : PlaybackQualityCatalog.MaximumProbeQuality,
            AudioQualityFormat = null, // 跟随 settings.Video.AudioQuality
            Directory = paths.Videos,
            RedownloadCompleted = false,
            FileNameParts = null,      // 跟随 settings.Video.FileNameParts（写回时已保证唯一性）
        };
    }

    private static bool IsYes(AllowStatus status) => status == AllowStatus.Yes;

    private static AllowStatus ToAllow(bool value) => value ? AllowStatus.Yes : AllowStatus.No;

    private static string SanitizeHeader(string? value) =>
        new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();

    private static int ClampToKnown(int value, HashSet<int> allowed, int fallback, string label, List<string> warnings)
    {
        if (allowed.Contains(value))
        {
            return value;
        }

        warnings.Add($"{label}值 {value} 不在合法目录里，已改为 {fallback}");
        return fallback;
    }

    private static int ClampLimit(int value, string label, List<string> warnings)
    {
        if (value == -1)
        {
            return -1; // 不限速
        }

        var clamped = Math.Clamp(value, 0, 20_000_000);
        if (clamped != value)
        {
            warnings.Add($"{label} {value} KB/s 超出范围，已调整为 {clamped}");
        }

        return clamped;
    }

    private static TEnum EnumClamp<TEnum>(int value, TEnum fallback, int max, string label, List<string> warnings)
        where TEnum : struct, Enum
    {
        if (value >= 0 && value <= max && Enum.IsDefined(typeof(TEnum), value))
        {
            return (TEnum)(object)value;
        }

        warnings.Add($"{label}值 {value} 非法，已改为 {fallback}");
        return fallback;
    }

    private static string SanitizeTimeFormat(string? value, List<string> warnings)
    {
        var cleaned = new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length is 0 or > 40)
        {
            warnings.Add("命名时间格式非法，已改为 yyyy-MM-dd");
            return "yyyy-MM-dd";
        }

        return cleaned;
    }

    private static ImmutableArray<FileNamePart> SanitizeFileNameParts(IReadOnlyList<int>? parts, List<string> warnings)
    {
        var sanitized = new List<FileNamePart>();
        if (parts is not null)
        {
            foreach (var raw in parts)
            {
                if (Enum.IsDefined(typeof(FileNamePart), raw))
                {
                    sanitized.Add((FileNamePart)raw);
                }
            }

            if (sanitized.Count > 48)
            {
                sanitized = sanitized.Take(48).ToList();
                warnings.Add("命名段超过 48 段，只保留前 48 段");
            }
        }

        if (sanitized.Count == 0)
        {
            warnings.Add("命名段为空，已换回分站默认命名");
            return DefaultFileNameParts;
        }

        // 多 P 视频的唯一性底线：至少要有一个每 P 不同的段，否则所有分 P 会撞在同一个文件名上。
        var uniqueParts = new HashSet<FileNamePart> { FileNamePart.Order, FileNamePart.Cid, FileNamePart.Avid, FileNamePart.Bvid };
        if (!sanitized.Any(part => uniqueParts.Contains(part)))
        {
            sanitized.Add(FileNamePart.Hyphen);
            sanitized.Add(FileNamePart.Cid);
            warnings.Add("命名段里没有任何能区分分 P 的字段，已自动补「-CID」防止多 P 互相覆盖");
        }

        return [.. sanitized];
    }
}
