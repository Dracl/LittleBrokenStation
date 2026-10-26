using LBS.Domain;

namespace LBS.Tests;

public class BilibiliLinkTests
{
    [Theory]
    [InlineData("bilibili.com", true)]
    [InlineData("www.bilibili.com", true)]
    [InlineData("m.bilibili.com", true)]
    [InlineData("space.bilibili.com", true)]
    [InlineData("WWW.BILIBILI.COM", true)] // 大小写不敏感
    [InlineData("b23.tv", true)]
    [InlineData("example.com", false)]
    [InlineData("bilibili.com.evil.net", false)]
    [InlineData("notbilibili.com", false)]
    public void 域名白名单(string host, bool expected)
    {
        Assert.Equal(expected, BilibiliLink.IsBilibiliHost(host));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7", "BV1GJ411x7h7")]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7?p=2&spm_id_from=333", "BV1GJ411x7h7")]
    [InlineData("看看这个 https://b23.tv/x BV1qt411w7BE 超好看", "BV1qt411w7BE")]
    public void 能提取BV号(string text, string expected)
    {
        Assert.True(BilibiliLink.TryExtractBvid(text, out var bvid));
        Assert.Equal(expected, bvid);
    }

    [Theory]
    [InlineData("BV1短")]
    [InlineData("BV2GJ411x7h7")]  // 前缀必须 BV1
    [InlineData("BV1GJ411x7h")]   // 少一位
    [InlineData("随便一句话")]
    public void 提取不到时返回false(string text)
    {
        Assert.False(BilibiliLink.TryExtractBvid(text, out _));
    }
}
