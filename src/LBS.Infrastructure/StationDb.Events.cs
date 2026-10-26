using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 事件台账读写（分站的海马体）。只增不删；查询为站长室与未来 AI 分析共用一口井。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>批量落事件（单事务多行，EventRecorder 专用）。</summary>
    public void InsertEventsBatch(IReadOnlyList<EventDraft> drafts)
    {
        if (drafts.Count == 0)
        {
            return;
        }

        using var connection = Open(_dbPath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events (ts_utc, actor_type, actor_mid, event, subject_type, subject_id, payload, ip)
            VALUES ($ts, $at, $am, $ev, $st, $si, $p, $ip);
            """;
        var pTs = command.Parameters.Add("$ts", SqliteType.Text);
        var pAt = command.Parameters.Add("$at", SqliteType.Text);
        var pAm = command.Parameters.Add("$am", SqliteType.Integer);
        var pEv = command.Parameters.Add("$ev", SqliteType.Text);
        var pSt = command.Parameters.Add("$st", SqliteType.Text);
        var pSi = command.Parameters.Add("$si", SqliteType.Text);
        var pP = command.Parameters.Add("$p", SqliteType.Text);
        var pIp = command.Parameters.Add("$ip", SqliteType.Text);

        foreach (var draft in drafts)
        {
            pTs.Value = draft.TsUtc;
            pAt.Value = draft.ActorType;
            pAm.Value = (object?)draft.ActorMid ?? DBNull.Value;
            pEv.Value = draft.EventType;
            pSt.Value = (object?)draft.SubjectType ?? DBNull.Value;
            pSi.Value = (object?)draft.SubjectId ?? DBNull.Value;
            pP.Value = (object?)draft.PayloadJson ?? DBNull.Value;
            pIp.Value = (object?)draft.Ip ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// 查事件（新→旧）。各过滤器可叠：身份、mid、事件名前缀（"watch." 即全部观看行为）、时间窗。
    /// limit 兜住体量；AI 分析按前缀+时间窗切片取数。
    /// </summary>
    public IReadOnlyList<EventRow> QueryEvents(int limit = 500, string? actorType = null,
        long? actorMid = null, string? eventPrefix = null, string? sinceUtc = null, string? untilUtc = null)
    {
        var sql = "SELECT id, ts_utc, actor_type, actor_mid, event, subject_type, subject_id, payload, ip FROM events";
        var where = new List<string>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();

        if (actorType != null)
        {
            where.Add("actor_type = $at");
            command.Parameters.AddWithValue("$at", actorType);
        }

        if (actorMid != null)
        {
            where.Add("actor_mid = $am");
            command.Parameters.AddWithValue("$am", actorMid.Value);
        }

        if (eventPrefix != null)
        {
            where.Add("event LIKE $prefix");
            command.Parameters.AddWithValue("$prefix", eventPrefix + "%");
        }

        if (sinceUtc != null)
        {
            where.Add("ts_utc >= $since");
            command.Parameters.AddWithValue("$since", sinceUtc);
        }

        if (untilUtc != null)
        {
            where.Add("ts_utc <= $until");
            command.Parameters.AddWithValue("$until", untilUtc);
        }

        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }

        sql += " ORDER BY id DESC LIMIT $limit;";
        command.CommandText = sql;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));

        var rows = new List<EventRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new EventRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return rows;
    }

    /// <summary>台账总笔数（站长室展示"海马体里存了多少"）。</summary>
    public long CountEvents()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events;";
        return (long)command.ExecuteScalar()!;
    }
}
