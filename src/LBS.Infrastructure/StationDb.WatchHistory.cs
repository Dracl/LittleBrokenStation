using LBS.Domain;

namespace LBS.Infrastructure;

/// <summary>
/// 观看历史（一人一片每 P 一行）：只存「这位朋友在这部片的这一 P 上看到哪了」这一个问题。
/// 三条口径：① 主键 (member_id, video_id, page_idx)——同 P 再报只覆盖位置与翻 updated_at_utc，一 P 一坑各记各位；
///    （2026-11-06 从「一人一片一行」迁过来：旧口径下切到新 P 报几个数就把看到一半的老 P 顶掉，多 P 续播不可用）
/// ② 片名/封面不抄进本表，读列表时 JOIN videos 现取（片名走有效值那张嘴：站内覆写优先，改了标题历史里挂的也是新那句）（封面走馆藏本地件优先，与首页卡片同口径），
///    片被删了这条历史就自然看不见，不留在库里当死行；
/// ③ 看完的也留行——位置过 95% 由端点照常盖戳落库，历史是完整播放足迹（要不要跳回是前端的闸，过线不续播）。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>一条观看历史（片名与封面是 JOIN 来的现取值，不是本表快照）。</summary>
    public sealed record WatchHistoryRow(
        long VideoId,
        string Bvid,
        string? Title,
        string? LocalCover,
        string? CoverUrl,
        int PageIdx,
        double PositionSec,
        double DurationSec,
        string UpdatedAtUtc);

    /// <summary>续播点位（GET progress 的料）。</summary>
    public sealed record WatchProgress(int PageIdx, double PositionSec, double DurationSec, string UpdatedAtUtc);

    /// <summary>报一个观看位置：一 P 一坑，同 P 新位盖旧位、updated_at_utc 翻新；别的 P 有自己的坑，互不相犯。</summary>
    public void UpsertWatchHistory(long memberId, long videoId, int pageIdx, double positionSec, double durationSec)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO watch_history (member_id, video_id, page_idx, position_sec, duration_sec, updated_at_utc)
            VALUES ($m, $v, $p, $pos, $dur, $now)
            ON CONFLICT (member_id, video_id, page_idx) DO UPDATE
                SET position_sec = excluded.position_sec,
                    duration_sec = excluded.duration_sec,
                    updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$m", memberId);
        command.Parameters.AddWithValue("$v", videoId);
        command.Parameters.AddWithValue("$p", pageIdx);
        command.Parameters.AddWithValue("$pos", positionSec);
        command.Parameters.AddWithValue("$dur", durationSec);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>某人的全部观看历史，最近看过的排前头（MemberPage 历史卡吃这个，滚动展最近十档；LIMIT 兜住别拉爆）。</summary>
    /// <remarks>尺子量在 videos 那一侧：别人转私的稿子不该再挂在别人历史里露标题与封面（行不删，
    /// 只是看不见，与「片被删了行就自然看不见」同一套路）。本人查自己的历史递 VisibilityScope.Of(自己)，
    /// 自己藏的那几条照旧在。
    /// 一 P 一坑后同一部片可能躺着几行，列表收成一卡一片：窗口函数取每部片最近更新的那一 P 露面，
    /// 不让多 P 片子把历史卡阵撑成 P 陈列架。</remarks>
    public IReadOnlyList<WatchHistoryRow> GetWatchHistory(long memberId, int limit = 100, VisibilityScope? scope = null)
    {
        var (visSql, bindVis) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "v");
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        // 封面口径照抄首页卡阵：kind='cover' 的本地件（/media/ + rel）优先，没归档才留 B 站直链给前端热链兑底。
        command.CommandText = $"""
            SELECT h.video_id, v.bvid, {EffTitleExpr},
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   v.cover_url, h.page_idx, h.position_sec, h.duration_sec, h.updated_at_utc
            FROM (
                SELECT video_id, page_idx, position_sec, duration_sec, updated_at_utc,
                       row_number() OVER (PARTITION BY video_id ORDER BY updated_at_utc DESC, rowid DESC) AS rn
                FROM watch_history WHERE member_id = $m
            ) h
            JOIN videos v ON v.id = h.video_id
            WHERE h.rn = 1 AND {visSql}
            ORDER BY h.updated_at_utc DESC, h.video_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$m", memberId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        bindVis(command);

        using var reader = command.ExecuteReader();
        var list = new List<WatchHistoryRow>();
        while (reader.Read())
        {
            list.Add(new WatchHistoryRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : "/media/" + reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetString(8)));
        }

        return list;
    }

    /// <summary>某人某片的一个点位：pageIdx 给了就查那一 P 的坑，不给取最近更新的那一 P；没看过返回 null（前端按「从头播」处置）。</summary>
    public WatchProgress? GetWatchHistoryOne(long memberId, long videoId, int? pageIdx = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT page_idx, position_sec, duration_sec, updated_at_utc
            FROM watch_history WHERE member_id = $m AND video_id = $v
              AND ($p IS NULL OR page_idx = $p)
            ORDER BY updated_at_utc DESC, rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$m", memberId);
        command.Parameters.AddWithValue("$v", videoId);
        command.Parameters.AddWithValue("$p", pageIdx is null ? DBNull.Value : pageIdx.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new WatchProgress(reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetString(3));
    }

    /// <summary>销一个人的某片历史（看完的自动清、用户手动清共用这一下）；没这行返回 false。</summary>
    public bool DeleteWatchHistory(long memberId, long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM watch_history WHERE member_id = $m AND video_id = $v;";
        command.Parameters.AddWithValue("$m", memberId);
        command.Parameters.AddWithValue("$v", videoId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>清空某人全部观看历史，返回清掉的行数（本来就是空的回 0，不算错）。</summary>
    public int ClearWatchHistory(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM watch_history WHERE member_id = $m;";
        command.Parameters.AddWithValue("$m", memberId);
        return command.ExecuteNonQuery();
    }
}
