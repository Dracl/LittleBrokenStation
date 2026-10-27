using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 站内信箱与私信读写（M5.2/M5.3，M5.4 起全面挂在站内席位 id 上）：
/// 系统传票（欢迎/归档/失败回执）与全站公告统一落 messages 表，用户往来落 dm_messages。
/// 写信的是服务端自己（MemberMail 送信员与站长公告），成员只读信与标读；
/// 标读永远带 to_member_id 条件，越席摸别人的信摸不到。旧 *_mid 列由迁移回填后不再写。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>一封信：fromName 由 from_mid 现查 members.nickname（系统/公告信为 null）。</summary>
    public sealed record InboxMessage(
        long Id,
        long ToMemberId,
        long? FromMid,
        string? FromName,
        string Kind,
        string Title,
        string Body,
        string? Link,
        string CreatedAtUtc,
        string? ReadAtUtc);

    /// <summary>投稿回执的收信席位与素材：无归属行（SubmitterMemberId 为 null）不发信。</summary>
    public sealed record VideoMailTarget(long? SubmitterMemberId, string Bvid, string? Title, string? LastError);

    public long SendToMember(long toMemberId, string kind, string title, string body, string? link = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages (to_mid, to_member_id, from_mid, kind, title, body, link, created_at_utc)
            VALUES (0, $to, NULL, $kind, $title, $body, $link, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$to", toMemberId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$link", (object?)link ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>全站播送：只发给 active 席位（封禁/待邀不发），返回送达席数。</summary>
    public int BroadcastToActive(string kind, string title, string body)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages (to_mid, to_member_id, from_mid, kind, title, body, link, created_at_utc)
            SELECT 0, id, NULL, $kind, $title, $body, NULL, $now
            FROM members
            WHERE status = $active;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$active", MemberStatus.Active);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>信箱清单：新→旧（id 倒序即落笔序），unreadOnly 只看未读，limit 封顶 200。</summary>
    public IReadOnlyList<InboxMessage> ListMessages(long toMemberId, int limit = 50, bool unreadOnly = false)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.to_member_id, m.from_mid, m.kind, m.title, m.body, m.link, m.created_at_utc, m.read_at_utc, s.nickname
            FROM messages m
            LEFT JOIN members s ON s.mid = m.from_mid
            WHERE m.to_member_id = $to AND ($unreadOnly = 0 OR m.read_at_utc IS NULL)
            ORDER BY m.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$to", toMemberId);
        command.Parameters.AddWithValue("$unreadOnly", unreadOnly ? 1 : 0);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        using var reader = command.ExecuteReader();
        var list = new List<InboxMessage>();
        while (reader.Read())
        {
            // record 声明序：(Id, ToMemberId, FromMid, FromName, Kind, Title, Body, Link, CreatedAtUtc, ReadAtUtc)
            list.Add(new InboxMessage(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return list;
    }

    public long CountUnread(long toMemberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE to_member_id = $to AND read_at_utc IS NULL;";
        command.Parameters.AddWithValue("$to", toMemberId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>标读：to_member_id 不符即空手（返回 false，不报错）——越席摸信摸不到。</summary>
    public bool MarkRead(long id, long toMemberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET read_at_utc = $now WHERE id = $id AND to_member_id = $to AND read_at_utc IS NULL;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$to", toMemberId);
        return command.ExecuteNonQuery() > 0;
    }

    public int MarkAllRead(long toMemberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET read_at_utc = $now WHERE to_member_id = $to AND read_at_utc IS NULL;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$to", toMemberId);
        return command.ExecuteNonQuery();
    }

    /// <summary>投稿回执取材：收信席位 + 标题 + 失败原因，一行拿齐。</summary>
    public VideoMailTarget? GetVideoMailTarget(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT submitter_member_id, bvid, title, last_error FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new VideoMailTarget(
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    // ─── 用户间私信（M5.3，M5.4 起挂在席位 id 上）：往来说一行一发，会话在查询时按 peer 聚合 ───

    /// <summary>一条私信（校验在端点层，这层只写）。MsgType：text 普通信 / dynamic 分享动态（dynJson 存定格卡面）。</summary>
    public sealed record DmMessage(long Id, long FromMemberId, long ToMemberId, string Body, string CreatedAtUtc, string? ReadAtUtc, string? MsgType = null, string? DynJson = null);

    /// <summary>会话摘要：对方席位名片 + 最后一句 + 未读数（LastMsgType 让列表页知道最后一句是不是分享卡）。</summary>
    public sealed record DmThread(long PeerMemberId, string? PeerName, string? PeerFace, string LastBody, string LastAtUtc, long Unread, string? LastMsgType = null);

    /// <summary>管理员视野的一行私信（带双方名片与已读态）。</summary>
    public sealed record DmInspectionRow(
        long Id,
        long FromMemberId,
        string? FromName,
        long ToMemberId,
        string? ToName,
        string Body,
        string CreatedAtUtc,
        string? ReadAtUtc);

    public long SendDm(long fromMemberId, long toMemberId, string body, string? msgType = null, string? dynamicId = null, string? dynJson = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dm_messages (from_mid, to_mid, from_member_id, to_member_id, body, created_at_utc, msg_type, dynamic_id, dyn_json)
            VALUES (0, 0, $from, $to, $body, $now, $msgType, $dynId, $dynJson);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$from", fromMemberId);
        command.Parameters.AddWithValue("$to", toMemberId);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$msgType", (object?)msgType ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynId", (object?)dynamicId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynJson", (object?)dynJson ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>放映厅分享定格快照：bvid 必查到馆（Arrived 才算在馆）；封面本地优先（与观看页同形 /media/…），无归档回落热链。</summary>
    public sealed record VideoShareSnapshot(string Bvid, string? Title, string? Cover, string? UpName, string? DurationText);

    public VideoShareSnapshot? GetVideoShareSnapshot(string bvid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.bvid, v.title,
                   COALESCE((SELECT '/media/' || a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                            v.cover_url),
                   m.up_name,
                   (SELECT p.duration_text FROM pages p WHERE p.video_id = v.id ORDER BY p.idx LIMIT 1)
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            WHERE v.bvid = $bvid AND v.status = $status;
            """;
        command.Parameters.AddWithValue("$bvid", bvid);
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new VideoShareSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    /// <summary>会话列表：对 from=me OR to=me 的行按 peer 席位聚合，最新一句倒序。名片现查 members（优先昵称，素净席位落 @username）。</summary>
    public IReadOnlyList<DmThread> ListDmThreads(long memberId)
    {
        using var connection = Open(_dbPath);

        var peerIds = new List<long>();
        using (var peers = connection.CreateCommand())
        {
            peers.CommandText = """
                SELECT DISTINCT CASE WHEN from_member_id = $me THEN to_member_id ELSE from_member_id END
                FROM dm_messages WHERE from_member_id = $me OR to_member_id = $me;
                """;
            peers.Parameters.AddWithValue("$me", memberId);
            using var reader = peers.ExecuteReader();
            while (reader.Read())
            {
                peerIds.Add(reader.GetInt64(0));
            }
        }

        var roster = ListMembers().ToDictionary(m => m.Id);
        var threads = new List<DmThread>();
        foreach (var peer in peerIds)
        {
            using var last = connection.CreateCommand();
            last.CommandText = """
                SELECT body, created_at_utc, msg_type FROM dm_messages
                WHERE (from_member_id = $me AND to_member_id = $peer) OR (from_member_id = $peer AND to_member_id = $me)
                ORDER BY id DESC LIMIT 1;
                """;
            last.Parameters.AddWithValue("$me", memberId);
            last.Parameters.AddWithValue("$peer", peer);
            using var lastReader = last.ExecuteReader();
            if (!lastReader.Read())
            {
                continue; // 对端刚被拆空（理论不可达），跳过
            }

            var lastBody = lastReader.GetString(0);
            var lastAt = lastReader.GetString(1);
            var lastMsgType = lastReader.IsDBNull(2) ? null : lastReader.GetString(2);

            using var unread = connection.CreateCommand();
            unread.CommandText = "SELECT COUNT(*) FROM dm_messages WHERE to_member_id = $me AND from_member_id = $peer AND read_at_utc IS NULL;";
            unread.Parameters.AddWithValue("$me", memberId);
            unread.Parameters.AddWithValue("$peer", peer);
            var unreadCount = Convert.ToInt64(unread.ExecuteScalar());

            var name = roster.TryGetValue(peer, out var m) ? DisplayOf(m) : null;
            var face = roster.TryGetValue(peer, out var f) ? f.FaceUrl : null;
            threads.Add(new DmThread(peer, name, face, lastBody, lastAt, unreadCount, lastMsgType));
        }

        return threads
            .OrderByDescending(t => DateTime.Parse(t.LastAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind))
            .ThenByDescending(t => t.PeerMemberId)
            .ToList();
    }

    /// <summary>双方往来升序（拉取方自己决定要不要顺手标读）。</summary>
    public IReadOnlyList<DmMessage> ListDmConversation(long memberA, long memberB, int limit = 200)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, from_member_id, to_member_id, body, created_at_utc, read_at_utc, msg_type, dyn_json
            FROM dm_messages
            WHERE (from_member_id = $a AND to_member_id = $b) OR (from_member_id = $b AND to_member_id = $a)
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$a", memberA);
        command.Parameters.AddWithValue("$b", memberB);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        using var reader = command.ExecuteReader();
        var list = new List<DmMessage>();
        while (reader.Read())
        {
            list.Add(new DmMessage(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        list.Reverse(); // 取最新一批后翻回转序
        return list;
    }

    /// <summary>标读：只销「对方发给我」的未读，我发出去的自己读不算数。</summary>
    public int MarkDmRead(long byMemberId, long peerMemberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dm_messages SET read_at_utc = $now
            WHERE to_member_id = $me AND from_member_id = $peer AND read_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$me", byMemberId);
        command.Parameters.AddWithValue("$peer", peerMemberId);
        return command.ExecuteNonQuery();
    }

    public long CountDmUnread(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dm_messages WHERE to_member_id = $me AND read_at_utc IS NULL;";
        command.Parameters.AddWithValue("$me", memberId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>可私聊名单：active 席位（排除自己留端点层，这里给全量 active）。</summary>
    public IReadOnlyList<StationMember> GetActiveMembersForDirectory()
        => ListMembers().Where(m => m.Status == MemberStatus.Active).ToList();

    /// <summary>站长视野：全部私信（可锁一对席位往来），带双方名片，新→旧。</summary>
    public IReadOnlyList<DmInspectionRow> ListDmForAdmin(long? peerA, long? peerB, int limit = 500)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        var pairFilter = peerA is not null && peerB is not null
            ? " AND ((d.from_member_id = $a AND d.to_member_id = $b) OR (d.from_member_id = $b AND d.to_member_id = $a))"
            : string.Empty;
        command.CommandText = $"""
            SELECT d.id, d.from_member_id, d.to_member_id, d.body, d.created_at_utc, d.read_at_utc, f.nickname, f.username, t.nickname, t.username
            FROM dm_messages d
            LEFT JOIN members f ON f.id = d.from_member_id
            LEFT JOIN members t ON t.id = d.to_member_id
            WHERE 1 = 1{pairFilter}
            ORDER BY d.id DESC
            LIMIT $limit;
            """;
        if (peerA is not null && peerB is not null)
        {
            command.Parameters.AddWithValue("$a", peerA.Value);
            command.Parameters.AddWithValue("$b", peerB.Value);
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));

        using var reader = command.ExecuteReader();
        var list = new List<DmInspectionRow>();
        while (reader.Read())
        {
            list.Add(new DmInspectionRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                PickName(reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)),
                reader.GetInt64(2),
                PickName(reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return list;
    }

    /// <summary>席位对人的展示名：B 站昵称优先，素净席位落 @username。</summary>
    public static string? DisplayOf(StationMember m) =>
        !string.IsNullOrWhiteSpace(m.Nickname) ? m.Nickname
        : !string.IsNullOrWhiteSpace(m.Username) ? "@" + m.Username
        : null;

    private static string? PickName(string? nickname, string? username) =>
        !string.IsNullOrWhiteSpace(nickname) ? nickname
        : !string.IsNullOrWhiteSpace(username) ? "@" + username
        : null;
}
