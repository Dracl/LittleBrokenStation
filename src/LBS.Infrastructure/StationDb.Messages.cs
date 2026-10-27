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
}
