namespace LBS.Domain;

/// <summary>稿件与合集的可见性。私有 = 只有投稿人（合集东家）本人与站长看得着。</summary>
public enum VideoVisibility
{
    /// <summary>公开：访客也看得见（存量行靠列的 DEFAULT 落在这一档）。</summary>
    Public = 0,

    /// <summary>私有：只有投稿人（合集则是东家）本人与站长看得着。</summary>
    Private = 1,
}

/// <summary>可见性落库列名与解析（宁缺毋假：认不出的值一律当 public，不抛）。</summary>
public static class VisibilityCodec
{
    /// <summary>库里的公开字面量（小写，写侧只可能出现这两个值）。</summary>
    public const string Public = "public";

    /// <summary>库里的私有字面量（小写）。</summary>
    public const string Private = "private";

    /// <summary>落库字面量：小写，与列的 DEFAULT 一字不差。</summary>
    public static string ToColumn(VideoVisibility v) => v == VideoVisibility.Private ? Private : Public;

    /// <summary>
    /// 读库：'private' 之外（含 null / 空 / 脏值）一律当 public。
    /// 读侧不抛是故意的——一列写坏最多让人多看见一条，不该把整页卡阵钉成 500。
    /// 大小写不敏感认 private：宁可多挡不误放，手写进库的 'Private' 也算私有。
    /// </summary>
    public static VideoVisibility FromColumn(string? raw) =>
        string.Equals(raw, Private, StringComparison.OrdinalIgnoreCase) ? VideoVisibility.Private : VideoVisibility.Public;

    /// <summary>
    /// 端点收参数用：认 public / private / 空（空 = public），其余回 false（调用方按 400 处置）。
    /// 回 false 时 v 垫 public，调用方只在 true 的分支里用它。
    /// </summary>
    public static bool TryParse(string? raw, out VideoVisibility v)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, Public, StringComparison.OrdinalIgnoreCase))
        {
            v = VideoVisibility.Public;
            return true;
        }

        if (string.Equals(trimmed, Private, StringComparison.OrdinalIgnoreCase))
        {
            v = VideoVisibility.Private;
            return true;
        }

        v = VideoVisibility.Public;
        return false;
    }
}

/// <summary>
/// 读侧那把尺子：Everything = 站长（全见）；Of(memberId) = 公开 + 自己的；PublicOnly = 访客。
/// 三档就够：数据层不认身份、不查角色（谁是站长归端点判），只认「全见不给」与「比哪枚席位 id」。
/// </summary>
public readonly record struct VisibilityScope(bool All, long? MemberId)
{
    /// <summary>站长尺子：什么都不滤。</summary>
    public static VisibilityScope Everything { get; } = new(true, null);

    /// <summary>访客尺子：只看 public。</summary>
    public static VisibilityScope PublicOnly { get; } = new(false, null);

    /// <summary>席位尺子：public 加上自己的（投稿人 / 东家本人）；memberId 为 null 就是访客。</summary>
    public static VisibilityScope Of(long? memberId) => memberId is null ? PublicOnly : new VisibilityScope(false, memberId);
}
