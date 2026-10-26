using System.Diagnostics;

namespace LBS.Infrastructure;

/// <summary>
/// ffprobe 薄封装：档案只关心一件事——视频流的真实编码。
/// 返回小写 'avc' / 'hevc' / 'av1'；探测不到（文件缺失、ffprobe 不在 PATH、格式怪）返回 null，
/// 调用方按"未知"降级显示，绝不猜。
/// </summary>
public static class Ffprobe
{
    public static string? ProbeVideoCodec(string absoluteFilePath)
    {
        if (!File.Exists(absoluteFilePath))
        {
            return null;
        }

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
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("v:0");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=codec_name");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(absoluteFilePath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var name = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 已经退出，无所谓
                }

                return null;
            }

            return name.ToLowerInvariant() switch
            {
                "h264" or "avc1" => "avc",
                "hevc" or "hvc1" or "hev1" => "hevc",
                "av1" or "av01" => "av1",
                _ => null,
            };
        }
        catch (Exception)
        {
            // ffprobe 缺失或任何环境毛病都不该拖垮观看页
            return null;
        }
    }
}
