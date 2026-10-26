namespace LBS.Domain;

/// <summary>收藏夹里解析出的一条链接候选。</summary>
public sealed record BookmarkCandidate(string Url, string? Title, string Folder);

/// <summary>
/// Netscape 书签格式（浏览器导出的收藏夹 HTML）解析器。纯函数，不碰 IO。
/// 只负责把 A 标签连同所在文件夹摊平成候选列表，链接是不是 B 站视频由 BilibiliLink 裁决。
/// </summary>
public static class NetscapeBookmarkParser
{
    private static readonly System.Text.RegularExpressions.Regex TagRegex = new(
        @"(?is)<h3[^>]*>(?<h3>.*?)</h3>|<a\s[^>]*href\s*=\s*""(?<href>[^"")]+)""[^>]*>(?<text>.*?)</a>|(?<dl><DL>)|(?<dlEnd></DL>)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool LooksLikeNetscapeBookmarkFile(string content) =>
        !string.IsNullOrWhiteSpace(content)
        && content.Contains("NETSCAPE-Bookmark-file", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<BookmarkCandidate> Parse(string content)
    {
        var candidates = new List<BookmarkCandidate>();
        var folderStack = new Stack<string>();

        foreach (System.Text.RegularExpressions.Match match in TagRegex.Matches(content))
        {
            if (match.Groups["h3"].Success)
            {
                folderStack.Push(CleanText(match.Groups["h3"].Value));
            }
            else if (match.Groups["dlEnd"].Success)
            {
                if (folderStack.Count > 0)
                {
                    folderStack.Pop();
                }
            }
            else if (match.Groups["href"].Success)
            {
                var url = match.Groups["href"].Value.Trim();
                if (url.Length == 0)
                {
                    continue;
                }

                var title = CleanText(match.Groups["text"].Value);
                candidates.Add(new BookmarkCandidate(url, title.Length == 0 ? null : title, string.Join('/', folderStack.Reverse())));
            }
        }

        return candidates;
    }

    private static string CleanText(string raw)
    {
        var noTags = System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]*>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(noTags).Trim();
    }
}
