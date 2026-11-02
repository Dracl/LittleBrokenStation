using LBS.Domain;

namespace LBS.Infrastructure;

/// <summary>
/// UP 空间的读侧（放映厅名片上那颗蓝钮进去的那一页）：某位 UP 在<b>本站已缓存</b>的稿件清单 + 页头名片。
///
/// 两条口径写在这里，不在前端：
/// ▸ 只给 <c>status = 'Arrived'</c> 的行 —— 没到货的（待提交 / 进站中 / 失败）不该出现在「这个 UP 在本站有什么」里，
///   那是站长的工程进度，不是观众看到的橱窗（与 <c>/api/search</c>、分区页同一把尺子）；
/// ▸ 可见性在取数那一刻量（<see cref="VisibilityFilter"/>），别人的私有稿在这页等于不存在。
///
/// 页头那份名片取的是 <c>video_meta</c> 的<b>站内快照</b>：这一页不替站长去敲 B 站（老稿没采过头像签名
/// 就画默认小人与「还没有签名」，要补齐走稿件设置里现成的「重拉封面与媒体」）。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>UP 空间页头：站内归档的名片快照。四个字段各取该 mid 最新一条非空值（早年稿子常有半截档案）。</summary>
    public sealed record UpProfile(long Mid, string? Name, string? Face, string? Sign, long? Fans);

    /// <summary>
    /// 该 UP 的站内名片快照。<b>站内一条这位的行都没有</b>时回 null —— 端点据此与「有这位但档案空白」区分开，
    /// 后者页头退化成「UP 主 {mid}」而不是整页 notfound。
    /// </summary>
    public UpProfile? GetUpProfileByMid(long mid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        // 四段各取「最新一条非空」而不是「最新一行的四件套」：名片补采是分头进行的，
        // 拿最新那一行可能恰好是个只写了名字的半截档案，把已有的头像与粉丝数一起埋了。
        command.CommandText = """
            SELECT
                (SELECT up_name FROM video_meta WHERE up_mid = $mid AND up_name IS NOT NULL AND up_name <> '' ORDER BY video_id DESC LIMIT 1),
                (SELECT up_face FROM video_meta WHERE up_mid = $mid AND up_face IS NOT NULL AND up_face <> '' ORDER BY video_id DESC LIMIT 1),
                (SELECT up_sign FROM video_meta WHERE up_mid = $mid AND up_sign IS NOT NULL AND up_sign <> '' ORDER BY video_id DESC LIMIT 1),
                (SELECT up_fans FROM video_meta WHERE up_mid = $mid AND up_fans IS NOT NULL ORDER BY video_id DESC LIMIT 1);
            """;
        command.Parameters.AddWithValue("$mid", mid);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var name = reader.IsDBNull(0) ? null : reader.GetString(0);
        var face = reader.IsDBNull(1) ? null : reader.GetString(1);
        var sign = reader.IsDBNull(2) ? null : reader.GetString(2);
        var fans = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
        return name is null && face is null && sign is null && fans is null
            ? null
            : new UpProfile(mid, name, face, sign, fans);
    }

    /// <summary>
    /// 「真实投稿时间」折算成可排序的 epoch 秒：<c>videos.published_at_utc</c>（ISO、UTC）优先，
    /// 拿不到时退吃 <c>video_meta.publish_text</c>（B 站归档的本地时间文本，形如 <c>2024-03-11 18:00:00</c>，
    /// 比 UTC 快 8 小时，故减掉 8×3600 才能与前者同一把尺比）。
    ///
    /// 为什么不能只按 <c>published_at_utc</c> 排：库里 156 条到货稿里只有 5 条带它，剩下的全住在 publish_text 里 ——
    /// 光拿那一列排，整个 UP 空间就退化成入站序（站长 11-02 就是看出了「新的没在最上面」）。
    /// 两个都拿不到的才垫到最后（现库里这种行 0 条，次序得先定好）。
    /// </summary>
    private const string EffPublishEpochSql = """
        COALESCE(
            CASE WHEN v.published_at_utc IS NULL THEN NULL
                 ELSE CAST(strftime('%s', replace(substr(v.published_at_utc, 1, 19), 'T', ' ')) AS INTEGER) END,
            CASE WHEN m.publish_text IS NULL OR m.publish_text = '' THEN NULL
                 ELSE CAST(strftime('%s', substr(m.publish_text, 1, 19)) AS INTEGER) - 8 * 3600 END)
        """;

    /// <summary>
    /// 该 UP 在本站已到货的稿件（<b>真实投稿时间</b>倒序，新的在上；两种投稿时间都拿不到的垫到最后，再按入站序号倒序）。
    /// 列形状与 <see cref="ListVideos"/> 一字不差 —— 它们共用 <c>ReadEntries</c>，列下标写死的，
    /// 少一列就是整页错位（悬停预览那一列尤其容易漏）。
    /// </summary>
    public IReadOnlyList<VideoEntry> ListVideosByUpMid(long mid, VisibilityScope? scope = null)
    {
        var (visSql, bindVis) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "v");
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id, v.url, v.bvid, v.avid, {EffTitleExpr}, v.status, v.created_at_utc, v.cover_url, v.last_error,
                   m.up_name, m.view_count, p.duration_text, v.published_at_utc, m.publish_text,
                   (SELECT COUNT(1) FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'danmaku'),
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   (SELECT p.rel_path FROM previews p WHERE p.video_id = v.id AND p.status = 'Arrived' LIMIT 1),
                   v.visibility, m.up_mid
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            LEFT JOIN pages p ON p.video_id = v.id
                 AND p.idx = (SELECT MIN(idx) FROM pages WHERE video_id = v.id)
            WHERE {visSql} AND v.status = 'Arrived' AND m.up_mid = $mid
            ORDER BY CASE WHEN ({EffPublishEpochSql}) IS NULL THEN 1 ELSE 0 END,
                     {EffPublishEpochSql} DESC,
                     v.id DESC;
            """;
        command.Parameters.AddWithValue("$mid", mid);
        bindVis(command);

        using var reader = command.ExecuteReader();
        return ReadEntries(reader);
    }
}
