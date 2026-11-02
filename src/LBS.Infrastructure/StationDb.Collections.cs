using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 站内合集：会员自己攒的片单（站内版收藏夹）。三条口径：
/// ① 归属只认 owner_member_id——谁开的集谁管，站长兜底代管（403 归端点判，台账不掺和身份）；
/// ② item 只存 (collection_id, video_id) 这一对，片名封面一律读时 JOIN videos 现取
///    （与 watch_history 同一哲学：片被删了这条 item 就自然看不见，不留在库里当死行）；
/// ③ 只数已入站（Arrived）的货：排队里的稿子连档案都不齐，不配占集里的名额，
///    所以计数、封面、片单三处一律带 status='Arrived' 的同一把尺子；
/// ④ 可见性（public/private）：公开集人人可见，私有集只有东家与站长可见（尺子从 VisibilityFilter 那一个出口出）；
///    集里的逐条稿件另按自己那一行的可见性剔，两边都量在取数那一刻。
/// 同片在同集只占一坑（主键挡重，重复入集幂等）；集与 item 的时间都存 "O" 格式 UTC。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>集名封顶：合集标题不是小作文，超了这个数就回炉（简介不限，那儿本来就是写话的地方）。</summary>
    public const int MaxCollectionNameLength = 30;

    /// <summary>合集清单的一条（封面是最新一条已到站 item 的封面，集里没到货就是 null）。</summary>
    /// <remarks>Visibility 随简报一起出：逛站页的私有角标与抽屉设置窗的回显都读这一格。
    /// 这一格本来就归简报管：早先简报里没带，端点只好另开一趟单问台账（同一件事两个出口，
    /// 清单与详情很轻易就错开），现在 <see cref="BriefSelect"/> 一趟 SELECT 带齐。</remarks>
    public record CollectionBrief(
        long Id,
        string Name,
        string? Intro,
        long OwnerMemberId,
        string? OwnerName,
        string CreatedAtUtc,
        long ItemCount,
        string? Cover,
        VideoVisibility Visibility);

    /// <summary>集里的一部片（形状与首页/分区卡阵一字不差，前端同一张卡直接复用）。</summary>
    /// <remarks>卡阵那三件（播放量 / 时长 / 弹幕数）与悬停预览必须齐着给：合集详情页用的就是首页那张
    /// VideoCard，少一个字段卡上就多一排「—」（站长原话：「合集里面的视频怎么没有参数」）。
    /// 取数口径与 ListVideos 同一把尺子：时长取首 P（idx 最小那条），弹幕数数弹幕档案的 Dialogue 行。</remarks>
    public sealed record CollectionItem(
        long Id,
        string Bvid,
        string? Title,
        string? CoverUrl,
        string? LocalCover,
        string? UpName,
        string? ViewCountText,
        string? DurationText,
        int? DanmakuCount,
        string? PreviewUrl,
        string? PublishedAtUtc,
        string? PublishText,
        string? Zone,
        // 这位 UP 的 mid（尾追一列，老调用点不受影响）：合集详情页用的就是首页那张 VideoCard，
        // 缺了它卡上的 UP 名只能画成死文本，进不了 UP 空间那一页。
        long? UpMid);

    /// <summary>合集详情 = 简报 + 片单（只含已到站的，最新入集的排前头）。</summary>
    public sealed record CollectionDetail(
        long Id,
        string Name,
        string? Intro,
        long OwnerMemberId,
        string? OwnerName,
        string CreatedAtUtc,
        long ItemCount,
        string? Cover,
        VideoVisibility Visibility,
        IReadOnlyList<CollectionItem> Items
    ) : CollectionBrief(Id, Name, Intro, OwnerMemberId, OwnerName, CreatedAtUtc, ItemCount, Cover, Visibility);

    /// <summary>清单 SELECT 的共用前段：一条 SQL 把简报八件套凑齐（计数与封面都是相关子查询，集就几十条，不打架）。
    /// videoFilter 是两条子查询里那把量稿件的尺子（计数与封面各自独立口径，两处都得剔，不然会出现
    /// 「数字说有三条、片单只摆两条」那种拆牌）；集本身那一侧的谓词由调用方接在 WHERE 里。</summary>
    private static string BriefSelect(string videoFilter) => $"""
        SELECT c.id, c.name, c.intro, c.owner_member_id, m.nickname, c.created_at_utc,
               (SELECT COUNT(*) FROM collection_items ci
                  JOIN videos v ON v.id = ci.video_id
                 WHERE ci.collection_id = c.id AND v.status = 'Arrived' AND {videoFilter}),
               (SELECT COALESCE((SELECT '/media/' || a.rel_path FROM artifacts a
                                  WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1), v.cover_url)
                  FROM collection_items ci
                  JOIN videos v ON v.id = ci.video_id
                 WHERE ci.collection_id = c.id AND v.status = 'Arrived' AND {videoFilter}
                 ORDER BY ci.added_at_utc DESC, ci.video_id DESC LIMIT 1),
               c.visibility
        FROM collections c
        LEFT JOIN members m ON m.id = c.owner_member_id
        """;

    /// <summary>开一个集：时间当场盖（"O" 格式 UTC），东家就是点这个按钮的席位。回集 id。</summary>
    public long CreateCollection(string name, string? intro, long ownerMemberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO collections (name, intro, owner_member_id, created_at_utc)
            VALUES ($name, $intro, $owner, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$intro", string.IsNullOrWhiteSpace(intro) ? DBNull.Value : intro.Trim());
        command.Parameters.AddWithValue("$owner", ownerMemberId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// 销一个集：集行与它的 item 行收进同一个事务一起删（留一堆无主 item 是给自己埋雷）。
    /// 集本来不存在回 false（404 归端点报）。片本身一根手指都不碰——它是馆藏，不归集管。
    /// </summary>
    public bool DeleteCollection(long id)
    {
        using var connection = Open(_dbPath);
        using var transaction = connection.BeginTransaction();

        using (var purge = connection.CreateCommand())
        {
            purge.Transaction = transaction;
            purge.CommandText = "DELETE FROM collection_items WHERE collection_id = $id;";
            purge.Parameters.AddWithValue("$id", id);
            purge.ExecuteNonQuery();
        }

        using var drop = connection.CreateCommand();
        drop.Transaction = transaction;
        drop.CommandText = "DELETE FROM collections WHERE id = $id;";
        drop.Parameters.AddWithValue("$id", id);
        var dropped = drop.ExecuteNonQuery() > 0;
        transaction.Commit();
        return dropped;
    }

    /// <summary>
    /// 改一个集：名字/简介/可见性，三个-null 就是「这一栏不动」，改到行回 true。
    /// 集本来不存在回 false（404 归端点报）；三个都没给值就不空转一圈 UPDATE（回 false，没话改）。
    /// 名字去首尾空白（长度归端点拿 MaxCollectionNameLength 卡）；简介空白存 null，与建集同一口径。
    /// 只改可见性走 SetCollectionVisibility，不拿这口一井。
    /// </summary>
    public bool UpdateCollection(long id, string? name, string? intro, VideoVisibility? visibility)
    {
        var sets = new List<string>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();

        // SET 子句只拼白名单列名，值一律走绑参（这三栏之外没东西能进来）。
        if (name is not null)
        {
            sets.Add("name = $name");
            command.Parameters.AddWithValue("$name", name.Trim());
        }

        if (intro is not null)
        {
            sets.Add("intro = $intro");
            command.Parameters.AddWithValue("$intro", string.IsNullOrWhiteSpace(intro) ? DBNull.Value : intro.Trim());
        }

        if (visibility is { } vis)
        {
            sets.Add("visibility = $vis");
            command.Parameters.AddWithValue("$vis", VisibilityCodec.ToColumn(vis));
        }

        if (sets.Count == 0)
        {
            return false;
        }

        command.CommandText = $"UPDATE collections SET {string.Join(", ", sets)} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>只改集的可见性（那枚「公开/私有」开关走这条，不碰名字与简介）；集不存在回 false。</summary>
    public bool SetCollectionVisibility(long id, VideoVisibility visibility)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE collections SET visibility = $vis WHERE id = $id;";
        command.Parameters.AddWithValue("$vis", VisibilityCodec.ToColumn(visibility));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 往集里放一部片：INSERT OR IGNORE 幂等——同片再放一次撞主键就当没这回事，照样回 true。
    /// 集不存在回 false（404 归端点报）；片在不在馆藏也由端点先查，这里不替它把关。
    /// </summary>
    public bool AddItem(long collectionId, long videoId)
    {
        using var connection = Open(_dbPath);
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM collections WHERE id = $id;";
            probe.Parameters.AddWithValue("$id", collectionId);
            if (probe.ExecuteScalar() is null)
            {
                return false;
            }
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO collection_items (collection_id, video_id, added_at_utc)
            VALUES ($c, $v, $now);
            """;
        insert.Parameters.AddWithValue("$c", collectionId);
        insert.Parameters.AddWithValue("$v", videoId);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return true;
    }

    /// <summary>把一部片挪出集（只删这一对，片照旧在馆藏里）；集里本来就没这部回 false。</summary>
    public bool RemoveItem(long collectionId, long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM collection_items WHERE collection_id = $c AND video_id = $v;";
        command.Parameters.AddWithValue("$c", collectionId);
        command.Parameters.AddWithValue("$v", videoId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 合集清单（最新开的排前头）。ownerMemberId 给了就只报这位的集（个人空间与「我的集」吃这个），
    /// null = 全站公开清单。封面/计数只认已到站的 item，且两边都过尺子：别人的私有稿不占计数、也不当封面；
    /// 私有集本身只对东家与站长露面。东家席位没了 ownerName 就落 null，集不跟着消失。
    /// </summary>
    public IReadOnlyList<CollectionBrief> ListCollections(long? ownerMemberId = null, VisibilityScope? scope = null)
    {
        var (visSql, bindVis) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "v");
        var (colSql, bindCol) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "c");
        var list = new List<CollectionBrief>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = BriefSelect(visSql) + $"""
             WHERE ($owner IS NULL OR c.owner_member_id = $owner) AND {colSql}
             ORDER BY c.created_at_utc DESC, c.id DESC;
            """;
        command.Parameters.AddWithValue("$owner", (object?)ownerMemberId ?? DBNull.Value);
        bindVis(command);
        bindCol(command);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadBrief(reader));
        }

        return list;
    }

    /// <summary>合集详情（片单最新入集的排前头）；查无回 null，对这位藏着的私有集也回 null（404 归端点报，
    /// 不给「集存在但你看不着」两种面孔）；计数/封面/片单三处同一把尺子。</summary>
    public CollectionDetail? GetCollection(long id, VisibilityScope? scope = null)
    {
        var effective = scope ?? VisibilityScope.PublicOnly;
        var (visSql, bindVis) = VisibilityFilter(effective, "v");
        var (colSql, bindCol) = VisibilityFilter(effective, "c");
        using var connection = Open(_dbPath);
        using var brief = connection.CreateCommand();
        brief.CommandText = BriefSelect(visSql) + $" WHERE c.id = $id AND {colSql};";
        brief.Parameters.AddWithValue("$id", id);
        bindVis(brief);
        bindCol(brief);
        using (var reader = brief.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            var head = ReadBrief(reader);
            return new CollectionDetail(
                head.Id,
                head.Name,
                head.Intro,
                head.OwnerMemberId,
                head.OwnerName,
                head.CreatedAtUtc,
                head.ItemCount,
                head.Cover,
                head.Visibility,
                ReadItems(connection, id, effective));
        }
    }

    /// <summary>集在不在（端点判归属前的第一道）。</summary>
    public bool CollectionExists(long id)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM collections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>集的主人家席位 id；查无回 null（403 归端点判，站长兜底）。</summary>
    public long? GetCollectionOwner(long id)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner_member_id FROM collections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long owner ? owner : null;
    }

    /// <summary>集里全部片子的 id（含没到货的：这是「这部在不在集里」的判定料，一次拉全比逐条问省腿）。
    /// 两边尺子都量：集本身对这位藏着（私有集不是东家也不是站长）→ 空手而归，
    /// 不借「在不在集里」这道缝确认集的存在；集里的逐条稿件另按自己那一行的可见性剔，
    /// 别人的私货也不该从这道缝里被验出来。端点拿这口一井时记得递同一把尺子。</summary>
    public IReadOnlyList<long> GetCollectionVideoIds(long collectionId, VisibilityScope? scope = null)
    {
        var effective = scope ?? VisibilityScope.PublicOnly;
        var (visSql, bindVis) = VisibilityFilter(effective, "v");
        var (colSql, bindCol) = VisibilityFilter(effective, "c");
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ci.video_id
            FROM collection_items ci
            JOIN videos v ON v.id = ci.video_id
            JOIN collections c ON c.id = ci.collection_id
            WHERE ci.collection_id = $c AND {visSql} AND {colSql};
            """;
        command.Parameters.AddWithValue("$c", collectionId);
        bindVis(command);
        bindCol(command);
        var list = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    /// <summary>bvid 换馆藏 id（入集时端点先拿它验「这部在不在架」）；查无回 null。</summary>
    public long? GetVideoIdByBvid(string bvid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM videos WHERE bvid = $bvid;";
        command.Parameters.AddWithValue("$bvid", bvid);
        return command.ExecuteScalar() is long id ? id : null;
    }

    private static CollectionBrief ReadBrief(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        VisibilityCodec.FromColumn(reader.IsDBNull(8) ? null : reader.GetString(8)));

    /// <summary>片单：卡阵十二件套 + 有效分区，口径照抄首页（片名走有效值那张嘴（站内覆写优先）；封面走馆藏本地件优先、
    /// 没归档留 B 站热链兑底；时长取首 P；弹幕数走 CountDanmakuLines 数档案里的 Dialogue 行）。</summary>
    /// <remarks>不是 static 了：弹幕数要碰盘上的档案文件，那是本类的实例活。计数子查询先筛出
    /// 「有弹幕档案的」那几条才去读文件，没档案的行一次盘都不碰。</remarks>
    private IReadOnlyList<CollectionItem> ReadItems(SqliteConnection connection, long collectionId, VisibilityScope scope)
    {
        var (visSql, bindVis) = VisibilityFilter(scope, "v");
        var list = new List<CollectionItem>();
        var pendingDanmaku = new List<int>(); // 待数弹幕的行下标（reader 开着不开新连接，出循环再算）
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id, v.bvid, {EffTitleExpr}, v.cover_url,
                   (SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   vm.up_name, vm.view_count, p.duration_text,
                   (SELECT pr.rel_path FROM previews pr WHERE pr.video_id = v.id AND pr.status = 'Arrived' LIMIT 1),
                   v.published_at_utc, vm.publish_text, {EffZoneExpr},
                   (SELECT COUNT(1) FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'danmaku'),
                   vm.up_mid
            FROM collection_items ci
            JOIN videos v ON v.id = ci.video_id
            LEFT JOIN video_meta vm ON vm.video_id = v.id
            LEFT JOIN pages p ON p.video_id = v.id
                 AND p.idx = (SELECT MIN(idx) FROM pages WHERE video_id = v.id)
            WHERE ci.collection_id = $c AND v.status = 'Arrived' AND {visSql}
            ORDER BY ci.added_at_utc DESC, ci.video_id DESC;
            """;
        command.Parameters.AddWithValue("$c", collectionId);
        bindVis(command);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var index = list.Count;
            list.Add(new CollectionItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                null, // 弹幕数：出循环后按档案文件数，读不到不给数（宁缺毋假）
                reader.IsDBNull(8) ? null : "/previews/" + reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(13) ? null : reader.GetInt64(13)));
            if (!reader.IsDBNull(12) && reader.GetInt64(12) > 0)
            {
                pendingDanmaku.Add(index);
            }
        }

        foreach (var index in pendingDanmaku)
        {
            list[index] = list[index] with { DanmakuCount = CountDanmakuLines(list[index].Id) };
        }

        return list;
    }
}
