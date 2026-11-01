using System.Text.Json;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.Sign;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 加档前的「预估」探子：在<b>一个字节都不下</b>的前提下，替放映厅那排「点单加档」问出
/// 各编码的标称码率与大概体积，让人点单之前就知道这一档值不值得占盘。
///
/// 一手来源是 <c>x/player/wbi/playurl</c>：<c>dash.video[]</c> 每条带 <c>codecid</c>（7=AVC、12=HEVC、13=AV1）
/// 与 <c>bandwidth</c>，音轨在 <c>dash.audio[]</c>；DASH 不给现成 size，体积按
/// (视频 + 音频) × <c>timelength</c> ÷ 8 算出来 —— 所以这份叫「预估」而不是「规格」。
///
/// 三条规矩：
/// ▸ 出号走 <see cref="LbsCollectPolicy"/>（站里的活吃采集号，站长席位优先），绝不为这份行情去烧投稿人的票；
/// ▸ 这一页<b>有行情就永不重问</b>，库里空着才去拉一份（不拆保鲜期、不设冷却窗）；
/// ▸ 拿不到就说拿不到（空清单），不拿匿名的失败冒充「B 站没给」，也不留上次的行情冒充这次。
///
/// 要哪一档跟着站长室「下载设置」的现值走（<see cref="ISettingsStore"/>），因为该报的是
/// 「你点单实际会拿到的那份」，不是 B 站货架上最贵的那份。
/// </summary>
public sealed class CodecEstimateService(
    StationDb db,
    LbsQueryClient client,
    IWbiKeyProvider wbiKeys,
    TimeProvider timeProvider,
    LbsCollectPolicy policy,
    ISettingsStore settings,
    ILogger<CodecEstimateService> logger)
{
    /// <summary>
    /// 这一页还要不要出网问 B 站（站长 11-02 定的规矩）：<b>库里已有行情就不问，空着才拉</b>。
    /// 预估只是标称值，不准是常态 —— 真要准的就直接加个版本（ffprobe 实测见 video_codecs），
    /// 所以不值得为它做时间新旧、冷却窗这类判断；行情落库后就定格在那儿，直到有人清表。
    /// </summary>
    public static bool ShouldSkipFetch(int rowCount) => rowCount > 0;

    private const string PlayUrlPath = "https://api.bilibili.com/x/player/wbi/playurl";

    /// <summary>这一页的预估规格：库里有就直给，空着才出网拉一份。</summary>
    public async Task<IReadOnlyList<CodecEstimate>> GetAsync(long videoId, string bvid, int pageIdx, CancellationToken cancellationToken)
    {
        var cached = db.ListCodecEstimates(videoId, pageIdx);
        if (ShouldSkipFetch(cached.Count))
        {
            return cached;
        }

        var cid = db.GetPageCid(videoId, pageIdx);
        if (cid is null)
        {
            // playurl 按 cid 出流，台账里没 cid 说明这一页还没归档。不是错，安静给空，也别盖戳（归档后该能问到）。
            logger.LogInformation("预估跳过 videoId={VideoId} P{Page}：台账里没有 cid", videoId, pageIdx);
            return cached;
        }

        var wantQn = settings.Current.Video.Quality;
        var wantAudio = settings.Current.Video.AudioQuality;

        var (answered, items) = await policy.RunForActivityAsync(async () =>
        {
            try
            {
                var raw = await WbiRequestExecutor.ExecuteAsync(
                    wbiKeys,
                    async (keys, wts) =>
                    {
                        var paras = new Dictionary<string, object?>
                        {
                            ["bvid"] = bvid,
                            ["cid"] = cid.Value,
                            ["qn"] = wantQn,
                            ["fnval"] = 4048,
                            ["fourk"] = 1,
                            ["high_quality"] = 1,
                            ["platform"] = "web",
                            ["web_location"] = "333.999",
                        };
                        var query = WbiSign.ParametersToQuery(WbiSign.EncodeWbi(paras, keys.ImgKey, keys.SubKey, wts));
                        return await client.GetStringAsync(
                            new BilibiliHttpRequest($"{PlayUrlPath}?{query}", $"https://www.bilibili.com/video/{bvid}"),
                            cancellationToken);
                    },
                    timeProvider,
                    cancellationToken);
                var parsed = CodecEstimateParser.Parse(raw, wantQn, wantAudio);
                if (parsed.Answered && parsed.Items.Count == 0)
                {
                    // 上游答过了却一条流都没给：多半是这一号的档位不够（非大会员拿不到 HEVC/AV1）或付费稿。
                    logger.LogInformation("预估 videoId={VideoId} P{Page}：B 站答了但没给流（要 qn={Qn} 音质={Audio}）",
                        videoId, pageIdx, wantQn, wantAudio);
                }

                return parsed;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LbsNoAccountException)
            {
                // 没出去成：不盖戳（下次开面板该重试），也绝不拿这一趟去覆盖上次的行情。
                logger.LogWarning("预估 videoId={VideoId} P{Page} 没问成：{Why}", videoId, pageIdx, ex.Message);
                return new CodecEstimateParser.Outcome(false, Array.Empty<CodecEstimate>());
            }
        });

        if (!answered)
        {
            return cached;
        }

        // 现问与吃缓存两条路得给同一个顺（按编码名字母序，与库里 ListCodecEstimates 的 ORDER BY codec 一字不差）：
        // 同一个口因为碰没碰缓存就换序，迟早坑到拿它的人（前端按名取不致于中招，但没必要留这个阱）。
        var fresh = items.OrderBy(i => i.Codec, StringComparer.Ordinal).ToList();
        var now = timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        db.SaveCodecEstimates(videoId, pageIdx, fresh, now);
        return fresh;
    }
}

