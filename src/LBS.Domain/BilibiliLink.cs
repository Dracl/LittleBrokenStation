using System.Text.RegularExpressions;

namespace LBS.Domain;

/// <summary>
/// B 站链接识别与 BV 号提取。链接入库的唯一校验 owner。
/// BV/AV 换算等协议细节 M1 起归移植后的 downkyicore Core，此处不做第二套。
/// </summary>
public static partial class BilibiliLink
{
    [GeneratedRegex(@"BV1[0-9A-Za-z]{9}", RegexOptions.CultureInvariant)]
    private static partial Regex BvidRegex();

    /// <summary>是否为 B 站系域名（bilibili.com 及其子域、b23.tv 短链）。</summary>
    public static bool IsBilibiliHost(string host) =>
        host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".b23.tv", StringComparison.OrdinalIgnoreCase);

    /// <summary>从任意含 BV 号的文本中提取第一个合法 BV 号。</summary>
    public static bool TryExtractBvid(string text, out string bvid)
    {
        var match = BvidRegex().Match(text);
        if (match.Success)
        {
            bvid = match.Value;
            return true;
        }

        bvid = string.Empty;
        return false;
    }
}
