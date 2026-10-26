using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>站长室「下载设置」映射器：白名单读写、钳制、命名唯一性底线、提交策略跟随设置。</summary>
public class StationEngineSettingsTests
{
    private static ApplicationSettings FreshSettings() => new(
        SchemaVersion: 1,
        Basic: new BasicApplicationSettings(
            ThemeMode.None,
            AfterDownloadOperation.NotSet,
            AllowStatus.None,
            DownloadFinishedSort.NotSet,
            false),
        Network: new NetworkApplicationSettings(
            AllowStatus.None,
            string.Empty,
            AllowStatus.None,
            NetworkProxy.None,
            string.Empty,
            -1,
            -1,
            -1,
            -1,
            AllowStatus.None,
            string.Empty,
            -1),
        Video: new VideoApplicationSettings(
            -1,
            -1,
            -1,
            AllowStatus.None,
            AllowStatus.None,
            FfmpegHardwareAcceleration.NotSet,
            -1,
            string.Empty,
            [],
            AllowStatus.None,
            new VideoContentApplicationSettings(
                true,
                true,
                true,
                true,
                true,
                false,
                SubtitleLanguagePreference.Unspecified,
                SubtitleTypePreference.Standard),
            ApplicationSettingsDefaults.FileNameParts,
            "yyyy-MM-dd",
            OrderFormat.Natural),
        Danmaku: new DanmakuApplicationSettings(
            DanmakuOutputFormat.Ass,
            AllowStatus.None,
            AllowStatus.None,
            AllowStatus.None,
            AllowStatus.None,
            [],
            [],
            AllowStatus.None,
            -1,
            -1,
            "黑体",
            -1,
            -1,
            DanmakuLayoutAlgorithm.None),
        About: new AboutApplicationSettings(AllowStatus.None, AllowStatus.None, string.Empty),
        User: new UserApplicationSettings(0, string.Empty, false, false, string.Empty, string.Empty),
        Window: new WindowApplicationSettings(0, 0, 0, 0));

    [Fact]
    public void 草稿回填_合法值原样落进设置()
    {
        var current = FreshSettings();
        var draft = StationEngineSettingsMapper.FromSettings(current) with
        {
            Network = StationEngineSettingsMapper.FromSettings(current).Network with
            {
                UserAgent = "LBS-Station/1.0",
                Split = 8,
                MaxOverallDownloadLimit = -1,
                MaxDownloadLimit = 512,
            },
        };

        Assert.True(StationEngineSettingsMapper.TryApplyDraft(current, draft, out var updated, out var error, out _));
        Assert.Null(error);
        Assert.Equal("LBS-Station/1.0", updated.Network.UserAgent);
        Assert.Equal(8, updated.Network.Split);
        Assert.Equal(-1, updated.Network.MaxOverallDownloadLimit);
        Assert.Equal(512, updated.Network.MaxDownloadLimit);
    }

    [Fact]
    public void 画质非法_钳回探针上限并留警告()
    {
        var current = FreshSettings();
        var draft = StationEngineSettingsMapper.FromSettings(current) with
        {
            Video = StationEngineSettingsMapper.FromSettings(current).Video with { Quality = 999 },
        };

        Assert.True(StationEngineSettingsMapper.TryApplyDraft(current, draft, out var updated, out _, out var warnings));
        Assert.Equal(127, updated.Video.Quality);
        Assert.Contains(warnings, warning => warning.Contains("画质", StringComparison.Ordinal));
    }

    [Fact]
    public void 内容全关_直接拒绝保存()
    {
        var current = FreshSettings();
        var baseDraft = StationEngineSettingsMapper.FromSettings(current);
        var draft = baseDraft with
        {
            Video = baseDraft.Video with
            {
                Content = baseDraft.Video.Content with { DownloadVideo = false, DownloadAudio = false },
            },
        };

        Assert.False(StationEngineSettingsMapper.TryApplyDraft(current, draft, out _, out var error, out _));
        Assert.NotNull(error);
    }

