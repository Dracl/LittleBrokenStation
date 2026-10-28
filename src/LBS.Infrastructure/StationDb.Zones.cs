using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 站内分区：稿件的「户口」。有效分区 = 站长改派（videos.site_zone）优先，没改派就继承 B 站归档的 zone
/// （video_meta.zone，形如「音乐>翻唱」）。分区页只认顶级段（'>' 前那一截）：站内几百条稿子摆几十个子分区是给自己找堵。
/// 只数已入站（Arrived）的货：取消条目连档案都不齐，不配占分区名额。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>分区名封顶：B 站现役最长分区名也超不过这个数，防着把一句长话粘进来当户口。</summary>
    public const int MaxZoneNameLength = 40;

    public sealed record ZoneEntry(string Name, long Count);

    public sealed record ZoneVideo(
        long Id,
        string Bvid,
        string? Title,
        string? CoverUrl,
        string? LocalCover,
        string? UpName,
        string? PublishedAtUtc,
        string? PublishText,
        string Zone);

    /// <summary>有效分区表达式（全站唯一口径）：改派优先，其次 B 站归档 zone，两头皆空就没有户口。</summary>
    private const string EffZoneExpr = "COALESCE(NULLIF(v.site_zone, ''), vm.zone)";

    /// <summary>
    /// 顶级分区清单：全量拉有效分区，在 C# 里掰顶级段聚合。
    /// 库里就几百行，不拿 SQL 的 LIKE 玩前缀匹配——分区名里的 '%' '_' 反咬一口不值得。
    /// </summary>
    public IReadOnlyList<ZoneEntry> ListZones()
    {
        var tally = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var zone in EnumEffectiveZones())
        {
            if (TopOf(zone) is { Length: > 0 } top)
            {
                tally[top] = tally.GetValueOrDefault(top) + 1;
            }
        }

        return tally
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ZoneEntry(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// 某顶级分区的稿件（到站时间倒序）。name 空或查无 → 空表不报 404：
    /// 分区页靠换瓷砖刷新下半屏，空手而归是常态，不该挨一脚。
    /// 同 ListZones 的理由，顶级段比对放在 C# 里做。
    /// </summary>
    public IReadOnlyList<ZoneVideo> ListZoneVideos(string? topZone)
    {
        var want = topZone?.Trim();
        if (string.IsNullOrEmpty(want))
        {
            return [];
        }

        var list = new List<ZoneVideo>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id, v.bvid, v.title, v.cover_url,
                   (SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   vm.up_name, v.published_at_utc, vm.publish_text, {EffZoneExpr}
            FROM videos v LEFT JOIN video_meta vm ON vm.video_id = v.id
            WHERE v.status = 'Arrived' AND {EffZoneExpr} IS NOT NULL
            ORDER BY v.created_at_utc DESC, v.id DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var zone = reader.GetString(8);
            if (!string.Equals(TopOf(zone), want, StringComparison.Ordinal))
            {
                continue;
            }

            list.Add(new ZoneVideo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                zone));
        }

        return list;
    }

    /// <summary>站长改派分区：null/空白 = 清空改派回继承；稿件不存在回 false（404 归端点报）。</summary>
    public bool SetSiteZone(long videoId, string? zone)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET site_zone = $zone WHERE id = $id;";
        command.Parameters.AddWithValue("$zone", string.IsNullOrWhiteSpace(zone) ? DBNull.Value : zone.Trim());
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 改派值的校验（端点与测试同一张嘴）：空 = 清空放行（zone 出 null）；
    /// 非空限 40 字、至多一层 '>'、两侧不许空白——「父>子」是户口格式，不是自由发挥。
    /// </summary>
    public static bool TryNormalizeZone(string? raw, out string? zone, out string message)
    {
        zone = null;
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            message = "已清空改派，这片的户口回跟 B 站档案走";
            return true;
        }

        if (trimmed.Length > MaxZoneNameLength)
        {
            message = $"分区名 {MaxZoneNameLength} 字封顶，这不是写简介的地方";
            return false;
        }

        var parts = trimmed.Split('>');
        if (parts.Length > 2 || parts.Any(p => string.IsNullOrWhiteSpace(p)))
        {
            message = "分区只认「父>子」一层，最多一个 >，两侧都得有字";
            return false;
        }

        zone = string.Join('>', parts.Select(p => p.Trim()));
        message = "改派落定";
        return true;
    }

    private static string? TopOf(string zone)
    {
        var idx = zone.IndexOf('>');
        var top = (idx >= 0 ? zone[..idx] : zone).Trim();
        return top.Length == 0 ? null : top;
    }

    private IEnumerable<string> EnumEffectiveZones()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EffZoneExpr}
            FROM videos v LEFT JOIN video_meta vm ON vm.video_id = v.id
            WHERE v.status = 'Arrived' AND {EffZoneExpr} IS NOT NULL;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetString(0);
        }
    }
}
