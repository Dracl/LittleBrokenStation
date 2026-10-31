using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 分轨码率的唯一探子：只干「拿到刚落盘的文件 → 一趟 ffprobe → 落库」这一件事。
/// 起进程的活一律留在 worker 侧，StationDb 只管写——台账不许长出手去起进程。
/// 两个事件点即时探：主片到站归档后、加档扫盘定档后；事件点漏掉的存量（探子上岗前的老货、
/// 当时 ffprobe 不巧失手的行）由 <see cref="BackfillOnce"/> 补员巡回收摊，探过即盖戳永不重扰。
/// </summary>
public sealed class MediaStreamProber(StationDb db, DataPaths paths, ILogger<MediaStreamProber> logger)
{
    /// <summary>
    /// 主版本：把这一稿里「正片已落盘、还没探过」的分 P 逐个探一次，
    /// 同一趟 ffprobe 出编码名 + 双轨码率 + 盖 stream_probed 防抖戳（一个文件只扰盘一趟）。
    /// </summary>
    public void ProbeArrivedVideo(long videoId)
    {
        foreach (var (pageIdx, rel) in db.GetPagesAwaitingStreamProbe(videoId))
        {
            TryProbe($"主版本 {videoId}/P{pageIdx}", rel, info => db.SetPageStreams(
                videoId, pageIdx, info?.VideoCodec, info?.VideoKbps, info?.AudioKbps,
                width: info?.Width, height: info?.Height, fps: info?.Fps, audioCodec: info?.AudioCodec));
        }
    }

    /// <summary>
    /// 加档版本：扫盘定档后对新落盘的版本文件探一次。
    /// video_codecs.codec 存的是「点单要的哪个编码」不是实测值，不拿探测结果去改它
    /// （改了 GetArrivedCodecRel 按编码名找文件就会找错），只补双轨码率。
    /// </summary>
    public void ProbeCodecFile(long rowId, long videoId, int pageIdx, string codec)
    {
        string? rel;
        try
        {
            rel = db.GetCodecVersions(videoId).FirstOrDefault(row => row.Id == rowId).RelPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Codec} 加档 {VideoId}/P{Page} 查定档路径失手，码率本轮不探", codec, videoId, pageIdx);
            return;
        }

        if (rel is null)
        {
            return; // 定档没写出相对路径，没得探
        }

        TryProbe($"{codec} 加档 {videoId}/P{pageIdx}", rel, info =>
        {
            db.SetCodecStreams(
                rowId, info?.VideoKbps, info?.AudioKbps, probed: true, measuredCodec: info?.VideoCodec,
                width: info?.Width, height: info?.Height, fps: info?.Fps, audioCodec: info?.AudioCodec);
            // 用户最怕的就是挂羊头卖狗肉：点单 HEVC、盘上却躺着个 AVC（B 站只给了那个、或引擎自己降级），
            // 台账上 codec 仍是点单值（找文件靠它），账目不能改口，只留一条 Warning 给站长看。
            if (info?.VideoCodec is { } measured && !string.Equals(measured, codec, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "编码加档 {VideoId}/P{Page} 挂羊头卖狗肉：点单 {Ordered}，盘上是 {Measured}（{Rel}）",
                    videoId,
                    pageIdx,
                    codec,
                    measured,
                    rel);
            }
        });
    }

    /// <summary>
    /// 补员巡视：每张表最探 limit 行没盖防抖戳的存量（探子上岗前的老货、事件点失手的行），
    /// 探完照常盖 stream_probed 戳——一个文件只扰盘一趟，交回本轮实际探掉的行数。
    /// </summary>
    public (int Pages, int Codecs) BackfillOnce(int limit)
    {
        var pages = 0;
        foreach (var (videoId, pageIdx, rel) in db.GetPagesAwaitingStreamProbeBackfill(limit))
        {
            TryProbe($"补探主版本 {videoId}/P{pageIdx}", rel, info => db.SetPageStreams(
                videoId, pageIdx, info?.VideoCodec, info?.VideoKbps, info?.AudioKbps,
                width: info?.Width, height: info?.Height, fps: info?.Fps, audioCodec: info?.AudioCodec));
            pages++;
        }

        var codecs = 0;
        foreach (var (rowId, videoId, pageIdx, codec, rel) in db.GetCodecsAwaitingStreamProbeBackfill(limit))
        {
            TryProbe($"补探 {codec} 加档 {videoId}/P{pageIdx}", rel, info =>
                db.SetCodecStreams(
                    rowId, info?.VideoKbps, info?.AudioKbps, probed: true, measuredCodec: info?.VideoCodec,
                    width: info?.Width, height: info?.Height, fps: info?.Fps, audioCodec: info?.AudioCodec));
            codecs++;
        }

        return (pages, codecs);
    }

    /// <summary>
    /// 探一个文件并交回写库动作。这里不吞 ffprobe 的毛病（Ffprobe 自己把一切包成 null），
    /// 只挡写库这类环境岔子：一件出岔不该拖着同批的其它分 P 不探。
    /// </summary>
    private void TryProbe(string what, string rel, Action<Ffprobe.MediaStreamInfo?> write)
    {
        try
        {
            var full = Path.Combine(paths.Videos, rel);
            var info = Ffprobe.ProbeStreams(full);
            write(info);
            if (info is null)
            {
                logger.LogInformation("{What} 分轨码率没探到（文件缺、ffprobe 不在盘上、格式怪都算），留 null：{Rel}", what, rel);
                return;
            }

            logger.LogInformation(
                "{What} 分轨形态入库：视频 {Video} kbps / 音频 {Audio} kbps，画面 {Width}x{Height}@{Fps}，音频编码 {AudioCodec}（{Rel}）",
                what,
                info.VideoKbps?.ToString() ?? "?",
                info.AudioKbps?.ToString() ?? "?",
                info.Width?.ToString() ?? "?",
                info.Height?.ToString() ?? "?",
                info.Fps?.ToString() ?? "?",
                info.AudioCodec ?? "?",
                rel);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{What} 分轨码率探测出岔（已忽略）：{Rel}", what, rel);
        }
    }
}
