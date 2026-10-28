using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>预览台账的一行（rel_path 相对 data/previews，只可能是 &lt;bvid&gt;.mp4）。</summary>
public sealed record PreviewRow(
    long VideoId,
    int? PageIdx,
    string RelPath,
    int? Width,
    int? Height,
    double? Seconds,
    long? Bytes,
    string Status,
    string? LastError,
    int Attempts,
    DateTimeOffset CreatedAtUtc,
    /// <summary>本件生成耗时（墙钟秒，只量 ffmpeg 那一跑），与 Seconds（产物时长）各是各的。</summary>
    double? CostSeconds = null);

/// <summary>预览补员候选：到站、有本地成片、却没 Arrived 预览行的视频。LocalVideoRel 相对 data/videos。</summary>
public sealed record PreviewCandidate(long VideoId, string Bvid, string LocalVideoRel, int PageIdx);

/// <summary>站长室流水牌的一行：最近动过的预览（含失败的），带馆藏标题好认人。Seconds 是产物时长，CostSeconds 才是生成耗时。</summary>
public sealed record PreviewFeed(string Bvid, string? Title, bool Ok, long? Bytes, double? Seconds, string? Error, DateTimeOffset AtUtc, double? CostSeconds = null);

/// <summary>单件实测均值（最近若干条 Arrived 行算出来的）：站长室用它报「一条真花多久 / 真占多大」。
/// 没一条带耗时的行就整体回 null（前端退回按基线推算那一路），不拿估计数扮实测。</summary>
public sealed record PreviewUnit(double CostSeconds, long Bytes, double Seconds, int Count);

/// <summary>预览总账（一次算齐，站长室不必为一张卡片开五条查询）。bytes 走磁盘实扫，不编。</summary>
public sealed record PreviewStats(long Covered, long Pending, long Inflight, long Failed, long Bytes, long AvgBytes);

