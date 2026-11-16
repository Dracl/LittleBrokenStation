using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 分站 AI 的读料口（M1）：今日到货数与席位当日调用数。两笔都是只读单发，
/// 单开一文件不挤 StationDb.cs 本体；时间窗口照 QueryEvents 的口径——调用方算好 UTC "O" 串递进来，
/// 字符串比大小（created_at_utc 与 ts_utc 两列都是同一把 "O" 尺写进去的，同形才同序）。
/// </summary>
public sealed partial class StationDb
{
    /// <summary>窗口内到货件数（分站 AI system prompt 的「今日到货」）：
    /// 只数到站的行（status='Arrived'，按 created_at_utc 落格）——与周报「本周新货」同一把上架尺子，
    /// 在途与 Failed 孤稿不算到货。</summary>
    public long CountArrivedToday(string sinceUtc, string untilUtc)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM videos
            WHERE status = 'Arrived' AND created_at_utc >= $since AND created_at_utc < $until;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc);
        command.Parameters.AddWithValue("$until", untilUtc);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>这位席位窗口内用了几次分站 AI（日额度闸门）：数台账里 agent.chat + member + 本席的行。
    /// 账即是额度，不另养第二本计数器；QueryEvents 不吃 actor_member_id 过滤器，这里单开窄口。</summary>
    public long CountAgentChatsToday(long memberId, string sinceUtc, string untilUtc)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM events
            WHERE event = $event AND actor_type = $actor AND actor_member_id = $member
              AND ts_utc >= $since AND ts_utc < $until;
            """;
        command.Parameters.AddWithValue("$event", EventNames.AgentChat);
        command.Parameters.AddWithValue("$actor", EventActors.Member);
        command.Parameters.AddWithValue("$member", memberId);
        command.Parameters.AddWithValue("$since", sinceUtc);
        command.Parameters.AddWithValue("$until", untilUtc);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>今日到站名录（看板娘 prompt，11-16 站长补数据令）：窗口内到站的标题，新先，
    /// 可见性照调用方递的尺子（成员对话用 PublicOnly，与搜索页同一张脸）。同一把 created_at_utc 上架尺，与 CountArrivedToday 同轨。</summary>
    public IReadOnlyList<string> ListTodayArrivalTitles(int limit, string sinceUtc, string untilUtc, VisibilityScope scope)
    {
        if (limit <= 0)
        {
            return Array.Empty<string>();
        }

        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        var (visSql, bindVis) = VisibilityFilter(scope, "v");
        command.CommandText = $"""
            SELECT {EffTitleExpr}
            FROM videos v JOIN video_meta m ON m.video_id = v.id
            WHERE v.status = 'Arrived' AND v.created_at_utc >= $since AND v.created_at_utc < $until AND {visSql}
            ORDER BY v.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc);
        command.Parameters.AddWithValue("$until", untilUtc);
        command.Parameters.AddWithValue("$limit", limit);
        bindVis(command);
        var list = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.IsDBNull(0) ? "(无题)" : reader.GetString(0));
        }

        return list;
    }

    /// <summary>热榜一件：有效标题 + B 站播放文本（「4618.3万」那种原样，prompt 里直接给人看）。</summary>
    public sealed record AgentHotItem(string Title, string? ViewText);

    /// <summary>全站热榜（看板娘 prompt）：按 B 站快照播放降序，只收到货且播数 > 0 的行，可见性照递的尺子。
    /// 排序用现成的 ViewPowerSql（把「4618.3万」折算成数），与首页 sort=plays 同一把尺。</summary>
    public IReadOnlyList<AgentHotItem> ListAgentHottest(int limit, VisibilityScope scope)
    {
        if (limit <= 0)
        {
            return Array.Empty<AgentHotItem>();
        }

        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        var (visSql, bindVis) = VisibilityFilter(scope, "v");
        command.CommandText = $"""
            SELECT {EffTitleExpr}, m.view_count
            FROM videos v JOIN video_meta m ON m.video_id = v.id
            WHERE v.status = 'Arrived' AND ({ViewPowerSql}) > 0 AND {visSql}
            ORDER BY ({ViewPowerSql}) DESC, v.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        bindVis(command);
        var list = new List<AgentHotItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AgentHotItem(reader.IsDBNull(0) ? "(无题)" : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return list;
    }

    /// <summary>这位席位的投稿小账（看板娘 prompt）：累计投稿几件、其中到货几件；锤 submitter_member_id，
    /// 与成员概览（M5.4）同一把归属尺；没人投过回 (0,0)。</summary>
    public (long Total, long Arrived) MemberPostStats(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN status = 'Arrived' THEN 1 ELSE 0 END), 0)
            FROM videos WHERE submitter_member_id = $m;
            """;
        command.Parameters.AddWithValue("$m", memberId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1))) : (0L, 0L);
    }

    // ── 成员自带 AI 接线（11-16 站长拍板：配置从站长室搬进个人空间，一人一行自带干粮）──

    /// <summary>一位席位的 AI 接线（null 行 = 还没接）。全 key 只在出站点现取现用，任何 HTTP 响应不得带它。
    /// ReasoningOptions：竖线分隔的推理强度选型（如 none|low|medium|xhigh），null/空 = 不提供思考档位。
    /// ContextK：上下文窗口大小（单位 k token，11-16 美化令），null/0 = 未配。
    /// ToolsEnabled：M2 工具调用开关，null = 开（席位自拆，站里不拦头）。</summary>
    public sealed record MemberAiConfig(string? BaseUrl, string? ApiKey, string? Model, string? ReasoningOptions, int? ContextK, bool? ToolsEnabled);

    /// <summary>取席位自己的 AI 接线；没配过回 null。</summary>
    public MemberAiConfig? GetMemberAi(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT base_url, api_key, model, reasoning_options, context_k, tools_enabled FROM member_ai WHERE member_id = $member;";
        command.Parameters.AddWithValue("$member", memberId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MemberAiConfig(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : (int)reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0);
    }

    /// <summary>存席位 AI 接线：null 栏不改、空串清栏（COALESCE 一把写完）；没行就凭 INSERT 立行。
    /// key 的「留空 = 不改」语义在端点判（空白归 null 再递进来），这里只认 null/非 null。</summary>
    public void SaveMemberAi(long memberId, string? baseUrl, string? apiKey, string? model, string? reasoningOptions, int? contextK, bool? toolsEnabled)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO member_ai (member_id, base_url, api_key, model, reasoning_options, context_k, tools_enabled, updated_at_utc)
            VALUES ($member, $base, $key, $model, $reasoning, $ctx, $tools, $now)
            ON CONFLICT(member_id) DO UPDATE SET
                base_url          = COALESCE($base, base_url),
                api_key           = COALESCE($key, api_key),
                model             = COALESCE($model, model),
                reasoning_options = COALESCE($reasoning, reasoning_options),
                context_k         = COALESCE($ctx, context_k),
                tools_enabled     = COALESCE($tools, tools_enabled),
                updated_at_utc    = $now;
            """;
        command.Parameters.AddWithValue("$member", memberId);
        command.Parameters.AddWithValue("$base", (object?)baseUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)apiKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasoning", (object?)reasoningOptions ?? DBNull.Value);
        command.Parameters.AddWithValue("$ctx", (object?)contextK ?? DBNull.Value);
        command.Parameters.AddWithValue("$tools", (object?)toolsEnabled switch { null => DBNull.Value, true => 1L, _ => 0L });
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>拆线：连行拔除（key 跟着没）。本人自拆，不需要别的入口。</summary>
    public void ClearMemberAi(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM member_ai WHERE member_id = $member;";
        command.Parameters.AddWithValue("$member", memberId);
        command.ExecuteNonQuery();
    }

    // ── 分站 AI 工具调用铺库（11-17 M2 地基：到货名录与席位活动小账两扇读料口）──

    /// <summary>到站名录一件（看板娘到货工具吃）：列表面七样——播数是 B 站快照文本原样
    /// （「4618.3万」那种，prompt 里直接给人看，与 AgentHotItem 同尺），时刻是到站尺的原样 "O" 串。</summary>
    public sealed record AgentArrivalItem(long Id, string Bvid, string? Title, string? UpName, string? ViewCountText, string? ArrivedAtUtc);

    /// <summary>近 N 日到货名录（看板娘 prompt/工具）：窗口内到站的行，新先。
    /// 口径——「今日/近 N 日」= UTC+8 日历日窗口由调用方算好（与 CountArrivedToday 同族，这里只认两把 "O" 串）；
    /// 到站时刻认 arrived_at_utc 真戳，存量老行为 NULL 时按 created_at_utc 近似（上架尺）；
    /// 可见性照调用方递的尺子（成员对话用 PublicOnly，与搜索页同一张脸）；
    /// JOIN video_meta 是 ListTodayArrivalTitles 的同款姿势：没档案的行进不了名录（UP 名与播数都出自那份档案，缺了报不得）。</summary>
    public IReadOnlyList<AgentArrivalItem> ListArrivalsForAgent(int limit, string sinceUtc, string untilUtc, VisibilityScope scope)
    {
        if (limit <= 0)
        {
            return Array.Empty<AgentArrivalItem>();
        }

        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        var (visSql, bindVis) = VisibilityFilter(scope, "v");
        // 到站尺只在这一处写：筛、排、选三处同一个表达式，改口径只改一行。
        const string arrivedAt = "COALESCE(v.arrived_at_utc, v.created_at_utc)";
        command.CommandText = $"""
            SELECT v.id, v.bvid, {EffTitleExpr}, m.up_name, m.view_count, {arrivedAt}
            FROM videos v JOIN video_meta m ON m.video_id = v.id
            WHERE v.status = 'Arrived' AND {arrivedAt} >= $since AND {arrivedAt} < $until AND {visSql}
            ORDER BY {arrivedAt} DESC, v.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc);
        command.Parameters.AddWithValue("$until", untilUtc);
        command.Parameters.AddWithValue("$limit", limit);
        bindVis(command);
        var list = new List<AgentArrivalItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AgentArrivalItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return list;
    }

    /// <summary>一位席位的活动小账（看板娘 member_activity 工具吃）：投稿/到货/弹幕三笔数
    /// + 最后一投一笔（标题与时刻）；没人投过的席位回全零/全 null，不抛。</summary>
    public sealed record AgentMemberActivity(int Posts, int Arrived, int Danmaku, string? LastPostTitle, string? LastPostAtUtc);

    /// <summary>这位席位的活动小账（看板娘 M2）：三笔聚合各走各的窄口，没人投过回全零/全 null。
    /// 口径——Posts/Arrived 锤 submitter_member_id（与 MemberPostStats 同一把归属尺：投稿行总数、其中 status='Arrived' 算到货）；
    /// Danmaku 从 danmaku_local 算人头（没身份的行人不塑到任何席位头上，与 ListMemberIngestStats 同尺）；
    /// 最后一取该席位 id 最大的那条投稿（任何状态在途也算「最近投了啥」），标题用 EffTitleExpr，
    /// 时刻用到站尺（COALESCE(arrived_at_utc, created_at_utc)，存量 NULL 按上架时刻近似）。</summary>
    public AgentMemberActivity MemberAgentActivity(long memberId)
    {
        using var connection = Open(_dbPath);

        var posts = 0L;
        var arrived = 0L;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(CASE WHEN status = 'Arrived' THEN 1 ELSE 0 END), 0)
                FROM videos WHERE submitter_member_id = $m;
                """;
            command.Parameters.AddWithValue("$m", memberId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                posts = Convert.ToInt64(reader.GetValue(0));
                arrived = Convert.ToInt64(reader.GetValue(1));
            }
        }

        // 弹幕算人头：谓词锤死 member_id（NULL 的行人行天然进不来），与 ListMemberIngestStats 同一把尺。
        var danmaku = 0L;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM danmaku_local WHERE member_id = $m;";
            command.Parameters.AddWithValue("$m", memberId);
            danmaku = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }

        // 最后一投不 JOIN video_meta：有效标题只吃 v 自己的栏，拉上档案反而会把没归档的行挡在门外。
        string? lastTitle = null;
        string? lastAtUtc = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {EffTitleExpr}, COALESCE(v.arrived_at_utc, v.created_at_utc)
                FROM videos v
                WHERE v.submitter_member_id = $m
                ORDER BY v.id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$m", memberId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                lastTitle = reader.IsDBNull(0) ? null : reader.GetString(0);
                lastAtUtc = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        return new AgentMemberActivity((int)posts, (int)arrived, (int)danmaku, lastTitle, lastAtUtc);
    }

    // ── 站内观映（11-17 站长补数据令：看板娘只见过 B 站快照那半张脸，站内观众账从此也递她）──

    /// <summary>站内热榜一件（看板娘 prompt）：有效标题 + 被看次数。</summary>
    public sealed record AgentSiteHotItem(string Title, long Plays);

    /// <summary>站内观映概览（看板娘 prompt / site_stats 工具吃）：窗口内观众账三摄 + 按被看次数的前 N。
    /// 口径是周报那把尺：观映事件族新旧两代都吃（playback.play/heartbeat，11-15 改名前的 watch.* 老行同认），
    /// subject_type='video' 锤 BV 号串；成员人头按 actor_mid 去重（没身份的行人头天然进不来），
    /// 游客没 mid 只能拿 ip 当尺（与 WeeklyDayBar.GuestHeads 同名同口径，同一出口会并成一人）——两笔人头各数各的，不加总。
    /// 热榜与周报「最多人看」不同处是不清全访客榜：按枪数排（成员与游客都算），可见性照调用方递的尺子。</summary>
    public (long Plays, long MemberHeads, long GuestHeads, IReadOnlyList<AgentSiteHotItem> Hottest) SitePlaySummary(
        int topTake, string sinceUtc, string untilUtc, VisibilityScope scope)
    {
        // 周报里这几枚是 private 常量，跨文件够不着；名分在这里对齐同一本账。
        const string playEvent = "playback.play";
        const string playLegacy = "watch.play";
        const string beatLegacy = "watch.heartbeat";

        using var connection = Open(_dbPath);
        long plays = 0, memberHeads = 0, guestHeads = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*),
                       COUNT(DISTINCT actor_mid),
                       COUNT(DISTINCT CASE WHEN actor_type = $guest AND ip IS NOT NULL AND ip <> '' THEN ip END)
                FROM events
                WHERE event IN ($play, $playOld, $beat, $beatOld)
                  AND subject_type = 'video'
                  AND ts_utc >= $since AND ts_utc < $until;
                """;
            command.Parameters.AddWithValue("$play", playEvent);
            command.Parameters.AddWithValue("$playOld", playLegacy);
            command.Parameters.AddWithValue("$beat", EventNames.PlaybackHeartbeat);
            command.Parameters.AddWithValue("$beatOld", beatLegacy);
            command.Parameters.AddWithValue("$guest", EventActors.Guest);
            command.Parameters.AddWithValue("$since", sinceUtc);
            command.Parameters.AddWithValue("$until", untilUtc);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                plays = Convert.ToInt64(reader.GetValue(0));
                memberHeads = Convert.ToInt64(reader.GetValue(1));
                guestHeads = Convert.ToInt64(reader.GetValue(2));
            }
        }

        var hottest = new List<AgentSiteHotItem>();
        if (topTake > 0)
        {
            using var command = connection.CreateCommand();
            var (visSql, bindVis) = VisibilityFilter(scope, "v");
            command.CommandText = $"""
                SELECT {EffTitleExpr}, COUNT(*)
                FROM events e JOIN videos v ON v.bvid = e.subject_id
                WHERE e.event IN ($play, $playOld, $beat, $beatOld)
                  AND e.subject_type = 'video'
                  AND e.ts_utc >= $since AND e.ts_utc < $until AND {visSql}
                GROUP BY v.id, v.title
                ORDER BY COUNT(*) DESC, MAX(e.id) DESC
                LIMIT $take;
                """;
            command.Parameters.AddWithValue("$play", playEvent);
            command.Parameters.AddWithValue("$playOld", playLegacy);
            command.Parameters.AddWithValue("$beat", EventNames.PlaybackHeartbeat);
            command.Parameters.AddWithValue("$beatOld", beatLegacy);
            command.Parameters.AddWithValue("$since", sinceUtc);
            command.Parameters.AddWithValue("$until", untilUtc);
            command.Parameters.AddWithValue("$take", topTake);
            bindVis(command);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                hottest.Add(new AgentSiteHotItem(reader.IsDBNull(0) ? "(无题)" : reader.GetString(0), Convert.ToInt64(reader.GetValue(1))));
            }
        }

        return (plays, memberHeads, guestHeads, hottest);
    }
}
