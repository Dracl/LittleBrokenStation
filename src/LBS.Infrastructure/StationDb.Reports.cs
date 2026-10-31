namespace LBS.Infrastructure;

/// <summary>
/// 站内弹幕的举报台账（最小闭环）：分站朋友对某条站内弹幕点个「举报」，这里落一张定格单，
/// 站长在站长室看一眼决定「删」还是「忽略」。
/// 三条铁律：① 举报即定格——弹幕正文、被举报人、举报人、视频链接在落单那一刻抄成本表自己的列，
/// 之后弹幕被删、席位改名都不回头改这张单（读列表一律直读本表，不 JOIN members，弹幕被删举报卡照样展示）；
/// ② 举报不预隐藏——本表只管登记，danmaku_local 一个字段都不动，pending 的弹幕照常飘；
/// ③ 处置只此一次——UPDATE 带 status='pending' 条件，抢不到的人吃 409，不会两个人抢着处置同一条。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>举报单待处理态（其余为 deleted / dismissed）。</summary>
    public const string DanmakuReportPending = "pending";

    /// <summary>一张举报单的全文（全是落单那一刻的快照列，站长室直接摆，不回头查别处）。</summary>
    public sealed record DanmakuReportRow(
        long Id,
        long DmId,
        long VideoId,
        string? Bvid,
        int PageIdx,
        double TSec,
        string Text,
        long? SenderMemberId,
        string? SenderDmid,
        string? SenderName,
        string? DmCreatedAtUtc,
        long ReporterMemberId,
        string? ReporterName,
        string? Reason,
        string Status,
        string CreatedAtUtc,
        string? HandledAtUtc,
        // 片子现在还在不在架：只决定「去放映厅看这条弹幕」那条链给不给点，
        // 上面那些快照列一个字不改（单子落库那一刻定格，删片不拖它下水）。
        bool VideoInStation = false);

    /// <summary>被举报弹幕的定格素材：举报那一刻要抄进台账的字段（连弹幕自己的发送时间，删了也还查得到）。</summary>
    public sealed record LocalDanmakuSnapshot(
        long Id,
        long VideoId,
        int PageIdx,
        double TSec,
        string Text,
        string? SenderDmid,
        long? SenderMemberId,
        string CreatedAtUtc);

    /// <summary>单条站内弹幕的定格素材；行不存在返回 null（弹幕已被删同样查不到，交端点报 404）。</summary>
    public LocalDanmakuSnapshot? GetLocalDanmakuSnapshot(long dmId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, video_id, page_idx, t_sec, text, sender_dmid, member_id, created_at_utc
            FROM danmaku_local WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", dmId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new LocalDanmakuSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetDouble(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetString(7));
    }

    /// <summary>
    /// 落一张举报单（快照由调用方备好）。同一席位对同一条弹幕只许举报一次：
    /// 撞唯一索引就当「已经举报过」幂等返回（Created=false + 那张旧单的 id），绝不炸前端。
    /// </summary>
    public (bool Created, long Id) AddDanmakuReport(
        LocalDanmakuSnapshot dm, string? bvid, string? senderName, long reporterMemberId, string? reporterName, string? reason)
    {
        using var connection = Open(_dbPath);
        int affected;
        using (var insert = connection.CreateCommand())
        {
            // 同一席位对同一条弹幕只许一次：撞唯一索引就当已经举报过，不报错炸前端。
            insert.CommandText = """
                INSERT INTO danmaku_reports
                    (dm_id, video_id, bvid, page_idx, t_sec, text,
                     sender_member_id, sender_dmid, sender_name, dm_created_at_utc,
                     reporter_member_id, reporter_name, reason, status, created_at_utc)
                VALUES ($dm, $v, $bvid, $p, $t, $x, $sm, $sd, $sn, $dmcreated, $rm, $rn, $reason, 'pending', $now)
                ON CONFLICT (dm_id, reporter_member_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$dm", dm.Id);
            insert.Parameters.AddWithValue("$v", dm.VideoId);
            insert.Parameters.AddWithValue("$bvid", (object?)bvid ?? DBNull.Value);
            insert.Parameters.AddWithValue("$p", dm.PageIdx);
            insert.Parameters.AddWithValue("$t", dm.TSec);
            insert.Parameters.AddWithValue("$x", dm.Text);
            insert.Parameters.AddWithValue("$sm", (object?)dm.SenderMemberId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sd", (object?)dm.SenderDmid ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sn", (object?)senderName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$dmcreated", (object?)dm.CreatedAtUtc ?? DBNull.Value);
            insert.Parameters.AddWithValue("$rm", reporterMemberId);
            insert.Parameters.AddWithValue("$rn", (object?)reporterName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            // ON CONFLICT DO NOTHING 时 sqlite 只改 0 行：拿这个数区分「新单」与「早就举报过」。
            affected = insert.ExecuteNonQuery();
        }

        // 插没插进去都按 (dm_id, reporter_member_id) 这把唯一尺子回读那张单：
        // 刚落的与早就举报过的都能拿到，拿不到只会是 0（理论上不发生，不拿它编一个假 id）。
        using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT id FROM danmaku_reports WHERE dm_id = $dm AND reporter_member_id = $rm;";
        lookup.Parameters.AddWithValue("$dm", dm.Id);
        lookup.Parameters.AddWithValue("$rm", reporterMemberId);
        return (affected > 0, Convert.ToInt64(lookup.ExecuteScalar()));
    }

    /// <summary>
    /// 站长视野的举报清单：pendingOnly=true 只看待处理的。全按快照列直读（不 JOIN members、不 JOIN danmaku_local），
    /// 弹幕删了、席位走了，这张单上的字都不跟着变。新单在前。
    /// </summary>
    public IReadOnlyList<DanmakuReportRow> ListDanmakuReports(bool pendingOnly, int limit = 300)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        var statusFilter = pendingOnly ? " WHERE status = $pending" : string.Empty;
        command.CommandText = $"""
            SELECT id, dm_id, video_id, bvid, page_idx, t_sec, text,
                   sender_member_id, sender_dmid, sender_name, dm_created_at_utc,
                   reporter_member_id, reporter_name, reason, status, created_at_utc, handled_at_utc,
                   -- 按 bvid 判死活，与用户点的那条 /watch/ 链逐字同口径（/api/watch 就是按 bvid 查的）：
                   -- 片进回收站时链死，捐回或重新投同一部时链活。不改动任何快照列，不算破「直读本表」那条铁规。
                   EXISTS (SELECT 1 FROM videos v WHERE v.bvid = danmaku_reports.bvid AND v.status = 'Arrived')
            FROM danmaku_reports{statusFilter}
            ORDER BY created_at_utc DESC, id DESC
            LIMIT $limit;
            """;
        if (pendingOnly)
        {
            command.Parameters.AddWithValue("$pending", DanmakuReportPending);
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));

        using var reader = command.ExecuteReader();
        var list = new List<DanmakuReportRow>();
        while (reader.Read())
        {
            list.Add(new DanmakuReportRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetDouble(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetInt32(17) == 1));
        }

        return list;
    }

    /// <summary>举报单计数：status 给 null 就数全部（站长室的 h3 徽章只数 pending）。</summary>
    public long CountDanmakuReports(string? status = DanmakuReportPending)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = status is null
            ? "SELECT COUNT(*) FROM danmaku_reports;"
            : "SELECT COUNT(*) FROM danmaku_reports WHERE status = $status;";
        if (status is not null)
        {
            command.Parameters.AddWithValue("$status", status);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// 处置一张举报单（盖章 + 翻状态）。UPDATE 只认 status='pending'：
    /// 抢到的那个返回 Claimed=true（调用方这时才去真删弹幕），抢不到的拿到 WasPending=false → 端点报 409。
    /// Exists=false 表示这张单压根不存在 → 404。
    /// </summary>
    public (bool Exists, bool Claimed, long DmId) HandleDanmakuReport(long id, string status)
    {
        using var connection = Open(_dbPath);
        long dmId;
        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT dm_id FROM danmaku_reports WHERE id = $id;";
            lookup.Parameters.AddWithValue("$id", id);
            var found = lookup.ExecuteScalar();
            if (found is null)
            {
                return (false, false, 0);
            }

            dmId = Convert.ToInt64(found);
        }

        using (var claim = connection.CreateCommand())
        {
            claim.CommandText = """
                UPDATE danmaku_reports SET status = $status, handled_at_utc = $now
                WHERE id = $id AND status = $pending;
                """;
            claim.Parameters.AddWithValue("$status", status);
            claim.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            claim.Parameters.AddWithValue("$id", id);
            claim.Parameters.AddWithValue("$pending", DanmakuReportPending);
            return (true, claim.ExecuteNonQuery() > 0, dmId);
        }
    }
}
