using LBS.Domain;
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
        string Zone,
        // 这位 UP 的 mid（尾追一列，老调用点不受影响）：分区卡行上那个 UP 名要点进站内 UP 空间，
        // 只带名字进不了那一页（同名不是理论风险，而且那一页本来就是按 mid 取数）。
        long? UpMid);

    /// <summary>
    /// 有效分区表达式（全站唯一口径）：改派优先，其次 B 站归档 zone，两头皆空就没有户口。
    /// 本体在 StationDb.VideoSettings.cs 的 <c>EffZoneExprWith</c>（与简介那一对同族），这里只是 vm 那一枚的简写——
    /// 搜索那条 LEFT JOIN 给 video_meta 起的是 m，它得递别名，不能多养一份本体。
    /// </summary>
    private static string EffZoneExpr => EffZoneExprWith(VideoMetaAlias);

    /// <summary>
    /// 顶级分区清单：全量拉有效分区，在 C# 里掰顶级段聚合。
    /// 库里就几百行，不拿 SQL 的 LIKE 玩前缀匹配——分区名里的 '%' '_' 反咬一口不值得。
    /// </summary>
    public IReadOnlyList<ZoneEntry> ListZones(VisibilityScope? scope = null)
    {
        var tally = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var zone in EnumEffectiveZones(scope ?? VisibilityScope.PublicOnly))
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
    /// 同 ListZones 的理由，顶级段比对放在 C# 里做；尺子量在取数那一刻，页面上的货与 ListZones 那个数字同一口径。
    /// </summary>
    public IReadOnlyList<ZoneVideo> ListZoneVideos(string? topZone, VisibilityScope? scope = null)
    {
        var want = topZone?.Trim();
        if (string.IsNullOrEmpty(want))
        {
            return [];
        }

        var (visSql, bindVis) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "v");
        var list = new List<ZoneVideo>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id, v.bvid, {EffTitleExpr}, v.cover_url,
                   (SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   vm.up_name, v.published_at_utc, vm.publish_text, {EffZoneExpr}, vm.up_mid
            FROM videos v LEFT JOIN video_meta vm ON vm.video_id = v.id
            WHERE v.status = 'Arrived' AND {visSql} AND {EffZoneExpr} IS NOT NULL
            ORDER BY v.created_at_utc DESC, v.id DESC;
            """;
        bindVis(command);
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
                zone,
                reader.IsDBNull(9) ? null : reader.GetInt64(9)));
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

    /// <summary>分区总览入口卡的底图一摄：冠军分区、区内最火那条的 bvid 与它的封面（整区拿不到封面时 Cover 为 null，前端回落渐变）。</summary>
    public sealed record ZoneEntryCover(string? Zone, string? Bvid, string? Cover);

    /// <summary>入口卡底图的按日快照钥匙：同日反复取都是同一张，本地（UTC+8）零点过才重算。</summary>
    public const string ZoneEntryDayKey = "zone-entry-day";
    public const string ZoneEntryVideoKey = "zone-entry-video";

    /// <summary>
    /// 分区总览入口卡的底图：先按「次数」选冠军分区，再在冠军分区里取次数最高的那条，用它的封面。
    /// 次数 = events 台账里 playback.play / playback.heartbeat 的行数（改名前老行 watch.* 同吃），不去重人头（访客也算）——
    /// 与周报「最多人看」的去重口径有意分开：那边数观众人数，这边数热度。
    /// 只认在站的货，与 ListZones 同一口径；顶级分区仍旧在 C# 里掰，不去 SQL 玩 LIKE。
    /// 按日固定：快照存 settings（日期 + 中选 bvid），当日命中就直接复用，免得站长自己每放一条视频首页就当场跳图。
    /// 尺子判在取数这一刻：快照钉着的那条今天转私，对没钥匙的人就当它已下架、重算一张，
    /// 不给「转私当天还露一天」留缝。
    /// </summary>
    public ZoneEntryCover GetZoneEntryCover(DateTimeOffset nowUtc, VisibilityScope? scope = null)
    {
        var effective = scope ?? VisibilityScope.PublicOnly;
        var today = nowUtc.ToOffset(LocalWeekOffset).ToString("yyyyMMdd");
        var pinned = GetSetting(ZoneEntryDayKey) == today ? GetSetting(ZoneEntryVideoKey) : null;
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            var kept = ReadZoneEntryOf(pinned!, effective);
            if (kept != null)
            {
                return kept;
            }
            // 快照那条已下架、封面丢了、或对这位藏着：重新算一次，下面会把新结果盖回快照
        }

        var (picked, pinnable) = PickZoneEntry(effective);
        // 只把人人看得着的那张钉进快照：私有那枚只是本人这一趟的现算结果，钉下去访客来一趟就把它抹了，
        // 共用一枚钥匙互相盖没意思，所以私有的一律不钉（现算现给）。
        if (picked.Bvid != null && pinnable)
        {
            SetSetting(ZoneEntryDayKey, today);
            SetSetting(ZoneEntryVideoKey, picked.Bvid);
        }

        return picked;
    }

    /// <summary>台账里每条在站稿件的实况次数（形状与 ListZoneVideos 同路：封面子查询、有效分区、顶级段留 C# 剔）。
    /// 带回来一枚 Pinnable：中选那条是不是人人看得着（私有那条只回给本人，不进按日快照）。</summary>
    private (ZoneEntryCover Cover, bool Pinnable) PickZoneEntry(VisibilityScope scope)
    {
        var (visSql, bindVis) = VisibilityFilter(scope, "v");
        var rows = new List<(string Bvid, string? Cover, string? Zone, long Shots, long LastId, bool Public)>();
        using (var connection = Open(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT v.bvid,
                       COALESCE((SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                                v.cover_url),
                       {EffZoneExpr},
                       COUNT(*),
                       MAX(e.id),
                       v.visibility
                FROM events e
                JOIN videos v ON v.bvid = e.subject_id
                LEFT JOIN video_meta vm ON vm.video_id = v.id
                WHERE e.event IN ($play, $playOld, $beat, $beatOld) AND e.subject_type = 'video' AND v.status = 'Arrived' AND {visSql}
                GROUP BY v.id, v.bvid, v.title;
                """;
                command.Parameters.AddWithValue("$play", PlaybackPlayEvent);
                command.Parameters.AddWithValue("$playOld", LegacyPlayEvent);
                command.Parameters.AddWithValue("$beat", EventNames.PlaybackHeartbeat);
                command.Parameters.AddWithValue("$beatOld", LegacyHeartbeatEvent);
            bindVis(command);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    VisibilityCodec.FromColumn(reader.IsDBNull(5) ? null : reader.GetString(5)) == VideoVisibility.Public));
            }
        }

        var champion = rows
            .Where(r => r.Zone != null && TopOf(r.Zone!) is { Length: > 0 })
            .GroupBy(r => TopOf(r.Zone!)!, StringComparer.Ordinal)
            .Select(g => (Zone: g.Key, Shots: g.Sum(r => r.Shots), Items: g.OrderByDescending(r => r.Shots).ThenByDescending(r => r.LastId).ToList()))
            .OrderByDescending(x => x.Shots)
            .ThenBy(x => x.Zone, StringComparer.Ordinal)
            .FirstOrDefault();

        if (champion.Items is null)
        {
            return (new ZoneEntryCover(null, null, null), false);
        }

        // 冠军区内从最火往下找到第一条有封面的为止；整区都没图就交 null，入口卡回落深渐变
        var hit = champion.Items.FirstOrDefault(r => r.Cover != null);
        return hit.Cover is null
            ? (new ZoneEntryCover(champion.Zone, null, null), false)
            : (new ZoneEntryCover(champion.Zone, hit.Bvid, hit.Cover), hit.Public);
    }

    /// <summary>按 bvid 取入口卡底图一摄（走按日快照时用它）；已下架、无封面、或对这位藏着 → null，让上层重算。
    /// 可见性判在这条取数上，不拿快照的旧账当令：转私当天就不该再露。</summary>
    private ZoneEntryCover? ReadZoneEntryOf(string bvid, VisibilityScope scope)
    {
        var (visSql, bindVis) = VisibilityFilter(scope, "v");
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EffZoneExpr},
                   COALESCE((SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                            v.cover_url)
            FROM videos v LEFT JOIN video_meta vm ON vm.video_id = v.id
            WHERE v.bvid = $bvid AND v.status = 'Arrived' AND {visSql};
            """;
        command.Parameters.AddWithValue("$bvid", bvid);
        bindVis(command);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1))
        {
            return null;
        }

        return new ZoneEntryCover(reader.IsDBNull(0) ? null : TopOf(reader.GetString(0)), bvid, reader.GetString(1));
    }

    private static string? TopOf(string zone)
    {
        var idx = zone.IndexOf('>');
        var top = (idx >= 0 ? zone[..idx] : zone).Trim();
        return top.Length == 0 ? null : top;
    }

    private IEnumerable<string> EnumEffectiveZones(VisibilityScope scope)
    {
        var (visSql, bindVis) = VisibilityFilter(scope, "v");
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EffZoneExpr}
            FROM videos v LEFT JOIN video_meta vm ON vm.video_id = v.id
            WHERE v.status = 'Arrived' AND {visSql} AND {EffZoneExpr} IS NOT NULL;
            """;
        bindVis(command);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetString(0);
        }
    }
}
