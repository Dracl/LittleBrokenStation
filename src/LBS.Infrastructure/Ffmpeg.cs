using System.Diagnostics;
using System.Globalization;

namespace LBS.Infrastructure;

/// <summary>
/// ffmpeg 薄封装（悬停预览成片区专用）：与 <see cref="Ffprobe"/> 同一套「外部进程 + 吞异常」哲学，
/// 但走异步——预览补员是后台长跑，不许像 Ffprobe 那样同步占着线程干等。
/// 可执行文件同样走裸名 ffmpeg / ffprobe（容器里 apt 装的，与 Ffprobe 一个口径）。
/// 全程不抛异常：出任何岔子只回一句人话原因，交给调用方记 Failed 账；站点关停的取消信号照原样上抛。
/// </summary>
public static class Ffmpeg
{
    /// <summary>站长能挑的预览清晰度（竖屏片会在转码时按实际高度收着来，不会超采样）。</summary>
    public static readonly int[] HeightChoices = [360, 480, 720];

    // 45 秒的 360p 短片在 NAS 上也用不了一分钟；给 10 分钟纯属兜底——真卡住了宁可掐掉记个失败，别把补员员拖死。
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(10);

    // ffprobe 探一次头是毫秒级的事，8 秒够宽裕（与 Ffprobe 同口径），卡住即弃。
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>探成片的宽高（只认第一条视频流）；文件不在、ffprobe 不答应、格式怪都回 null。</summary>
    public static async Task<(int Width, int Height)?> ProbeSizeAsync(string absoluteFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(absoluteFilePath))
        {
            return null;
        }

