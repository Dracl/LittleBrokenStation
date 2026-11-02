using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 封面缩略件台账（站长 11-03「缩略图要本站自己从本地原图裁，入站时一次切完」）。
///
/// 为什么单开一张表而不进 <c>artifacts</c>：与 <c>previews</c> 同一套理由，而且这里更凶——
/// <c>ClassifyArtifact</c> 会把带 <c>.cover</c> 的图片认成 <c>kind='cover'</c>，而全站十几处读侧取的是
/// <c>ORDER BY a.id LIMIT 1</c>（最早那一行），插队就让首页集体显缩略图；<c>IsMediaMissing</c> 验盘又不筛
/// kind，档位文件没落全会把整条稿件 <c>ResetToPending</c> 重下正片。所以缩略件既不写进 videos 目录，
/// 也不写进 artifacts，只记这张表。
///
/// <c>rel_path</c> 存的是相对 <c>data/thumbs</c> 的文件名，这一列同时是私有稿件反查的比对列
/// （<see cref="StationDb.Visibility.cs"/> 的 MediaRelTables 第四条腿拿它认归属）。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>一条稿件的一条缩略档位。状态只两种：arrived（已落盘）与 failed（切砸了，记账等下一轮）。</summary>
    public sealed record CoverThumbCandidate(long VideoId, string Bvid, string CoverRel);

    /// <summary>档位件写盘成功后落账：同 (视频, 档位) 只留一行（表上钉了 UNIQUE），重拉封面后版本变了就原地换名换戳。</summary>
    public void RegisterCoverThumbArrived(long videoId, string relPath, string tier, string stamp, string format)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cover_thumbs (video_id, rel_path, tier, fmt, src_stamp, status, last_error, attempts, updated_at_utc)
            VALUES ($id, $rel, $tier, $fmt, $stamp, 'arrived', NULL, 0, $now)
            ON CONFLICT (video_id, tier) DO UPDATE SET
                rel_path = excluded.rel_path,
                fmt = excluded.fmt,
                src_stamp = excluded.src_stamp,
                status = 'arrived',
                last_error = NULL,
                attempts = 0,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$rel", relPath);
        command.Parameters.AddWithValue("$tier", tier);
        command.Parameters.AddWithValue("$fmt", format);
        command.Parameters.AddWithValue("$stamp", stamp);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>切砸了记一笔：留档以便站长室看出「这条一直是原图」，也用来给补员员封顶重试次数。</summary>
    public void MarkCoverThumbFailed(long videoId, string tier, string error)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cover_thumbs (video_id, rel_path, tier, fmt, src_stamp, status, last_error, attempts, updated_at_utc)
            VALUES ($id, '', $tier, '', '', 'failed', $error, 1, $now)
            ON CONFLICT (video_id, tier) DO UPDATE SET
                attempts = cover_thumbs.attempts + 1,
                last_error = excluded.last_error,
                updated_at_utc = excluded.updated_at_utc
             WHERE cover_thumbs.status <> 'arrived';
            """;
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$tier", tier);
        command.Parameters.AddWithValue("$error", TruncateCoverThumbError(error));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>稿件删除/进回收站时清账（盘上文件由调用方删，这里只管台账）。</summary>
    public void DeleteCoverThumbs(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cover_thumbs WHERE video_id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        command.ExecuteNonQuery();
    }

    /// <summary>删档时顺手抹掉这条馆藏的缩略件与台账行（与 <c>DropPreviewForRecycle</c> 同一套规矩）。
    /// 缩略不进回收站：它是 ffmpeg 从封面原图转出的派生物，随时能再生，收进去只是白占地方；
    /// 馆藏恢复后什么都不用做——缩略补员下一轮发现它欠着，自己再切回来。</summary>
    internal void DropCoverThumbsForRecycle(SqliteConnection connection, long videoId, string? bvid)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM cover_thumbs WHERE video_id = $id;";
            delete.Parameters.AddWithValue("$id", videoId);
            delete.ExecuteNonQuery();
        }

        // 文件名前缀直接拿来当通配符，所以只认字母数字：脏 bvid 休想借个 * 扫到别人的件。
        if (string.IsNullOrWhiteSpace(bvid) || !bvid.All(char.IsAsciiLetterOrDigit) || !Directory.Exists(_paths.Thumbs))
        {
            return;
        }

        foreach (var extension in new[] { CoverThumbTiers.FormatWebp, CoverThumbTiers.FormatJpg })
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_paths.Thumbs, $"{bvid}_*.{extension}"))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 有人正读着这一档：账已销、地址再也不会被拼出来，文件留给下一轮 PruneStaleFiles 收拾
            }
        }
    }

    /// <summary>
    /// 已落盘档位拼成前端要的那串（逗号相接的文件名，形如 <c>BV1xx_352x220-6389012-3f2a1.webp</c>）。
    /// 一条都没有就回 null——前端照旧走本地原图/热链，「没切过」在契约上就是 null，不是空串。
    /// </summary>
    public string? GetCoverThumbManifest(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(rel_path, ',') FROM cover_thumbs WHERE video_id = $id AND status = 'arrived';";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 待切片名单：有封面产物行、却还欠着档的稿件。存量全量回填与新货自愈共用这一条查询。
    /// 「欠着」有两种：一档都没落成的，以及<b>已落成的行还带着旧算法版本戳</b>的（存量追新规矩的通道）。
    /// 后者非有不可：同名件在 <c>TryBuildAsync</c> 里靠 <c>File.Exists</c> 直接跳过不重切，
    /// 只把 <see cref="CoverThumbTiers.AlgoVersion"/> 一老一新而不进名单，旧档会永远赖在盘上。
    /// 某档连续失败到 <paramref name="maxAttempts"/> 次就整体搁笔，别让一张坏封面每小时拖全站重切一遍。
    /// </summary>
    public List<CoverThumbCandidate> ListVideosNeedingCoverThumbs(int limit = 40, int maxAttempts = 5)
    {
        var rows = new List<CoverThumbCandidate>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id,
                   v.bvid,
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1) AS cover_rel
              FROM videos v
             WHERE cover_rel IS NOT NULL
               AND (
                     NOT EXISTS (SELECT 1 FROM cover_thumbs t WHERE t.video_id = v.id AND t.status = 'arrived')
                     -- 版本戳尾段就是算法版本（<ticks>-<len>-<algo>），所以「不是 %-<当前版>」就是旧存量。
                     -- 这一支的判据方向不许写反：写成 LIKE 会把「已是最新」当成欠账，补员员就永远转个没完。
                     -- 也不用 instr 比子串：那会把旧版 a2 认成新版 a20 的「已是最新」。
                     OR EXISTS (SELECT 1 FROM cover_thumbs t WHERE t.video_id = v.id AND t.status = 'arrived' AND t.src_stamp NOT LIKE $algoLike)
                   )
               AND NOT EXISTS (SELECT 1 FROM cover_thumbs t WHERE t.video_id = v.id AND t.attempts >= $cap)
             ORDER BY v.id
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$cap", maxAttempts);
        command.Parameters.AddWithValue("$algoLike", $"%-{CoverThumbTiers.AlgoVersion}");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2))
            {
                continue; // 没 bvid 的存量行造不出可反查归属的文件名，宁可留原图
            }

            rows.Add(new CoverThumbCandidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>这条稿件的 bvid（缩略文件名靠它，而私有反查的文件名那条腿也靠它）；查无此行回 null。</summary>
    public string? GetBvid(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bvid FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 这条稿件当前当封面使的那个本地件（相对 <c>data/videos</c>）。
    /// 取的是最早那一行（<c>ORDER BY a.id LIMIT 1</c>）——与全站十几处读侧同一张嘴，
    /// 切的是屏幕上那张，不是盘上另一张同名旧件（<see cref="GetArtifactRel"/> 取的是最新行，别看错）。
    /// </summary>
    public string? GetCoverRel(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rel_path FROM artifacts WHERE video_id = $id AND kind = 'cover' ORDER BY id LIMIT 1;";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() as string;
    }

    private static string TruncateCoverThumbError(string? error)
        => string.IsNullOrEmpty(error) ? "未知原因" : (error.Length <= 300 ? error : error[..300] + "…");
}
