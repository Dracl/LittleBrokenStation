using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 可见性的唯一出口：把「谁在问」掰成 SQL 片段 + 绑参动作，全站各读出口共用这一张嘴。
/// 三条口径：
/// ① 谓词只在这里长出来——各查询里手写 `visibility='public'` 迟早漏一处（分区 count、封面子查询、随机补位都是这种死角）；
/// ② 归属比的是站内席位 id（videos.submitter_member_id / collections.owner_member_id），与端点那把
///    CanManageVideo 同一张嘴，不绕 B 站 mid；
/// ③ 认不出的一律往「看得见」那侧倒（FromColumn 宁缺毋假），但尺子本身不认人时（PublicOnly）
///    $visMember 绑 -1 —— 席位 id 从 1 起，永不命中，私有行一条都溜不过来。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>绑参名前缀（带上表别名后缀：同一命令里 v/c 两处尺子各有各的坑，不会互相盖掉值）。</summary>
    private const string VisibilityMemberParamPrefix = "$visMember";

    /// <summary>
    /// 可见性谓词的唯一出口：把尺子掰成 SQL 片段 + 绑参动作，全站各读出口共用。
    /// alias 传表别名（videos 用 "v"，collections 用 "c"）；$visMember 一律绑 MemberId ?? -1（永不命中）。
    /// 站长尺子（scope.All）直接回 <c>1=1</c> 且不绑参——写侧留个不绑参的空动作，调用方不必分叉。
    /// </summary>
    /// <remarks>两张表的「本人」列名不同（投稿人 / 东家），但形状同一句话：
    /// 公开的一律放行，私有的只认本人那一行；站长走 All 分支根本不比。
    /// 合集侧「集里有没有私有稿」不归这里判——那是视频侧各读出口（片单、计数、封面子查询）自己的事。</remarks>
    private static (string Sql, Action<SqliteCommand> Bind) VisibilityFilter(VisibilityScope scope, string alias)
    {
        if (alias != "v" && alias != "c")
        {
            // 编程期错误：别名写错就是想让谓词比错列，直接炸在开发路上，不给它进库的机会。
            throw new ArgumentException("可见性谓词只认表别名 v（videos）与 c（collections）", nameof(alias));
        }

        if (scope.All)
        {
            return ("1=1", _ => { });
        }

        var owner = alias == "v" ? "v.submitter_member_id" : "c.owner_member_id";
        var param = VisibilityMemberParamPrefix + "_" + alias;
        var member = scope.MemberId ?? -1; // 没席位就是访客：-1 永不命中
        var sql = "(" + alias + ".visibility = '" + VisibilityCodec.Public + "'"
            + " OR (" + alias + ".visibility = '" + VisibilityCodec.Private + "' AND " + owner + " = " + param + "))";
        return (sql, command => command.Parameters.AddWithValue(param, member));
    }

    /// <summary>这稿的可见性；查无回 null（404 归端点报，「没这条」与「藏着」是两件事，这儿只说有没有）。
    /// 集的档位不在这上头问：它随 <c>CollectionBrief</c> 那一挑一起出（<c>BriefSelect</c> 一趟带齐），
    /// 这里只留稿件那一把（<c>ViewerGate.CanView</c> 在用）。</summary>
    public VideoVisibility? GetVideoVisibility(long videoId)
    {
        using var connection = Open(_dbPath);
        return ReadVisibility(connection, "SELECT visibility FROM videos WHERE id = $key;", "$key", videoId);
    }

    /// <summary>
    /// 静态区挡门用：拿相对路径（正斜杠，相对 data/videos；previews 那支相对 data/previews，同一字符串传进来）
    /// 问这个件属于哪条稿子、那条藏没藏。
    /// 认法三级：artifacts.rel_path → video_codecs.rel_path → previews.rel_path（字面不中再走一轮 COLLATE NOCASE）；
    /// 台账都不认（头像/表情/评论图那类件根本不在这些表里）就退到文件名尾部的 BV 号反查 bvid。
    /// 彻底认不出回 null —— 调用方自己决定放行，不许拿 null 当私有，否则别人家的头像就跟着吃 404 了。
    /// </summary>
    public VideoVisibility? GetVideoVisibilityByMediaRel(string relPath)
    {
        var rel = NormalizeMediaRel(relPath);
        if (rel is null)
        {
            return null;
        }

        using var connection = Open(_dbPath);
        return TryResolveMediaVideoId(connection, rel) is { } videoId
            ? ReadVisibility(connection, "SELECT visibility FROM videos WHERE id = $key;", "$key", videoId)
            : null;
    }

    /// <summary>静态区挡门用：把请求相对路径认成站内稿件 id。台账不认这个件回 null（调用方必须放行，不许当私有）。</summary>
    /// <remarks>认法与 <see cref="GetVideoVisibilityByMediaRel"/> 同一张嘴（共用 <see cref="TryResolveMediaVideoId"/>）：
    /// 静态闸拿这枚 id 去问 <c>CanViewVideo</c>，问不出 id 就是「台账不管这个件」（头像/表情/评论图那类），必须放行。</remarks>
    public long? GetVideoIdByMediaRel(string relPath)
    {
        var rel = NormalizeMediaRel(relPath);
        if (rel is null)
        {
            return null;
        }

        using var connection = Open(_dbPath);
        return TryResolveMediaVideoId(connection, rel);
    }

    /// <summary>
    /// 「这个件属于哪条稿子」的唯一认法：三条腿按「产物 → 加档件 → 悬停预览」的顺序问（先比字面，
    /// 三条都不中再把三条跑一轮 COLLATE NOCASE），命中即回，不做前缀花活；台账都不认就退到文件名尾部那枚 BV 号反查 bvid。彻底认不出回 null。
    /// 三腿都 JOIN videos：稿件行已经不在的悬空台账不当命中，留给后面那腿（与分腿查可见性的老口径一字不差）。
    /// </summary>
    private static long? TryResolveMediaVideoId(SqliteConnection connection, string rel)
    {
        // 轮次顺位不许倒过来：字面腿全部跑完才轮到 NOCASE 腿——大小写敏感的文件系统（ext4）上
        // 同形不同大小写的两个件本可各自属不同稿，先把三条字面腿跑完，原有语义一字不差。
        // NOCASE 那一轮的来由：静态区落在 NTFS / Docker 卷这类大小写不敏感的文件系统上时，
        // /media/bv1xxxxxx.mp4（小写）照样从盘上取得出文件，台账按字面比却不中 → 认不到 id
        // → 调用方按「台账不管这个件」fail-open 放行，私有稿的正片就这么溜出去。
        // COLLATE NOCASE 不走索引，但这是低频反查（前面还压着一层 mediaOwnerCache），认这个代价。
        foreach (var nocase in new[] { false, true })
        {
            foreach (var (table, alias) in MediaRelTables)
            {
                var sql = $"SELECT {alias}.video_id FROM {table} {alias} JOIN videos v ON v.id = {alias}.video_id "
                    + $"WHERE {alias}.rel_path = $key{(nocase ? " COLLATE NOCASE" : "")} LIMIT 1;";
                if (ReadScalarLong(connection, sql, "$key", rel) is { } videoId)
                {
                    return videoId;
                }
            }
        }

        // 台账不认这个件：只剩文件名里那枚 BV 号可拄（引擎落盘名常带 bvid，如 <bvid>/<bvid>-P1-1080P-AVC）。
        if (BvidInFileName(rel) is { } bvid)
        {
            // 先按字面比，再让一步大小写：库里 bvid 从 URL 来，盘上名字可能被哪一环改过大小写，别为这个误放行。
            return ReadScalarLong(connection, "SELECT id FROM videos WHERE bvid = $key LIMIT 1;", "$key", bvid)
                ?? ReadScalarLong(connection, "SELECT id FROM videos WHERE bvid = $key COLLATE NOCASE LIMIT 1;", "$key", bvid);
        }

        return null;
    }

    /// <summary>三条腿的表与别名（产物 / 加档件 / 悬停预览 / 封面缩略件，认的都是 rel_path 这一列）。</summary>
    private static readonly (string Table, string Alias)[] MediaRelTables =
    [
        ("artifacts", "a"),
        ("video_codecs", "d"),
        ("previews", "p"),
        // 封面缩略件住在 data/thumbs，台账不在 artifacts 里；不补这一腿，私有稿件的缩略图会被
        // 「台账不管这个件」的 fail-open 口径直接匿名放行（文件名里那枚 BV 号只是第二保险）。
        ("cover_thumbs", "t"),
    ];

    private static VideoVisibility? ReadVisibility(SqliteConnection connection, string sql, string paramName, object value)
        => ReadScalar(connection, sql, paramName, value) is string raw ? VisibilityCodec.FromColumn(raw) : null;

    /// <summary>拿一列整数（id）：没行回真 null，不当 0 号稿子使（ExecuteScalar 空手给的是 null/DBNull）。</summary>
    private static long? ReadScalarLong(SqliteConnection connection, string sql, string paramName, object value)
        => ReadScalar(connection, sql, paramName, value) switch
        {
            long l => l,
            int i => i,
            string => null, // 拿 id 的问句不可能回字串：宁当没命中，也不为这个抛
            null or DBNull => null,
            IConvertible convertible => convertible.ToInt64(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };

    private static object? ReadScalar(SqliteConnection connection, string sql, string paramName, object value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(paramName, value);
        return command.ExecuteScalar();
    }

    /// <summary>把传进来的路径掰成库里存的那形：正斜杠、去头斜杠与 ./ 前缀；掰完没字就 null。</summary>
    private static string? NormalizeMediaRel(string? relPath)
    {
        var rel = relPath?.Trim().Replace('\\', '/').TrimStart('/');
        while (rel is { Length: > 2 } && rel.StartsWith("./", StringComparison.Ordinal))
        {
            rel = rel[2..];
        }

        return string.IsNullOrEmpty(rel) ? null : rel;
    }

    /// <summary>B 站稿件号定长：'BV' + 10 位。</summary>
    private const int BvidLength = 12;

    /// <summary>
    /// 从文件名尾部往前掰 BV 号：引擎落盘名多带 bvid（<c>&lt;bvid&gt;-P1-1080P-AVC</c> / <c>&lt;bvid&gt;.mp4</c>），
    /// 而预览件干脆就是 <c>&lt;bvid&gt;.mp4</c>。只认整段（按 - _ . 空格 + 括号 切开后）正好 12 位且 BV 打头、
    /// 余下全是 ASCII 字母数字的那段——差一位都不算，免得把标题里的什么「BV1」当号反查出个不相干的稿子。
    /// </summary>
    private static string? BvidInFileName(string rel)
    {
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        var segments = name.Split(['-', '_', '.', ' ', '+', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var token = segments[i];
            if (token.Length != BvidLength
                || !token.StartsWith("BV", StringComparison.OrdinalIgnoreCase)
                || !IsAsciiAlphaNum(token, 2))
            {
                continue;
            }

            return token;
        }

        return null;
    }

    private static bool IsAsciiAlphaNum(string value, int from)
    {
        for (var i = from; i < value.Length; i++)
        {
            var c = value[i];
            if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')))
            {
                return false;
            }
        }

        return value.Length > from;
    }
}
