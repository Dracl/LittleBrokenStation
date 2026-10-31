using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace LBS.Infrastructure;

/// <summary>
/// ffprobe 薄封装：档案只关心一件事——文件里音视频两路的真实形态。
/// 编码名返回小写 'avc' / 'hevc' / 'av1'；码率取 bit_rate 折算 kbps（四舍五入）。
/// 探测不到（文件缺失、ffprobe 不在 PATH、格式怪、超时）一律返回 null，
/// 调用方按"未知"降级显示，绝不猜。
/// </summary>
public static class Ffprobe
{
    // ffprobe 探一次头是毫秒级的事，8 秒够宽裕（与 Ffmpeg 同口径），卡住即弃。
    private const int ProbeTimeoutMs = 8000;

    /// <summary>
    /// 一路 ffprobe 探回来的档案：视频编码名（归一成 'avc'/'hevc'/'av1'，认不出给 null）、
    /// 音视频码率（kbps）、画面宽高与平均帧率（给清晰度菜单判 60帧 档用），以及音频编码名
    /// （归一成 'flac'/'aac'/'eac3'/'ac3'/'opus'，白名单外给 null——给 Hi-Res 徽记当判据，不认的不猜）。
    /// 没有这条流、或容器压根不报该项，那一项就是 null——不给 0 冒充静音/零码率/零帧率。
    /// </summary>
    public sealed record MediaStreamInfo(
        string? VideoCodec,
        int? VideoKbps,
        int? AudioKbps,
        int? Width = null,
        int? Height = null,
        double? Fps = null,
        string? AudioCodec = null);

    /// <summary>
    /// 只问编码名：返回 'avc' / 'hevc' / 'av1'，探测不到给 null。
    /// 走的是 <see cref="ProbeStreams"/> 同一次 ffprobe（不重复起进程），语义与老版一字不差。
    /// </summary>
    public static string? ProbeVideoCodec(string absoluteFilePath) => ProbeStreams(absoluteFilePath)?.VideoCodec;

    /// <summary>
    /// 一次 ffprobe 探清 v:0 / a:0 两路（取到的第一条视频流即 v:0，第一条音频流即 a:0）。
    /// 任何异常、文件缺失、超时都返回 null，绝不拖垮调用方。
    /// </summary>
    public static MediaStreamInfo? ProbeStreams(string absoluteFilePath)
    {
        if (!File.Exists(absoluteFilePath))
        {
            return null;
        }

        try
        {
            var output = RunProbe(absoluteFilePath);
            if (output is null)
            {
                return null;
            }

            // 不选流、全量拿 stream 再由代码挑头一条视频/头一条音频：
            // -select_streams 只认一个串（v:0,a:0 这种写法在 Windows 版 ffprobe 上直接报 Invalid stream specifier），
            // 传两遍后者又覆盖前者，索性让 ffprobe 少干活、代码多担待。
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? videoCodec = null;
            int? videoKbps = null;
            int? audioKbps = null;
            int? width = null;
            int? height = null;
            double? fps = null;
            string? audioCodec = null;
            var seenVideo = false;
            var seenAudio = false;
            foreach (var stream in streams.EnumerateArray())
            {
                switch (Field(stream, "codec_type"))
                {
                    case "video" when !seenVideo:
                        seenVideo = true;
                        videoCodec = NormalizeCodec(Field(stream, "codec_name"));
                        videoKbps = ToKbps(Field(stream, "bit_rate"));
                        width = ToPositiveInt(Field(stream, "width") ?? NumField(stream, "width"));
                        height = ToPositiveInt(Field(stream, "height") ?? NumField(stream, "height"));
                        fps = ToFps(Field(stream, "avg_frame_rate"));
                        break;
                    case "audio" when !seenAudio:
                        seenAudio = true;
                        audioKbps = ToKbps(Field(stream, "bit_rate"));
                        audioCodec = NormalizeAudioCodec(Field(stream, "codec_name"));
                        break;
                }

                if (seenVideo && seenAudio)
                {
                    break;
                }
            }

            return new MediaStreamInfo(videoCodec, videoKbps, audioKbps, width, height, fps, audioCodec);
        }
        catch (Exception)
        {
            // ffprobe 缺失或任何环境毛病都不该拖垮观看页
            return null;
        }
    }

