using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 站内信箱读写（M5.2）：系统传票（欢迎信/归档回执/失败回执）与全站公告统一落 messages 表。
/// 写信的是服务端自己（MemberMail 送信员与站长公告），成员只读信与标读；
/// 标读永远带 to_mid 条件，越席摸别人的信摸不到。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>一封信：fromName 由 from_mid 现查 members.nickname（系统/公告信为 null）。</summary>
    public sealed record InboxMessage(
        long Id,
        long ToMid,
        long? FromMid,
        string? FromName,
        string Kind,
        string Title,
        string Body,
        string? Link,
        string CreatedAtUtc,
        string? ReadAtUtc);

    /// <summary>投稿回执的收信人与素材：无归属行（SubmitterMid 为 null）不发信。</summary>
    public sealed record VideoMailTarget(long? SubmitterMid, string Bvid, string? Title, string? LastError);

    public long SendToMember(long toMid, string kind, string title, string body, string? link = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages (to_mid, from_mid, kind, title, body, link, created_at_utc)
            VALUES ($to, NULL, $kind, $title, $body, $link, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$to", toMid);
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
            INSERT INTO messages (to_mid, from_mid, kind, title, body, link, created_at_utc)
            SELECT mid, NULL, $kind, $title, $body, NULL, $now
            FROM members
            WHERE status = $active AND mid IS NOT NULL;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$active", Domain.MemberStatus.Active.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>信箱清单：新→旧（id 倒序即落笔序），unreadOnly 只看未读，limit 封顶 200。</summary>
    public IReadOnlyList<InboxMessage> ListMessages(long toMid, int limit = 50, bool unreadOnly = false)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.to_mid, m.from_mid, m.kind, m.title, m.body, m.link, m.created_at_utc, m.read_at_utc, s.nickname
            FROM messages m
            LEFT JOIN members s ON s.mid = m.from_mid
            WHERE m.to_mid = $to AND ($unreadOnly = 0 OR m.read_at_utc IS NULL)
            ORDER BY m.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$to", toMid);
        command.Parameters.AddWithValue("$unreadOnly", unreadOnly ? 1 : 0);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        using var reader = command.ExecuteReader();
        var list = new List<InboxMessage>();
        while (reader.Read())
        {
            // record 声明序：(Id, ToMid, FromMid, FromName, Kind, Title, Body, Link, CreatedAtUtc, ReadAtUtc)
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

    public long CountUnread(long toMid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE to_mid = $to AND read_at_utc IS NULL;";
        command.Parameters.AddWithValue("$to", toMid);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>标读：to_mid 不符即空手（返回 false，不报错）——越权摸信摸不到。</summary>
    public bool MarkRead(long id, long toMid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET read_at_utc = $now WHERE id = $id AND to_mid = $to AND read_at_utc IS NULL;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$to", toMid);
        return command.ExecuteNonQuery() > 0;
    }

    public int MarkAllRead(long toMid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET read_at_utc = $now WHERE to_mid = $to AND read_at_utc IS NULL;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$to", toMid);
        return command.ExecuteNonQuery();
    }

    /// <summary>投稿回执取材：收信人 + 标题 + 失败原因，一行拿齐。</summary>
    public VideoMailTarget? GetVideoMailTarget(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT submitter_mid, bvid, title, last_error FROM videos WHERE id = $id;";
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

    // ─── M5.3 用户间私信：往来说一行一发，会话在查询时按 peer 聚合（成员量小，直白为先）───

    /// <summary>一条私信（校验在端点层，这层只写）。</summary>
    public sealed record DmMessage(long Id, long FromMid, long ToMid, string Body, string CreatedAtUtc, string? ReadAtUtc);

    /// <summary>会话摘要：对方名片 + 最后一句 + 未读数。</summary>
    public sealed record DmThread(long PeerMid, string? PeerName, string? PeerFace, string LastBody, string LastAtUtc, long Unread);

    /// <summary>管理员视野的一行私信（带双方名片与已读态）。</summary>
    public sealed record DmInspectionRow(
        long Id,
        long FromMid,
        string? FromName,
        long ToMid,
        string? ToName,
        string Body,
        string CreatedAtUtc,
        string? ReadAtUtc);

    public long SendDm(long fromMid, long toMid, string body)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dm_messages (from_mid, to_mid, body, created_at_utc)
            VALUES ($from, $to, $body, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$from", fromMid);
        command.Parameters.AddWithValue("$to", toMid);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>会话列表：对 from=me OR to=me 的行按 peer 聚合，最新一句倒序。名片现查 members。</summary>
    public IReadOnlyList<DmThread> ListDmThreads(long mid)
    {
        using var connection = Open(_dbPath);

        var peerIds = new List<long>();
        using (var peers = connection.CreateCommand())
        {
            peers.CommandText = """
                SELECT DISTINCT CASE WHEN from_mid = $me THEN to_mid ELSE from_mid END
                FROM dm_messages WHERE from_mid = $me OR to_mid = $me;
                """;
            peers.Parameters.AddWithValue("$me", mid);
            using var reader = peers.ExecuteReader();
            while (reader.Read())
            {
                peerIds.Add(reader.GetInt64(0));
            }
        }

        var threads = new List<DmThread>();
        foreach (var peer in peerIds)
        {
            using var last = connection.CreateCommand();
            last.CommandText = """
                SELECT body, created_at_utc FROM dm_messages
                WHERE (from_mid = $me AND to_mid = $peer) OR (from_mid = $peer AND to_mid = $me)
                ORDER BY id DESC LIMIT 1;
                """;
            last.Parameters.AddWithValue("$me", mid);
            last.Parameters.AddWithValue("$peer", peer);
            using var lastReader = last.ExecuteReader();
            if (!lastReader.Read())
            {
                continue; // 对端刚被拆空（理论不可达），跳过
            }

            var lastBody = lastReader.GetString(0);
            var lastAt = lastReader.GetString(1);

            using var unread = connection.CreateCommand();
            unread.CommandText = "SELECT COUNT(*) FROM dm_messages WHERE to_mid = $me AND from_mid = $peer AND read_at_utc IS NULL;";
            unread.Parameters.AddWithValue("$me", mid);
            unread.Parameters.AddWithValue("$peer", peer);
            var unreadCount = Convert.ToInt64(unread.ExecuteScalar());

            threads.Add(new DmThread(peer, null, null, lastBody, lastAt, unreadCount));
        }

        // 成员量小：名片在库外现补，排序按最后一句的新旧倒序
        var roster = ListMembers().Where(m => m.Mid is not null).ToDictionary(m => m.Mid!.Value);
        return threads
            .Select(t => roster.TryGetValue(t.PeerMid, out var m)
                ? t with { PeerName = m.Nickname, PeerFace = m.FaceUrl }
                : t)
            .OrderByDescending(t => DateTime.Parse(t.LastAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind))
            .ThenByDescending(t => t.PeerMid)
            .ToList();
    }

    /// <summary>双方往来升序（拉取方自己决定要不要顺手标读）。</summary>
    public IReadOnlyList<DmMessage> ListDmConversation(long a, long b, int limit = 200)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, from_mid, to_mid, body, created_at_utc, read_at_utc
            FROM dm_messages
            WHERE (from_mid = $a AND to_mid = $b) OR (from_mid = $b AND to_mid = $a)
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$a", a);
        command.Parameters.AddWithValue("$b", b);
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
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        list.Reverse(); // 取最新一批后翻回转序
        return list;
    }

    /// <summary>标读：只销「对方发给我」的未读，我发出去的自己读不算数。</summary>
    public int MarkDmRead(long byMid, long peerMid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dm_messages SET read_at_utc = $now
            WHERE to_mid = $me AND from_mid = $peer AND read_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$me", byMid);
        command.Parameters.AddWithValue("$peer", peerMid);
        return command.ExecuteNonQuery();
    }

    public long CountDmUnread(long mid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dm_messages WHERE to_mid = $me AND read_at_utc IS NULL;";
        command.Parameters.AddWithValue("$me", mid);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>可私聊名单：active 席位（排除自己留端点层，这里给全量 active）。</summary>
    public IReadOnlyList<StationMember> GetActiveMembersForDirectory()
        => ListMembers().Where(m => m.Status == Domain.MemberStatus.Active && m.Mid is not null).ToList();

    /// <summary>站长视野：全部私信（可锁一对往来），带双方名片，新→旧。</summary>
    public IReadOnlyList<DmInspectionRow> ListDmForAdmin(long? peerA, long? peerB, int limit = 500)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        var pairFilter = peerA is not null && peerB is not null
            ? " AND ((from_mid = $a AND to_mid = $b) OR (from_mid = $b AND to_mid = $a))"
            : string.Empty;
        command.CommandText = $"""
            SELECT d.id, d.from_mid, d.to_mid, d.body, d.created_at_utc, d.read_at_utc, f.nickname, t.nickname
            FROM dm_messages d
            LEFT JOIN members f ON f.mid = d.from_mid
            LEFT JOIN members t ON t.mid = d.to_mid
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
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(2),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return list;
    }
}
