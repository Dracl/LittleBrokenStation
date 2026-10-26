using LBS.Domain;

namespace LBS.Tests;

public class NetscapeBookmarkParserTests
{
    private const string Sample = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <TITLE>Bookmarks</TITLE>
        <DL><p>
            <DT><H3>外层夹</H3>
            <DL><p>
                <DT><A HREF="https://www.bilibili.com/video/BV1GJ411x7h7?spm=1" ADD_DATE="1">带参数的视频 &amp; 转义</A>
                <DT><A HREF="" >空链接</A>
                <DT><A HREF="https://space.bilibili.com/123">不是视频</A>
            </DL><p>
            <DT><H3>内层夹</H3>
            <DL><p>
                <DT><H3>子夹</H3>
                <DL><p>
                    <DT><A HREF="https://www.bilibili.com/video/BV1xx411c7mD">嵌套视频</A>
                </DL><p>
            </DL><p>
        </DL><p>
        """;

    [Fact]
    public void 识别Netscape声明()
    {
        Assert.True(NetscapeBookmarkParser.LooksLikeNetscapeBookmarkFile(Sample));
        Assert.False(NetscapeBookmarkParser.LooksLikeNetscapeBookmarkFile("<html><body>hello</body></html>"));
        Assert.False(NetscapeBookmarkParser.LooksLikeNetscapeBookmarkFile("   "));
    }

    [Fact]
    public void 摊平候选并带文件夹路径()
    {
        var candidates = NetscapeBookmarkParser.Parse(Sample);

        Assert.Equal(3, candidates.Count);

        var first = candidates[0];
        Assert.Equal("https://www.bilibili.com/video/BV1GJ411x7h7?spm=1", first.Url);
        Assert.Equal("带参数的视频 & 转义", first.Title);
        Assert.Equal("外层夹", first.Folder);

        var nested = candidates[2];
        Assert.Equal("内层夹/子夹", nested.Folder);
    }

    [Fact]
    public void 空href被跳过_多余DL闭合不崩()
    {
        var content = "<DL></DL></DL><DT><A HREF=\"https://b23.tv/abc\">x</A>";
        var candidates = NetscapeBookmarkParser.Parse(content);
        Assert.Single(candidates);
    }

    [Fact]
    public void 标题为空时置null()
    {
        var content = "<A HREF=\"https://www.bilibili.com/video/BV1GJ411x7h7\">   </A>";
        var candidates = NetscapeBookmarkParser.Parse(content);
        Assert.Null(candidates[0].Title);
    }
}
