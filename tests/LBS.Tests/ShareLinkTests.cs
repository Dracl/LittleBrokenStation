using LBS.Infrastructure;
using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// 分享链接门面地址（站长室「分享链接设置」，11-15 站长点单）：TryNormalize 真值表 + settings KV 往还。
/// 端点薄皮不测（无 WebApplicationFactory 先例，见 DmTests 的说明）——校验与落库全在这里直测；
/// 门闸归属钉在 SubmitterManageTests 的两张门矩阵里（/api/settings 公开、engine 与 admin 写口守门）。
/// </summary>
public sealed class ShareLinkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public ShareLinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-sharelink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _db = new StationDb(new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir));
        _db.Initialize();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    // ── TryNormalize 真值表 ──

    [Theory]
    // 收：http/https、带端口、IP、尾斜杠剃净、首尾空白剃净、大写协议归小写
    [InlineData("http://localhost:8090", "http://localhost:8090")]
    [InlineData("https://lbs.sijii.space", "https://lbs.sijii.space")]
    [InlineData("https://lbs.sijii.space/", "https://lbs.sijii.space")]
    [InlineData("https://lbs.sijii.space///", "https://lbs.sijii.space")]
    [InlineData("  http://192.168.1.8:9000  ", "http://192.168.1.8:9000")]
    [InlineData("HTTP://LBX.Example.COM", "http://lbx.example.com")]
    [InlineData("http://[::1]:8090", "http://[::1]:8090")]
    public void 收下的门面地址归一成形(string input, string expected)
    {
        Assert.True(ShareLinkEndpoints.TryNormalize(input, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    // 拒：协议歪的、带路径/查询/锚点的、夹用户信息的、认不出的、超长
    [InlineData("ftp://lbs.example.com")]
    [InlineData("lbs.example.com")]
    [InlineData("/watch/BV1gEps62EQY")]
    [InlineData("http://lbs.example.com/watch/xx")]
    [InlineData("http://lbs.example.com?next=/admin")]
    [InlineData("http://lbs.example.com#top")]
    [InlineData("http://admin:[redacted]@lbs.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://")]
    public void 歪门面一律打回(string input)
    {
        Assert.False(ShareLinkEndpoints.TryNormalize(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void 超长门面打回()
    {
        Assert.False(ShareLinkEndpoints.TryNormalize("https://" + new string('a', 400) + ".example.com", out _, out var error));
        Assert.NotNull(error);
    }

    // ── settings KV 往还（端点写的就是这三笔，语义在此钉死） ──

    [Fact]
    public void 门面地址_存读删往还()
    {
        Assert.Null(_db.GetSetting(ShareLinkEndpoints.SettingKey));

        _db.SetSetting(ShareLinkEndpoints.SettingKey, "https://lbs.sijii.space");
        Assert.Equal("https://lbs.sijii.space", _db.GetSetting(ShareLinkEndpoints.SettingKey));

        // 改派不增行：同键覆写
        _db.SetSetting(ShareLinkEndpoints.SettingKey, "http://192.168.1.8:9000");
        Assert.Equal("http://192.168.1.8:9000", _db.GetSetting(ShareLinkEndpoints.SettingKey));

        // 恢复默认 = 删键，读回 null（前端回退跟随访问地址）
        _db.RemoveSetting(ShareLinkEndpoints.SettingKey);
        Assert.Null(_db.GetSetting(ShareLinkEndpoints.SettingKey));
    }
}
