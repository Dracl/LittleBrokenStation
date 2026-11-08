using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 单条站内弹幕的回收站（11-08 站长拍板：「单删弹幕进回收站」）。
/// 为什么另开一张表而不挤 videos 那台回收站：recycle_bin 的形状是「一部片」——
/// video_id / bvid NOT NULL，外加 file_count / bytes / page_count 一堆对一条弹幕毫无意义的账目列，
/// 而 RestoreFromRecycle 在清单里没有 Video 段时直接判「捞不回来」，还要先做一轮文件搬运。
/// 要塞一条弹幕进去得伪造一个空 Video 字典并绕开文件搬运，比新表更贵。
/// 为什么不给 danmaku_local 加状态列：那条铁律（StationDb.cs 里写着）是「pending 的弹幕照常飘，
/// 处置才真删」，加状态列等于把「已删」变成第二种可见态，弹幕查询全得跟着加判据。
/// 删的行搬进本表、原表照旧只剩活着的弹幕，读路径一个字不用改。
/// 两条删弹幕的路（发送者自删、举报处置删）共用本表，用 source 列分开记账：
/// 'self' = 发送者自己收回去，'report' = 站长按举报单删的，'admin' = 站长直接删的。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>回收站里的一条弹幕（全是删那一刻的快照，原行没了也照样摆得出）。</summary>
    public sealed record DanmakuRecycleEntry(
        long Id,
        long DmId,
        long VideoId,
        string? Bvid,
        int PageIdx,
        double TSec,
        string Text,
        string? SenderDmid,
        bool SenderOwner,
        long? MemberId,
        string? SenderName,
        string? DmCreatedAtUtc,
        string DeletedAtUtc,
        string? DeletedBy,
        string Source,
        // 片子还在不在架：捐回要有去处，片已进视频回收站时这一条捐不动（前端据此把捐回钮摆灰）。
        bool VideoInStation);

    /// <summary>
    /// 把一条弹幕搬进回收站（快照 + 删原行，同一连接内做完）。行不存在返回 0——
    /// 调用方据此分辨「弹幕早没了」与「搬走了」，举报处置那条路尤其要这个数（原行被发送者先删过就不必再搬）。
    /// bvid 现查一次带进快照：只为让站长室能摆「去看这部片」的链，不参与任何判据。
    /// </summary>
    public long MoveDanmakuToRecycle(long dmId, string? deletedBy, long? deletedByMemberId, string source)
    {
        using var connection = Open(_dbPath);
        DanmakuRecycleSnapshot? row;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT id, video_id, page_idx, t_sec, text, created_at_utc, sender_dmid, sender_owner, member_id
                FROM danmaku_local WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", dmId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return 0;
            }

            row = new DanmakuRecycleSnapshot(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetDouble(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                reader.IsDBNull(8) ? null : reader.GetInt64(8));
        }

        var bvid = GetVideoBvidInPlace(connection, row.Value.VideoId);
        using (var park = connection.CreateCommand())
        {
            park.CommandText = """
                INSERT INTO danmaku_recycle
                    (dm_id, video_id, bvid, page_idx, t_sec, text, sender_dmid, sender_owner, member_id,
                     dm_created_at_utc, deleted_at_utc, deleted_by, deleted_by_member_id, source)
                VALUES ($dm, $v, $bvid, $p, $t, $x, $sd, $so, $m, $dmcreated, $now, $by, $byid, $source);
                SELECT last_insert_rowid();
                """;
            park.Parameters.AddWithValue("$dm", row.Value.Id);
            park.Parameters.AddWithValue("$v", row.Value.VideoId);
            park.Parameters.AddWithValue("$bvid", (object?)bvid ?? DBNull.Value);
            park.Parameters.AddWithValue("$p", row.Value.PageIdx);
            park.Parameters.AddWithValue("$t", row.Value.TSec);
            park.Parameters.AddWithValue("$x", row.Value.Text);
            park.Parameters.AddWithValue("$sd", (object?)row.Value.SenderDmid ?? DBNull.Value);
            park.Parameters.AddWithValue("$so", row.Value.SenderOwner ? 1 : 0);
            park.Parameters.AddWithValue("$m", (object?)row.Value.MemberId ?? DBNull.Value);
            park.Parameters.AddWithValue("$dmcreated", (object?)row.Value.CreatedAtUtc ?? DBNull.Value);
            park.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            park.Parameters.AddWithValue("$by", (object?)deletedBy ?? DBNull.Value);
            park.Parameters.AddWithValue("$byid", (object?)deletedByMemberId ?? DBNull.Value);
            park.Parameters.AddWithValue("$source", source);
            var recycleId = Convert.ToInt64(park.ExecuteScalar());

            using var erase = connection.CreateCommand();
            erase.CommandText = "DELETE FROM danmaku_local WHERE id = $id;";
            erase.Parameters.AddWithValue("$id", dmId);
            if (erase.ExecuteNonQuery() == 0)
            {
                // 快照落了、原行却没删动（并发下被人抢先删了）：把刚塞进去的快照一起收回来，
                // 绝不留一条「回收站里躺着、其实没人删过」的假账。
                using var rollback = connection.CreateCommand();
                rollback.CommandText = "DELETE FROM danmaku_recycle WHERE id = $id;";
                rollback.Parameters.AddWithValue("$id", recycleId);
                rollback.ExecuteNonQuery();
                return 0;
            }

            return recycleId;
        }
    }

    /// <summary>回收站清单（新删的在前）。sender_name 现查席位：席位走了给 null，与弹幕列表那套「只是没脸」一致。</summary>
    /// <summary>回收站列表（新→旧）。<paramref name="selfMemberId"/> 非空时只留「这位自己收回的那几条」（个人空间的自捐用）。</summary>
    public IReadOnlyList<DanmakuRecycleEntry> ListDanmakuRecycle(int limit = 200, long? selfMemberId = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        var where = selfMemberId is null ? "" : "WHERE r.source = 'self' AND r.member_id = $m";
        command.CommandText = $"""
            SELECT r.id, r.dm_id, r.video_id, r.bvid, r.page_idx, r.t_sec, r.text,
                   r.sender_dmid, r.sender_owner, r.member_id, m.nickname, m.username,
                   r.dm_created_at_utc, r.deleted_at_utc, r.deleted_by, r.source,
                   EXISTS (SELECT 1 FROM videos v WHERE v.id = r.video_id AND v.status = 'Arrived')
            FROM danmaku_recycle r
            LEFT JOIN members m ON m.id = r.member_id
            {where}
            ORDER BY r.id DESC
            LIMIT $limit;
            """;
        if (selfMemberId is not null)
        {
            command.Parameters.AddWithValue("$m", selfMemberId.Value);
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        using var reader = command.ExecuteReader();
        var list = new List<DanmakuRecycleEntry>();
        while (reader.Read())
        {
            var nickname = reader.IsDBNull(10) ? null : reader.GetString(10);
            var username = reader.IsDBNull(11) ? null : reader.GetString(11);
            // 名片口径与站内其余处子一致：昵称优先，没昵称落 @用户名，席位走了给 null（只是没脸）。
            var seatName = nickname is { Length: > 0 } n ? n
                : username is { Length: > 0 } u ? $"@{u}"
                : null;
            list.Add(new DanmakuRecycleEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetDouble(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                !reader.IsDBNull(8) && reader.GetInt32(8) == 1,
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                seatName,
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetString(15),
                reader.GetInt32(16) == 1));
        }

        return list;
    }

    /// <summary>回收站计数（站长室那枚徽章）。</summary>
    /// <summary>
    /// 取一条回收行的<b>归属</b>（谁动的手、以哪个席位的名义）：自捐那口得先验「这是不是你自己收回的」再动手，
    /// 不能让任何人拿一排行 id 就把站长替人删的那条捐回来。查无此行回 null。
    /// </summary>
    public (string Source, long? MemberId, long DmId)? GetDanmakuRecycleOwner(long recycleId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source, member_id, dm_id FROM danmaku_recycle WHERE id = $id;";
        command.Parameters.AddWithValue("$id", recycleId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2));
    }

    public long CountDanmakuRecycle()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM danmaku_recycle;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// 捐回一条弹幕：**连原 id 一起捐回**（与整片回收站「连站内弹幕的 id 都不变」同一口径）。
    /// 保住原号不是怀旧：举报单存的是 dm_id，换成新号那张单的「去看这条弹幕」就链死了，
    /// 而 (dm_id, reporter_member_id) 上有唯一索引，换了号会把「同一个人再举报同一条」的幂等判据打歪。
    /// created_at_utc / sender_dmid / sender_owner / member_id 也原样带回：
    /// 「谁在什么时候说的」不能因为站长反悔一次就变了，自删判据（认 lbs_dmid）也还认得它。
    /// 片子不在架时拒捐（回 (false, 原因)），免得塞回一条没人看得见的孤儿弹幕。
    /// </summary>
    public (bool Ok, string Message) RestoreDanmakuFromRecycle(long recycleId)
    {
        using var connection = Open(_dbPath);
        DanmakuRecycleSnapshot? row;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT dm_id, video_id, page_idx, t_sec, text, dm_created_at_utc, sender_dmid, sender_owner, member_id
                FROM danmaku_recycle WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", recycleId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return (false, "回收站里没有这一条");
            }

            row = new DanmakuRecycleSnapshot(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetDouble(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                reader.IsDBNull(8) ? null : reader.GetInt64(8));
        }

        using (var alive = connection.CreateCommand())
        {
            alive.CommandText = "SELECT 1 FROM videos WHERE id = $v AND status = 'Arrived';";
            alive.Parameters.AddWithValue("$v", row.Value.VideoId);
            if (alive.ExecuteScalar() is null)
            {
                return (false, "这部片子已经不在架上了，捐回去也没地方摆");
            }
        }

        using (var taken = connection.CreateCommand())
        {
            // 撞号护栅：那个位置已经被别的弹幕占了（理论上只会是捐过一回又被删的竞态），不拿 INSERT 去碰 UNIQUE。
            taken.CommandText = "SELECT 1 FROM danmaku_local WHERE id = $dm;";
            taken.Parameters.AddWithValue("$dm", row.Value.Id);
            if (taken.ExecuteScalar() is not null)
            {
                return (false, "这个位置已经有另一条弹幕了，捐回去会撞号，先把它处理掉再试");
            }
        }

        using (var back = connection.CreateCommand())
        {
            back.CommandText = """
                INSERT INTO danmaku_local (id, video_id, page_idx, t_sec, text, created_at_utc, sender_dmid, sender_owner, member_id)
                VALUES ($dm, $v, $p, $t, $x, $created, $sd, $so, $m);
                """;
            back.Parameters.AddWithValue("$dm", row.Value.Id);
            back.Parameters.AddWithValue("$v", row.Value.VideoId);
            back.Parameters.AddWithValue("$p", row.Value.PageIdx);
            back.Parameters.AddWithValue("$t", row.Value.TSec);
            back.Parameters.AddWithValue("$x", row.Value.Text);
            // 原说话时刻优先；老行没有 created_at_utc（理论不发生）才退回现在，不拿当下冒充当初。
            back.Parameters.AddWithValue("$created", (object?)row.Value.CreatedAtUtc
                ?? DateTimeOffset.UtcNow.ToString("O"));
            back.Parameters.AddWithValue("$sd", (object?)row.Value.SenderDmid ?? DBNull.Value);
            back.Parameters.AddWithValue("$so", row.Value.SenderOwner ? 1 : 0);
            back.Parameters.AddWithValue("$m", (object?)row.Value.MemberId ?? DBNull.Value);
            back.ExecuteNonQuery();
        }

        using (var erase = connection.CreateCommand())
        {
            erase.CommandText = "DELETE FROM danmaku_recycle WHERE id = $id;";
            erase.Parameters.AddWithValue("$id", recycleId);
            erase.ExecuteNonQuery();
        }

        return (true, "这条弹幕已捐回，飘在原处");
    }

    /// <summary>
    /// 按原弹幕 id 捐回（举报重开用：那张单只认得 dm_id，不知道回收站里那行的 id）。
    /// 同名多条（同一条弹幕被删过两遗）时捐最新那一遗。
    /// </summary>
    public (bool Ok, string Message) RestoreDanmakuByDmId(long dmId)
    {
        using var connection = Open(_dbPath);
        using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT id FROM danmaku_recycle WHERE dm_id = $dm ORDER BY id DESC LIMIT 1;";
        lookup.Parameters.AddWithValue("$dm", dmId);
        return lookup.ExecuteScalar() is long recycleId
            ? RestoreDanmakuFromRecycle(recycleId)
            : (false, "回收站里没有这条弹幕（可能从来没进过，或已被销毁）");
    }

    /// <summary>彻底销毁一条回收站里的弹幕（真删，回 false = 本来就没有）。</summary>
    public bool PurgeDanmakuRecycle(long recycleId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM danmaku_recycle WHERE id = $id;";
        command.Parameters.AddWithValue("$id", recycleId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>清空弹幕回收站（回清掉几条）。</summary>
    public int PurgeAllDanmakuRecycle()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM danmaku_recycle;";
        return command.ExecuteNonQuery();
    }

    /// <summary>bvid 现查（回收快照只为摆链用，查不到给 null，不拦正事）。</summary>
    private static string? GetVideoBvidInPlace(SqliteConnection connection, long videoId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bvid FROM videos WHERE id = $v;";
        command.Parameters.AddWithValue("$v", videoId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>搬运途中的临时形状（原行的字段，不含账目列）。</summary>
    private readonly record struct DanmakuRecycleSnapshot(
        long Id,
        long VideoId,
        int PageIdx,
        double TSec,
        string Text,
        string? CreatedAtUtc,
        string? SenderDmid,
        bool SenderOwner,
        long? MemberId);
}
