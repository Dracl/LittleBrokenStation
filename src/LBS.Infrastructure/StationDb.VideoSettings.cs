using LBS.Domain;

namespace LBS.Infrastructure;

/// <summary>
/// 稿件设置里「站内自己那份」的读写：标题 / 简介 / 分区三栏，外加可见性。
/// 形状照 <c>site_zone</c> 那一族（StationDb.Zones.cs）：<c>videos</c> 上开 <c>site_*</c> 槽，
/// 有效值 = 站内覆写优先、没覆写就吃 B 站归档的原值，写 NULL 即还原。三条口径：
/// ① <b>NULL 与空串都算「未覆写」</b>：有效值表达式一律用 <c>NULLIF(..., '')</c> 归一，写侧也只可能落
///    「非空串」或 NULL（去首尾空白后空白即还原，与 <see cref="SetSiteZone"/> / 合集简介同一张嘴），
///    所以「覆写」与「还原」之间不存在第三种状态；
/// ② 有效值只从下面那几个表达式常量出（<see cref="EffTitleExpr"/> / <see cref="EffDescExpr"/> /
///    <see cref="EffZoneExprWith"/>）：卡阵、搜索、分区、相关推荐、放映厅、合集、观看历史念的都是同一张嘴，
///    免得出现「改了标题只有放映厅变、卡阵还是旧的」那种两处真相；
/// ③ 格式与长度的校验只从 <c>TryNormalize*</c> 那一族出（端点与测试同一张嘴：认不了回 false + 人话，端点据此回 400）。
/// 稿件本地备注（site_note）站长按约没勾，这里不占坑。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>
    /// 站内覆写标题的封顶：80 字。依据——B 站稿件标题本身就 80 字封顶，站内 videos.title 全从 B 站归档来
    /// （活站 155 条实测最长 73 字），比它宽没有意义；比站内其它输入框（合集名 30 / 公告标题 60）高一档，
    /// 因为它顶掉的是 B 站原标题，总得装得下原样。
    /// </summary>
    public const int MaxSiteTitleLength = 80;

    /// <summary>
    /// 站内覆写简介的封顶：2000 字。依据——活站 video_meta.description 实测最长 1916 字（B 站简介本就是千级量头），
    /// 与站内其它长文本框（公告正文 / 私信正文的 maxlength=2000）对齐，不留两天花板。
    /// </summary>
    public const int MaxSiteDescLength = 2000;

    /// <summary>
    /// 有效标题表达式（全站唯一口径）：站内覆写优先，没覆写吃 B 站归档的 videos.title。
    /// 别名口径与 <c>EffZoneExpr</c> 同族：videos = v（各查询一律这么起别名，写错别名 SQL 会直接报无此列，不会静默给错值）。
    /// </summary>
    private const string EffTitleExpr = "COALESCE(NULLIF(v.site_title, ''), v.title)";

    /// <summary>video_meta 在大部分查询里的惯用别名（分区与简介那两式的默认对手；搜索那条 JOIN 起的是 m，得递别名）。</summary>
    private const string VideoMetaAlias = "vm";

    /// <summary>
    /// 有效分区表达式的本体（全站唯一口径）：站内改派优先，空则继承 B 站归档的 zone（形如「音乐>翻唱」）。
    /// 本体只在这一处写：别名由调用方递（<see cref="EffZoneExpr"/> 就是它的 vm 一枚），免得换个 JOIN 别名就多一份本体然对不上。
    /// </summary>
    private static string EffZoneExprWith(string metaAlias) => $"COALESCE(NULLIF(v.site_zone, ''), {metaAlias}.zone)";

    /// <summary>有效简介表达式的本体：站内覆写优先，没覆写吃 B 站归档的 description（别名口径同 <see cref="EffZoneExprWith"/>）。</summary>
    private static string EffDescExprWith(string metaAlias) => $"COALESCE(NULLIF(v.site_desc, ''), {metaAlias}.description)";

    /// <summary>有效简介表达式（vm 那一枚）：放映厅与搜索都念它——简介覆写后卡阵不显它、但搜索得搜得着新那句（两处真相拆干净）。</summary>
    private static string EffDescExpr => EffDescExprWith(VideoMetaAlias);

    /// <summary>「这一栏正被覆写吗」的判据（NULLIF 与上面几式一字同源）：放映厅面板据此画「已改」标、摆「还原」钮。</summary>
    private static string OverriddenExpr(string column) => $"(NULLIF(v.{column}, '') IS NOT NULL)";

    /// <summary>
    /// 覆写标题（端点与测试同一张嘴）：空 = 还原（值出 null）；去首尾空白后限 <see cref="MaxSiteTitleLength"/> 字。
    /// 认不了回 false + 人话，端点据此回 400——长度这一刀在数据层砍，不给前端自己判（两处真相）。
    /// </summary>
    public static bool TryNormalizeTitle(string? raw, out string? title, out string message) =>
        TryNormalizeOverride(raw, MaxSiteTitleLength, "标题", out title, out message);

    /// <summary>覆写简介（端点与测试同一张嘴）：空 = 还原；限 <see cref="MaxSiteDescLength"/> 字。段内换行留着，只削首尾空白。</summary>
    public static bool TryNormalizeDesc(string? raw, out string? desc, out string message) =>
        TryNormalizeOverride(raw, MaxSiteDescLength, "简介", out desc, out message);

    /// <summary>
    /// 覆写值的一般规矩（照 <see cref="TryNormalizeZone"/> 的形状）：空 = 还原（值出 null、放行）；
    /// 非空去首尾空白后限长，超了回 false + 人话。
    /// </summary>
    private static bool TryNormalizeOverride(string? raw, int maxLength, string field, out string? value, out string message)
    {
        value = null;
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            message = $"{field}已还原，这栏回跟 B 站档案走";
            return true;
        }

        if (trimmed.Length > maxLength)
        {
            message = $"{field}{maxLength} 字封顶，你递了 {trimmed.Length} 字";
            return false;
        }

        value = trimmed;
        message = field + "已覆写";
        return true;
    }

    /// <summary>覆写标题：非空串写值；null / 空白一律落 NULL = 还原成 B 站原标题。查无此片回 false（404 归端点报）。</summary>
    public bool SetVideoTitleOverride(long videoId, string? title) => SetOverrideColumn(videoId, "site_title", title);

    /// <summary>覆写简介：同上，只在放映厅生效（列表与卡阵本来不念简介）。查无此片回 false。</summary>
    public bool SetVideoDescOverride(long videoId, string? desc) => SetOverrideColumn(videoId, "site_desc", desc);

    /// <summary>
    /// 覆写分区（<b>投稿人本人也走这口</b>，与站长室那口 POST /api/admin/zones/set 同用
    /// <see cref="TryNormalizeZone"/> 与 <see cref="SetSiteZone"/>，一张嘴不改派两样）。
    /// 认不了回 false（端点回 400）；空 = 清空改派、户口回跟 B 站档案走；查无此片也回 false（404 归端点报）。
    /// </summary>
    public bool SetVideoZoneOverride(long videoId, string? zone) =>
        TryNormalizeZone(zone, out var normalized, out _) && SetSiteZone(videoId, normalized);

    /// <summary>
    /// 改这稿的可见性（稿件设置窗那枚「公开/私有」开关）：写侧只可能落 <see cref="VisibilityCodec"/> 那两枚小写字面量。
    /// 查无此片回 false（404 归端点报）。这一口当初故意没做（那时只有投稿链写 visibility），稿件设置窗要用才补上——
    /// 归属那把钥匙在端点（CanManageVideo），数据层不认身份，与 <see cref="SetCollectionVisibility"/> 同一分工。
    /// </summary>
    public bool SetVideoVisibility(long videoId, VideoVisibility visibility)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET visibility = $vis WHERE id = $id;";
        command.Parameters.AddWithValue("$vis", VisibilityCodec.ToColumn(visibility));
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 覆写槽的同一张写嘴：去首尾空白后空白落 NULL（还原），否则落去空白后的值。
    /// 列名走白名单——这口一井只服务下面两处写死的 <c>site_*</c> 槽，外部输入永远进不了 SET 子句。
    /// </summary>
    private bool SetOverrideColumn(long videoId, string column, string? value)
    {
        if (column is not ("site_title" or "site_desc"))
        {
            // 编程期错误：列名是代码写的，写成别的就是想让外部输入进 SET 子句，直接炸在开发路上。
            throw new ArgumentException("覆写槽只认 site_title / site_desc", nameof(column));
        }

        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE videos SET {column} = $value WHERE id = $id;";
        command.Parameters.AddWithValue("$value", string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim());
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteNonQuery() > 0;
    }
}
