using LBS.Engine;

namespace LBS.Tests;

/// <summary>
/// UP 头像值验货：NULL 要采、空串是失败销账不回头、热链算能用、/avatars/ 本地路径要盘上真有文件。
/// 钉住那次「头像命名换 sha1 后 video_meta.up_face 还留着 up-{mid}.jpg，UpCard 永远 404 到粉底首字」的回归。
/// </summary>
public class UpFaceHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lbs-upface-" + Guid.NewGuid().ToString("N"));

    public UpFaceHealthTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "8fb3072cf2578e29187f.jpg"), "x");
        File.WriteAllText(Path.Combine(_dir, "ok.jpg"), "x");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void 没采过_要重采() => Assert.True(LbsUpInfoFetcher.NeedsRefetch(null, _dir));

    [Fact]
    public void 失败销账的空串_不重采() => Assert.False(LbsUpInfoFetcher.NeedsRefetch("", _dir));

    [Fact]
    public void 空白也算销账_不重采() => Assert.False(LbsUpInfoFetcher.NeedsRefetch("   ", _dir));

    [Theory]
    [InlineData("https://i0.hdslb.com/bfs/face/x.jpg")]
    [InlineData("http://i0.hdslb.com/bfs/face/x.jpg")]
    public void 热链回退值_算能用(string hotlink) => Assert.False(LbsUpInfoFetcher.NeedsRefetch(hotlink, _dir));

    [Fact]
    public void 本地文件在_算能用() => Assert.False(LbsUpInfoFetcher.NeedsRefetch("/avatars/8fb3072cf2578e29187f.jpg", _dir));

    [Fact]
    public void 老命名_up_mid_文件已不在_要重采() => Assert.True(LbsUpInfoFetcher.NeedsRefetch("/avatars/up-1802654492.jpg", _dir));

    [Theory]
    [InlineData("/avatars/")]                    // 只有前缀，没文件名
    [InlineData("/avatars/../ok.jpg")]           // 想越过 avatars 目录也不放行
    [InlineData("avatars/nope.jpg")]             // 少了头斜杠的方言
    [InlineData("nope.jpg")]                     // 裸文件名
    [InlineData("/media/whatever.jpg")]          // 不是头像目录的路子
    public void 认不出或越界的值_当坏了(string raw) => Assert.True(LbsUpInfoFetcher.NeedsRefetch(raw, _dir));
}
