using LBS.Domain;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

public enum AddVideoResult
{
    Inserted,
    Duplicate,
}

/// <summary>入库结果：重复时 VideoId 为 0。</summary>
public sealed record AddVideoOutcome(AddVideoResult Result, long VideoId);

/// <summary>
/// 分站元数据库（SQLite，WAL）。M0 只有 videos 表；
/// 收藏夹、分 P、弹幕、字幕表随 M1/M3 功能到达时逐个建，不提前占坑。
/// 成员/会话/事件台账的读写在 partial 文件 StationDb.Members.cs / StationDb.Events.cs。
/// </summary>
public sealed partial class StationDb
{
    private readonly string _dbPath;
    private readonly DataPaths _paths;

    public StationDb(DataPaths paths)
    {
        _dbPath = paths.DatabaseFile;
        _paths = paths;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        // busy_timeout 放宽到 15 秒：巡捕批量落库的高峰，读写撞车时让 SQLite 自己多等几秒，
        // 别让 GetWatchData 这种无辜读客吃 database is locked。
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    public void Initialize()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS videos (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                url            TEXT NOT NULL,
                bvid           TEXT NOT NULL UNIQUE,
                avid           INTEGER,
                title          TEXT,
                status         TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                engine_task_ids TEXT,
                last_error     TEXT
            );
            """;
        command.ExecuteNonQuery();

        // 归档目录三件套：档案信息、分 P、产物文件。建表幂等，无需迁移。
        using (var archive = connection.CreateCommand())
        {
            archive.CommandText = """
                CREATE TABLE IF NOT EXISTS video_meta (
                    video_id       INTEGER PRIMARY KEY,
                    up_name        TEXT,
                    up_mid         INTEGER,
                    zone           TEXT,
                    publish_text   TEXT,
                    description    TEXT,
                    view_count     TEXT
                );
                CREATE TABLE IF NOT EXISTS pages (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id         INTEGER NOT NULL,
                    task_id          TEXT NOT NULL,
                    idx              INTEGER NOT NULL,
                    title            TEXT NOT NULL,
                    cid              INTEGER,
                    duration_text    TEXT,
                    base_path        TEXT NOT NULL,
                    local_video_path TEXT
                );
                CREATE TABLE IF NOT EXISTS artifacts (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id  INTEGER NOT NULL,
                    page_idx  INTEGER,
                    kind      TEXT NOT NULL,
                    rel_path  TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS video_codecs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id        INTEGER NOT NULL,
                    page_idx        INTEGER NOT NULL,
                    codec           TEXT NOT NULL,
                    status          TEXT NOT NULL,
                    engine_task_ids TEXT,
                    base_path       TEXT,
                    rel_path        TEXT,
                    last_error      TEXT,
                    created_at_utc  TEXT NOT NULL,
                    UNIQUE(video_id, page_idx, codec)
                );
                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS danmaku_local (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id       INTEGER NOT NULL,
                    page_idx       INTEGER NOT NULL,
                    t_sec          REAL NOT NULL,
                    text           TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                """;
            archive.ExecuteNonQuery();
        }

        // 悬停预览台账：紧跟 artifacts 之后另开一锤（幂等无迁移）。
        // 预览成片只进这张表与 data/previews，绝不进 artifacts——否则缺件回炉（MissingMediaReconciler）
        // 会拿预览行验盘，把它当缺产物、整片回炉重下。
        using (var preview = connection.CreateCommand())
        {
            preview.CommandText = """
                CREATE TABLE IF NOT EXISTS previews (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id       INTEGER NOT NULL UNIQUE,
                    page_idx       INTEGER,
                    rel_path       TEXT NOT NULL,
                    width          INTEGER,
                    height         INTEGER,
                    seconds        REAL,
                    bytes          INTEGER,
                    cost_seconds   REAL,
                    status         TEXT NOT NULL,
                    last_error     TEXT,
                    attempts       INTEGER NOT NULL DEFAULT 0,
                    created_at_utc TEXT NOT NULL
                );
                """;
            preview.ExecuteNonQuery();
        }

        // 站内弹幕举报台账：举报即定格——弹幕正文、被举报人、举报人、视频链接全在落单那一刻抄成快照，
        // 之后弹幕被删、席位改名都不回头改这张单（所以读列表一律直读本表，不再 JOIN 别处）。
        // danmaku_local 本身没有状态列也不加：pending 的弹幕照常飘，只有站长处置才真删。
        using (var report = connection.CreateCommand())
        {
            report.CommandText = """
                CREATE TABLE IF NOT EXISTS danmaku_reports (
                    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                    dm_id              INTEGER NOT NULL,
                    video_id           INTEGER NOT NULL,
                    bvid               TEXT,
                    page_idx           INTEGER NOT NULL,
                    t_sec              REAL NOT NULL,
                    text               TEXT NOT NULL,
                    sender_member_id   INTEGER,
                    sender_dmid        TEXT,
                    sender_name        TEXT,
                    dm_created_at_utc  TEXT,
                    reporter_member_id INTEGER NOT NULL,
                    reporter_name      TEXT,
                    reason             TEXT,
                    status             TEXT NOT NULL DEFAULT 'pending',
                    created_at_utc     TEXT NOT NULL,
                    handled_at_utc     TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_dmreport_dm_reporter ON danmaku_reports (dm_id, reporter_member_id);
                CREATE INDEX IF NOT EXISTS idx_dmreport_status ON danmaku_reports (status, created_at_utc);
                """;
            report.ExecuteNonQuery();
        }

        // 观看历史：一人一片一行（位置随看随覆盖），只存「看到哪了」这四个数。
        // 看完的不留行（95% 以上直接删行，不把看完的片子堆在历史里），行也不存片名与封面：
        // 那些随馆藏走，读列表时 JOIN videos 现取（片被删了行就跟着看不见，不留死行）。
        using (var history = connection.CreateCommand())
        {
            history.CommandText = """
                CREATE TABLE IF NOT EXISTS watch_history (
                    member_id    INTEGER NOT NULL,
                    video_id     INTEGER NOT NULL,
                    page_idx     INTEGER NOT NULL,
                    position_sec REAL NOT NULL,
                    duration_sec REAL NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (member_id, video_id)
                );
                CREATE INDEX IF NOT EXISTS idx_watchhistory_member ON watch_history (member_id, updated_at_utc);
                """;
            history.ExecuteNonQuery();
        }

        // 站内合集：会员自己攒的片单。集只存名、简介与东家；item 只存 (collection_id, video_id) 这一对，
        // 片名封面一律读时 JOIN videos 现取（片被删了这条 item 就自然看不见，不留死行，与观看历史同一哲学）。
        // 一人可开几个集，同片在同集只占一坑（主键挡重，重复入集幂等）。
        using (var collections = connection.CreateCommand())
        {
            collections.CommandText = """
                CREATE TABLE IF NOT EXISTS collections (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    name            TEXT NOT NULL,
                    intro           TEXT,
                    owner_member_id INTEGER NOT NULL,
                    created_at_utc  TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS collection_items (
                    collection_id INTEGER NOT NULL,
                    video_id      INTEGER NOT NULL,
                    added_at_utc  TEXT NOT NULL,
                    PRIMARY KEY (collection_id, video_id)
                );
                CREATE INDEX IF NOT EXISTS idx_collection_owner ON collections (owner_member_id, created_at_utc);
                CREATE INDEX IF NOT EXISTS idx_collection_items ON collection_items (collection_id, added_at_utc);
                """;
            collections.ExecuteNonQuery();
        }

        // 逐件生成耗时（秒，ffmpeg 从起跑到交件的墙钟时间）：站长室「单件耗时」只吃这一列的真数，
        // 绝拿 seconds（产物时长）冒充。早先建的库没这列，缺就补。
        EnsureColumn(connection, "previews", "cost_seconds", "REAL");

        // 站内分区的站长改派槽：有效分区 = site_zone 优先，空则继承 B 站归档的 video_meta.zone（形如「音乐>翻唱」）。
        // 分区页只认顶级段；未入站无档案又没改派的货没户口，不进分区页。
        EnsureColumn(connection, "videos", "site_zone", "TEXT");

        // 旧库升级：M0 建的表没有引擎字段，缺列就补（SQLite ADD COLUMN 不锁表，小库无所谓）。
        EnsureColumn(connection, "videos", "engine_task_ids", "TEXT");
        EnsureColumn(connection, "videos", "last_error", "TEXT");
        EnsureColumn(connection, "videos", "cover_url", "TEXT");
        // B 站投稿时间（ISO）：导入源（UP 投稿列表 created / 收藏夹 pubtime）入库时就存，首页卡阵展示用。
        EnsureColumn(connection, "videos", "published_at_utc", "TEXT");
        // 投稿人（B 站 UID，与 events.actor_mid 同口径，NULL=站长或史前）：下载提交时据此走投稿人的账号池。
        EnsureColumn(connection, "videos", "submitter_mid", "INTEGER");
        // 主版本编码实测缓存（'avc'/'hevc'/'av1'）：首次看片时 ffprobe 实测回填，之后不再探。
        EnsureColumn(connection, "pages", "codec", "TEXT");
        // UP 主名片快照（头像本地化相对路径 / 简介 / 粉丝数），由 LbsUpInfoFetcher 到站时采。
        EnsureColumn(connection, "video_meta", "up_face", "TEXT");
        EnsureColumn(connection, "video_meta", "up_sign", "TEXT");
        EnsureColumn(connection, "video_meta", "up_fans", "INTEGER");
        // B 站互动数据快照（view 接口 stat 字段真采）：stat_probed=1 表示已尝试过，失败也不重复扰 B 站。
        EnsureColumn(connection, "video_meta", "stat_like", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_coin", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_fav", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_share", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_reply", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_probed", "INTEGER");
        // 站内弹幕的发送者匿名凭证（仅发送者本人可删，主人例外）。
        EnsureColumn(connection, "danmaku_local", "sender_dmid", "TEXT");
        EnsureColumn(connection, "danmaku_local", "sender_owner", "INTEGER DEFAULT 0");
        // 发弹幕时带着席位会话就把人存下来：弹幕那颗小圆才有真容可画（头像只存 members 一处，不冗余 URL）。
        EnsureColumn(connection, "danmaku_local", "member_id", "INTEGER");

        // M5 成员体系：席位（邀请制）、账号池（cookie 只进不出）、会话（落盘，重启不踢朋友）。
        using (var members = connection.CreateCommand())
        {
            members.CommandText = """
                CREATE TABLE IF NOT EXISTS members (
                    id                INTEGER PRIMARY KEY AUTOINCREMENT,
                    mid               INTEGER UNIQUE,
                    invite_code       TEXT UNIQUE,
                    nickname          TEXT,
                    face_url          TEXT,
                    note              TEXT,
                    status            TEXT NOT NULL,
                    role              TEXT NOT NULL DEFAULT 'member',
                    created_at_utc    TEXT NOT NULL,
                    bound_at_utc      TEXT,
                    last_login_at_utc TEXT
                );
                CREATE TABLE IF NOT EXISTS member_accounts (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    member_id      INTEGER NOT NULL,
                    account_mid    INTEGER NOT NULL,
                    account_name   TEXT,
                    face_url       TEXT,
                    cookie         TEXT NOT NULL,
                    is_primary     INTEGER NOT NULL DEFAULT 0,
                    expired        INTEGER NOT NULL DEFAULT 0,
                    created_at_utc TEXT NOT NULL,
                    last_seen_utc  TEXT,
                    UNIQUE(member_id, account_mid)
                );
                CREATE TABLE IF NOT EXISTS member_sessions (
                    token_hash     TEXT PRIMARY KEY,
                    member_id      INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL
                );
                """;
            members.ExecuteNonQuery();
        }

        // M5.2 站内信箱：系统传票（欢迎/归档/失败回执）与全站公告都落行；
        // 读态按 (to_mid, read_at_utc) 索引扫，标读永远带 to_mid 条件防越席。
        using (var messages = connection.CreateCommand())
        {
            messages.CommandText = """
                CREATE TABLE IF NOT EXISTS messages (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    to_mid         INTEGER NOT NULL,
                    from_mid       INTEGER,
                    kind           TEXT NOT NULL,
                    title          TEXT NOT NULL,
                    body           TEXT NOT NULL,
                    link           TEXT,
                    created_at_utc TEXT NOT NULL,
                    read_at_utc    TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_messages_to_read ON messages (to_mid, read_at_utc);
                """;
            messages.ExecuteNonQuery();
        }

        // M5.3 用户间私信：往来说一行一发，会话在查询时按 peer 聚合；
        // 双索引各管一头：收信方扫未读走 (to_mid, read_at_utc)，会话拉取走 (from_mid, to_mid)。
        using (var dm = connection.CreateCommand())
        {
            dm.CommandText = """
                CREATE TABLE IF NOT EXISTS dm_messages (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    from_mid       INTEGER NOT NULL,
                    to_mid         INTEGER NOT NULL,
                    body           TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    read_at_utc    TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_dm_to_read ON dm_messages (to_mid, read_at_utc);
                CREATE INDEX IF NOT EXISTS idx_dm_from_to ON dm_messages (from_mid, to_mid);
                """;
            dm.ExecuteNonQuery();
        }

        // M6 UP 主动态缓存：targets 是被盯的 UP（订阅台账种子 + 站长自加），dynamics 是抓回的动态橱窗。
        // 缓存键 dynamic_id 全局唯一（同一动态被两个 target 拓到只存一份），游标存 targets.last_offset。
        using (var dynamics = connection.CreateCommand())
        {
            dynamics.CommandText = """
                CREATE TABLE IF NOT EXISTS dynamics_targets (
                    id                INTEGER PRIMARY KEY AUTOINCREMENT,
                    mid               INTEGER UNIQUE NOT NULL,
                    nickname          TEXT,
                    face_url          TEXT,
                    enabled           INTEGER NOT NULL DEFAULT 1,
                    last_offset       TEXT,
                    last_fetch_at_utc TEXT,
                    created_at_utc    TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS dynamics (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    dynamic_id       TEXT UNIQUE NOT NULL,
                    target_mid       INTEGER NOT NULL,
                    author_mid       INTEGER,
                    author_name      TEXT,
                    type             TEXT NOT NULL,
                    text             TEXT,
                    pics_json        TEXT,
                    bvid             TEXT,
                    published_at_utc TEXT,
                    cached_at_utc    TEXT NOT NULL,
                    author_face_url  TEXT,
                    archive_title    TEXT,
                    archive_cover    TEXT,
                    archive_duration TEXT,
                    stat_comment     INTEGER,
                    stat_forward     INTEGER,
                    stat_like        INTEGER,
                    orig_author_mid  INTEGER,
                    orig_author_name TEXT,
                    orig_author_face TEXT,
                    comment_type     INTEGER,
                    comment_oid      TEXT,
                    emotes_json      TEXT,
                    comments_fetched_at_utc TEXT,
                    pics_local_json  TEXT,
                    cover_local      TEXT,
                    body_emotes_json TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_dynamics_target ON dynamics (target_mid, published_at_utc);
                CREATE INDEX IF NOT EXISTS idx_dynamics_published ON dynamics (published_at_utc);
                CREATE INDEX IF NOT EXISTS idx_dynamics_cmt_pending ON dynamics (comments_fetched_at_utc, id);
                CREATE TABLE IF NOT EXISTS dynamic_comments (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    dynamic_id    TEXT NOT NULL,
                    rpid          INTEGER NOT NULL,
                    root_rpid     INTEGER NOT NULL DEFAULT 0,
                    parent_rpid   INTEGER NOT NULL DEFAULT 0,
                    mid           INTEGER,
                    name          TEXT,
                    face          TEXT,
                    face_local    TEXT,
                    message       TEXT NOT NULL,
                    like_count    INTEGER NOT NULL DEFAULT 0,
                    ctime_utc     TEXT,
                    cached_at_utc TEXT NOT NULL,
                    pictures_json TEXT,
                    pictures_local_json TEXT,
                    UNIQUE (dynamic_id, rpid)
                );
                CREATE INDEX IF NOT EXISTS idx_dyncmt_tree ON dynamic_comments (dynamic_id, root_rpid, id);
                """;
            dynamics.ExecuteNonQuery();
        }

        // 动态卡面四件料（头像缓存 + 档案三件套）：早期无列的开发库在这里补上。
        EnsureColumn(connection, "dynamics", "author_face_url", "TEXT");
        EnsureColumn(connection, "dynamics", "archive_title", "TEXT");
        EnsureColumn(connection, "dynamics", "archive_cover", "TEXT");
        EnsureColumn(connection, "dynamics", "archive_duration", "TEXT");
        // 动态底栏三计数 + 转发灰块原作者名片 + 评论抓取完工碑：同样建表自带头 + EnsureColumn 补旧库。
        EnsureColumn(connection, "dynamics", "stat_comment", "INTEGER");
        EnsureColumn(connection, "dynamics", "stat_forward", "INTEGER");
        EnsureColumn(connection, "dynamics", "stat_like", "INTEGER");
        EnsureColumn(connection, "dynamics", "orig_author_mid", "INTEGER");
        EnsureColumn(connection, "dynamics", "orig_author_name", "TEXT");
        EnsureColumn(connection, "dynamics", "orig_author_face", "TEXT");
        EnsureColumn(connection, "dynamics", "comments_fetched_at_utc", "TEXT");
        // 评论区寻址坐标：B 站把评论区挂在 basic.comment_type/comment_id_str 上（视频卡→视频评论区、图文卡→相簿），巡捕抓评论靠它。
        EnsureColumn(connection, "dynamics", "comment_type", "INTEGER");
        EnsureColumn(connection, "dynamics", "comment_oid", "TEXT");
        // 表情定格（转义符→本地图 url 的 JSON）挂动态行；评论头像本地化路径挂评论行，与视频评论区同一套 data/avatars、data/emotes。
        EnsureColumn(connection, "dynamics", "emotes_json", "TEXT");
        EnsureColumn(connection, "dynamic_comments", "face_local", "TEXT");
        // 动态侧图片资产落盘路径（与视频侧同库 data/avatars、emotes、cmt-img）：九宫格本地图数组与 pics_json 同序对齐（单图失败存 null 元素）、
        // 卡面封面前本地、正文 [表情] 定格映射；动态评论配图远程/本地两数组同序对齐。
        EnsureColumn(connection, "dynamics", "pics_local_json", "TEXT");
        EnsureColumn(connection, "dynamics", "cover_local", "TEXT");
        EnsureColumn(connection, "dynamics", "body_emotes_json", "TEXT");
        EnsureColumn(connection, "dynamic_comments", "pictures_json", "TEXT");
        EnsureColumn(connection, "dynamic_comments", "pictures_local_json", "TEXT");
        // 发评人 B 站等级（官方徽记渲染用）；存量行由巡捕的等级补员逐批盖章。
        EnsureColumn(connection, "dynamic_comments", "level", "INTEGER");

        // M6+ 私信变厚：支持「分享动态」卡，msg_type 分文本/动态，dyn_json 存分享那刻定格的卡面快照
        // （B 站删动态不连累分站留档，会话渲染也不回头查表）。
        EnsureColumn(connection, "dm_messages", "msg_type", "TEXT");
        EnsureColumn(connection, "dm_messages", "dynamic_id", "TEXT");
        EnsureColumn(connection, "dm_messages", "dyn_json", "TEXT");

        // 成员角色（owner/member）：头名即站长。建表语句自带头，早期无 role 列的开发库在这里补上。
        EnsureColumn(connection, "members", "role", "TEXT NOT NULL DEFAULT 'member'");

        // M5 事件台账：分站的海马体，AI 分析的原料。只增不删，双索引保查询。
        using (var events = connection.CreateCommand())
        {
            events.CommandText = """
                CREATE TABLE IF NOT EXISTS events (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts_utc       TEXT NOT NULL,
                    actor_type   TEXT NOT NULL,
                    actor_mid    INTEGER,
                    event        TEXT NOT NULL,
                    subject_type TEXT,
                    subject_id   TEXT,
                    payload      TEXT,
                    ip           TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_events_actor ON events(actor_mid, ts_utc);
                CREATE INDEX IF NOT EXISTS idx_events_event ON events(event, ts_utc);
                """;
            events.ExecuteNonQuery();
        }

        // M5.4 站内账户体系：站内身份才是真身份，B 站 mid 降级为可选绑定。
        // members 加 username/password_hash（SQLite 不能给既有表 ADD COLUMN UNIQUE，唯一性走 COLLATE NOCASE 唯一索引，
        // NULL 随便多份——素净席位可以没用户名）；四张锚在 mid 上的表各加 *_member_id 列并一次性回填，
        // 旧 mid 列保留不再写（存量日志与 AI 旧账照旧可读）。
        EnsureColumn(connection, "members", "username", "TEXT");
        EnsureColumn(connection, "members", "password_hash", "TEXT");
        EnsureColumn(connection, "messages", "to_member_id", "INTEGER");
        EnsureColumn(connection, "dm_messages", "from_member_id", "INTEGER");
        EnsureColumn(connection, "dm_messages", "to_member_id", "INTEGER");
        EnsureColumn(connection, "videos", "submitter_member_id", "INTEGER");
        EnsureColumn(connection, "events", "actor_member_id", "INTEGER");
        using (var uname = connection.CreateCommand())
        {
            // 大小写不敏感判重（NOCASE 对 ASCII 生效，汉字本无大小写）；登录也按这把尺子认人。
            uname.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_members_username ON members(username COLLATE NOCASE) WHERE username IS NOT NULL;";
            uname.ExecuteNonQuery();
        }

        // 回填只跑一次：settings KV 立碑守卫（跑前查、跑后置位），整段一个事务不给半吊子留缝。
        var migrated = false;
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM settings WHERE key = 'm54-migrated' LIMIT 1;";
            migrated = probe.ExecuteScalar() is not null;
        }

        if (!migrated)
        {
            using var backfill = connection.BeginTransaction();
            foreach (var sql in new[]
            {
                "UPDATE messages SET to_member_id = (SELECT id FROM members WHERE members.mid = messages.to_mid) WHERE to_member_id IS NULL;",
                """
                UPDATE dm_messages
                SET from_member_id = (SELECT id FROM members WHERE members.mid = dm_messages.from_mid),
                    to_member_id   = (SELECT id FROM members WHERE members.mid = dm_messages.to_mid)
                WHERE from_member_id IS NULL OR to_member_id IS NULL;
                """,
                "UPDATE videos SET submitter_member_id = (SELECT id FROM members WHERE members.mid = videos.submitter_mid) WHERE submitter_member_id IS NULL AND submitter_mid IS NOT NULL;",
                // 访客事件 actor_mid=0 不是任何人，不挂身份。
                "UPDATE events SET actor_member_id = (SELECT id FROM members WHERE members.mid = events.actor_mid) WHERE actor_member_id IS NULL AND actor_mid IS NOT NULL AND actor_mid <> 0;",
            })
            {
                using var statement = connection.CreateCommand();
                statement.Transaction = backfill;
                statement.CommandText = sql;
                statement.ExecuteNonQuery();
            }

            using var marker = connection.CreateCommand();
            marker.Transaction = backfill;
            marker.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ('m54-migrated', $now);";
            marker.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            marker.ExecuteNonQuery();
            backfill.Commit();
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        var exists = false;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 站长室页头总览：台账聚合 + 视频库磁盘总体积。
    /// 播放量用 ViewPowerSql 把「4618.3万」这类 B 站文本折算成数；互动五维是 stat_* 真数直接 SUM。
    /// 体积每次现扫磁盘（只给字节真数，不编），页头一次加载扫一遍可接受。
    /// </summary>
    public AdminStats AdminStats()
    {
        using var connection = Open(_dbPath);

        var total = 0;
        var arrived = 0;
        var downloading = 0;
        var pending = 0;
        var failed = 0;
        var liveAccounts = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(status = 'Arrived'), 0),
                       COALESCE(SUM(status = 'Downloading'), 0),
                       COALESCE(SUM(status = 'Pending'), 0),
                       COALESCE(SUM(status = 'Failed'), 0)
                FROM videos;
                """;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                total = reader.GetInt32(0);
                arrived = reader.GetInt32(1);
                downloading = reader.GetInt32(2);
                pending = reader.GetInt32(3);
                failed = reader.GetInt32(4);
            }
        }

        // 采集号的家底：成员号池里还有几支活号（站里的活——评论/弹幕归档、UP 名片、动态、加档——全指着它们，
        // 一支不剩则采集停摆；以前这个位置报的是「副号定档」多少条，副号随站级登录态一起退役了）。
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM member_accounts a
                JOIN members m ON m.id = a.member_id
                WHERE a.expired = 0 AND m.status = 'active';
                """;
            liveAccounts = (int)(long)(command.ExecuteScalar() ?? 0L);
        }

        long viewTotal = 0;
        var likeTotal = 0L;
        var coinTotal = 0L;
        var favTotal = 0L;
        var shareTotal = 0L;
        var replyTotal = 0L;
        var upCount = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT COUNT(DISTINCT up_mid),
                       COALESCE(SUM(CASE WHEN ({ViewPowerSql}) > 0 THEN ({ViewPowerSql}) ELSE 0 END), 0),
                       COALESCE(SUM(stat_like), 0),
                       COALESCE(SUM(stat_coin), 0),
                       COALESCE(SUM(stat_fav), 0),
                       COALESCE(SUM(stat_share), 0),
                       COALESCE(SUM(stat_reply), 0)
                FROM video_meta m;
                """;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                upCount = reader.GetInt32(0);
                viewTotal = (long)reader.GetDouble(1);
                likeTotal = reader.GetInt64(2);
                coinTotal = reader.GetInt64(3);
                favTotal = reader.GetInt64(4);
                shareTotal = reader.GetInt64(5);
                replyTotal = reader.GetInt64(6);
            }
        }

        long totalBytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_paths.Videos, "*", SearchOption.AllDirectories))
            {
                try
                {
                    totalBytes += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 占用/没权限的文件不拦汇总，少算一个算一个
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录不可读则体积不给数（前端显破折号）
        }

        return new AdminStats(total, arrived, downloading, pending, failed, liveAccounts, totalBytes, viewTotal, likeTotal, coinTotal, favTotal, shareTotal, replyTotal, upCount);
    }

    public IReadOnlyList<VideoEntry> ListVideos()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id, v.url, v.bvid, v.avid, v.title, v.status, v.created_at_utc, v.cover_url, v.last_error,
                   m.up_name, m.view_count, p.duration_text, v.published_at_utc, m.publish_text,
                   (SELECT COUNT(1) FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'danmaku'),
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   (SELECT p.rel_path FROM previews p WHERE p.video_id = v.id AND p.status = 'Arrived' LIMIT 1)
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            LEFT JOIN pages p ON p.video_id = v.id
                 AND p.idx = (SELECT MIN(idx) FROM pages WHERE video_id = v.id)
            ORDER BY v.id DESC;
            """;

        using var reader = command.ExecuteReader();
        return ReadEntries(reader);
    }

    /// <summary>
    /// 全站搜索：只搜已到站的视频（搜索是为了看，不是为了查队列）。
    /// 空格分词后词间 AND，每个词在标题/UP主/BV号/分区/简介任一命中即算；LIKE 通配符按字面处理。
    /// sort="plays" 按播放数降序（见 ViewPowerSql），默认新到站优先。
    /// </summary>
    public IReadOnlyList<VideoEntry> SearchVideos(string query, string? sort, int limit = 50)
    {
        var words = query.Split((char[]?)[' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || limit <= 0)
        {
            return Array.Empty<VideoEntry>();
        }

        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();

        var conditions = new List<string>();
        var wordIndex = 0;
        foreach (var word in words)
        {
            // 转义 LIKE 通配符：搜「100%」就该匹配字面量，而不是变成全表扫。
            var pattern = "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var name = "$w" + wordIndex++;
            command.Parameters.AddWithValue(name, pattern);
            // SQL 的 ESCAPE 子句字面量：单引号包一个反斜杠，用逐字串避开转义歧义。
            const string esc = @"'\'";
            conditions.Add($"(v.title LIKE {name} ESCAPE {esc} OR m.up_name LIKE {name} ESCAPE {esc} OR v.bvid LIKE {name} ESCAPE {esc} OR m.zone LIKE {name} ESCAPE {esc} OR m.description LIKE {name} ESCAPE {esc})");
        }

        // sort 是白名单拼接，不取用户自由输入进 ORDER BY。
        var order = sort == "plays" ? ViewPowerSql + " DESC, v.id DESC" : "v.id DESC";

        command.CommandText = $"""
            SELECT v.id, v.url, v.bvid, v.avid, v.title, v.status, v.created_at_utc, v.cover_url, v.last_error,
                   m.up_name, m.view_count, p.duration_text, v.published_at_utc, m.publish_text,
                   (SELECT COUNT(1) FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'danmaku'),
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   (SELECT p.rel_path FROM previews p WHERE p.video_id = v.id AND p.status = 'Arrived' LIMIT 1)
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            LEFT JOIN pages p ON p.video_id = v.id
                 AND p.idx = (SELECT MIN(idx) FROM pages WHERE video_id = v.id)
            WHERE v.status = $status AND {string.Join(" AND ", conditions)}
            ORDER BY {order}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        return ReadEntries(reader);
    }

    /// <summary>
    /// 把 B 站文本播放数折算成可排序数值的 SQL 表达式（m.view_count 作用域内）。
    /// 「4582」「4618.3万」「2.1亿」混着存：去小数点后 CAST 取整数部（遇中文自动截断），
    /// 有小数点再除回 0.1，最后乘万/亿量级；空文本与无数字脏值垫底 -1。
    /// </summary>
    private const string ViewPowerSql = """
        CASE
            WHEN m.view_count IS NULL OR REPLACE(REPLACE(REPLACE(m.view_count,'万',''),'亿',''),' ','') = '' THEN -1
            ELSE (
                CAST(REPLACE(m.view_count, '.', '') AS REAL) * CASE
                    WHEN instr(m.view_count, '.') > 0 THEN 0.1
                    ELSE 1
                END
            ) * CASE
                WHEN m.view_count LIKE '%亿' THEN 100000000.0
                WHEN m.view_count LIKE '%万' THEN 10000.0
                ELSE 1.0
            END
        END
        """;

    /// <summary>把列表查询的固定列形状读成 VideoEntry（ListVideos / SearchVideos / 相关推荐共用），并补扫弹幕条数。
    /// 列下标写死的：加一列要同步三处 SELECT，漏一处就是整页报锈。</summary>
    private List<VideoEntry> ReadEntries(SqliteDataReader reader)
    {
        var list = new List<VideoEntry>();
        var danmakuFiles = new List<(long Id, int Files)>();
        while (reader.Read())
        {
            var dmFiles = reader.GetInt32(14);
            list.Add(new VideoEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Enum.Parse<VideoStatus>(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                dmFiles > 0 ? 0 : null, // 占位，后面真数 Dialogue 行
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                // 本地封面与观看页同形（/media/ + rel）；没 kind='cover' 行留 null，前端热链兑底。
                reader.IsDBNull(15) ? null : "/media/" + reader.GetString(15),
                // 悬停预览与本地封面同形（/previews/ + rel，rel 相对 data/previews）；没 Arrived 行留 null，前端不摆预览。
                // 具名传参：CanManage 由上层 TagManageable 后补，这里不占位。
                PreviewUrl: reader.IsDBNull(16) ? null : "/previews/" + reader.GetString(16)));
            if (dmFiles > 0)
            {
                danmakuFiles.Add((list[^1].Id, dmFiles));
            }
        }

        foreach (var (id, _) in danmakuFiles)
        {
            var count = CountDanmakuLines(id);
            var index = list.FindIndex(e => e.Id == id);
            if (index >= 0)
            {
                list[index] = list[index] with { DanmakuCount = count };
            }
        }

        return list;
    }

    /// <summary>数某视频全部弹幕档案的 Dialogue 行；任一文件读不到就整条不给数（宁缺毋假）。</summary>
    private int? CountDanmakuLines(long videoId)
    {
        var relPaths = new List<string>();
        using (var connection = Open(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT rel_path FROM artifacts WHERE video_id = $id AND kind = 'danmaku';";
            command.Parameters.AddWithValue("$id", videoId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                relPaths.Add(reader.GetString(0));
            }
        }

        var total = 0;
        foreach (var rel in relPaths)
        {
            try
            {
                var file = Path.Combine(_paths.Videos, rel);
                total += File.ReadLines(file).Count(static line => line.StartsWith("Dialogue:", StringComparison.Ordinal));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return relPaths.Count > 0 ? total : null;
    }

    /// <summary>批量入库：库内已有 bvid 与批内重复都计入 Duplicated，单事务提交。</summary>
    public AddVideosOutcome AddVideosBulk(IReadOnlyList<NewVideo> videos)
    {
        using var connection = Open(_dbPath);
        using var transaction = connection.BeginTransaction();

        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var load = connection.CreateCommand())
        {
            load.CommandText = "SELECT bvid FROM videos;";
            using var reader = load.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(0));
            }
        }

        var added = 0;
        var duplicated = 0;
        var insertedIds = new List<long>();
        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var video in videos)
        {
            if (!existing.Add(video.Bvid))
            {
                duplicated++;
                // 重复行补档案：导入源（UP 投稿/收藏夹）自带封面与投稿时间，旧行缺哪件补哪件，
                // 已有的不覆盖（到站后本地封面/校过的时间更可信）。
                if (!string.IsNullOrWhiteSpace(video.CoverUrl) || !string.IsNullOrWhiteSpace(video.PublishedAtUtc))
                {
                    using var backfill = connection.CreateCommand();
                    backfill.CommandText = """
                        UPDATE videos SET
                            cover_url = CASE WHEN cover_url IS NULL THEN COALESCE($cover, cover_url) ELSE cover_url END,
                            published_at_utc = COALESCE(published_at_utc, $pub)
                        WHERE bvid = $bvid;
                        """;
                    backfill.Parameters.AddWithValue("$cover", (object?)video.CoverUrl ?? DBNull.Value);
                    backfill.Parameters.AddWithValue("$pub", (object?)video.PublishedAtUtc ?? DBNull.Value);
                    backfill.Parameters.AddWithValue("$bvid", video.Bvid);
                    backfill.ExecuteNonQuery();
                }
                continue;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO videos (url, bvid, title, cover_url, published_at_utc, status, created_at_utc)
                VALUES ($url, $bvid, $title, $cover, $pub, $status, $now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$url", video.Url);
            insert.Parameters.AddWithValue("$bvid", video.Bvid);
            insert.Parameters.AddWithValue("$title", (object?)video.Title ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cover", (object?)video.CoverUrl ?? DBNull.Value);
            insert.Parameters.AddWithValue("$pub", (object?)video.PublishedAtUtc ?? DBNull.Value);
            insert.Parameters.AddWithValue("$status", VideoStatus.Pending.ToString());
            insert.Parameters.AddWithValue("$now", now);
            insertedIds.Add(Convert.ToInt64(insert.ExecuteScalar()));
            added++;
        }

        transaction.Commit();
        return new AddVideosOutcome(added, duplicated, insertedIds);
    }

    public AddVideoOutcome AddVideo(string url, string bvid)
    {
        var outcome = AddVideosBulk(new[] { new NewVideo(url, bvid, null) });
        return outcome.Added > 0
            ? new AddVideoOutcome(AddVideoResult.Inserted, outcome.InsertedIds[0])
            : new AddVideoOutcome(AddVideoResult.Duplicate, 0);
    }

    /// <summary>启动崩溃恢复用：所有还排在队里的 Pending 视频。</summary>
    public IReadOnlyList<SubmissionCandidate> GetPendingVideos()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, url, title FROM videos WHERE status = $status ORDER BY id;";
        command.Parameters.AddWithValue("$status", VideoStatus.Pending.ToString());

        using var reader = command.ExecuteReader();
        var list = new List<SubmissionCandidate>();
        while (reader.Read())
        {
            list.Add(new SubmissionCandidate(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return list;
    }

    /// <summary>取一条可提交的记录；不存在或已不在 Pending 态（重复入队/已提交）返回 null。</summary>
    public SubmissionCandidate? GetSubmissionCandidate(long id)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url, title, status FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(2) != VideoStatus.Pending.ToString())
        {
            return null;
        }

        return new SubmissionCandidate(id, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>分站自有设置 KV（进站配置等），与引擎设置分开。</summary>
    public string? GetSetting(string key)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>删一条 KV 设定（迁移守卫重跑、开关复位用）。</summary>
    public void RemoveSetting(string key)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    /// <summary>站内弹幕入库（访客发的，B 站爬来的不走这里）：记下发在哪个分 P 的哪个时点、谁发的、发的时候是不是主人。</summary>
    /// <summary>站内弹幕入库（分站朋友发的，B 站爬来的不走这里）：记下发在哪个分 P 的哪个时点、谁发的、发的时候是不是主人、是不是某个席位发的（member_id 只为挂头像，没登录就是 null）。</summary>
    public long AddLocalDanmaku(long videoId, int pageIdx, double tSec, string text, string? senderDmid, bool senderOwner, long? memberId = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO danmaku_local (video_id, page_idx, t_sec, text, created_at_utc, sender_dmid, sender_owner, member_id)
            VALUES ($v, $p, $t, $x, $now, $s, $o, $m);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$v", videoId);
        command.Parameters.AddWithValue("$p", pageIdx);
        command.Parameters.AddWithValue("$t", tSec);
        command.Parameters.AddWithValue("$x", text);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$s", (object?)senderDmid ?? DBNull.Value);
        command.Parameters.AddWithValue("$o", senderOwner ? 1 : 0);
        command.Parameters.AddWithValue("$m", (object?)memberId ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>某视频全部站内弹幕（跨分 P，前端按 pageIdx 自筛）；发送者凭证由端点算 canDelete 不外泄，主人标记可外泄（就是给弹幕挂头像用的）。</summary>
    public IReadOnlyList<(long Id, int PageIdx, double TSec, string Text, string? SenderDmid, bool SenderOwner, long? MemberId, string? MemberFace)> ListLocalDanmaku(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        // 席位头像按 member_id 现取：members.face_url 一处真值，朋友改头像不会把旧弹幕冻成两张脸。
        // LEFT JOIN 而非 INNER：席位被删了弹幕照得见，只是没脸（画小圆兼底）。
        command.CommandText = """
            SELECT d.id, d.page_idx, d.t_sec, d.text, d.sender_dmid, COALESCE(d.sender_owner, 0), d.member_id, m.face_url
            FROM danmaku_local d LEFT JOIN members m ON m.id = d.member_id
            WHERE d.video_id = $v ORDER BY d.id;
            """;
        command.Parameters.AddWithValue("$v", videoId);

        using var reader = command.ExecuteReader();
        var list = new List<(long, int, double, string, string?, bool, long?, string?)>();
        while (reader.Read())
        {
            list.Add((
                reader.GetInt64(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return list;
    }

    /// <summary>单条站内弹幕的发送者凭证；行不存在返回 null（用空串区分「发了但无凭证」的老弹幕）。</summary>
    public string? GetLocalDanmakuSender(long id)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sender_dmid FROM danmaku_local WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
    }

    /// <summary>删一条站内弹幕；行不存在返回 false。</summary>
    public bool DeleteLocalDanmaku(long id)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM danmaku_local WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>清空分站（危险区一键清空）：台账全销（含预览行：预览文件已随目录一起清掉，留行只会指向空气），只留 settings KV（登录态在 cookies 盘，不受影响）。</summary>
    public int PurgeAllLedger()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM video_codecs;
            DELETE FROM artifacts;
            DELETE FROM previews;
            DELETE FROM pages;
            DELETE FROM video_meta;
            DELETE FROM danmaku_local;
            DELETE FROM videos;
            """;
        return command.ExecuteNonQuery();
    }

    /// <summary>提交成功：记下引擎 task id 列表（JSON）与解析到的标题/封面，转入下载中。</summary>
    public void MarkSubmitted(long id, IReadOnlyList<string> taskIds, string? title = null, string? coverUrl = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE videos
            SET status = $status,
                engine_task_ids = $taskIds,
                last_error = NULL,
                title = COALESCE($title, title),
                cover_url = COALESCE($cover, cover_url)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Downloading.ToString());
        command.Parameters.AddWithValue("$taskIds", System.Text.Json.JsonSerializer.Serialize(taskIds));
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$cover", (object?)coverUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>引擎侧全部 task 完成，馆藏落盘就绪。</summary>
    public void MarkArrived(long id)
    {
        ExecuteUpdate(id, VideoStatus.Arrived, null, null);
    }

    /// <summary>同步器用：所有还在下载中的记录及其引擎 task id 列表（JSON）。</summary>
    public IReadOnlyList<(long Id, string? TaskIdsJson)> GetDownloadingVideos()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, engine_task_ids FROM videos WHERE status = $status ORDER BY id;";
        command.Parameters.AddWithValue("$status", VideoStatus.Downloading.ToString());

        using var reader = command.ExecuteReader();
        var list = new List<(long, string?)>();
        while (reader.Read())
        {
            list.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return list;
    }

    /// <summary>引擎判定无需下载（判重/不支持），不重试。</summary>
    public void MarkSkipped(long id, string reason)
    {
        ExecuteUpdate(id, VideoStatus.Skipped, null, reason);
    }

    /// <summary>提交失败，可由站长在站长室重试。</summary>
    public void MarkFailed(long id, string error)
    {
        ExecuteUpdate(id, VideoStatus.Failed, null, error);
    }

    /// <summary>同步员停滞提示专用：只动 last_error 不动状态（下载中才用，到站/失败时另有人清零）。</summary>
    public void SetLastError(long id, string? error)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET last_error = $error WHERE id = $id;";
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>续下打不动时的硬重投通道：记录退回待提交并清引擎任务与错误，重投判重从此看不见旧行（行靠 Reviver 先删）。</summary>
    public void ResetToPending(long id)
    {
        ExecuteUpdate(id, VideoStatus.Pending, null, null);
    }

    /// <summary>导入缺件点名花名册：完成态视频（id, bvid），供磁盘实物核对。</summary>
    public IReadOnlyList<(long Id, string Bvid)> GetArrivedVideosForMediaScan()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, bvid FROM videos WHERE status = $status ORDER BY id;";
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());

        using var reader = command.ExecuteReader();
        var list = new List<(long, string)>();
        while (reader.Read())
        {
            list.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return list;
    }

    /// <summary>某视频的全部产物相对路径（相对 data/videos）；无行回空表。</summary>
    public IReadOnlyList<string> GetArtifactRelPaths(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rel_path FROM artifacts WHERE video_id = $id;";
        command.Parameters.AddWithValue("$id", videoId);

        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>下载面板的下载中行：除 id 外带卡片展示字段，TaskIdsJson 供暂停板拿引擎任务。</summary>
    public sealed record DownloadingRow(long Id, string Bvid, string? Title, string? CoverUrl, string? TaskIdsJson);

    public IReadOnlyList<DownloadingRow> GetDownloadingRows()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, bvid, title, cover_url, engine_task_ids FROM videos WHERE status = $status ORDER BY id;";
        command.Parameters.AddWithValue("$status", VideoStatus.Downloading.ToString());

        using var reader = command.ExecuteReader();
        var list = new List<DownloadingRow>();
        while (reader.Read())
        {
            list.Add(new DownloadingRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return list;
    }

    /// <summary>下载面板的排队行（提交前的台账行，还没碰引擎）。</summary>
    public IReadOnlyList<DownloadQueueItem> GetPendingQueueItems()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, bvid, title, cover_url FROM videos WHERE status = $status ORDER BY id;";
        command.Parameters.AddWithValue("$status", VideoStatus.Pending.ToString());

        using var reader = command.ExecuteReader();
        var list = new List<DownloadQueueItem>();
        while (reader.Read())
        {
            list.Add(new DownloadQueueItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return list;
    }

    /// <summary>取消排队行（Canceled）：只动还没交给引擎的 Pending 行，零引擎成本；不给 ids 就取消整个排队区。</summary>
    public int CancelPendingVideos(IReadOnlyList<long>? ids)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        if (ids is { Count: > 0 })
        {
            var names = new List<string>();
            for (var i = 0; i < ids.Count; i++)
            {
                var name = "$pid" + i.ToString();
                names.Add(name);
                command.Parameters.AddWithValue(name, ids[i]);
            }

            command.CommandText = "UPDATE videos SET status = $status WHERE status = $pending AND id IN (" + string.Join(",", names) + ");";
        }
        else
        {
            command.CommandText = "UPDATE videos SET status = $status WHERE status = $pending;";
        }

        command.Parameters.AddWithValue("$status", VideoStatus.Canceled.ToString());
        command.Parameters.AddWithValue("$pending", VideoStatus.Pending.ToString());
        return command.ExecuteNonQuery();
    }

    private void ExecuteUpdate(long id, VideoStatus status, string? taskIdsJson, string? error)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET status = $status, engine_task_ids = $taskIds, last_error = $error WHERE id = $id;";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$taskIds", (object?)taskIdsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>删除前取记录（含引擎任务列表）；不存在返回 null。</summary>
    public (string Bvid, VideoStatus Status, string? TaskIdsJson)? GetVideoForDelete(long id)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bvid, status, engine_task_ids FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (
            reader.GetString(0),
            Enum.Parse<VideoStatus>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>
    /// 彻底删站：账本四表连坐 + 本地文件（产物表记录 + 分 P 基准路径前缀文件）。
    /// 只删 videosRoot 内的文件（逐条防穿越校验）；返回清理的文件数。
    /// </summary>
    public int DeleteVideoCascade(long videoId, string videosRoot)
    {
        var relPaths = new List<string>();
        var basePaths = new List<string>();
        string? mainFolder = null;

        using (var connection = Open(_dbPath))
        {
            using (var load = connection.CreateCommand())
            {
                load.CommandText = "SELECT rel_path FROM artifacts WHERE video_id = $id;";
                load.Parameters.AddWithValue("$id", videoId);
                using var reader = load.ExecuteReader();
                while (reader.Read())
                {
                    relPaths.Add(reader.GetString(0));
                }
            }

            using (var loadPages = connection.CreateCommand())
            {
                loadPages.CommandText = "SELECT base_path FROM pages WHERE video_id = $id;";
                loadPages.Parameters.AddWithValue("$id", videoId);
                using var reader = loadPages.ExecuteReader();
                while (reader.Read())
                {
                    basePaths.Add(reader.GetString(0));
                }
            }

            using var transaction = connection.BeginTransaction();
            foreach (var table in new[] { "artifacts", "pages", "video_meta", "video_codecs", "videos" })
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = table == "videos" ? "DELETE FROM videos WHERE id = $id;" : $"DELETE FROM {table} WHERE video_id = $id;";
                delete.Parameters.AddWithValue("$id", videoId);
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var root = Path.GetFullPath(videosRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var deletedFiles = 0;

        foreach (var rel in relPaths)
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 防穿越
            }

            if (mainFolder is null)
            {
                mainFolder = Path.GetDirectoryName(full);
            }

            if (File.Exists(full))
            {
                try
                {
                    File.Delete(full);
                    deletedFiles++;
                }
                catch (IOException)
                {
                    // 文件被占用（引擎还在写），留给下次
                }
            }
        }

        // 产物表之外可能还有未归档的同源文件（如重复后缀 (1) 版本），按分 P 基准路径前缀补扫。
        var knownExtensions = new[] { ".mp4", ".mkv", ".flv", ".webm", ".ass", ".xml", ".srt", ".vtt", ".json", ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        foreach (var basePath in basePaths)
        {
            var fullBase = Path.GetFullPath(basePath);
            if (!fullBase.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(fullBase);
            var baseName = Path.GetFileName(fullBase);
            if (directory is null || !Directory.Exists(directory))
            {
                continue;
            }

            mainFolder ??= directory;
            foreach (var file in Directory.GetFiles(directory, baseName + "*"))
            {
                if (knownExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(file);
                        deletedFiles++;
                    }
                    catch (IOException)
                    {
                        // 同上
                    }
                }
            }
        }

        // 正片目录空了就一并清掉。
        if (mainFolder is not null && Directory.Exists(mainFolder) && Directory.GetFileSystemEntries(mainFolder).Length == 0)
        {
            try
            {
                Directory.Delete(mainFolder);
            }
            catch (IOException)
            {
                // 删不掉就算了
            }
        }

        return deletedFiles;
    }

    /// <summary>提交成功时把一次采集的档案信息落库（重提交覆盖旧档案）。</summary>
    public void SaveSubmissionMetadata(long videoId, VideoMetaDraft meta, IReadOnlyList<PageDraft> pages)
    {
        using var connection = Open(_dbPath);
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM video_meta WHERE video_id = $id; DELETE FROM pages WHERE video_id = $id;";
            delete.Parameters.AddWithValue("$id", videoId);
            delete.ExecuteNonQuery();
        }

        using (var insertMeta = connection.CreateCommand())
        {
            insertMeta.CommandText = """
                INSERT INTO video_meta (video_id, up_name, up_mid, zone, publish_text, description, view_count)
                VALUES ($id, $upName, $upMid, $zone, $publish, $desc, $views);
                """;
            insertMeta.Parameters.AddWithValue("$id", videoId);
            insertMeta.Parameters.AddWithValue("$upName", (object?)meta.UpName ?? DBNull.Value);
            insertMeta.Parameters.AddWithValue("$upMid", (object?)meta.UpMid ?? DBNull.Value);
            insertMeta.Parameters.AddWithValue("$zone", (object?)meta.Zone ?? DBNull.Value);
            insertMeta.Parameters.AddWithValue("$publish", (object?)meta.PublishText ?? DBNull.Value);
            insertMeta.Parameters.AddWithValue("$desc", (object?)meta.Description ?? DBNull.Value);
            insertMeta.Parameters.AddWithValue("$views", (object?)meta.ViewCountText ?? DBNull.Value);
            insertMeta.ExecuteNonQuery();
        }

        foreach (var page in pages)
        {
            using var insertPage = connection.CreateCommand();
            insertPage.CommandText = """
                INSERT INTO pages (video_id, task_id, idx, title, cid, duration_text, base_path)
                VALUES ($id, $taskId, $idx, $title, $cid, $duration, $basePath);
                """;
            insertPage.Parameters.AddWithValue("$id", videoId);
            insertPage.Parameters.AddWithValue("$taskId", page.TaskId);
            insertPage.Parameters.AddWithValue("$idx", page.Index);
            insertPage.Parameters.AddWithValue("$title", page.Title);
            insertPage.Parameters.AddWithValue("$cid", (object?)page.Cid ?? DBNull.Value);
            insertPage.Parameters.AddWithValue("$duration", (object?)page.DurationText ?? DBNull.Value);
            insertPage.Parameters.AddWithValue("$basePath", page.OutputBasePath);
            insertPage.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// 到站后按分 P 的输出基准路径扫盘归档产物（正片/弹幕/字幕/封面），重复扫描安全。
    /// 返回归档统计：归档文件数与缺正片的分 P 数。
    /// </summary>
    public (int Archived, int MissingVideo) FinalizeArtifacts(long videoId, string videosRoot)
    {
        using var connection = Open(_dbPath);

        var rows = new List<(long Id, string BasePath, bool HasLocal)>();
        using (var load = connection.CreateCommand())
        {
            load.CommandText = "SELECT id, base_path, local_video_path FROM pages WHERE video_id = $id;";
            load.Parameters.AddWithValue("$id", videoId);
            using var reader = load.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), !reader.IsDBNull(2)));
            }
        }

        var archived = 0;
        var missingVideo = 0;
        foreach (var (pageRowId, basePath, hasLocal) in rows)
        {
            var directory = Path.GetDirectoryName(basePath) ?? videosRoot;
            var baseName = Path.GetFileName(basePath);
            if (!Directory.Exists(directory))
            {
                missingVideo++;
                continue;
            }

            var candidates = Directory.GetFiles(directory, baseName + "*");
            var videoRel = default(string?);
            foreach (var file in candidates)
            {
                var suffix = file[(baseName.Length + directory.Length + 1)..];
                var kind = ClassifyArtifact(suffix);
                if (kind is null)
                {
                    continue;
                }

                var pageIdx = GetColumn(connection, "SELECT idx FROM pages WHERE id = $id;", pageRowId);
                var rel = ToRelative(file, videosRoot);

                if (kind == "video")
                {
                    videoRel ??= rel;
                    if (hasLocal)
                    {
                        continue; // 已归档过，不重复记
                    }

                    using var setLocal = connection.CreateCommand();
                    setLocal.CommandText = "UPDATE pages SET local_video_path = $rel WHERE id = $id;";
                    setLocal.Parameters.AddWithValue("$rel", rel);
                    setLocal.Parameters.AddWithValue("$id", pageRowId);
                    setLocal.ExecuteNonQuery();
                }
                else
                {
                    using var exists = connection.CreateCommand();
                    exists.CommandText = "SELECT COUNT(1) FROM artifacts WHERE video_id = $id AND rel_path = $rel;";
                    exists.Parameters.AddWithValue("$id", videoId);
                    exists.Parameters.AddWithValue("$rel", rel);
                    if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
                    {
                        continue;
                    }
                }

                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO artifacts (video_id, page_idx, kind, rel_path) VALUES ($id, $pageIdx, $kind, $rel);";
                insert.Parameters.AddWithValue("$id", videoId);
                insert.Parameters.AddWithValue("$pageIdx", (object?)pageIdx ?? DBNull.Value);
                insert.Parameters.AddWithValue("$kind", kind);
                insert.Parameters.AddWithValue("$rel", rel);
                insert.ExecuteNonQuery();
                archived++;
            }

            if (videoRel is null)
            {
                missingVideo++;
            }
        }

        return (archived, missingVideo);
    }

    private static string? ClassifyArtifact(string suffixAfterBase)
    {
        var lower = suffixAfterBase.ToLowerInvariant();
        // 封面带 .Cover 中缀，先判它免得被其它后缀误吃。
        if (lower.Contains(".cover") && (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".png") || lower.EndsWith(".webp") || lower.EndsWith(".gif")))
        {
            return "cover";
        }

        return lower switch
        {
            ".mp4" or ".mkv" or ".flv" or ".webm" => "video",
            ".ass" or ".xml" => "danmaku",
            ".srt" or ".vtt" or ".json" => "subtitle",
            _ => null,
        };
    }

    private static string ToRelative(string file, string videosRoot)
    {
        var full = Path.GetFullPath(file);
        var root = Path.GetFullPath(videosRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[(root.Length + 1)..]
            : Path.GetFileName(full);
        return rel.Replace('\\', '/');
    }

    private static long? GetColumn(SqliteConnection connection, string sql, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value);
    }

    // ─── 多编码版本（播放页点单加档） ────────────────────────────────────────

    /// <summary>分站认的编码版本名（小写），与前端/接口的取值一字不差。</summary>
    public static readonly IReadOnlyList<string> SupportedCodecs = new[] { "avc", "hevc", "av1" };

    /// <summary>
    /// 登记一个编码版本下载请求。Arrived/Downloading 重复请求返回 false（幂等），
    /// Failed 允许重跑（重置成 Downloading）。
    /// </summary>
    public bool AddCodecVersion(long videoId, int pageIdx, string codec)
    {
        var normalized = codec.Trim().ToLowerInvariant();
        if (!SupportedCodecs.Contains(normalized, StringComparer.Ordinal))
        {
            return false;
        }

        using var connection = Open(_dbPath);
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT status FROM video_codecs WHERE video_id = $id AND page_idx = $page AND codec = $codec;";
            check.Parameters.AddWithValue("$id", videoId);
            check.Parameters.AddWithValue("$page", pageIdx);
            check.Parameters.AddWithValue("$codec", normalized);
            var existing = check.ExecuteScalar() as string;
            if (existing is "Arrived" or "Downloading")
            {
                return false;
            }

            if (existing == "Failed")
            {
                using var retry = connection.CreateCommand();
                retry.CommandText = "UPDATE video_codecs SET status = 'Downloading', last_error = NULL, engine_task_ids = NULL WHERE video_id = $id AND page_idx = $page AND codec = $codec;";
                retry.Parameters.AddWithValue("$id", videoId);
                retry.Parameters.AddWithValue("$page", pageIdx);
                retry.Parameters.AddWithValue("$codec", normalized);
                retry.ExecuteNonQuery();
                return true;
            }
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO video_codecs (video_id, page_idx, codec, status, created_at_utc) VALUES ($id, $page, $codec, 'Downloading', $now);";
        insert.Parameters.AddWithValue("$id", videoId);
        insert.Parameters.AddWithValue("$page", pageIdx);
        insert.Parameters.AddWithValue("$codec", normalized);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return true;
    }

    /// <summary>同步器用：所有在途的编码版本及其引擎任务列表。</summary>
    public IReadOnlyList<(long Id, long VideoId, int PageIdx, string Codec, string? TaskIdsJson, string? BasePath)> GetDownloadingCodecVersions()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, video_id, page_idx, codec, engine_task_ids, base_path FROM video_codecs WHERE status = 'Downloading' ORDER BY id;";

        using var reader = command.ExecuteReader();
        var list = new List<(long, long, int, string, string?, string?)>();
        while (reader.Read())
        {
            list.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return list;
    }

    public void MarkCodecSubmitted(long id, IReadOnlyList<string> taskIds, string? basePath)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video_codecs SET engine_task_ids = $taskIds, base_path = $base WHERE id = $id;";
        command.Parameters.AddWithValue("$taskIds", System.Text.Json.JsonSerializer.Serialize(taskIds));
        command.Parameters.AddWithValue("$base", (object?)basePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void MarkCodecArrived(long id, string relPath)
    {
        UpdateCodecRow(id, "Arrived", relPath, null);
    }

    public void MarkCodecFailed(long id, string error)
    {
        UpdateCodecRow(id, "Failed", null, error);
    }

    private void UpdateCodecRow(long id, string status, string? relPath, string? error)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video_codecs SET status = $status, rel_path = COALESCE($rel, rel_path), last_error = $error WHERE id = $id;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$rel", (object?)relPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<(long Id, int PageIdx, string Codec, string Status, string? RelPath, string? LastError)> GetCodecVersions(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, page_idx, codec, status, rel_path, last_error FROM video_codecs WHERE video_id = $id ORDER BY page_idx, codec;";
        command.Parameters.AddWithValue("$id", videoId);

        using var reader = command.ExecuteReader();
        var list = new List<(long, int, string, string, string?, string?)>();
        while (reader.Read())
        {
            list.Add((
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return list;
    }

    /// <summary>访客下载用：查已到站的额外编码版本相对路径；没有则 null。</summary>
    public string? GetArrivedCodecRel(long videoId, int pageIdx, string codec)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rel_path FROM video_codecs
            WHERE video_id = $id AND page_idx = $page AND codec = $codec AND status = 'Arrived';
            """;
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$page", pageIdx);
        command.Parameters.AddWithValue("$codec", codec);
        return command.ExecuteScalar() as string;
    }

    /// <summary>访客下载用：主版本（首下那个档）的相对路径；没有则 null。</summary>
    public string? GetPageMainRel(long videoId, int pageIdx)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_video_path FROM pages WHERE video_id = $id AND idx = $page;";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$page", pageIdx);
        return command.ExecuteScalar() as string;
    }

    /// <summary>访客下载的文件名素材：bvid + 标题。</summary>
    public (string Bvid, string? Title)? GetVideoFileNameInfo(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bvid, title FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>主版本的实测编码（'avc'/'hevc'/'av1'，ffprobe 回填写入）；未测过给 null。</summary>
    public string? GetPageMainCodec(long videoId, int pageIdx)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT codec FROM pages WHERE video_id = $id AND idx = $page;";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$page", pageIdx);
        return command.ExecuteScalar() as string;
    }

    /// <summary>删单个编码版本：销账并交回 rel_path，文件删除归调用方；行不存在返回 null。</summary>
    public string? TakeCodecVersionForDelete(long videoId, int pageIdx, string codec)
    {
        using var connection = Open(_dbPath);
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT rel_path FROM video_codecs WHERE video_id = $id AND page_idx = $page AND codec = $codec;";
        select.Parameters.AddWithValue("$id", videoId);
        select.Parameters.AddWithValue("$page", pageIdx);
        select.Parameters.AddWithValue("$codec", codec.Trim().ToLowerInvariant());
        var rel = select.ExecuteScalar() as string;

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM video_codecs WHERE video_id = $id AND page_idx = $page AND codec = $codec;";
        delete.Parameters.AddWithValue("$id", videoId);
        delete.Parameters.AddWithValue("$page", pageIdx);
        delete.Parameters.AddWithValue("$codec", codec.Trim().ToLowerInvariant());
        var gone = delete.ExecuteNonQuery();
        return gone > 0 ? rel : null;
    }

    /// <summary>版本号列表（归档扫描/版本提交用）。</summary>
    public IReadOnlyList<int> GetPageIndexes(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx FROM pages WHERE video_id = $id ORDER BY idx;";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var list = new List<int>();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }

    /// <summary>
    /// 编码版本下完后的扫盘定档：按提交时登记的基准路径找视频文件，找到即 Arrived；
    /// 没找到返回 false（引擎已完成却没文件，归失败）。
    /// </summary>
    public bool FinalizeCodecVersion(long id, string basePath, string videosRoot)
    {
        var directory = Path.GetDirectoryName(basePath);
        var baseName = Path.GetFileName(basePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return false;
        }

        var videoExtensions = new[] { ".mp4", ".mkv", ".flv", ".webm" };
        var hit = Directory.GetFiles(directory, baseName + "*")
            .FirstOrDefault(f => videoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        if (hit is null)
        {
            return false;
        }

        MarkCodecArrived(id, ToRelative(hit, videosRoot));
        return true;
    }

    /// <summary>版本文件的磁盘体积；文件缺失（被手删）给 null，前端显示破折号。</summary>
    private long? SizeOfVideoFile(string? rel)
    {
        if (rel is null)
        {
            return null;
        }

        try
        {
            var file = Path.Combine(_paths.Videos, rel);
            return File.Exists(file) ? new FileInfo(file).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>UP 主名片登记：face 存本地化路径（或热链），fans 可空；失败重试时覆盖式更新。</summary>
    public void SetUpInfo(long videoId, string? face, string? sign, long? fans)
    {
        using var connection = Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE video_meta SET up_face = $face, up_sign = $sign, up_fans = $fans WHERE video_id = $id;";
        cmd.Parameters.AddWithValue("$face", (object?)face ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sign", (object?)sign ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fans", (object?)fans ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", videoId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>名片采集前的台账检查：有 mid 且把 up_face 原样带回，坏没坏由调用方（LbsUpInfoFetcher.NeedsRefetch）验货 —— 读层不猜盘上有没有文件。</summary>
    public (long? Mid, string? Face) GetUpCardState(long videoId)
    {
        using var connection = Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT up_mid, up_face FROM video_meta WHERE video_id = $id;";
        cmd.Parameters.AddWithValue("$id", videoId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null);
        }

        var mid = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
        return (mid, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>互动数据登记：五维真数 + 销账标记；失败重试时覆盖式更新。</summary>
    public void SetVideoStat(long videoId, long? like, long? coin, long? fav, long? share, long? reply)
    {
        using var connection = Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE video_meta SET stat_like = $like, stat_coin = $coin, stat_fav = $fav,
                stat_share = $share, stat_reply = $reply, stat_probed = 1
            WHERE video_id = $id;
            """;
        cmd.Parameters.AddWithValue("$like", (object?)like ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$coin", (object?)coin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fav", (object?)fav ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$share", (object?)share ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reply", (object?)reply ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", videoId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>互动数据销账（采不到也标已试，不每次开页都扰 B 站）。</summary>
    public void MarkStatProbed(long videoId)
    {
        using var connection = Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE video_meta SET stat_probed = 1 WHERE video_id = $id;";
        cmd.Parameters.AddWithValue("$id", videoId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>互动数据补采前的台账检查：未销账且未采到过才值得出去跑一趟。</summary>
    public bool ShouldBackfillStat(long videoId)
    {
        using var connection = Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT stat_probed, stat_like FROM video_meta WHERE video_id = $id;";
        cmd.Parameters.AddWithValue("$id", videoId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var probed = !reader.IsDBNull(0) && reader.GetInt64(0) == 1;
        var hasStat = !reader.IsDBNull(1);
        return !probed && !hasStat;
    }

    /// <summary>
    /// 接下来播放：同 UP 主优先、新入站补齐，排除自身；列形状与列表一致，前端卡片直接复用。
    /// </summary>
    public List<VideoEntry> GetRelatedVideos(long videoId, int take = 8)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id, v.url, v.bvid, v.avid, v.title, v.status, v.created_at_utc, v.cover_url, v.last_error,
                   m.up_name, m.view_count, p.duration_text, v.published_at_utc, m.publish_text,
                   (SELECT COUNT(1) FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'danmaku'),
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   (SELECT p.rel_path FROM previews p WHERE p.video_id = v.id AND p.status = 'Arrived' LIMIT 1)
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            LEFT JOIN pages p ON p.video_id = v.id
                 AND p.idx = (SELECT MIN(idx) FROM pages WHERE video_id = v.id)
            WHERE v.status = $status AND v.id <> $id
            ORDER BY (CASE WHEN $mid IS NOT NULL AND m.up_mid = $mid THEN 0
                           WHEN $up IS NOT NULL AND m.up_name = $up THEN 0
                           ELSE 1 END),
                     v.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        command.Parameters.AddWithValue("$id", videoId);

        // 自身坐标（同 UP 判定用）：一次自查，查不到就按全新入站序排。
        long? mid = null;
        string? up = null;
        using (var self = connection.CreateCommand())
        {
            self.CommandText = "SELECT m.up_mid, m.up_name FROM video_meta m WHERE m.video_id = $id;";
            self.Parameters.AddWithValue("$id", videoId);
            using var selfReader = self.ExecuteReader();
            if (selfReader.Read())
            {
                mid = selfReader.IsDBNull(0) ? null : selfReader.GetInt64(0);
                up = selfReader.IsDBNull(1) ? null : selfReader.GetString(1);
            }
        }

        command.Parameters.AddWithValue("$mid", (object?)mid ?? DBNull.Value);
        command.Parameters.AddWithValue("$up", (object?)up ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", take);

        using var reader = command.ExecuteReader();
        return ReadEntries(reader);
    }

    /// <summary>评论快照登记：同一条视频只保留最新一份 comments 记录（文件覆写由调用方负责）。</summary>
    public void RegisterCommentsArtifact(long videoId, string relPath)
    {
        using var connection = Open(_dbPath);
        using (var purge = connection.CreateCommand())
        {
            purge.CommandText = "DELETE FROM artifacts WHERE video_id = $id AND kind = 'comments';";
            purge.Parameters.AddWithValue("$id", videoId);
            purge.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO artifacts (video_id, page_idx, kind, rel_path) VALUES ($id, NULL, 'comments', $rel);";
        insert.Parameters.AddWithValue("$id", videoId);
        insert.Parameters.AddWithValue("$rel", relPath);
        insert.ExecuteNonQuery();
    }

    /// <summary>查某类产物的相对路径（评论快照判断是否已有）。</summary>
    public string? GetArtifactRel(long videoId, string kind)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rel_path FROM artifacts WHERE video_id = $id AND kind = $kind ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$kind", kind);
        return command.ExecuteScalar() as string;
    }

    /// <summary>封面补员候选：到站、有 cover_url、却没 kind='cover' 产物行的视频，老货优先限量取。
    /// BaseRel 带同视频任一产物行的 rel（正片行优先）供推导封面目录与基名；完全无产物行的没处落盘，不入列。</summary>
    public sealed record CoverBackfillCandidate(long VideoId, string Bvid, string CoverUrl, string BaseRel);

    public IReadOnlyList<CoverBackfillCandidate> ListVideosMissingCover(int limit = 40)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id, v.bvid, v.cover_url,
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id
                    ORDER BY (a.kind = 'video') DESC, a.id LIMIT 1)
            FROM videos v
            WHERE v.status = $status
              AND v.cover_url IS NOT NULL AND v.cover_url <> ''
              AND NOT EXISTS (SELECT 1 FROM artifacts c WHERE c.video_id = v.id AND c.kind = 'cover')
              AND EXISTS (SELECT 1 FROM artifacts a WHERE a.video_id = v.id)
            ORDER BY v.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var list = new List<CoverBackfillCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CoverBackfillCandidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return list;
    }

    /// <summary>登记补下的封面（先文件后行：缺件回炉 MissingMediaReconciler 拿 artifacts 行验盘，先插行会把「有行无件」误判回炉）。同名行不重。</summary>
    public void RegisterCoverArtifact(long videoId, string relPath)
    {
        using var connection = Open(_dbPath);
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(1) FROM artifacts WHERE video_id = $id AND rel_path = $rel;";
        exists.Parameters.AddWithValue("$id", videoId);
        exists.Parameters.AddWithValue("$rel", relPath);
        if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
        {
            return;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO artifacts (video_id, page_idx, kind, rel_path) VALUES ($id, NULL, 'cover', $rel);";
        insert.Parameters.AddWithValue("$id", videoId);
        insert.Parameters.AddWithValue("$rel", relPath);
        insert.ExecuteNonQuery();
    }

    /// <summary>第一个分 P 的输出基准路径，评论快照文件就落在它旁边。</summary>
    public string? GetFirstPageBasePath(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT base_path FROM pages WHERE video_id = $id ORDER BY idx LIMIT 1;";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 重拉弹幕要用的一趟分 P：序号、cid、成片基准路径（绝对路径，就在 data/videos 下）。
    /// cid 为 NULL 的分 P 拉不了（B 站拿 oid 取弹幕），调用方跳过它并回报。
    /// </summary>
    public IReadOnlyList<(int Idx, long? Cid, string BasePath)> GetDanmakuPageTargets(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx, cid, base_path FROM pages WHERE video_id = $id ORDER BY idx;";
        command.Parameters.AddWithValue("$id", videoId);
        var rows = new List<(int Idx, long? Cid, string BasePath)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(2))
            {
                continue;
            }

            rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>评论采集需要的台账摘要：bvid、状态与 aid（采到后回填）。</summary>
    public (string Bvid, VideoStatus Status, long? Aid)? GetVideoBrief(long id)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bvid, status, avid FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), Enum.Parse<VideoStatus>(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    /// <summary>评论采集时顺手拿到的 aid 回填台账，后续重采免二次解析。</summary>
    public void SaveAid(long videoId, long aid)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET avid = $aid WHERE id = $id;";
        command.Parameters.AddWithValue("$aid", aid);
        command.Parameters.AddWithValue("$id", videoId);
        command.ExecuteNonQuery();
    }

    /// <summary>观看页数据：档案 + 分 P + 本地文件。查不到 bvid 返回 null。</summary>
    public WatchData? GetWatchData(string bvid)
    {
        using var connection = Open(_dbPath);

        long id;
        string url, primaryBvid;
        string? title, coverUrl, createdAtText;
        long? aid;
        VideoStatus status;
        using (var video = connection.CreateCommand())
        {
            video.CommandText = "SELECT id, url, bvid, title, status, cover_url, created_at_utc, avid FROM videos WHERE bvid = $bvid;";
            video.Parameters.AddWithValue("$bvid", bvid);
            using var reader = video.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            id = reader.GetInt64(0);
            url = reader.GetString(1);
            primaryBvid = reader.GetString(2);
            title = reader.IsDBNull(3) ? null : reader.GetString(3);
            status = Enum.Parse<VideoStatus>(reader.GetString(4));
            coverUrl = reader.IsDBNull(5) ? null : reader.GetString(5);
            createdAtText = reader.GetString(6);
            aid = reader.IsDBNull(7) ? null : reader.GetInt64(7);
        }

        string? upName = null, zone = null, publishText = null, description = null, viewCount = null;
        long? upMid = null;
        string? upFace = null;
        string? upSign = null;
        long? upFans = null;
        long? statLike = null;
        long? statCoin = null;
        long? statFav = null;
        long? statShare = null;
        long? statReply = null;
        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "SELECT up_name, zone, publish_text, description, view_count, up_mid, up_face, up_sign, up_fans, stat_like, stat_coin, stat_fav, stat_share, stat_reply FROM video_meta WHERE video_id = $id;";
            meta.Parameters.AddWithValue("$id", id);
            using var reader = meta.ExecuteReader();
            if (reader.Read())
            {
                upName = reader.IsDBNull(0) ? null : reader.GetString(0);
                zone = reader.IsDBNull(1) ? null : reader.GetString(1);
                publishText = reader.IsDBNull(2) ? null : reader.GetString(2);
                description = reader.IsDBNull(3) ? null : reader.GetString(3);
                viewCount = reader.IsDBNull(4) ? null : reader.GetString(4);
                upMid = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                upFace = reader.IsDBNull(6) ? null : reader.GetString(6);
                upSign = reader.IsDBNull(7) ? null : reader.GetString(7);
                upFans = reader.IsDBNull(8) ? null : reader.GetInt64(8);
                statLike = reader.IsDBNull(9) ? null : reader.GetInt64(9);
                statCoin = reader.IsDBNull(10) ? null : reader.GetInt64(10);
                statFav = reader.IsDBNull(11) ? null : reader.GetInt64(11);
                statShare = reader.IsDBNull(12) ? null : reader.GetInt64(12);
                statReply = reader.IsDBNull(13) ? null : reader.GetInt64(13);
            }
        }

        var pages = new List<(int Index, string Title, long? Cid, string? Duration, string? VideoRel, string? Codec)>();
        using (var pageCmd = connection.CreateCommand())
        {
            pageCmd.CommandText = "SELECT idx, title, cid, duration_text, local_video_path, codec FROM pages WHERE video_id = $id ORDER BY idx;";
            pageCmd.Parameters.AddWithValue("$id", id);
            using var reader = pageCmd.ExecuteReader();
            while (reader.Read())
            {
                var idx = reader.GetInt32(0);
                var videoRel = reader.IsDBNull(4) ? null : reader.GetString(4);
                var codec = reader.IsDBNull(5) ? null : reader.GetString(5);

                // 懒测：老档案或入站时未探到的，首次打开时 ffprobe 实测一次并回填，此后永不再探。
                if (codec is null && videoRel is not null)
                {
                    codec = Ffprobe.ProbeVideoCodec(Path.Combine(_paths.Videos, videoRel));
                    if (codec is not null)
                    {
                        using var setCodec = connection.CreateCommand();
                        setCodec.CommandText = "UPDATE pages SET codec = $codec WHERE video_id = $id AND idx = $idx;";
                        setCodec.Parameters.AddWithValue("$codec", codec);
                        setCodec.Parameters.AddWithValue("$id", id);
                        setCodec.Parameters.AddWithValue("$idx", idx);
                        setCodec.ExecuteNonQuery();
                    }
                }

                pages.Add((
                    idx,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    videoRel,
                    codec));
            }
        }

        var extras = new List<(int? PageIdx, string Kind, string Rel)>();
        using (var artifactCmd = connection.CreateCommand())
        {
            artifactCmd.CommandText = "SELECT page_idx, kind, rel_path FROM artifacts WHERE video_id = $id;";
            artifactCmd.Parameters.AddWithValue("$id", id);
            using var reader = artifactCmd.ExecuteReader();
            while (reader.Read())
            {
                extras.Add((
                    reader.IsDBNull(0) ? null : reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }

        var codecRows = new List<(int PageIdx, string Codec, string Status, string? Rel, string? Err)>();
        using (var codecCmd = connection.CreateCommand())
        {
            codecCmd.CommandText = "SELECT page_idx, codec, status, rel_path, last_error FROM video_codecs WHERE video_id = $id;";
            codecCmd.Parameters.AddWithValue("$id", id);
            using var reader = codecCmd.ExecuteReader();
            while (reader.Read())
            {
                codecRows.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        // 本地封面优先于 B 站直链。
        var localCover = extras.FirstOrDefault(e => e.Kind == "cover").Rel;
        var coverFinal = localCover is null ? coverUrl : "/media/" + localCover;

        var pageFiles = pages.Select(p => new PageWithFiles(
            p.Index,
            p.Title,
            p.Cid,
            p.Duration,
            p.VideoRel is null ? null : "/media/" + p.VideoRel,
            DanmakuRel(extras, p.Index) is { } dm ? "/media/" + dm : null,
            extras.Where(e => e.Kind == "subtitle" && e.PageIdx == p.Index).Select(e => "/media/" + e.Rel).ToList(),
            codecRows.Where(c => c.PageIdx == p.Index).Select(c => new CodecVersion(
                c.Codec,
                c.Status,
                c.Rel is { } rel ? "/media/" + rel : null,
                SizeOfVideoFile(c.Rel))).ToList(),
            p.Codec,
            SizeOfVideoFile(p.VideoRel)
        )).ToList();

        return new WatchData(
            id, primaryBvid, url, title, status, coverFinal,
            upName, zone, publishText, description, viewCount,
            DateTimeOffset.Parse(createdAtText), pageFiles,
            aid,
            extras.FirstOrDefault(e => e.Kind == "comments").Rel is { } cm ? "/media/" + cm : null,
            upMid,
            upFace,
            upSign,
            upFans,
            CountDanmakuLines(id),
            statLike,
            statCoin,
            statFav,
            statShare,
            statReply);
    }

    /// <summary>
    /// 一个分 P 的弹幕指针：引擎常常 .ass/.xml 两行台账各写一份，而前端只会解 ass。
    /// FirstOrDefault 撞上 xml 那行，重拉完看着就像「没生效」，所以按扩展名优取 ass。
    /// </summary>
    private static string? DanmakuRel(IReadOnlyList<(int? PageIdx, string Kind, string Rel)> extras, int pageIdx) =>
        extras
            .Where(e => e.Kind == "danmaku" && e.PageIdx == pageIdx)
            .OrderByDescending(e => e.Rel.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Rel)
            .FirstOrDefault();
}

/// <summary>站长室页头总览快照：库存分布 + 磁盘体积 + 全站互动真数。</summary>
public sealed record AdminStats(
    int Total,
    int Arrived,
    int Downloading,
    int Pending,
    int Failed,
    /// <summary>成员号池里的活号数（采与加档全靠它们；零支 = 采集已停摆）。</summary>
    int LiveAccounts,
    long TotalBytes,
    long ViewTotal,
    long LikeTotal,
    long CoinTotal,
    long FavTotal,
    long ShareTotal,
    long ReplyTotal,
    int UpCount);