/// <summary>
/// 悬停预览台账：首页卡片悬停播的那段低码率无声短片，账记在 previews 表、文件躺在 data/previews。
/// 语义照 pages.codec 那套：没有行 = 没试过；Failed 且 attempts &lt; 3 才算还能重试；
/// Arrived 行是硬通货，失败的记账绝不许把它涂掉。
/// 与 artifacts 划清界限——预览行一旦混进产物表，缺件回炉就会拿它验盘、把整片误判缺件重下。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>预览状态字面量：只此两值（没有行 = 没试过，不占状态）。</summary>
    public static class PreviewStatus
    {
        public const string Arrived = "Arrived";
        public const string Failed = "Failed";

        /// <summary>连吃三次闭门羹就搁下，等站长在站长室手动重试——ffmpeg 坏了不该每小时撞一次。</summary>
        public const int MaxAttempts = 3;
    }

    /// <summary>预览补员名单：到站 + 有本地成片 + 还没成片的视频，新到站的优先（先伺候新货）。</summary>
    public IReadOnlyList<PreviewCandidate> ListVideosNeedingPreview(int limit = 40)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id, v.bvid, p.local_video_path, p.idx
            FROM videos v
            JOIN pages p ON p.video_id = v.id
                 AND p.local_video_path IS NOT NULL AND p.local_video_path <> ''
                 AND p.idx = (SELECT MIN(p2.idx) FROM pages p2
                              WHERE p2.video_id = v.id AND p2.local_video_path IS NOT NULL AND p2.local_video_path <> '')
            WHERE v.status = $status
              AND NOT EXISTS (SELECT 1 FROM previews q WHERE q.video_id = v.id AND q.status = $arrived)
              AND NOT EXISTS (SELECT 1 FROM previews q WHERE q.video_id = v.id AND q.status = $failed AND q.attempts >= $maxAttempts)
            ORDER BY v.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        command.Parameters.AddWithValue("$arrived", PreviewStatus.Arrived);
        command.Parameters.AddWithValue("$failed", PreviewStatus.Failed);
        command.Parameters.AddWithValue("$maxAttempts", PreviewStatus.MaxAttempts);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var list = new List<PreviewCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PreviewCandidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return list;
    }

    /// <summary>站长点名的补件名单（「补今天新货」走这里）：不论试过几次、也不论已有 Arrived 行，报名就重做。
    /// 所以这里故意不加 attempts / NOT EXISTS 那两道闸；唯一硬条件是有本地成片可转。名单外的一律不响。</summary>
    public IReadOnlyList<PreviewCandidate> ListPreviewCandidatesByBvids(IReadOnlyList<string> bvids)
    {
        var names = bvids.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
        if (names.Length == 0)
        {
            return [];
        }

        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id, v.bvid, p.local_video_path, p.idx
            FROM videos v
            JOIN pages p ON p.video_id = v.id
                 AND p.local_video_path IS NOT NULL AND p.local_video_path <> ''
                 AND p.idx = (SELECT MIN(p2.idx) FROM pages p2
                              WHERE p2.video_id = v.id AND p2.local_video_path IS NOT NULL AND p2.local_video_path <> '')
            WHERE v.status = $status
              AND v.bvid IN ({string.Join(",", names.Select((_, i) => "$b" + i))})
            ORDER BY v.id DESC;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        for (var i = 0; i < names.Length; i++)
        {
            command.Parameters.AddWithValue("$b" + i, names[i]);
        }

        var list = new List<PreviewCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PreviewCandidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return list;
    }

    /// <summary>某条馆藏的预览行（没有 = 没试过）。</summary>
    public PreviewRow? GetPreview(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT video_id, page_idx, rel_path, width, height, seconds, bytes, status, last_error, attempts, created_at_utc, cost_seconds
            FROM previews WHERE video_id = $id;
            """;
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PreviewRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt32(9),
            DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetDouble(11));
    }

    /// <summary>预览转正记账（先文件后行：文件没落到 data/previews 就别调这里）。UPSERT：已 Arrived 也整行覆盖，重新生成一次就当换了个新底片。
    /// costSeconds 是这一件 ffmpeg 实跑的墙钟秒（补员员拿表量的），没量到就留 null，不许拿产物时长顶上。</summary>
    public void RegisterPreviewArrived(long videoId, int? pageIdx, string relPath, int? width, int? height, double? seconds, long? bytes, double? costSeconds = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO previews (video_id, page_idx, rel_path, width, height, seconds, bytes, cost_seconds, status, last_error, attempts, created_at_utc)
            VALUES ($id, $page, $rel, $w, $h, $s, $b, $cost, $arrived, NULL, 0, $now)
            ON CONFLICT(video_id) DO UPDATE SET
                page_idx = excluded.page_idx,
                rel_path = excluded.rel_path,
                width = excluded.width,
                height = excluded.height,
                seconds = excluded.seconds,
                bytes = excluded.bytes,
                cost_seconds = excluded.cost_seconds,
                status = excluded.status,
                last_error = NULL,
                attempts = 0,
                created_at_utc = excluded.created_at_utc;
            """;
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$page", (object?)pageIdx ?? DBNull.Value);
        command.Parameters.AddWithValue("$rel", relPath);
        command.Parameters.AddWithValue("$w", (object?)width ?? DBNull.Value);
        command.Parameters.AddWithValue("$h", (object?)height ?? DBNull.Value);
        command.Parameters.AddWithValue("$s", (object?)seconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$b", (object?)bytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$cost", (object?)costSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$arrived", PreviewStatus.Arrived);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>预览失败记账：没行就插一条 Failed/attempts=1，有行 attempts+1 并盖掉 last_error。
    /// 已经是 Arrived 的行整条不动——转码偶尔失手不该把已经能播的预览抹成失败。</summary>
    public void MarkPreviewFailed(long videoId, string error)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO previews (video_id, page_idx, rel_path, status, last_error, attempts, created_at_utc)
            VALUES ($id, NULL, '', $failed, $error, 1, $now)
            ON CONFLICT(video_id) DO UPDATE SET
                attempts = previews.attempts + 1,
                last_error = excluded.last_error
            WHERE previews.status <> $arrived;
            """;
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$failed", PreviewStatus.Failed);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$arrived", PreviewStatus.Arrived);
        command.ExecuteNonQuery();
    }

    /// <summary>把 Failed 行清成可重试（attempts 归零、last_error 留着当病历）。bvids 给了就只点这些名，给空名单就一个不动；
    /// 已经可重试（attempts=0）的行不重计，回报数只算真被救回来的那些。</summary>
    public int ResetPreviewFailures(string[]? bvids)
    {
        var names = bvids?.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).Distinct().ToArray() ?? [];
        var onlySelected = names.Length > 0;
        if (bvids is not null && !onlySelected)
        {
            return 0; // 给了名单却一个名字都没认：宁可一个不动，不当「重置全部」办
        }

        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = onlySelected
            ? "UPDATE previews SET attempts = 0 WHERE status = $failed AND attempts <> 0 AND video_id IN (SELECT id FROM videos WHERE bvid IN ("
              + string.Join(",", names.Select((_, i) => "$b" + i)) + "));"
            : "UPDATE previews SET attempts = 0 WHERE status = $failed AND attempts <> 0;";
        command.Parameters.AddWithValue("$failed", PreviewStatus.Failed);
        if (onlySelected)
        {
            for (var i = 0; i < names.Length; i++)
            {
                command.Parameters.AddWithValue("$b" + i, names[i]);
            }
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>销账：预览行全清（文件由调用方删——StationDb 只管账，不擅自动磁盘）。</summary>
    public int DeleteAllPreviews()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM previews;";
        return command.ExecuteNonQuery();
    }

    /// <summary>预览总账。bytes/avgBytes 走磁盘实扫（.part 是转瞬即逝的在途件，不计账面）；
    /// inflight 由补员员自报——在途转码只有它自己知道，台账里查不出来。</summary>
    public PreviewStats PreviewStats(long inflight = 0)
    {
        using var connection = Open(_dbPath);
        long covered = 0;
        long pending = 0;
        long failed = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    (SELECT COUNT(1) FROM previews WHERE status = $arrived),
                    (SELECT COUNT(1) FROM previews WHERE status = $failed),
                    (SELECT COUNT(1) FROM videos v
                      WHERE v.status = $status
                        AND EXISTS (SELECT 1 FROM pages p WHERE p.video_id = v.id AND p.local_video_path IS NOT NULL AND p.local_video_path <> '')
                        AND NOT EXISTS (SELECT 1 FROM previews q WHERE q.video_id = v.id AND q.status = $arrived)
                        AND NOT EXISTS (SELECT 1 FROM previews q WHERE q.video_id = v.id AND q.status = $failed AND q.attempts >= $maxAttempts));
                """;
            command.Parameters.AddWithValue("$arrived", PreviewStatus.Arrived);
            command.Parameters.AddWithValue("$failed", PreviewStatus.Failed);
            command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
            command.Parameters.AddWithValue("$maxAttempts", PreviewStatus.MaxAttempts);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                covered = reader.GetInt64(0);
                failed = reader.GetInt64(1);
                pending = reader.GetInt64(2);
            }
        }

        long bytes = 0;
        try
        {
            if (Directory.Exists(_paths.Previews))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.Previews))
                {
                    if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        bytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 占用/没权限的文件不拦汇总，少算一个算一个
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录读不动就不给体积数（前端显破折号）
        }

        return new PreviewStats(covered, pending, inflight, failed, bytes, covered > 0 ? bytes / covered : 0);
    }

    /// <summary>单件实测均值（最近 take 条带耗时的 Arrived 行）：一条真跑几秒、真占多少字节、当时切片多长。
    /// 只统计 cost_seconds > 0 的行（旧行没这列的不算样本），一条都没有就回 null——前端退回推算轨，不拿基线常数扮实测。</summary>
    public PreviewUnit? PreviewUnit(int take = 20)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1), AVG(q.cost_seconds), AVG(q.bytes), AVG(q.seconds)
            FROM (
                SELECT cost_seconds, bytes, seconds
                FROM previews
                WHERE status = $arrived AND cost_seconds IS NOT NULL AND cost_seconds > 0 AND bytes IS NOT NULL AND bytes > 0
                ORDER BY id DESC
                LIMIT $take
            ) q;
            """;
        command.Parameters.AddWithValue("$arrived", PreviewStatus.Arrived);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 100));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) == 0)
        {
            return null;
        }

        var count = (int)reader.GetInt64(0);
        var cost = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
        var bytes = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetDouble(2));
        var seconds = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
        if (cost <= 0 || bytes <= 0)
        {
            return null;
        }

        return new PreviewUnit(Math.Round(cost, 2), bytes, Math.Round(seconds, 1), count);
    }

    /// <summary>站长室流水牌：最近动过的预览行（含失败的），新到的排前面，关联馆藏标题。</summary>
    public IReadOnlyList<PreviewFeed> ListRecentPreviews(int take = 20)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.bvid, v.title, p.status, p.bytes, p.seconds, p.last_error, p.created_at_utc, p.cost_seconds
            FROM previews p
            JOIN videos v ON v.id = p.video_id
            ORDER BY p.id DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 100));

        var list = new List<PreviewFeed>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PreviewFeed(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2) == PreviewStatus.Arrived,
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetDouble(7)));
        }

        return list;
    }

    /// <summary>失败点名册：还挂着 Failed 行的 bvid 与最后一手的错话（已放弃的那些也照列，站长得看得见）。</summary>
    public IReadOnlyList<(string Bvid, string? Error)> ListPreviewFailures(int take = 50)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.bvid, p.last_error
            FROM previews p
            JOIN videos v ON v.id = p.video_id
            WHERE p.status = $failed
            ORDER BY p.id DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$failed", PreviewStatus.Failed);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 200));

        var list = new List<(string, string?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return list;
    }

    /// <summary>站内有没有下载压力（排队 + 在途）：预览补员靠它让路，NAS 上 ffmpeg 会拖慢下载。</summary>
    public int CountInFlightDownloads()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM videos WHERE status = $pending OR status = $downloading;";
        command.Parameters.AddWithValue("$pending", VideoStatus.Pending.ToString());
        command.Parameters.AddWithValue("$downloading", VideoStatus.Downloading.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>删档时顺手抹掉这条馆藏的预览成片与台账行（在既有连接上办，跟删六张表的行同批）。
    /// 预览不进回收站：它是 ffmpeg 从成片转出的派生物，随时能再生，收进回收站只是白占地方；
    /// 馆藏恢复后什么都不用做——预览补员下一轮发现它欠着，自己再转一段回来。</summary>
    internal void DropPreviewForRecycle(SqliteConnection connection, long videoId)
    {
        var rel = null as string;
        using (var load = connection.CreateCommand())
        {
            load.CommandText = "SELECT rel_path FROM previews WHERE video_id = $id;";
            load.Parameters.AddWithValue("$id", videoId);
            rel = load.ExecuteScalar() as string;
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM previews WHERE video_id = $id;";
            delete.Parameters.AddWithValue("$id", videoId);
            delete.ExecuteNonQuery();
        }

        if (string.IsNullOrWhiteSpace(rel))
        {
            return;
        }

        // rel 只该是 <bvid>.mp4；真有野路径想越狱出预览区，就地当它不存在（绝不跟着跑出去删）。
        var root = Path.GetFullPath(_paths.Previews).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_paths.Previews, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
        {
            return;
        }

        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 播放器正攥着预览件：账已销，文件留给下次清扫（开机扫 .part、站长室「清空预览」都能收拾）
        }
    }
}