        var run = await RunAsync(
            "ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", absoluteFilePath],
            ProbeTimeout,
            cancellationToken);
        if (run is null || !run.Value.Ok)
        {
            return null;
        }

        // 输出形如 "1920,1080"；两值都要能读成整数，认不出就当没探着。
        var parts = run.Value.StdOut.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0)
        {
            return null;
        }

        return (width, height);
    }

    /// <summary>探时长（秒）；读不出正数回 null。转正前的验货靠它——半截件的时长要么读不出要么是 0。</summary>
    public static async Task<double?> ProbeDurationAsync(string absoluteFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(absoluteFilePath))
        {
            return null;
        }

        var run = await RunAsync(
            "ffprobe",
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", absoluteFilePath],
            ProbeTimeout,
            cancellationToken);
        if (run is null || !run.Value.Ok)
        {
            return null;
        }

        return double.TryParse(run.Value.StdOut, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : null;
    }

    /// <summary>
    /// 从本地成片转一段低码率无声短片：定高缩放（宽度 -2 自动取偶）、libx264 veryfast crf 30、丢音轨、moov 提到头（边下边播）。
    /// 目标高度取「站长设定的清晰度」与「片子实际高度」的较小者再向下取偶数——竖屏与低清片不许被硬拉大。
    /// 成功回 null，失败回一句原因（调用方原样记进 last_error）。
    /// </summary>
    public static async Task<string?> EncodePreviewAsync(
        string sourceFile,
        string destinationFile,
        int maxHeight,
        double seconds,
        CancellationToken cancellationToken)
    {
        var size = await ProbeSizeAsync(sourceFile, cancellationToken);
        if (size is null)
        {
            return "读不到成片分辨率（ffprobe 没答应）";
        }

        var height = Math.Min(maxHeight, size.Value.Height);
        height -= height % 2; // libx264 + scale=-2 要偶数高
        if (height < 2)
        {
            return $"成片太矮，转不出预览（实测高 {size.Value.Height}）";
        }

        var run = await RunAsync(
            "ffmpeg",
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-i", sourceFile,
                "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-vf", $"scale=-2:{height}",
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "30",
                "-pix_fmt", "yuv420p",
                "-an",
                "-movflags", "+faststart",
                // 临时件叫 <bvid>.mp4.part，ffmpeg 认不出这种后缀（Unable to choose an output format），
                // 所以封装格式得嘴上说清楚：不靠文件后缀猜。
                "-f", "mp4",
                destinationFile,
            ],
            EncodeTimeout,
            cancellationToken);
        if (run is null)
        {
            return "ffmpeg 没答应";
        }

        return run.Value.Ok
            ? null
            : Truncate(run.Value.Error.Length > 0 ? run.Value.Error : $"ffmpeg 退出码 {run.Value.ExitCode}");
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";

    // libwebp 在不在，开站探一次就定下来：Debian 的 ffmpeg 大多带，但不是保证，不能每次切片都去跑一遍 -encoders。
    // 三级取价：null = 还没探过；探不出来一律当没带，降级 jpg，不静默。
    private static bool? webpEncoderReady;
    private static readonly SemaphoreSlim WebpProbeGate = new(1, 1);

    /// <summary>
    /// 探一跟 ffmpeg 到底能不能编 webp（<see cref="CoverThumbTiers"/> 那套缩略件靠它）。
    /// 结果缓存在进程里，探失败也当「没带」定下来，免得整站切片反复去跑子进程。
    /// </summary>
    public static async Task<bool> HasWebpEncoderAsync(CancellationToken cancellationToken)
    {
        if (webpEncoderReady is { } cached)
        {
            return cached;
        }

        await WebpProbeGate.WaitAsync(cancellationToken);
        try
        {
            if (webpEncoderReady is { } again)
            {
                return again;
            }

            var run = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-encoders"], ProbeTimeout, cancellationToken);
            // 进程起不来（ffmpeg 没装）也回 false：jpg 那一路用的是 mjpeg，ffmpeg 自带，还能切得动。
            webpEncoderReady = run is { Ok: true } && run.Value.StdOut.Contains("libwebp", StringComparison.OrdinalIgnoreCase);
            return webpEncoderReady.Value;
        }
        finally
        {
            WebpProbeGate.Release();
        }
    }

    /// <summary>
    /// 从本地封面原图切一枚指定尺幅的缩略件：先等比放大到铺满目标框，再居中裁到正好——与 CSS
    /// <c>object-cover</c> 同一种裁法，所以卡片上看到的构图与浏览器自己裁时一致。
    /// 目标格式 webp（带 libwebp 时）或 jpg；临时件的名字就带着真后缀，所以不用像预览那样嘴上补 <c>-f</c>。
    /// 成功回 null，失败回一句人话原因（调用方原样记进 last_error）。
    /// </summary>
    public static async Task<string?> EncodeCoverTileAsync(
        string sourceFile,
        string destinationFile,
        int width,
        int height,
        string format,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceFile))
        {
            return "封面原图不在盘上，切不了缩略";
        }

        var codec = new List<string>();
        if (string.Equals(format, CoverThumbTiers.FormatWebp, StringComparison.OrdinalIgnoreCase))
        {
            codec.Add("-c:v");
            codec.Add("libwebp");
            codec.Add("-quality");
            codec.Add("80"); // 与浏览器侧 IndexedDB 那套的 0.82 同一档观感
        }
        else
        {
            codec.Add("-c:v");
            codec.Add("mjpeg");
            codec.Add("-q:v");
            codec.Add("4"); // mjpeg 的 q 是 2(好)~31(差)，4 相当于 webp q80 那一档
        }

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", sourceFile,
            "-frames:v", "1",
            "-vf", $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}",
        };
        args.AddRange(codec);
        args.Add(destinationFile);

        var run = await RunAsync("ffmpeg", args, TimeSpan.FromSeconds(60), cancellationToken);
        if (run is null)
        {
            return "ffmpeg 没答应";
        }

        return run.Value.Ok
            ? null
            : Truncate(run.Value.Error.Length > 0 ? run.Value.Error : $"ffmpeg 退出码 {run.Value.ExitCode}");
    }

    /// <summary>
    /// 跑一趟外部进程：输出全部收进内存（-loglevel error 下没几行），超时整树掐掉。
    /// 回 null 表示进程根本没起来（ffmpeg 不在 PATH 之类）；取消信号原样上抛，不吞站点关停。
    /// </summary>
    private static async Task<(bool Ok, int ExitCode, string StdOut, string Error)?> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument); // 逐项加参，绝不拼字符串：路径里有空格与中文都不怕
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // ffmpeg 没装、不给起：一句 null 打发，让调用方记失败
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    KillQuietly(process);
                    await DrainQuietly(stdout, stderr);
                    return (false, -1, string.Empty, $"{fileName} 超过 {(int)timeout.TotalSeconds} 秒没交差，已终止");
                }

                var output = await stdout;
                var errors = await stderr;
                return process.ExitCode == 0
                    ? (true, 0, output.Trim(), string.Empty)
                    : (false, process.ExitCode, output.Trim(), errors.Trim());
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                await DrainQuietly(stdout, stderr);
                throw; // 站点关停：该往上抛就得抛，别把它伪装成「转码失败」
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                KillQuietly(process);
                await DrainQuietly(stdout, stderr);
                return (false, -1, string.Empty, $"{fileName} 没跑利索：{ex.Message}");
            }
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // 已经退了或掐不动：无所谓，等它的命
        }
    }

    /// <summary>掐完把管道读干净：不读的话读流任务会挂着，异常也没人收。</summary>
    private static async Task DrainQuietly(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr);
        }
        catch
        {
            // 收尾阶段的读取毛病不影响结论
        }
    }
}