    [Fact]
    public void 命名段没有唯一字段_自动补CID防多P互撞()
    {
        var current = FreshSettings();
        var baseDraft = StationEngineSettingsMapper.FromSettings(current);
        var draft = baseDraft with
        {
            Video = baseDraft.Video with
            {
                FileNameParts = [(int)DownKyi.Application.FileName.FileNamePart.MainTitle],
            },
        };

        Assert.True(StationEngineSettingsMapper.TryApplyDraft(current, draft, out var updated, out _, out var warnings));
        Assert.Contains(DownKyi.Application.FileName.FileNamePart.Cid, updated.Video.FileNameParts);
        Assert.Contains(warnings, warning => warning.Contains("CID", StringComparison.Ordinal));
    }

    [Fact]
    public void 命名段为空_回落到分站默认序列() 
    {
        var current = FreshSettings();
        var baseDraft = StationEngineSettingsMapper.FromSettings(current);
        var draft = baseDraft with
        {
            Video = baseDraft.Video with { FileNameParts = [] },
        };

        Assert.True(StationEngineSettingsMapper.TryApplyDraft(current, draft, out var updated, out _, out var warnings));
        Assert.Contains(DownKyi.Application.FileName.FileNamePart.Bvid, updated.Video.FileNameParts);
        Assert.Contains(DownKyi.Application.FileName.FileNamePart.Order, updated.Video.FileNameParts);
        // 默认序列自带序号+bvid，不该再触发补 CID 的警告
        Assert.DoesNotContain(warnings, warning => warning.Contains("CID", StringComparison.Ordinal));
    }

    [Fact]
    public void UA带换行注入_被清洗成纯文本()
    {
        var current = FreshSettings();
        var baseDraft = StationEngineSettingsMapper.FromSettings(current);
        var draft = baseDraft with
        {
            Network = baseDraft.Network with { UserAgent = "GoodUA\r\nX-Injected: 1\r\n" },
        };

        Assert.True(StationEngineSettingsMapper.TryApplyDraft(current, draft, out var updated, out _, out _));
        Assert.DoesNotContain('\r', updated.Network.UserAgent);
        Assert.DoesNotContain('\n', updated.Network.UserAgent);
        Assert.StartsWith("GoodUA", updated.Network.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void 提交策略_内容勾选与画质跟随设置现值()
    {
        var current = FreshSettings();
        var withPartial = current with
        {
            Video = current.Video with
            {
                Quality = 80,
                Content = current.Video.Content with
                {
                    DownloadAudio = false,
                    DownloadVideo = true,
                    DownloadDanmaku = true,
                    DownloadSubtitle = false,
                    DownloadCover = false,
                },
            },
        };

        var paths = new DataPaths(
            Path.Combine(Path.GetTempPath(), "lbs-policy-test"),
            Path.Combine(Path.GetTempPath(), "lbs-policy-test", "videos"),
            Path.Combine(Path.GetTempPath(), "lbs-policy-test", "downkyi"),
            Path.Combine(Path.GetTempPath(), "lbs-policy-test", "cookies"),
            Path.Combine(Path.GetTempPath(), "lbs-policy-test", "cache"));
        var options = LbsSubmissionProfile.ForStation(paths, withPartial);

        Assert.NotNull(options.Content);
        Assert.False(options.Content!.Audio);
        Assert.True(options.Content.Video);
        Assert.True(options.Content.Danmaku);
        Assert.False(options.Content.Subtitle);
        Assert.False(options.Content.Cover);
        Assert.Equal(80, options.VideoQuality);
        Assert.Equal(paths.Videos, options.Directory);
    }

    [Fact]
    public void 提交策略_内容全被改残时退回全量()
    {
        var current = FreshSettings();
        var broken = current with
        {
            Video = current.Video with
            {
                Content = current.Video.Content with
                {
                    DownloadAudio = false,
                    DownloadVideo = false,
                    DownloadDanmaku = false,
                    DownloadSubtitle = false,
                    DownloadCover = false,
                },
            },
        };

        var paths = new DataPaths(
            Path.Combine(Path.GetTempPath(), "lbs-policy-test2"),
            Path.Combine(Path.GetTempPath(), "lbs-policy-test2", "videos"),
            Path.Combine(Path.GetTempPath(), "lbs-policy-test2", "downkyi"),
            Path.Combine(Path.GetTempPath(), "lbs-policy-test2", "cookies"),
            Path.Combine(Path.GetTempPath(), "lbs-policy-test2", "cache"));
        var options = LbsSubmissionProfile.ForStation(paths, broken);

        Assert.Equal(DownloadContentSelection.All, options.Content);
        Assert.Equal(127, options.VideoQuality);
    }
}
