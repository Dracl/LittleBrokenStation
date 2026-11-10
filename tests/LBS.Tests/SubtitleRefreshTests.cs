using DownKyi.Core.BiliApi.Models.Json;
using LBS.Infrastructure;
using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// 「重拉字幕存档」的落盘判据。拉取那截吃 B 站的脸色，钉不进 CI；
/// 但命名、撞车、默认副本择优、srt 验货这四道判据错一毫，扫盘台账就乱套——
/// 轨文件名对不上形就被当无名轨甚至不认轨，所以照弹幕链的旧例钉判据本身。
/// 命名契约两头对着钉：重拉侧拼名（SubtitleArchiveService）→ 扫盘侧认名（StationDb.ParseSubtitleTrackLabel）。
/// </summary>
public sealed class SubtitleRefreshTests
{
    private const string GoodSrt = "1\n00:00:01,000 --> 00:00:02,000\n你好\n";

    private static SubRipText Sub(long trackId, string lan, string lanDoc, string srt = GoodSrt) =>
        new() { TrackId = trackId, Lan = lan, LanDoc = lanDoc, SrtString = srt };

    [Fact]
    public void 同语言撞车_两枚中文轨都算数_AI与人工同视之()
    {
        // B 站同一片常见 zh-CN 与 ai-zh 都标「中文」：不并成撞车组，两轨会写进同一个文件名互踩。
        var subs = new[] { Sub(1, "zh-CN", "中文"), Sub(2, "ai-zh", "中文"), Sub(3, "en-US", "English") };
        var dup = SubtitleArchiveService.DuplicateLanguages(subs);
        Assert.Contains("中文", dup);
        Assert.DoesNotContain("English", dup);
    }

    [Fact]
    public void 同语言名撞车_大小写不同也并组_查哪副面孔都命中()
    {
        var subs = new[] { Sub(1, "en-US", "English"), Sub(2, "en-US", "ENGLISH") };
        var dup = SubtitleArchiveService.DuplicateLanguages(subs);
        // 忽略大小写并成一族：组只留一枚钥匙，但两副面孔去查都得着（拼名处的 Contains 同口径）。
        Assert.Single(dup);
        Assert.Contains("English", dup);
        Assert.Contains("ENGLISH", dup);
    }

    [Fact]
    public void 撞车追尾拼名_扫盘认名剥得回真名()
    {
        // 契约闭环：重拉侧按 DownloadArtifactWriter 的形状拼 {base}_{lan_doc}[_{trackId}].srt，
        // 扫盘侧 ParseSubtitleTrackLabel 必须剥回「中文」——两头一张嘴，面板才摆得出真名。
        var subs = new[] { Sub(101, "zh-CN", "中文"), Sub(202, "ai-zh", "中文"), Sub(303, "en-US", "English") };
        var dup = SubtitleArchiveService.DuplicateLanguages(subs);
        var suffixes = subs
            .Select(s => dup.Contains(s.LanDoc) ? $"_{s.LanDoc}_{s.TrackId}.srt" : $"_{s.LanDoc}.srt")
            .ToList();
        Assert.Equal(new[] { "_中文_101.srt", "_中文_202.srt", "_English.srt" }, suffixes);
        Assert.Equal(new[] { "中文", "中文", "English" }, suffixes.Select(StationDb.ParseSubtitleTrackLabel).ToArray());
    }

    [Fact]
    public void 默认副本_人工中文优先_再中文名_再AI中文_再第一轨()
    {
        var humanZh = Sub(1, "zh-CN", "中文");
        var aiZh = Sub(2, "ai-zh", "中文");
        var en = Sub(3, "en-US", "English");
        var jp = Sub(4, "ja-JP", "日本語");

        // 人工中文压过一切：外部播放器开片即有的那枚字，最好是没人机味的。
        Assert.Same(humanZh, SubtitleArchiveService.PickDefaultTrack([en, aiZh, humanZh]));
        Assert.Same(aiZh, SubtitleArchiveService.PickDefaultTrack([en, aiZh]));
        Assert.Same(en, SubtitleArchiveService.PickDefaultTrack([en, jp]));
        Assert.Null(SubtitleArchiveService.PickDefaultTrack([]));

        // lan 不中用（非 zh 前缀）时退吃 lan_doc 里的「中文」字样。
        var zhDocOddLan = Sub(5, "xx-XX", "中文");
        Assert.Same(zhDocOddLan, SubtitleArchiveService.PickDefaultTrack([en, zhDocOddLan]));
    }

    [Fact]
    public void srt验货_空货半截货不许盖旧轨()
    {
        Assert.True(SubtitleArchiveService.IsUsableSrt(GoodSrt));
        Assert.False(SubtitleArchiveService.IsUsableSrt(""));
        Assert.False(SubtitleArchiveService.IsUsableSrt("   "));
        Assert.False(SubtitleArchiveService.IsUsableSrt("1\n没有箭头的半截货"));
    }

    [Fact]
    public void 轨名消毒_非法字符换下划线_首尾空白剃净()
    {
        // lan_doc 是给面板摆的名字，理论上是干净词；文件名不赌它，非法字符一律换掉别炸写盘。
        Assert.Equal("中文AI", SubtitleArchiveService.SanitizeFileName(" 中文AI "));
        Assert.Equal("a_b", SubtitleArchiveService.SanitizeFileName("a/b"));
        Assert.Equal("x_y", SubtitleArchiveService.SanitizeFileName("x:y"));
    }
}
