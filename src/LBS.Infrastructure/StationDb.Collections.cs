using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 站内合集：会员自己攒的片单（站内版收藏夹）。三条口径：
/// ① 归属只认 owner_member_id——谁开的集谁管，站长兜底代管（403 归端点判，台账不掺和身份）；
/// ② item 只存 (collection_id, video_id) 这一对，片名封面一律读时 JOIN videos 现取
///    （与 watch_history 同一哲学：片被删了这条 item 就自然看不见，不留在库里当死行）；
/// ③ 只数已入站（Arrived）的货：排队里的稿子连档案都不齐，不配占集里的名额，
///    所以计数、封面、片单三处一律带 status='Arrived' 的同一把尺子。
/// 同片在同集只占一坑（主键挡重，重复入集幂等）；集与 item 的时间都存 "O" 格式 UTC。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>集名封顶：合集标题不是小作文，超了这个数就回炉（简介不限，那儿本来就是写话的地方）。</summary>
    public const int MaxCollectionNameLength = 30;

    /// <summary>合集清单的一条（封面是最新一条已到站 item 的封面，集里没到货就是 null）。</summary>
    public record CollectionBrief(
        long Id,
        string Name,
        string? Intro,
        long OwnerMemberId,
        string? OwnerName,
        string CreatedAtUtc,
        long ItemCount,
        string? Cover);

    /// <summary>集里的一部片（形状与首页/分区卡阵一字不差，前端同一张卡直接复用）。</summary>
    public sealed record CollectionItem(
        long Id,
        string Bvid,
        string? Title,
        string? CoverUrl,
        string? LocalCover,
        string? UpName,
        string? PublishedAtUtc,
        string? PublishText,
        string? Zone);

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
        IReadOnlyList<CollectionItem> Items
    ) : CollectionBrief(Id, Name, Intro, OwnerMemberId, OwnerName, CreatedAtUtc, ItemCount, Cover);

    /// <summary>清单 SELECT 的共用前段：一条 SQL 把简报八件套凑齐（计数与封面都是相关子查询，集就几十条，不打架）。</summary>
    private const string BriefSelect = """
        SELECT c.id, c.name, c.intro, c.owner_member_id, m.nickname, c.created_at_utc,
               (SELECT COUNT(*) FROM collection_items ci
                  JOIN videos v ON v.id = ci.video_id
                 WHERE ci.collection_id = c.id AND v.status = 'Arrived'),
               (SELECT COALESCE((SELECT '/media/' || a.rel_path FROM artifacts a
                                  WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1), v.cover_url)
                  FROM collection_items ci
                  JOIN videos v ON v.id = ci.video_id
                 WHERE ci.collection_id = c.id AND v.status = 'Arrived'
                 ORDER BY ci.added_at_utc DESC, ci.video_id DESC LIMIT 1)
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
    /// null = 全站公开清单。封面/计数只认已到站的 item；东家席位没了 ownerName 就落 null，集不跟着消失。
    /// </summary>
    public IReadOnlyList<CollectionBrief> ListCollections(long? ownerMemberId = null)
    {
        var list = new List<CollectionBrief>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = BriefSelect + """
             WHERE ($owner IS NULL OR c.owner_member_id = $owner)
             ORDER BY c.created_at_utc DESC, c.id DESC;
            """;
        command.Parameters.AddWithValue("$owner", (object?)ownerMemberId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadBrief(reader));
        }

        return list;
    }

    /// <summary>合集详情（片单最新入集的排前头）；查无回 null（404 归端点报）。</summary>
    public CollectionDetail? GetCollection(long id)
    {
        using var connection = Open(_dbPath);
        using var brief = connection.CreateCommand();
        brief.CommandText = BriefSelect + " WHERE c.id = $id;";
        brief.Parameters.AddWithValue("$id", id);
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
                ReadItems(connection, id));
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

    /// <summary>集里全部片子的 id（含没到货的：这是「这部在不在集里」的判定料，一次拉全比逐条问省腿）。</summary>
    public IReadOnlyList<long> GetCollectionVideoIds(long collectionId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT video_id FROM collection_items WHERE collection_id = $c;";
        command.Parameters.AddWithValue("$c", collectionId);
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
        reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <summary>片单：卡阵八件套 + 有效分区，封面口径照抄首页（kind='cover' 本地件优先，无归档留 B 站热链兑底）。</summary>
    private static IReadOnlyList<CollectionItem> ReadItems(SqliteConnection connection, long collectionId)
    {
        var list = new List<CollectionItem>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id, v.bvid, v.title, v.cover_url,
                   (SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   vm.up_name, v.published_at_utc, vm.publish_text, {EffZoneExpr}
            FROM collection_items ci
            JOIN videos v ON v.id = ci.video_id
            LEFT JOIN video_meta vm ON vm.video_id = v.id
            WHERE ci.collection_id = $c AND v.status = 'Arrived'
            ORDER BY ci.added_at_utc DESC, ci.video_id DESC;
            """;
        command.Parameters.AddWithValue("$c", collectionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CollectionItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return list;
    }
}
