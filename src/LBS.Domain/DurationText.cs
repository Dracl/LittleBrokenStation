using System.Text.RegularExpressions;

namespace LBS.Domain;

/// <summary>
/// 时长文本归一：库里存着三种方言——DownKyi 的「4m3s」、B 站档案的「03:09」、老数据的纯秒数「243」。
/// 展示一律收成 B 站同款：一小时内「m:ss」，一小时以上「h:mm:ss」。认不出的原文照放，宁脏不空。
/// </summary>
public static class DurationText
{
    private static readonly Regex HmsPattern = new(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim().ToLowerInvariant();
        int total;
        var match = HmsPattern.Match(s);
        if (match.Success &&
            (match.Groups[1].Success || match.Groups[2].Success || match.Groups[3].Success))
        {
            total = Hours(match.Groups[1].Value) * 3600 + Hours(match.Groups[2].Value) * 60 + Hours(match.Groups[3].Value);
        }
        else
        {
            var parts = s.Split(':');
            if (parts.Length is < 1 or > 3 || !parts.All(p => p.Length > 0 && p.All(char.IsDigit)))
            {
                return raw; // 「时长未知」这类人话，不硬掰
            }

            total = parts.Aggregate(0, static (acc, p) => acc * 60 + int.Parse(p));
        }

        var (hh, mm, ss) = (total / 3600, total / 60 % 60, total % 60);
        return hh > 0 ? $"{hh}:{mm:D2}:{ss:D2}" : $"{mm}:{ss:D2}";
    }

    private static int Hours(string digits) => int.TryParse(digits, out var v) ? v : 0;
}
