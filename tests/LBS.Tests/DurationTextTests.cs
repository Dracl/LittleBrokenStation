using LBS.Domain;
using Xunit;

namespace LBS.Tests;

/// <summary>时长文本归一：三种入库方言都要收成 B 站同款，认不出的原文照放。</summary>
public class DurationTextTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("4m3s", "4:03")]          // DownKyi 方言
    [InlineData("4M3S", "4:03")]          // 大小写不挑
    [InlineData("1h2m3s", "1:02:03")]     // 跨小时补零
    [InlineData("1h", "1:00:00")]
    [InlineData("3s", "0:03")]
    [InlineData("03:09", "3:09")]         // B 站档案冒号式
    [InlineData("4:03", "4:03")]          // 已是目标格式
    [InlineData("1:02:03", "1:02:03")]
    [InlineData("243", "4:03")]           // 纯秒数老数据
    [InlineData("0", "0:00")]
    [InlineData("时长未知", "时长未知")]     // 人话不硬掰
    [InlineData("--:--", "--:--")]
    public void Normalize_收方言(string? raw, string? expected)
    {
        Assert.Equal(expected, DurationText.Normalize(raw));
    }
}
