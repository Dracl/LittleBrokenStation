using System.Security.Cryptography;
using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 成员体系读写：席位（邀请制）、账号池（cookie 只进不出，列表形不带 cookie 字段）、
/// 会话（令牌哈希落盘，分站重启不踢朋友）。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>邀请码字符集：剔掉易混的 I O L 0 1，抄给朋友不用逐字母拼读。</summary>
    private const string InviteAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int InviteLength = 10;

    private static string NewInviteCode()
    {
        Span<byte> bytes = stackalloc byte[InviteLength];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[InviteLength];
        for (var i = 0; i < InviteLength; i++)
        {
            chars[i] = InviteAlphabet[bytes[i] % InviteAlphabet.Length];
        }

        return new string(chars);
    }

    private static readonly string[] MemberColumns =
    [
        "id", "mid", "nickname", "face_url", "note", "status", "role",
        "created_at_utc", "bound_at_utc", "last_login_at_utc", "username",
    ];

    private static StationMember ReadMember(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? MemberRoles.Member : reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    /// <summary>站长建空位发邀请码：mid 留白，等朋友扫码绑定后转 active。</summary>
    public (long Id, string Code) CreateMemberInvite(string? note)
    {
        // 邀请码撞车（10 位 31 字符空间，概率可忽略）就重试几把，不跟 UNIQUE 较劲。
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = NewInviteCode();
            try
            {
                using var connection = Open(_dbPath);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO members (mid, invite_code, note, status, created_at_utc)
                    VALUES (NULL, $code, $note, 'invited', $now);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$code", code);
                command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                return ((long)command.ExecuteScalar()!, code);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // 撞码，换一枚再来。
            }
        }

        throw new InvalidOperationException("邀请码连撞五次，见鬼了");
    }

    public IReadOnlyList<StationMember> ListMembers()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", MemberColumns)} FROM members ORDER BY id;";
        var list = new List<StationMember>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadMember(reader));
        }

        return list;
    }

    public StationMember? GetMemberByMid(long mid) => QueryMember("mid", mid);

    public StationMember? GetMemberById(long id) => QueryMember("id", id);

    /// <summary>按用户名认人（唯一索引就是 NOCASE 的，登录与判重共用这把尺子）。</summary>
    public StationMember? GetMemberByUsername(string username)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {string.Join(", ", MemberColumns)} FROM members WHERE username = $u COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$u", username);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMember(reader) : null;
    }

    private StationMember? QueryMember(string column, long value)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", MemberColumns)} FROM members WHERE {column} = $v;";
        command.Parameters.AddWithValue("$v", value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMember(reader) : null;
    }

    /// <summary>按邀请码找未绑定的席位（已用掉/封禁的都不算数）。</summary>
    public StationMember? FindInvitedByCode(string code)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {string.Join(", ", MemberColumns)} FROM members
            WHERE invite_code = $code AND status = 'invited';
            """;
        command.Parameters.AddWithValue("$code", code.Trim().ToUpperInvariant());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMember(reader) : null;
    }

    /// <summary>邀请码兑现：空位认主（mid 落账、码作废、转 active）。mid 已被占则失败交调用方处置。</summary>
    public StationMember? BindMember(long memberId, long mid, string? nickname, string? faceUrl)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            using var connection = Open(_dbPath);
            using (var command = connection.CreateCommand())
            {
                // 头名即站长：同一语句内自问“站上有主吗”，无主则认主者继位，不给竞速留缝。
                command.CommandText = """
                    UPDATE members
                    SET mid = $mid, nickname = $name, face_url = $face, status = 'active',
                        invite_code = NULL, bound_at_utc = $now, last_login_at_utc = $now,
                        role = CASE WHEN NOT EXISTS (SELECT 1 FROM members WHERE role = 'owner')
                                    THEN 'owner' ELSE 'member' END
                    WHERE id = $id AND status = 'invited';
                    """;
                command.Parameters.AddWithValue("$mid", mid);
                command.Parameters.AddWithValue("$name", (object?)nickname ?? DBNull.Value);
                command.Parameters.AddWithValue("$face", (object?)faceUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$id", memberId);
                if (command.ExecuteNonQuery() == 0)
                {
                    return null;
                }
            }

            return GetMemberById(memberId);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // mid 唯一键撞车：这个 B 站号已有席位，交上层走重登分支。
            return null;
        }
    }

    /// <summary>
    /// 账密注册兑现：空位认主转 active（无 mid，素净席位）——同样带“无主则继位”的同语句自问，
    /// 与扫码 BindMember 的首扫继位语义对齐。用户名撞 NOCASE 唯一索引返 null 交上层报重名。
    /// </summary>
    public StationMember? ActivateMemberByRegistration(long memberId, string username, string passwordHash)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            using var connection = Open(_dbPath);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE members
                    SET username = $username, password_hash = $hash, status = 'active',
                        invite_code = NULL, bound_at_utc = $now, last_login_at_utc = $now,
                        role = CASE WHEN NOT EXISTS (SELECT 1 FROM members WHERE role = 'owner')
                                    THEN 'owner' ELSE 'member' END
                    WHERE id = $id AND status = 'invited';
                    """;
                command.Parameters.AddWithValue("$username", username);
                command.Parameters.AddWithValue("$hash", passwordHash);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$id", memberId);
                if (command.ExecuteNonQuery() == 0)
                {
                    return null;
                }
            }

            return GetMemberById(memberId);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // 用户名撞车（NOCASE 唯一索引）：交上层报「这站名被人占了」。
            return null;
        }
    }

    /// <summary>设/换密码（哈希串由上层用 MemberPasswords 铸好送来；站长代重置也走这张门）。</summary>
    public void SetMemberPassword(long memberId, string passwordHash)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE members SET password_hash = $h WHERE id = $id;";
        command.Parameters.AddWithValue("$h", passwordHash);
        command.Parameters.AddWithValue("$id", memberId);
        command.ExecuteNonQuery();
    }

    /// <summary>验密：没设过密码的席位一律不过（调用方给访客同一句拒词，不泄席位形态）。</summary>
    public bool VerifyMemberPassword(long memberId, string? candidate)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT password_hash FROM members WHERE id = $id;";
        command.Parameters.AddWithValue("$id", memberId);
        var stored = command.ExecuteScalar() as string;
        return Domain.MemberPasswords.Verify(stored, candidate);
    }

    public bool HasMemberPassword(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM members WHERE id = $id AND password_hash IS NOT NULL AND password_hash <> '';";
        command.Parameters.AddWithValue("$id", memberId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>账密登录刷登录时刻（不碰昵称头像——素净席位的名号自己说了算）。</summary>
    public void TouchMemberLoginById(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE members SET last_login_at_utc = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", memberId);
        command.ExecuteNonQuery();
    }

    /// <summary>站上有主了吗？（登录页用来挂“首位扫码者继位站长”的告示）。</summary>
    public bool HasOwner()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM members WHERE role = 'owner' LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    /// <summary>站长席位（头名 owner）的头像，没在席/没脸都返 null。
    /// 给没记 member_id 的主人弹幕当脸：拿 lbs_owner 钥匙发的、身份体系之前的老行，都该认得出是站长。</summary>
    public string? GetOwnerSeatFace()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT face_url FROM members
            WHERE role = 'owner' AND status = 'active' AND face_url IS NOT NULL AND face_url <> ''
            ORDER BY id LIMIT 1;
            """;
        return command.ExecuteScalar() as string;
    }

    /// <summary>老席位重登：刷新登录时刻，昵称头像顺手换新。</summary>
    public void TouchMemberLogin(long mid, string? nickname, string? faceUrl)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE members
            SET last_login_at_utc = $now,
                nickname = COALESCE($name, nickname),
                face_url = COALESCE($face, face_url)
            WHERE mid = $mid;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$name", (object?)nickname ?? DBNull.Value);
        command.Parameters.AddWithValue("$face", (object?)faceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$mid", mid);
        command.ExecuteNonQuery();
    }

    /// <summary>启停席位；封禁顺手吊销其全部会话（即刻请出分站）。</summary>
    public void SetMemberStatus(long memberId, string status)
    {
        using var connection = Open(_dbPath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE members SET status = $s WHERE id = $id;";
            command.Parameters.AddWithValue("$s", status);
            command.Parameters.AddWithValue("$id", memberId);
            command.ExecuteNonQuery();
        }

        if (status == MemberStatus.Banned)
        {
            RevokeMemberSessions(memberId);
        }
    }

    /// <summary>撤席位：连账号池与会话一起清；历史事件与投稿归属（actor/submitter mid）留着给 AI。</summary>
    public void DeleteMember(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM member_sessions WHERE member_id = $id;
            DELETE FROM member_accounts WHERE member_id = $id;
            DELETE FROM members WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", memberId);
        command.ExecuteNonQuery();
    }

    // ── 账号池 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 收下一个 B 站号（扫码/粘贴的 cookie 都从这进）：同 (成员,账号) 再登录就换 cookie 续命。
    /// 池里第一个号自动立为主力；cookie 从此只进不出，列表形 MemberAccount 不带它。
    /// </summary>
    public void UpsertMemberAccount(long memberId, long accountMid, string? accountName, string? faceUrl, string cookie)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open(_dbPath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO member_accounts
                    (member_id, account_mid, account_name, face_url, cookie, is_primary, created_at_utc, last_seen_utc)
                VALUES ($m, $am, $name, $face, $cookie, 0, $now, $now)
                ON CONFLICT(member_id, account_mid) DO UPDATE SET
                    cookie = excluded.cookie,
                    account_name = COALESCE(excluded.account_name, account_name),
                    face_url = COALESCE(excluded.face_url, face_url),
                    last_seen_utc = excluded.last_seen_utc,
                    expired = 0;
                """;
            command.Parameters.AddWithValue("$m", memberId);
            command.Parameters.AddWithValue("$am", accountMid);
            command.Parameters.AddWithValue("$name", (object?)accountName ?? DBNull.Value);
            command.Parameters.AddWithValue("$face", (object?)faceUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$cookie", cookie);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        // 池里彻底没主力才立一个（按添加先后，第一个扫码的活号当主力）；已有主力时按兵不动。
        using (var promote = connection.CreateCommand())
        {
            promote.CommandText = """
                UPDATE member_accounts SET is_primary = 1
                WHERE member_id = $m AND is_primary = 0 AND expired = 0
                  AND id = (SELECT MIN(id) FROM member_accounts WHERE member_id = $m AND expired = 0)
                  AND NOT EXISTS (
                      SELECT 1 FROM member_accounts WHERE member_id = $m AND is_primary = 1 AND expired = 0);
                UPDATE member_accounts SET is_primary = 0
                WHERE member_id = $m AND expired = 1 AND is_primary = 1;
                """;
            promote.Parameters.AddWithValue("$m", memberId);
            promote.ExecuteNonQuery();
        }
    }

    /// <summary>账号列表（不含 cookie，可安全过 HTTP）。</summary>
    public IReadOnlyList<MemberAccount> ListMemberAccounts(long? memberId = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, member_id, account_mid, account_name, face_url, is_primary, expired, created_at_utc, last_seen_utc
            FROM member_accounts
            """ + (memberId == null ? "" : " WHERE member_id = $m") + " ORDER BY member_id, is_primary DESC, id;";
        if (memberId != null)
        {
            command.Parameters.AddWithValue("$m", memberId.Value);
        }

        var list = new List<MemberAccount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MemberAccount(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetInt64(5) == 1,
                !reader.IsDBNull(6) && reader.GetInt64(6) == 1,
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return list;
    }

    /// <summary>下载/查询链取 cookie：主力优先，主力失效退其他活号；一个都不活返回 null 交调用方回落。</summary>
    public MemberAccountCredential? PickMemberAccountForDownload(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, member_id, account_mid, cookie FROM member_accounts
            WHERE member_id = $m AND expired = 0
            ORDER BY is_primary DESC, id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$m", memberId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MemberAccountCredential(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
    }

    /// <summary>标/解账号失效（nav 探测 -101 或下载败北时打旗；重新扫码自动摘旗）。</summary>
    public void MarkMemberAccountExpired(long memberId, long accountMid, bool expired)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE member_accounts SET expired = $e WHERE member_id = $m AND account_mid = $am;";
        command.Parameters.AddWithValue("$e", expired ? 1 : 0);
        command.Parameters.AddWithValue("$m", memberId);
        command.Parameters.AddWithValue("$am", accountMid);
        command.ExecuteNonQuery();
    }

    /// <summary>指定主力（站长室/成员页手动换位）：同池其余降为备用。</summary>
    public void SetMemberAccountPrimary(long accountId)
    {
        using var connection = Open(_dbPath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE member_accounts SET is_primary = 0 WHERE member_id = (SELECT member_id FROM member_accounts WHERE id = $id);";
            command.Parameters.AddWithValue("$id", accountId);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE member_accounts SET is_primary = 1 WHERE id = $id AND expired = 0;";
            command.Parameters.AddWithValue("$id", accountId);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteMemberAccount(long accountId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM member_accounts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", accountId);
        command.ExecuteNonQuery();
    }

    // ── 会话 ────────────────────────────────────────────────────────────────

    /// <summary>发会话（存令牌哈希，明文只在 cookie 里走）；发之前顺手清过期票。</summary>
    public void IssueMemberSession(long memberId, string tokenHash, DateTimeOffset expiresUtc)
    {
        using var connection = Open(_dbPath);
        using (var sweep = connection.CreateCommand())
        {
            sweep.CommandText = "DELETE FROM member_sessions WHERE expires_at_utc <= $now;";
            sweep.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            sweep.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO member_sessions (token_hash, member_id, created_at_utc, expires_at_utc)
                VALUES ($t, $m, $now, $exp);
                """;
            command.Parameters.AddWithValue("$t", tokenHash);
            command.Parameters.AddWithValue("$m", memberId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$exp", expiresUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>验票：有效回 member_id，过期票当场撕掉。</summary>
    public long? ResolveMemberSession(string tokenHash)
    {
        using var connection = Open(_dbPath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT member_id FROM member_sessions WHERE token_hash = $t AND expires_at_utc > $now;";
            command.Parameters.AddWithValue("$t", tokenHash);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return reader.GetInt64(0);
            }
        }

        using (var sweep = connection.CreateCommand())
        {
            sweep.CommandText = "DELETE FROM member_sessions WHERE token_hash = $t;";
            sweep.Parameters.AddWithValue("$t", tokenHash);
            sweep.ExecuteNonQuery();
        }

        return null;
    }

    public void RevokeMemberSessions(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM member_sessions WHERE member_id = $m;";
        command.Parameters.AddWithValue("$m", memberId);
        command.ExecuteNonQuery();
    }

    // ── 入站统计（站长室成员卡 / 未来 AI） ──────────────────────────────────

    /// <summary>
    /// 某成员的入站概览（M5.4 起锤 submitter_member_id）：投稿数、占用字节（与 AdminStats 同口径）、站内弹幕条数。
    /// 产物登记了但文件已蒸发的那份不计，宁缺毋假。
    /// </summary>
    public IReadOnlyList<MemberIngestStats> ListMemberIngestStats()
    {
        var counts = new Dictionary<long, int>();
        var bytes = new Dictionary<long, long>();
        var danmaku = new Dictionary<long, int>();
        using var connection = Open(_dbPath);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT submitter_member_id, COUNT(*) FROM videos WHERE submitter_member_id IS NOT NULL GROUP BY submitter_member_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counts[reader.GetInt64(0)] = (int)reader.GetInt64(1);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT v.submitter_member_id, a.rel_path
                FROM artifacts a JOIN videos v ON v.id = a.video_id
                WHERE v.submitter_member_id IS NOT NULL;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var memberId = reader.GetInt64(0);
                try
                {
                    bytes[memberId] = bytes.GetValueOrDefault(memberId) + new FileInfo(Path.Combine(_paths.Videos, reader.GetString(1))).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 产物文件不可读/已不在：这份体积不计入。
                }
            }
        }

        // 弹幕算人头：没身份的行人（member_id 为空）不塑到任何席位头上。
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT member_id, COUNT(*) FROM danmaku_local WHERE member_id IS NOT NULL GROUP BY member_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                danmaku[reader.GetInt64(0)] = (int)reader.GetInt64(1);
            }
        }

        // 两边键取并集：只发弹幕没投稿的人也得在表里，不然个人空间会把「说过 20 条」显示成 0。
        return counts.Keys.Concat(danmaku.Keys).Distinct()
            .Select(id => new MemberIngestStats(id, counts.GetValueOrDefault(id), bytes.GetValueOrDefault(id), danmaku.GetValueOrDefault(id)))
            .OrderBy(s => s.SubmitterMemberId)
            .ToList();
    }

    /// <summary>投稿定人（M5.4：定的是站内席位 id，提交入库那一刻由提交端点落笔）。</summary>
    public void SetVideoSubmitterMember(long videoId, long? submitterMemberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET submitter_member_id = $s WHERE id = $id;";
        command.Parameters.AddWithValue("$s", (object?)submitterMemberId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", videoId);
        command.ExecuteNonQuery();
    }

    public long? GetVideoSubmitterMember(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT submitter_member_id FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetInt64(0) : null;
    }

    /// <summary>
    /// 全站投稿归属速查：卡阵两百多行要算 canManage，逐行问库不划算，一次拿全表。
    /// 爬虫代投、老数据反查失败的行留 NULL，调用方按「无归属 → 只有站长可操作」处置。
    /// </summary>
    public IReadOnlyDictionary<long, long?> GetVideoSubmitters()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, submitter_member_id FROM videos;";
        var map = new Dictionary<long, long?>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetInt64(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        return map;
    }
}