/// <summary>
/// playurl 响应的解析（纯函数，真样例直接喂进来测）。
///
/// <see cref="Outcome.Answered"/> 与 <see cref="Outcome.Items"/> 分开是有讲究的：
/// 「上游答过但没给货」（code 非零、非大会员、付费稿）应当盖戳别再撞，
/// 而「根本没问成」（传输层炸了、号没了）不能盖戳 —— 两者都返回空清单，但下一轮该不该重试完全不同。
/// </summary>
public static class CodecEstimateParser
{
    public sealed record Outcome(bool Answered, IReadOnlyList<CodecEstimate> Items);

    // B 站的 codecid 与站内编码名的对应：7=H.264、12=HEVC、13=AV1（与 StationDb.SupportedCodecs 三个名字一一对上）。
    private static readonly (string Name, int CodecId)[] CodecTable = [("avc", 7), ("hevc", 12), ("av1", 13)];

    public static Outcome Parse(string? raw, int wantQn, int wantAudioId)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new(false, Array.Empty<CodecEstimate>());
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return new(false, Array.Empty<CodecEstimate>());
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number)
            {
                return new(false, Array.Empty<CodecEstimate>());
            }

            if (code.GetInt32() != 0 || !root.TryGetProperty("data", out var data))
            {
                return new(true, Array.Empty<CodecEstimate>()); // 答过了，只是这一发没货
            }

            var ms = data.TryGetProperty("timelength", out var tl) && tl.TryGetInt64(out var msValue) ? msValue : 0;
            if (!data.TryGetProperty("dash", out var dash) || dash.ValueKind != JsonValueKind.Object)
            {
                return new(true, Array.Empty<CodecEstimate>()); // durl（mp4）形态或压根没流：这一手只认 dash
            }

            var videos = Enumerate(dash, "video");
            if (videos.Count == 0)
            {
                return new(true, Array.Empty<CodecEstimate>());
            }

            var audioBandwidth = PickAudio(Enumerate(dash, "audio"), wantAudioId);
            var audioKbps = audioBandwidth is long ab ? ToKbps(ab) : (int?)null;

            var items = new List<CodecEstimate>();
            foreach (var (name, codecId) in CodecTable)
            {
                var picked = PickVideo(videos, codecId, wantQn);
                if (picked is null)
                {
                    continue; // 这一档 B 站没给（非大会员最常见），就不编一个数出来
                }

                var videoBandwidth = ReadLong(picked.Value, "bandwidth");
                if (videoBandwidth is not > 0)
                {
                    continue; // 没带宽的条目什么也估不出来
                }

                long? bytes = ms > 0
                    ? (long)Math.Round((videoBandwidth.Value + (audioBandwidth ?? 0)) * ms / 8000.0)
                    : null;
                items.Add(new CodecEstimate(
                    name,
                    ToKbps(videoBandwidth.Value),
                    audioKbps,
                    bytes,
                    ReadInt(picked.Value, "id"),
                    ReadInt(picked.Value, "width"),
                    ReadInt(picked.Value, "height")));
            }

            return new(true, items);
        }
    }

    private static List<JsonElement> Enumerate(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
            : new List<JsonElement>();

    /// <summary>
    /// 一个编码取哪一条流：先要站长设的那一档；没有就往低取最高的一档（宁报低的也不报高的，
    /// 报高了是拿没到的体积吓人）；连比它低的都没有时，只能取比它高的最低一档 ——
    /// 那正是真点单会拿到的那份，实际 qn 一并记下，让前端能说清「按 1080P 估的」。
    /// </summary>
    private static JsonElement? PickVideo(List<JsonElement> videos, int codecId, int wantQn)
    {
        var mine = videos.Where(e => ReadInt(e, "codecid") == codecId).ToList();
        if (mine.Count == 0)
        {
            return null;
        }

        var exact = mine.FirstOrDefault(e => ReadInt(e, "id") == wantQn);
        if (exact.ValueKind == JsonValueKind.Object)
        {
            return exact;
        }

        var lower = mine.Where(e => (ReadInt(e, "id") ?? int.MaxValue) <= wantQn)
            .OrderByDescending(e => ReadInt(e, "id"))
            .FirstOrDefault();
        if (lower.ValueKind == JsonValueKind.Object)
        {
            return lower;
        }

        var higher = mine.Where(e => (ReadInt(e, "id") ?? 0) > wantQn)
            .OrderBy(e => ReadInt(e, "id"))
            .FirstOrDefault();
        return higher.ValueKind == JsonValueKind.Object ? higher : null;
    }

    private static long? PickAudio(List<JsonElement> audios, int wantAudioId)
    {
        if (audios.Count == 0)
        {
            return null;
        }

        var wanted = audios.FirstOrDefault(e => ReadInt(e, "id") == wantAudioId);
        if (wanted.ValueKind == JsonValueKind.Object && ReadLong(wanted, "bandwidth") is > 0)
        {
            return ReadLong(wanted, "bandwidth");
        }

        return audios.Select(e => ReadLong(e, "bandwidth") ?? 0).Max() is var best && best > 0 ? best : null;
    }

    private static int ToKbps(long bandwidth) => (int)Math.Round(bandwidth / 1000.0);

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.TryGetInt64(out var value) ? value : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.TryGetInt32(out var value) ? value : null;
}
