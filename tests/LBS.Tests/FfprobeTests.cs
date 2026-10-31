using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>
/// ffprobe 封装的真件验尸：造一件真视频（ffmpeg 生成）再探，验编码名与分轨码率同趟探回。
/// 机器上没装 ffmpeg/ffprobe 时整例直接放过——这封装的本分就是「探不到一律 null」的降级哲学，
/// 缺外部工具不该把测试拖红（观看页同样不会因为没装 ffprobe 就崩）。
/// </summary>
public sealed class FfprobeTests
{
    [Fact]
    public void 真件_编码名与分轨码率同趟探回()
    {
        if (!MediaSample.HasTools())
        {
            return; // 没外部工具：下面这些「探得到」的断言无从谈起，降级路径另例照验
        }

        var dir = Path.Combine(Path.GetTempPath(), "lbs-ffprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "real.mp4");
            Assert.True(MediaSample.MakeVideo(video), "ffmpeg 没造出样本件");

            var info = Ffprobe.ProbeStreams(video);
            Assert.NotNull(info);
            Assert.Equal("avc", info!.VideoCodec);
            Assert.NotNull(info.VideoKbps);
            Assert.NotNull(info.AudioKbps);
            // 形态同趟探回（清晰度菜单与 Hi-Res 徽记的判据口径）：样本件就是 128x72@10 + aac。
            Assert.Equal(128, info.Width);
            Assert.Equal(72, info.Height);
            Assert.NotNull(info.Fps);
            Assert.InRange(info.Fps!.Value, 9.5, 10.5);
            Assert.Equal("aac", info.AudioCodec);
            // 小分辨率样片的 x264 会大幅跑不满目标码率，只验「探得到、量级不离谱」。
            Assert.InRange(info.VideoKbps!.Value, 1, 2_000);
            Assert.InRange(info.AudioKbps!.Value, 20, 300);

            // 薄封装与 ProbeStreams 同一趟探测、同一个口径
            Assert.Equal("avc", Ffprobe.ProbeVideoCodec(video));

            // 纯音频件：视频两列 null（不给 0 冒充），音频列照给。
            var audio = Path.Combine(dir, "audio.m4a");
            Assert.True(MediaSample.MakeAudio(audio), "ffmpeg 没造出纯音频件");
            var audioInfo = Ffprobe.ProbeStreams(audio);
            Assert.NotNull(audioInfo);
            Assert.Null(audioInfo!.VideoCodec);
            Assert.Null(audioInfo.VideoKbps);
            Assert.NotNull(audioInfo.AudioKbps);
            Assert.Null(audioInfo.Width);
            Assert.Null(audioInfo.Height);
            Assert.Null(audioInfo.Fps);
            Assert.Equal("aac", audioInfo.AudioCodec);

            // flac 样本（Hi-Res 徽记的唯一硬判据）：音频编码认成 flac 就照报，白名单外的才会被哑成 null。
            var flac = Path.Combine(dir, "audio.mka");
            Assert.True(MediaSample.MakeAudioFlac(flac), "ffmpeg 没造出 flac 音频样本件");
            var flacInfo = Ffprobe.ProbeStreams(flac);
            Assert.NotNull(flacInfo);
            Assert.Equal("flac", flacInfo!.AudioCodec);

            // 假文件与不存在的文件：整体 null，绝不半截档案、也绝不让异常漏到调用方。
            var junk = Path.Combine(dir, "junk.mp4");
            File.WriteAllText(junk, "这不是视频");
            Assert.Null(Ffprobe.ProbeStreams(junk));
            Assert.Null(Ffprobe.ProbeVideoCodec(junk));
            Assert.Null(Ffprobe.ProbeStreams(Path.Combine(dir, "nope.mp4")));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响结论
            }
        }
    }
}
