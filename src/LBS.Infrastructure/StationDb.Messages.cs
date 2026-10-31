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
        string? ReadAtUtc,
        // 信里那条 /watch/{bvid} 现在落不落得进去：由读路径现算（不存字段），null = 这封信不携片链。
        bool? VideoInStation = null);

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
            SELECT m.id, m.to_member_id, m.from_mid, m.kind, m.title, m.body, m.link, m.created_at_utc, m.read_at_utc, s.nickname,
                   -- 归档回执的 link 形状由 MemberMail.NotifyVideoArrived 唯一写钉死为 /watch/ + bvid，
                   -- substr 剥前七字符即得 bvid；welcome 那类 link 是 /me，靠 kind 闸住不进去。
                   -- 这里用 EXISTS 而不是标量子查询：子查询查无此行会给 NULL，跟「没带片链」的 NULL 撞成一堆，分不开。
                   CASE WHEN m.kind = 'complete' AND m.link LIKE '/watch/%' THEN
                       EXISTS (SELECT 1 FROM videos v WHERE v.bvid = substr(m.link, 8) AND v.status = 'Arrived')
                   END
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
            // record 声明序：(Id, ToMemberId, FromMid, FromName, Kind, Title, Body, Link, CreatedAtUtc, ReadAtUtc, VideoInStation)
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
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(10) ? null : reader.GetInt32(10) == 1));
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
    public sealed record DmMessage(long Id, long FromMemberId, long ToMemberId, string Body, string CreatedAtUtc, string? ReadAtUtc, string? MsgType = null, string? DynJson = null, bool? VideoInStation = null);

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
        // 开口即复活：我主动给 TA 发信，之前单边销/清掉的会话自动摆回列表。
        using (var unhide = connection.CreateCommand())
        {
            unhide.CommandText = "DELETE FROM dm_thread_hidden WHERE (member_id = $me AND peer_member_id = $peer) OR (member_id = $peer AND peer_member_id = $me);";
            unhide.Parameters.AddWithValue("$me", fromMemberId);
            unhide.Parameters.AddWithValue("$peer", toMemberId);
            unhide.ExecuteNonQuery();
        }

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

    /// <summary>
    /// 撤回（真删）：发件人销自己那句，双方账本同时没，审计视角也不留。
    /// WHERE 把发件人锁死在登录者身上：抽不到行（没这句/不是自己发的）返回 false。
    /// </summary>
    public bool DeleteDmMessageAsSender(long memberId, long messageId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dm_messages WHERE id = $id AND from_member_id = $me;";
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$me", memberId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 从自己列表抹掉会话（只隐列表，不清记录；供单边销账用）。
    /// 聊天窗口的「清空聊天记录」走 ClearDmThread（盖章隐旧 + 双键集齐真删）。
    /// </summary>
    public void HideDmThread(long memberId, long peerMemberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO dm_thread_hidden (member_id, peer_member_id, hidden_at_utc)
            VALUES ($me, $peer, $now);
            """;
        command.Parameters.AddWithValue("$me", memberId);
        command.Parameters.AddWithValue("$peer", peerMemberId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 清空聊天记录（单边盖章，双键墓碑）：给 TA 的会话从列表抹掉，盖章时刻之前的往来对自己不再露面；
    /// 对方账本不动。若对方早已盖过章，两把钥匙集齐 → 往来消息真删，双方墓碑一并销账。
    /// 盖章后任一方重新开口，会话复活（只看得见新话）。
    /// </summary>
    /// <returns>true = 双方都清过，往来已真删；false = 只盖了自己的章。</returns>
    public bool ClearDmThread(long memberId, long peerMemberId)
    {
        using var connection = Open(_dbPath);
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var stamp = connection.CreateCommand())
        {
            stamp.CommandText = """
                INSERT INTO dm_thread_cleared (member_id, peer_member_id, cleared_at_utc)
                VALUES ($me, $peer, $now)
                ON CONFLICT (member_id, peer_member_id) DO UPDATE SET cleared_at_utc = excluded.cleared_at_utc;
                """;
            stamp.Parameters.AddWithValue("$me", memberId);
            stamp.Parameters.AddWithValue("$peer", peerMemberId);
            stamp.Parameters.AddWithValue("$now", now);
            stamp.ExecuteNonQuery();
        }

        // 盖章即隐列：顺手把自己的会话从列表抹掉（重新开口或对方来话自动摆回）
        using var hide = connection.CreateCommand();
        hide.CommandText = """
            INSERT OR IGNORE INTO dm_thread_hidden (member_id, peer_member_id, hidden_at_utc)
            VALUES ($me, $peer, $now);
            """;
        hide.Parameters.AddWithValue("$me", memberId);
        hide.Parameters.AddWithValue("$peer", peerMemberId);
        hide.Parameters.AddWithValue("$now", now);
        hide.ExecuteNonQuery();

        // 对方是否早已盖过章（两把钥匙集齐才算真删时机）
        long peerStamped;
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM dm_thread_cleared WHERE member_id = $peer AND peer_member_id = $me;";
            probe.Parameters.AddWithValue("$me", memberId);
            probe.Parameters.AddWithValue("$peer", peerMemberId);
            peerStamped = Convert.ToInt64(probe.ExecuteScalar());
        }

        if (peerStamped > 0)
        {
            // 双键集齐：往来消息物理销毁，两座墓碑与两边的列表隐藏记一并销掉（无账可藏）
            using var burn = connection.CreateCommand();
            burn.CommandText = """
                DELETE FROM dm_messages
                WHERE (from_member_id = $me AND to_member_id = $peer) OR (from_member_id = $peer AND to_member_id = $me);
                DELETE FROM dm_thread_cleared
                WHERE (member_id = $me AND peer_member_id = $peer) OR (member_id = $peer AND peer_member_id = $me);
                DELETE FROM dm_thread_hidden
                WHERE (member_id = $me AND peer_member_id = $peer) OR (member_id = $peer AND peer_member_id = $me);
                """;
            burn.Parameters.AddWithValue("$me", memberId);
            burn.Parameters.AddWithValue("$peer", peerMemberId);
            burn.ExecuteNonQuery();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 会话列表：对 from=me OR to=me 的行按 peer 席位聚合，最新一句倒序。名片现查 members（优先昵称，素净席位落 username）；
    /// 被单边销账的会话不露面；被单边清空的会话隐到盖章时刻之前的旧话——直到任一方说了章后的新话才摆回列表。
    /// </summary>
    public IReadOnlyList<DmThread> ListDmThreads(long memberId)
    {
        using var connection = Open(_dbPath);

        var peerIds = new List<long>();
        using (var peers = connection.CreateCommand())
        {
            // 可见规矩：未被列表隐藏，且至少还有一句「章后的话」可看（没盖过章则句句算）
            peers.CommandText = """
                WITH peer_list AS (
                    SELECT to_member_id AS peer FROM dm_messages WHERE from_member_id = $me
                    UNION
                    SELECT from_member_id FROM dm_messages WHERE to_member_id = $me
                )
                SELECT p.peer FROM peer_list p
                WHERE NOT EXISTS (
                    SELECT 1 FROM dm_thread_hidden h
                    WHERE h.member_id = $me AND h.peer_member_id = p.peer
                )
                AND EXISTS (
                    SELECT 1 FROM dm_messages m2
                    WHERE ((m2.from_member_id = $me AND m2.to_member_id = p.peer) OR (m2.from_member_id = p.peer AND m2.to_member_id = $me))
                      AND NOT EXISTS (
                          SELECT 1 FROM dm_thread_cleared c
                          WHERE c.member_id = $me AND c.peer_member_id = p.peer
                            AND m2.created_at_utc <= c.cleared_at_utc
                      )
                );
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
            // 盖过章的列表摘要也只认章后的话，免得列表预览偷看已隐身的旧事
            using (var stamp = connection.CreateCommand())
            {
                stamp.CommandText = "SELECT cleared_at_utc FROM dm_thread_cleared WHERE member_id = $me AND peer_member_id = $peer;";
                stamp.Parameters.AddWithValue("$me", memberId);
                stamp.Parameters.AddWithValue("$peer", peer);
                var clearedAt = stamp.ExecuteScalar() as string;
                if (clearedAt is not null && string.CompareOrdinal(lastAt, clearedAt) <= 0)
                {
                    using var fresh = connection.CreateCommand();
                    fresh.CommandText = """
                        SELECT body, created_at_utc, msg_type FROM dm_messages
                        WHERE ((from_member_id = $me AND to_member_id = $peer) OR (from_member_id = $peer AND to_member_id = $me))
                          AND created_at_utc > $after
                        ORDER BY id DESC LIMIT 1;
                        """;
                    fresh.Parameters.AddWithValue("$me", memberId);
                    fresh.Parameters.AddWithValue("$peer", peer);
                    fresh.Parameters.AddWithValue("$after", clearedAt);
                    using var freshReader = fresh.ExecuteReader();
                    if (!freshReader.Read())
                    {
                        continue; // 章后无话（理论不可达，上面 EXISTS 已筛），保险跳过
                    }

                    lastBody = freshReader.GetString(0);
                    lastAt = freshReader.GetString(1);
                    lastMsgType = freshReader.IsDBNull(2) ? null : freshReader.GetString(2);
                }
            }

            using var unread = connection.CreateCommand();
            // 未读也只数章后的话：盖章前的旧未读是已被隐身的事，不该再报数
            unread.CommandText = """
                SELECT COUNT(*) FROM dm_messages m
                WHERE m.to_member_id = $me AND m.from_member_id = $peer AND m.read_at_utc IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM dm_thread_cleared c
                      WHERE c.member_id = $me AND c.peer_member_id = $peer
                        AND m.created_at_utc <= c.cleared_at_utc
                  );
                """;
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

    /// <summary>
    /// 双方往来升序（拉取方自己决定要不要顺手标读）。
    /// viewerMemberId 非空时按它的清空章过滤：盖章时刻之前的老话对这侧隐身（只认 memberA 侧的章，不传则全量，站长审计视角用）。
    /// </summary>
    public IReadOnlyList<DmMessage> ListDmConversation(long memberA, long memberB, long? viewerMemberId = null, int limit = 200)
    {
        if (viewerMemberId is long v && v != memberA)
        {
            // 章挂在会话的「我」这一侧；调用方把 peer 传在前面时，跟着换位，免得隐身失效
            (memberA, memberB) = (memberB, memberA);
        }

        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();

        var viewerFilter = "";
        if (viewerMemberId is long viewer)
        {
            // 双键集齐时消息已真删，这里盖到一半的章才会生效；ISO-8601 同时区，字符串序即时间序。
            // 章永远挂在「查看者 ↔ 对方（$b）」这对键上，与每条消息的行方向无关。
            // 这里绝不能带分号：本串拼在 ORDER BY / LIMIT 之前，SQLite 只预备首个分号前的那一串，
            // 带上分号就等于静默丢掉排序与条数上限（行回 rowid 升序，再被下面的 Reverse 翻成倒序，
            // 会话就成了「新的压在上面」）。
            viewerFilter = """
                AND NOT EXISTS (
                    SELECT 1 FROM dm_thread_cleared c
                    WHERE c.member_id = $viewer AND c.peer_member_id = $b
                      AND dm_messages.created_at_utc <= c.cleared_at_utc
                )
                """;
        }

        command.CommandText = $"""
            SELECT id, from_member_id, to_member_id, body, created_at_utc, read_at_utc, msg_type, dyn_json,
                   -- 分享卡在发送那一刻定格，片子删了卡上字段不会变；这里现算一枚死活，只给 video 型用
                   -- （dynamic 型的 dynamic_id 存的是真动态 id，不能拿去比 bvid）。用 EXISTS 是为了把「下架」给成 0 而非 NULL。
                   CASE WHEN msg_type = 'video' THEN
                       EXISTS (SELECT 1 FROM videos v WHERE v.bvid = dm_messages.dynamic_id AND v.status = 'Arrived')
                   END
            FROM dm_messages
            WHERE ((from_member_id = $a AND to_member_id = $b) OR (from_member_id = $b AND to_member_id = $a))
            {viewerFilter}
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$a", memberA);
        command.Parameters.AddWithValue("$b", memberB);
        if (viewerMemberId is long viewerId)
        {
            command.Parameters.AddWithValue("$viewer", viewerId);
        }
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
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8) == 1));
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

    /// <summary>
    /// 选人框搜索门槛：纯数字 1 位起搜（撞 memberId/mid 等值，没有枚举面）；
    /// 文字关键词至少 2 字，单字不收——防的就是「猫」这类广撒网把名册筛出来。
    /// 前端同步拦一道，这里是被绕过时的真闸。
    /// </summary>
    public static bool DirectorySearchAllowed(string? keyword)
    {
        var q = keyword?.Trim() ?? string.Empty;
        if (q.Length == 0) return false;
        return q.All(char.IsAsciiDigit) ? q.Length >= 1 : q.Length >= 2;
    }

    /// <summary>
    /// 可私信选人：在 active 席位池里按关键词搜（昵称/用户名 Contains 忽略大小写；
    /// 纯数字撞站内席位 id 与 B 站 mid），限 limit 条。不达门槛一律空手——绝不全量兑底。
    /// 注：全站点名接口已拆，普通成员不再能整本领走花名册。
    /// </summary>
    public static IReadOnlyList<StationMember> MatchMembersForDirectory(IReadOnlyList<StationMember> pool, string? keyword, int limit = 20)
    {
        if (!DirectorySearchAllowed(keyword)) return Array.Empty<StationMember>();
        var q = keyword!.Trim();
        var numeric = q.All(char.IsAsciiDigit);
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        return pool
            .Where(m => m.Status == MemberStatus.Active)
            .Where(m => numeric
                ? m.Id.ToString() == q || m.Mid?.ToString() == q
                : m.Nickname?.Contains(q, comparison) == true || m.Username?.Contains(q, comparison) == true)
            .Take(limit)
            .ToList();
    }

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

    /// <summary>席位对人的展示名：B 站昵称优先，素净席位落 username——不搭 @，站不玩前院那套风味。</summary>
    public static string? DisplayOf(StationMember m) =>
        !string.IsNullOrWhiteSpace(m.Nickname) ? m.Nickname
        : !string.IsNullOrWhiteSpace(m.Username) ? m.Username
        : null;

    private static string? PickName(string? nickname, string? username) =>
        !string.IsNullOrWhiteSpace(nickname) ? nickname
        : !string.IsNullOrWhiteSpace(username) ? username
        : null;
}
