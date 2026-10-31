using System.Diagnostics;

namespace LBS.Tests;

/// <summary>
/// 验「真探到编码与码率」就得有真件可探：这里用 ffmpeg 现造 2 秒的 h264+aac 样本。
/// 分站本来就依赖容器里的 ffmpeg/ffprobe（见 Ffmpeg.cs 口径），但开发机与 CI 未必装了——
/// 没装时调用方直接判「这例没东西可验」放过，不许把缺外部工具算成代码的错。
/// </summary>
internal static class MediaSample
{
    /// <summary>ffmpeg 与 ffprobe 都在 PATH 里才算有工具。</summary>
    public static bool HasTools() => HasTool("ffmpeg") && HasTool("ffprobe");

    public static bool HasTool(string name)
    {
        try
        {
            return Run(name, ["-version"]) == 0;
        }
        catch (Exception)
        {
            return false; // 不在 PATH
        }
    }

    /// <summary>造一件带视频 + 音频两轨的真件（小分辨率 testsrc + 440Hz 正弦）。</summary>
    public static bool MakeVideo(string path) =>
        Run("ffmpeg",
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=size=128x72:rate=10:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "libx264", "-b:v", "300k", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "64k", path,
        ]) == 0
        && File.Exists(path);

    /// <summary>造一件纯音频件（验「没有视频轨时视频那几列留 null」）。</summary>
    public static bool MakeAudio(string path) =>
        Run("ffmpeg",
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:a", "aac", "-b:a", "48k", path,
        ]) == 0
        && File.Exists(path);

    /// <summary>造一件纯 flac 音频件（Hi-Res 徽记的硬判据：音频编码得认成 flac；mka 壳子不改 codec_name）。</summary>
    public static bool MakeAudioFlac(string path) =>
        Run("ffmpeg",
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:a", "flac", path,
        ]) == 0
        && File.Exists(path);

    private static int Run(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"起不动 {fileName}");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return process.ExitCode;
    }
}
