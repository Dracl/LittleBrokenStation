using System.Collections.Concurrent;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>字幕重拉结果：Tracks 是这次落盘的轨文件数（不含默认副本），Pages 是落盘的分 P 数。</summary>
public sealed record SubtitleArchiveOutcome(bool Ok, string Message, int Tracks, int Pages);

/// <summary>
/// 稿件设置窗那枚「重拉字幕存档」的后端半边：回 B 站把这份稿件的字幕轨全拉一遍。
/// 为什么需要它：引擎下载时被当年的语言偏好筛剩过轨（BV1Q44v6RE2V 三轨只进站一条的病灶），
/// 偏好闸 11-11 已拆，但老稿盘上缺的轨救不回来——以前只能销档重下，这枚钮把它变成一键回拉。
/// 链路与「重拉弹幕快照」同族：按片闸防双开 → 三连校验 → 挂采集号出网 → 原子写盘 → 重扫登账。
/// vendor 零改动：<see cref="VideoStreamApi"/> 的 GetSubtitleAsync 一条链（PlayerV2 → subtitle_url →
/// 下载 JSON → ToSubRip）吐成品 srt 字符串；不传偏好与勾选 = 接口回几轨拉几轨。
/// 落盘命名与引擎写盘同形（{base}_{lan_doc}.srt，同语言撞车追 _{trackId}，另备无名默认副本）——
/// 扫盘认轨（ClassifyArtifact/ParseSubtitleTrackLabel）就是照这个形状认账的，写完重扫自动登账落真名。
/// 语义是整档刷新不是只补缺：同名覆盖内容同步，缺的补齐；B 站空手而归时盘上一个字都不动。
/// </summary>
public sealed class SubtitleArchiveService(
    StationDb db,
    DataPaths paths,
    LbsQueryClient client,
    LbsCollectPolicy policy,
    IWbiKeyProvider wbiKeys,
    TimeProvider timeProvider,
    ILogger<SubtitleArchiveService> logger)
{
    /// <summary>按片在途闸（DanmakuArchiveService.refreshing 同款）：双开会两拨各拉各写，
    /// 同秒撞名互踩；重拉本身是整档覆盖，后写盖先写虽不丢数据也白费一趟网。</summary>
    private static readonly ConcurrentDictionary<long, byte> refreshing = new();

    /// <summary>重拉一条馆藏的全部分 P 字幕；B 站空手、缺 cid、被占用都不算炸，只是回报里说清楚。</summary>
    public async Task<SubtitleArchiveOutcome> TryRefreshAsync(long videoId, CancellationToken cancellationToken)
    {
        if (!refreshing.TryAdd(videoId, 0))
        {
            return new SubtitleArchiveOutcome(false, "这条已有字幕重拉在跑，稍等片刻再点", 0, 0);
        }

        try
        {
            var brief = db.GetVideoBrief(videoId);
            if (brief is null)
            {
                return new SubtitleArchiveOutcome(false, "馆藏里没有这个 id", 0, 0);
            }

            if (brief.Value.Status != VideoStatus.Arrived)
            {
                return new SubtitleArchiveOutcome(false, "这条还没到站，等成片落盘再拉字幕", 0, 0);
            }

            if (brief.Value.Aid is not { } aid || aid <= 0)
            {
                return new SubtitleArchiveOutcome(false, "还没有 av 号，先补采一次评论（它会顺带把 av 号解出来）", 0, 0);
            }

            // 分 P 家底与弹幕链共用一口查询：字幕要的也就是 cid 与基名这两样。
            var targets = db.GetDanmakuPageTargets(videoId);
            if (targets.Count == 0)
            {
                return new SubtitleArchiveOutcome(false, "还没有分 P 记录，字幕无处安放", 0, 0);
            }

            var (tracks, pages, empty, noCid, locked) = await policy.RunForVideoAsync(
                videoId,
                () => PullAsync(aid, brief.Value.Bvid, targets, cancellationToken));

            if (pages == 0)
            {
                var why = empty > 0
                    ? "B 站这边空手而归（多半是风控或这条真没字幕），盘上老件原样未动"
                    : noCid > 0
                        ? "分 P 缺少 cid，B 站没法按它取字幕"
                        : locked > 0
                            ? "字幕文件正被播放器占着，等它松手再试"
                            : "B 站这次没有东西可落";
                return new SubtitleArchiveOutcome(false, $"「{brief.Value.Bvid}」字幕没刷动：{why}", 0, 0);
            }

            // 重扫登账：新落盘的轨文件在这里进 artifacts 台账并解析轨真名（label），面板即刻认得出新轨。
            db.FinalizeArtifacts(videoId, paths.Videos);
            logger.LogInformation(
                "馆藏 {VideoId} 字幕存档重拉：{Tracks} 轨落盘，{Pages} 个分 P（空手 {Empty}，缺 cid {NoCid}，占用 {Locked}）",
                videoId, tracks, pages, empty, noCid, locked);
            var tail = empty > 0 || noCid > 0 || locked > 0
                ? $"（{empty} 个分 P 空手，{noCid} 个缺 cid，{locked} 件被占用）"
                : "";
            return new SubtitleArchiveOutcome(
                true,
                $"「{brief.Value.Bvid}」字幕存档已重拉：{tracks} 轨落盘，覆盖 {pages} 个分 P{tail}",
                tracks,
                pages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "馆藏 {VideoId} 字幕重拉炸了（不影响馆藏）", videoId);
            return new SubtitleArchiveOutcome(false, "字幕重拉出错，分站日志里有详情", 0, 0);
        }
        finally
        {
            refreshing.TryRemove(videoId, out _);
        }
    }

    private async Task<(int Tracks, int Pages, int Empty, int NoCid, int Locked)> PullAsync(
        long aid,
        string bvid,
        IReadOnlyList<(int Idx, long? Cid, string BasePath)> targets,
        CancellationToken cancellationToken)
    {
        var tracks = 0;
        var pages = 0;
        var empty = 0;
        var noCid = 0;
        var locked = 0;

        foreach (var (_, cid, basePath) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cid is not { } cidValue || cidValue <= 0)
            {
                noCid++;
                continue;
            }

            // 不传 preference 也不传勾选单 = 接口回几轨拉几轨（当年就是这两枚筛子把多轨筛剩一条的）。
            var subs = await WbiRequestExecutor.ExecuteAsync(
                wbiKeys,
                (keys, unixTimeSeconds) => client.GetSubtitleAsync(keys, unixTimeSeconds, aid, bvid, cidValue, cancellationToken),
                timeProvider,
                cancellationToken);

            if (subs is not { Count: > 0 })
            {
                // B 站抽风/风控/真没字幕：盘上一个字都不动，旧轨旧账原样留着。
                empty++;
                continue;
            }

            var dupLangs = DuplicateLanguages(subs);
            var usable = subs.Where(s => IsUsableSrt(s.SrtString)).ToList();
            var pageTracks = 0;
            foreach (var sub in usable)
            {
                var label = dupLangs.Contains(sub.LanDoc) ? $"{sub.LanDoc}_{sub.TrackId}" : sub.LanDoc;
                var target = $"{basePath}_{SanitizeFileName(label)}.srt";
                if (!AtomicTextFile.TryWrite(target, sub.SrtString))
                {
                    locked++;
                    continue;
                }

                pageTracks++;
            }

            if (pageTracks == 0)
            {
                // 全被占用或全是坏货：这一 P 没落成一轨，不算落盘页；占用数已在 locked 里报过。
                continue;
            }

            // 无名默认副本 {base}.srt：引擎下载时也是轨文件与副本各写一份（外部播放器自动挂载吃它）。
            // 挑轨见 PickDefaultTrack；刷不动不算这一 P 的败事——面板吃的是带名轨，副本是给外人顺带用的。
            var preferred = PickDefaultTrack(usable);
            if (preferred is not null)
            {
                AtomicTextFile.TryWrite(basePath + ".srt", preferred.SrtString);
            }

            tracks += pageTracks;
            pages++;
        }

        return (tracks, pages, empty, noCid, locked);
    }

    /// <summary>
    /// 同语言撞车判据（vendor DownloadArtifactWriter 同源）：lan_doc 分组数 &gt; 1 的那些语言，
    /// 文件名要追 _{trackId}，不然两轨互踩只剩一枚。
    /// </summary>
    internal static HashSet<string> DuplicateLanguages(IReadOnlyList<SubRipText> subs) =>
        subs.GroupBy(s => s.LanDoc ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 默认副本挑哪一轨：人工中文（lan 形如 zh-CN）→ 名字里带「中文」的（含 AI 中文）→ ai-zh → 第一轨。
    /// 与引擎当年「人工优先」的口味一致：外部播放器开片即有的那枚字，最好是没人机味的。
    /// </summary>
    internal static SubRipText? PickDefaultTrack(IReadOnlyList<SubRipText> usable)
    {
        if (usable.Count == 0)
        {
            return null;
        }

        return usable.FirstOrDefault(s => Norm(s.Lan).StartsWith("zh", StringComparison.Ordinal))
            ?? usable.FirstOrDefault(s => Norm(s.LanDoc).StartsWith("中文", StringComparison.Ordinal))
            ?? usable.FirstOrDefault(s => Norm(s.Lan).StartsWith("ai-zh", StringComparison.Ordinal))
            ?? usable[0];
    }

    /// <summary>srt 验货：时间戳箭头必须在——半截货/空货不许盖上盘上的旧轨。</summary>
    internal static bool IsUsableSrt(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains(" --> ", StringComparison.Ordinal);

    /// <summary>lan_doc 是给面板摆的名字，理论上是干净词，但文件名不赌它：非法字符一律换成下划线。</summary>
    internal static string SanitizeFileName(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = label.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();
}
