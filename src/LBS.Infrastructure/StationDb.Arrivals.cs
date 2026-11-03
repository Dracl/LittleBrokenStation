using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 到站播报读料（站长 11-03 点头的路线图 🔭 首件「到站通知」）：
/// 爬工进站的货没有归属行（<c>submitter_member_id IS NULL</c>，SubmissionIntake 的注释钉着
/// 「后台爬工本就没有投稿人」），成员自投必带归属——这一谓词本身就是分流闸，
/// 所以「谁的货该播」不问爬工、不加标记列，台账形状自己会说话。
/// 落账与水位不归这里管（那是 <c>ArrivalReportWorker</c> 的活），这里只负责把「水位之后新到的货」
/// 连卡面素材一并端出去。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>到站播报的水位钥匙（settings KV，与周报水位 <see cref="WeeklySentWatermarkKey"/> 同一族）：
    /// 值是「已播过的最大 videos.id」，数字比值不搞字符串花活。</summary>
    public const string ArrivalReportWatermarkKey = "arrival-report-sent";

    /// <summary>到站播报卡面的一件货。Cover 是本地原图（/media/… 观看页同形），CoverUrl 是热链兜底，
    /// CoverThumbs 是逗号相接的档位件清单（前端 coverSrcOf 三级兜底的口粮）；缺哪个前端降级哪个。</summary>
    public sealed record ArrivalCard(
        long VideoId,
        string Bvid,
        string? Title,
        string? Cover,
        string? CoverUrl,
        string? CoverThumbs,
        string? UpName,
        string? DurationText);

    /// <summary>站内最大 videos.id（到站播报员首岗垫底用：「存量一律算已播」的尺子）。</summary>
    public long GetMaxVideoId()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM videos;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// 水位之后的新到货：status=Arrived、无归属行、id 大于水位，小到大取 take 条。
    /// 判活与取封面的口径与放映厅分享快照（<see cref="GetVideoShareSnapshot"/>）同源：
    /// Arrived 才算到站；封面本地优先、无归档回落热链；档位件纯增益（对账 worker 会补，切不出也不拦播）。
    /// 爬工货恒 public（写侧三处焊死），卡面标题不涉私有泄漏；若站长日后把某枚改私有，
    /// 旧通报里的标题曝光口径与周报聚合一致（现状如此，不新开洞也不在此补闸）。
    /// </summary>
    public IReadOnlyList<ArrivalCard> ListArrivalsSince(long afterVideoId, int take = 8)
    {
        var list = new List<ArrivalCard>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id, v.bvid, v.title,
                   (SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   v.cover_url,
                   (SELECT group_concat(t.rel_path, ',') FROM cover_thumbs t WHERE t.video_id = v.id AND t.status = 'arrived'),
                   m.up_name,
                   (SELECT p.duration_text FROM pages p WHERE p.video_id = v.id ORDER BY p.idx LIMIT 1)
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            WHERE v.status = $status
              AND v.submitter_member_id IS NULL
              AND v.id > $after
            ORDER BY v.id
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        command.Parameters.AddWithValue("$after", afterVideoId);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 50));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ArrivalCard(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return list;
    }
}
