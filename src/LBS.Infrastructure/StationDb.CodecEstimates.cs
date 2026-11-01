using LBS.Domain;

namespace LBS.Infrastructure;

/// <summary>
/// 加档前的「预估」规格读写（B 站 playurl 标称值）。一张表 <c>video_codec_estimates</c>，
/// 按「稿件 + 分P + 编码」一行，存探子抄回来的行情。
///
/// 没另设「问没问过」的戳表：规矩就一条 —— 这一页有行情就永不重问，库里空着才去拉。
/// 预估只是标称值，不准是常态（真要准的直接加个版本，ffprobe 实测见 video_codecs），
/// 所以不值得为它做保鲜期、冷却窗这些判断。
/// 这份数据随时可清可覆盖，与下载台账（video_codecs）互不相干。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>这一页的 cid（playurl 按 cid 出流，没它就问不到规格）。</summary>
    public long? GetPageCid(long videoId, int pageIdx)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cid FROM pages WHERE video_id = $id AND idx = $idx LIMIT 1;";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$idx", pageIdx);
        return command.ExecuteScalar() is long cid && cid > 0 ? cid : null;
    }

    /// <summary>这一页已存的预估（按编码名字母序，稳定输出便于比对与测试）。</summary>
    public IReadOnlyList<CodecEstimate> ListCodecEstimates(long videoId, int pageIdx)
    {
        var rows = new List<CodecEstimate>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT codec, video_kbps, audio_kbps, est_bytes, qn, width, height
            FROM video_codec_estimates
            WHERE video_id = $id AND page_idx = $page
            ORDER BY codec;
            """;
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$page", pageIdx);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CodecEstimate(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }

        return rows;
    }

    /// <summary>
    /// 整页覆盖式写入：先把这一页的旧行情抹掉再插新的。
    /// 覆盖而非 upsert 是有意的 —— 万一哪天要重拉（手动清了表），B 站不再给的档不能留着「上次有」的旧行继续给人看。
    /// </summary>
    public void SaveCodecEstimates(long videoId, int pageIdx, IReadOnlyList<CodecEstimate> rows, string fetchedAtUtc)
    {
        using var connection = Open(_dbPath);
        using (var wipe = connection.CreateCommand())
        {
            wipe.CommandText = "DELETE FROM video_codec_estimates WHERE video_id = $id AND page_idx = $page;";
            wipe.Parameters.AddWithValue("$id", videoId);
            wipe.Parameters.AddWithValue("$page", pageIdx);
            wipe.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO video_codec_estimates
                    (video_id, page_idx, codec, video_kbps, audio_kbps, est_bytes, qn, width, height, fetched_at_utc)
                VALUES ($id, $page, $codec, $vkbps, $akbps, $bytes, $qn, $w, $h, $now);
                """;
            insert.Parameters.AddWithValue("$id", videoId);
            insert.Parameters.AddWithValue("$page", pageIdx);
            insert.Parameters.AddWithValue("$codec", row.Codec);
            insert.Parameters.AddWithValue("$vkbps", (object?)row.VideoKbps ?? DBNull.Value);
            insert.Parameters.AddWithValue("$akbps", (object?)row.AudioKbps ?? DBNull.Value);
            insert.Parameters.AddWithValue("$bytes", (object?)row.Bytes ?? DBNull.Value);
            insert.Parameters.AddWithValue("$qn", (object?)row.Qn ?? DBNull.Value);
            insert.Parameters.AddWithValue("$w", (object?)row.Width ?? DBNull.Value);
            insert.Parameters.AddWithValue("$h", (object?)row.Height ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", fetchedAtUtc);
            insert.ExecuteNonQuery();
        }
    }
}
