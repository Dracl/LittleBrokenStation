namespace LBS.Infrastructure;

/// <summary>
/// UP 历史脸档案（周巡脸员 <c>UpFaceSweepWorker</c> 的台账）：一位 UP 在本站存过的<b>历张</b>脸，
/// 换一次脸添一行，同一张脸每周再见只挪 <c>last_seen</c>。
///
/// 为什么不与 <c>video_meta.up_face</c> 共用一处：那张是「一条稿件一行」的档案快照，一个 mid 在几十条稿里
/// 有几十行，脸只留最新那张、还随稿件一起进回收站 —— 拿它当历史脸册，一是「同一个人换过几张脸」表达不了
/// （得往一列里塞 JSON 数组，查不了也比不了新旧），二是稿子一删档那张脸的历史就跟着没了。
/// 所以脸册独立成表，件只落 <see cref="DataPaths.UpFaces"/>，读侧要画哪张由 <c>/api/up/{mid}/faces</c> 现验盘。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>一张历史脸：件地址（<c>/up_faces/{名}</c>，与 <c>up_face</c> 同族写法）+ 本站首见 / 再见时刻（UTC ISO）。</summary>
    public sealed record UpFaceRow(string FaceRel, string FirstSeen, string LastSeen);

    /// <summary>
    /// 记一张脸：没见过的 (mid, 脸) 就立一行（首见 = 再见 = now）；见过的<b>只刷 last_seen</b> ——
    /// 首见是那位第一次被本站撞见的时刻，后头的巡检不许回头改它。同图重复调用幂等。
    /// </summary>
    public void UpsertUpFace(long mid, string faceRel, string nowUtcIso)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO up_faces (mid, face_rel, first_seen, last_seen)
            VALUES ($mid, $rel, $now, $now)
            ON CONFLICT (mid, face_rel) DO UPDATE SET last_seen = excluded.last_seen;
            """;
        command.Parameters.AddWithValue("$mid", mid);
        command.Parameters.AddWithValue("$rel", faceRel);
        command.Parameters.AddWithValue("$now", nowUtcIso);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 该 UP 的全部历史脸，<b>再见时刻新的在前</b>（同一时刻按件名定序，免得同一份数据两轮排两个样）。
    /// 本方法只报台账、不验盘：件被人手删了台账还是有的，「盘上真有件」那把尺归端点量
    /// （与 <see cref="ReconcileCoverThumbRowsWithDisk"/> 那一族同理：台账说有过 ≠ 盘上还有）。
    /// </summary>
    public IReadOnlyList<UpFaceRow> ListUpFaces(long mid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT face_rel, first_seen, last_seen FROM up_faces
            WHERE mid = $mid
            ORDER BY last_seen DESC, face_rel ASC;
            """;
        command.Parameters.AddWithValue("$mid", mid);
        using var reader = command.ExecuteReader();
        var list = new List<UpFaceRow>();
        while (reader.Read())
        {
            list.Add(new UpFaceRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return list;
    }

    /// <summary>
    /// 该巡脸的名单：<c>video_meta.up_mid</c>（馆藏稿子的作者）与 <c>dynamics_targets.mid</c>（盯梢的 UP）
    /// 两条腿的并集去重 —— 只稿子里有过的人漏掉「只被盯没被投稿」的那批，只看板又漏掉存量老货，
    /// 两边都得巡。脏值（NULL 与 &lt;=0）不进名单：那不是任何人，敲 B 站只会白挨一发。
    /// </summary>
    public IReadOnlyList<long> ListSweepableMids()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT up_mid FROM video_meta WHERE up_mid IS NOT NULL AND up_mid > 0
            UNION
            SELECT mid FROM dynamics_targets WHERE mid IS NOT NULL AND mid > 0
            ORDER BY 1;
            """;
        using var reader = command.ExecuteReader();
        var list = new List<long>();
        while (reader.Read())
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }
}
