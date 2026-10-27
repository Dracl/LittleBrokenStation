using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// M6 UP 主动态缓存读写：targets 是分站盯的 UP 名单（订阅台账种子 + 站长手加），
/// dynamics 是抓回的动态橱窗（dynamic_id 全局唯一，抓回即定格，不回改）。
/// 端点的公开橱窗只走这里的只读方法；写入全在抓取 worker 与站长管理面。
/// </summary>
public sealed partial class StationDb
{
    public sealed record DynamicTargetRow(
        long Id,
        long Mid,
        string? Nickname,
        string? FaceUrl,
        bool Enabled,
        string? LastOffset,
        string? LastFetchAtUtc,
        string CreatedAtUtc,
        long Count);

    public sealed record CachedDynamic(
        long Id,
        string DynamicId,
        long TargetMid,
        long? AuthorMid,
        string? AuthorName,
        string Type,
        string? Text,
        string? PicsJson,
        string? Bvid,
        string? PublishedAtUtc,
        string CachedAtUtc,
        bool InStation);

    /// <summary>落一条动态：dynamic_id 撞车即 IGNORE（返回 false = 历史已有）。新落行顺手把 target 的空白名片补齐。</summary>
    public bool UpsertDynamic(string dynamicId, long targetMid, long? authorMid, string? authorName,
        string type, string? text, string? picsJson, string? bvid, string? publishedAtUtc)
    {
        using var connection = Open(_dbPath);
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO dynamics
                (dynamic_id, target_mid, author_mid, author_name, type, text, pics_json, bvid, published_at_utc, cached_at_utc)
            VALUES ($did, $target, $author, $authorName, $type, $text, $pics, $bvid, $published, $cached);
            """;
        insert.Parameters.AddWithValue("$did", dynamicId);
        insert.Parameters.AddWithValue("$target", targetMid);
        insert.Parameters.AddWithValue("$author", (object?)authorMid ?? DBNull.Value);
        insert.Parameters.AddWithValue("$authorName", (object?)authorName ?? DBNull.Value);
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pics", (object?)picsJson ?? DBNull.Value);
        insert.Parameters.AddWithValue("$bvid", (object?)bvid ?? DBNull.Value);
        insert.Parameters.AddWithValue("$published", (object?)publishedAtUtc ?? DBNull.Value);
        insert.Parameters.AddWithValue("$cached", DateTimeOffset.UtcNow.ToString("O"));
        var fresh = insert.ExecuteNonQuery() > 0;

        if (fresh && !string.IsNullOrWhiteSpace(authorName))
        {
            // 名片回填只在 target 还是空白时动笔，站长改过的昵称不覆盖。
            using var backfill = connection.CreateCommand();
            backfill.CommandText = """
                UPDATE dynamics_targets SET nickname = $name WHERE mid = $target AND (nickname IS NULL OR nickname = '');
                """;
            backfill.Parameters.AddWithValue("$name", authorName);
            backfill.Parameters.AddWithValue("$target", targetMid);
            backfill.ExecuteNonQuery();
        }

        return fresh;
    }

    /// <summary>橱窗列表：新→旧（id 倒序），可按 target 过滤，beforeId 游标翻页；inStation 现 JOIN videos。</summary>
    public IReadOnlyList<CachedDynamic> ListDynamics(long? targetMid, int limit = 20, long? beforeId = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.dynamic_id, d.target_mid, d.author_mid, d.author_name, d.type, d.text,
                   d.pics_json, d.bvid, d.published_at_utc, d.cached_at_utc,
                   (SELECT 1 FROM videos v WHERE v.bvid = d.bvid LIMIT 1)
            FROM dynamics d
            WHERE ($target IS NULL OR d.target_mid = $target)
              AND ($before IS NULL OR d.id < $before)
            ORDER BY d.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$target", (object?)targetMid ?? DBNull.Value);
        command.Parameters.AddWithValue("$before", (object?)beforeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

        using var reader = command.ExecuteReader();
        var list = new List<CachedDynamic>();
        while (reader.Read())
        {
            list.Add(new CachedDynamic(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                !reader.IsDBNull(11)));
        }

        return list;
    }

    public IReadOnlyList<DynamicTargetRow> ListDynamicTargets()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.mid, t.nickname, t.face_url, t.enabled, t.last_offset, t.last_fetch_at_utc, t.created_at_utc,
                   (SELECT COUNT(*) FROM dynamics d WHERE d.target_mid = t.mid)
            FROM dynamics_targets t
            ORDER BY t.id;
            """;
        using var reader = command.ExecuteReader();
        var list = new List<DynamicTargetRow>();
        while (reader.Read())
        {
            list.Add(new DynamicTargetRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8)));
        }

        return list;
    }

    /// <summary>登记盯的 UP：mid 撞车静默跳过（返回 false = 已在盯）。种子可带名片。</summary>
    public bool AddDynamicTarget(long mid, string? nickname = null, string? faceUrl = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO dynamics_targets (mid, nickname, face_url, created_at_utc)
            VALUES ($mid, $name, $face, $now);
            """;
        command.Parameters.AddWithValue("$mid", mid);
        command.Parameters.AddWithValue("$name", (object?)nickname ?? DBNull.Value);
        command.Parameters.AddWithValue("$face", (object?)faceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    public int RemoveDynamicTarget(long mid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        // 只撤盯梢名单，已抓的动态留在橱窗（可能是别人转发的同一批货，删了不可再生）。
        command.CommandText = "DELETE FROM dynamics_targets WHERE mid = $mid;";
        command.Parameters.AddWithValue("$mid", mid);
        return command.ExecuteNonQuery();
    }

    public void SetTargetOffset(long mid, string? offset, string fetchedAtUtc)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dynamics_targets SET last_offset = $offset, last_fetch_at_utc = $at WHERE mid = $mid;";
        command.Parameters.AddWithValue("$offset", (object?)offset ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", fetchedAtUtc);
        command.Parameters.AddWithValue("$mid", mid);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 订阅台账灌种子：subscriptions KV 里 Kind=="up" 的行取 mid，INSERT OR IGNORE 进 targets。
    /// MID 认法与 SubscriptionCrawler.ParseMid 同款（纯数字 / space.bilibili.com/123 带不带斜杠）——
    /// 那边在 Server 层、这边在 Infrastructure，跨层引用不值当，照抄并注明同源。
    /// </summary>
    public int SeedTargetsFromSubscriptions()
    {
        var json = GetSetting("subscriptions");
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        var mids = new List<long>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var kind = row.TryGetProperty("Kind", out var k) ? k.GetString()
                    : row.TryGetProperty("kind", out var kl) ? kl.GetString() : null;
                if (!string.Equals(kind, "up", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = row.TryGetProperty("Target", out var t) ? t.GetString()
                    : row.TryGetProperty("target", out var tl) ? tl.GetString() : null;
                if (TryParseMid(target) is { } mid)
                {
                    mids.Add(mid);
                }
            }
        }
        catch (JsonException)
        {
            return 0; // 台账坏了不硬猜，等站长手动加
        }

        if (mids.Count == 0)
        {
            return 0;
        }

        using var connection = Open(_dbPath);
        var added = 0;
        foreach (var mid in mids.Distinct())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO dynamics_targets (mid, created_at_utc) VALUES ($mid, $now);";
            command.Parameters.AddWithValue("$mid", mid);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            added += command.ExecuteNonQuery();
        }

        return added;
    }

    /// <summary>SubscriptionCrawler.ParseMid 的同源副本（跨层不互抄引用）：纯数字、space.bilibili.com/123（含 h5/big 变体）、带斜杠尾巴。</summary>
    private static long? TryParseMid(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var t = target.Trim().TrimEnd('/');
        if (long.TryParse(t, out var direct) && direct > 0)
        {
            return direct;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            t, @"space\.bilibili\.com/(?:h5/)?(?:big/)?(\d{2,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, out var mid) && mid > 0 ? mid : null;
    }
}