    /// <summary>跑一趟 ffprobe 交回 stdout；超时/起不动进程给 null。</summary>
    private static string? RunProbe(string absoluteFilePath)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=codec_type,codec_name,bit_rate,width,height,avg_frame_rate");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(absoluteFilePath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            // 两路管道都得排空，而且要在超时之前读：先 ReadToEnd 再 WaitForExit(8s) 的写法其实拿不到超时保护
            // ——ffprobe 卡住不吐字节时，ReadToEnd 自己就永远不返回，8 秒形同虚设；stderr 只重定向不读，
            // 碰上它抱怨几十 KB 就会把管道塞满、对端写阻塞 → 进程不退出，探测静默变 null 还查不到根因。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!Task.WhenAll(stdout, stderr).Wait(ProbeTimeoutMs))
            {
                KillProbe(process);
                return null;
            }

            var output = stdout.Result.Trim();
            if (!process.WaitForExit(ProbeTimeoutMs))
            {
                KillProbe(process);
                return null;
            }

            return output;
        }
        catch (Exception)
        {
            // ffprobe 不在 PATH（容器没装 / 用户自己删了）也是常态，安静降级
            return null;
        }
    }

    /// <summary>探到点不返就掀桌：连子进程树一起端，ffprobe 卡死也不给分站留孤儿进程。</summary>
    private static void KillProbe(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 已经退出，无所谓
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 进程刚好自己走人了，抢着杀空手而归也算干净
        }
    }

    /// <summary>JSON 字段取值：键不存在或不是字符串给 null（ffprobe 对探不到的项直接省略键）。</summary>
    private static string? Field(JsonElement stream, string name) =>
        stream.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>数字字段取值：ffprobe 把 width/height 报成 JSON 数字而非字符串，Field 那条吃不下它。</summary>
    private static string? NumField(JsonElement stream, string name) =>
        stream.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;

    /// <summary>宽/高取正整数；不是数、为 0 或负数都算没有（容器报了个寂寞也不留）。</summary>
    private static int? ToPositiveInt(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return null;
        }

        return n;
    }

    /// <summary>avg_frame_rate 是「60000/1001」这种有理数串：折成小数；0/0、非数、离谱帧率（&lt;1 或 &gt;240）都算没有。</summary>
    private static double? ToFps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var slash = raw.IndexOf('/');
        var numText = slash < 0 ? raw : raw[..slash];
        var denText = slash < 0 ? "1" : raw[(slash + 1)..];
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) ||
            !double.TryParse(denText, NumberStyles.Float, CultureInfo.InvariantCulture, out var den) ||
            den <= 0)
        {
            return null;
        }

        var fps = num / den;
        return fps is > 1 and < 240 ? Math.Round(fps, 3) : null;
    }

    /// <summary>音频编码只认 B 站 DASH 会给的几家，白名单外一律 null：徽记判据宁缺毋滥，认不出的不猜。</summary>
    private static string? NormalizeAudioCodec(string? name) => name?.ToLowerInvariant() switch
    {
        "flac" => "flac",
        "aac" => "aac",
        "eac3" or "ec-3" => "eac3",
        "ac3" or "ac-3" => "ac3",
        "opus" => "opus",
        _ => null,
    };

    private static string? NormalizeCodec(string? name) => name?.ToLowerInvariant() switch
    {
        "h264" or "avc1" => "avc",
        "hevc" or "hvc1" or "hev1" => "hevc",
        "av1" or "av01" => "av1",
        _ => null,
    };

    /// <summary>bit_rate（比特/秒）折 kbps，四舍五入；探不到、为 0 或离谱到装不进 int 都算没有。</summary>
    private static int? ToKbps(string? bitRate)
    {
        if (!long.TryParse(bitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) || bits <= 0)
        {
            return null;
        }

        var kbps = (long)Math.Round(bits / 1000d, MidpointRounding.AwayFromZero);
        return kbps is > 0 and <= int.MaxValue ? (int)kbps : null;
    }
}
