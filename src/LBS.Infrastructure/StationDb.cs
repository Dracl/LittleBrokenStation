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
                last_error     TEXT,
                visibility     TEXT NOT NULL DEFAULT 'public'
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
                -- 加档前的「预估」规格（B 站 playurl 标称值，不是实测）：按（稿件, 分P, 编码）存一行。
                -- 与 video_codecs 分开两张：那张是「点单与到货的台账」，这张只是探子抄回来的行情，
                -- 随时可清可覆盖，不该跟下载台账绑在一起。
                CREATE TABLE IF NOT EXISTS video_codec_estimates (
                    video_id       INTEGER NOT NULL,
                    page_idx       INTEGER NOT NULL,
                    codec          TEXT NOT NULL,
                    video_kbps     INTEGER,
                    audio_kbps     INTEGER,
                    est_bytes      INTEGER,
                    qn             INTEGER,
                    width          INTEGER,
                    height         INTEGER,
                    fetched_at_utc TEXT NOT NULL,
                    PRIMARY KEY (video_id, page_idx, codec)
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

        // 封面缩略档位台账（站长 11-03「入站时一次切完、存量也全量回填」）：与 previews 同理不进 artifacts。
        // rel_path 同时是私有稿件反查的比对列（MediaRelTables 第四条腿），所以这一列不许省、不许改名。
        using (var coverThumb = connection.CreateCommand())
        {
            coverThumb.CommandText = """
                CREATE TABLE IF NOT EXISTS cover_thumbs (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id       INTEGER NOT NULL,
                    rel_path       TEXT NOT NULL DEFAULT '',
                    tier           TEXT NOT NULL,
                    fmt            TEXT NOT NULL DEFAULT '',
                    src_stamp      TEXT NOT NULL DEFAULT '',
                    status         TEXT NOT NULL DEFAULT 'failed',
                    last_error     TEXT,
                    attempts       INTEGER NOT NULL DEFAULT 0,
                    updated_at_utc TEXT NOT NULL,
                    UNIQUE (video_id, tier)
                );
                CREATE INDEX IF NOT EXISTS idx_cover_thumbs_rel ON cover_thumbs(rel_path);
                """;
            coverThumb.ExecuteNonQuery();
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

        // 观看历史：一人一片每 P 一行（同 P 再报才覆盖），只存「看到哪了」这四个数。
        // 2026-11-06 站长拿多 P 片子试出旧口径的坑：一人一片一行时 P2 的位置盖掉 P1，而进放映厅恒落第一 P，
        // 行上的 page_idx 对不上就从头播——多 P 续播几乎不可用。改成一 P 一坑，各记各位。
        // 看完的也留行（2026-10-29 站长改口：历史记所有不只没看完的，过线由端点盖戳留档），行也不存片名与封面：
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
                    PRIMARY KEY (member_id, video_id, page_idx)
                );
                CREATE INDEX IF NOT EXISTS idx_watchhistory_member ON watch_history (member_id, updated_at_utc);
                """;
            history.ExecuteNonQuery();
        }

        MigrateWatchHistoryPerPage(connection);

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
                    created_at_utc  TEXT NOT NULL,
                    visibility      TEXT NOT NULL DEFAULT 'public'
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

        // UP 历史脸档案：一位 UP 在本站存过的历张脸（周巡脸员每周去 card 口问一次，换脸就添一行）。
        // 为什么单开一张表而不塞进 video_meta：那张是「一条稿件一行」的档案快照，up_face 只留最新那张脸，
        // 而且它的行随稿件走 —— 销档 / 进回收站时档案跟着被扰走，「这位三年前长什么样」就没了；
        // 同一个人换过的几张脸更没地方摆（一列一个值，塞 JSON 数组就查不了也比不了新旧）。
        // 脸件本身只落 data/up_faces（与 thumbs / previews 同一族派生物区），这里只记「谁、哪张脸、何时首见/再见」。
        using (var upFace = connection.CreateCommand())
        {
            upFace.CommandText = """
                CREATE TABLE IF NOT EXISTS up_faces (
                    mid        INTEGER NOT NULL,
                    face_rel   TEXT    NOT NULL,
                    first_seen TEXT    NOT NULL,
                    last_seen  TEXT    NOT NULL,
                    UNIQUE (mid, face_rel)
                );
                CREATE INDEX IF NOT EXISTS idx_up_faces_mid ON up_faces (mid, last_seen DESC);
                """;
            upFace.ExecuteNonQuery();
        }

        // 逐件生成耗时（秒，ffmpeg 从起跑到交件的墙钟时间）：站长室「单件耗时」只吃这一列的真数，
        // 绝拿 seconds（产物时长）冒充。早先建的库没这列，缺就补。
        EnsureColumn(connection, "previews", "cost_seconds", "REAL");

        // 稿件与合集的可见性（'public'/'private'）：私有只有投稿人（合集东家）本人与站长看得着。
        // 存量行靠 DEFAULT 天然落 public，不回填、不立碑（站长拍板）；回收站那些 SELECT * 快照对新列免改
        // （恢复时拿 PRAGMA table_info 筛列名，新列跟着账走）。读侧谓词只从 VisibilityFilter 那一个出口出。
        EnsureColumn(connection, "videos", "visibility", "TEXT NOT NULL DEFAULT 'public'");
        EnsureColumn(connection, "collections", "visibility", "TEXT NOT NULL DEFAULT 'public'");

        // 站内分区的站长改派槽：有效分区 = site_zone 优先，空则继承 B 站归档的 video_meta.zone（形如「音乐>翻唱」）。
        // 分区页只认顶级段；未入站无档案又没改派的货没户口，不进分区页。
        EnsureColumn(connection, "videos", "site_zone", "TEXT");
        // 稿件设置那三个本地覆写槽，形状照上面 site_zone 一族：NULL 与空串都算「未覆写」（有效值表达式一律 NULLIF 归一），
        // 覆写 = 写非空串、还原 = 置 NULL；写口只有 StationDb.VideoSettings.cs 里 SetVideo*Override 那一族，不留第二张写嘴。
        // site_title / site_desc：站内自己那份标题与简介（顶掉 B 站归档值；简介只在放映厅念，列表本来就不显它）。
        EnsureColumn(connection, "videos", "site_title", "TEXT");
        EnsureColumn(connection, "videos", "site_desc", "TEXT");
        // 历史遗留：早先「封面来源三档」那件功能已在今天全站拆除（写口、读口 CASE、字段、测试全拆干净），
        // 留下一枚 site_cover_pref 空列：站内没人读、也没人写，存量库里那 155 条实测全是 NULL。
        // 新库不再建它（本行已删）；存量库里那一枚就当死件放着，等下次整库整理时一并收（为一个空列写 DROP COLUMN
        // 的一次性迁移，比留着它更难看）。

        // 旧库升级：M0 建的表没有引擎字段，缺列就补（SQLite ADD COLUMN 不锁表，小库无所谓）。
        EnsureColumn(connection, "videos", "engine_task_ids", "TEXT");
        EnsureColumn(connection, "videos", "last_error", "TEXT");
        EnsureColumn(connection, "videos", "cover_url", "TEXT");
        // B 站投稿时间（ISO）：导入源（UP 投稿列表 created / 收藏夹 pubtime）入库时就存，首页卡阵展示用。
        EnsureColumn(connection, "videos", "published_at_utc", "TEXT");
        // 投稿人（B 站 UID，与 events.actor_mid 同口径，NULL=站长或史前）：下载提交时据此走投稿人的账号池。
        EnsureColumn(connection, "videos", "submitter_mid", "INTEGER");
        // Skipped 出口旗：引擎历史台账里有同名行时，重投会在门口被打回（判重键 = (cid, 需要下载的内容)）。
        // 站长点「销档重下」时置 1，提交链读到它就带 RedownloadCompleted 走正门，提交完（无论成败）自动清零。
        EnsureColumn(connection, "videos", "force_redownload", "INTEGER NOT NULL DEFAULT 0");
        // 主版本编码实测缓存（'avc'/'hevc'/'av1'）：首次看片时 ffprobe 实测回填，之后不再探。
        EnsureColumn(connection, "pages", "codec", "TEXT");
        // 分轨码率实测缓存（kbps，给播放页版本行显示用；前端不现探，数据得后端备齐）+ 防抖戳：
        // stream_probed=1 表示「ffprobe 探过一次」，探失败也置位（与 video_meta.stat_probed 同一套路）
        // ——省得每次开片都起一个 ffprobe 进程。
        EnsureColumn(connection, "pages", "video_kbps", "INTEGER");
        EnsureColumn(connection, "pages", "audio_kbps", "INTEGER");
        EnsureColumn(connection, "pages", "stream_probed", "INTEGER");
        EnsureColumn(connection, "video_codecs", "video_kbps", "INTEGER");
        EnsureColumn(connection, "video_codecs", "audio_kbps", "INTEGER");
        EnsureColumn(connection, "video_codecs", "stream_probed", "INTEGER");
        // 画面形态实测（ffprobe 同趟顺带）：宽高/平均帧率给清晰度菜单判 1080P 60帧 这类档，
        // audio_codec 只收白名单（'flac'/'aac'/…）给 Hi-Res 徽记当判据。
        // shape_probed=1 是「升级后探过形态」的另枚防抖戳：旧行 stream_probed 已盖但新列全空，
        // 补员巡视靠它把存量重探一趟；没 ffprobe 的环境也能区分「试过没探到」与「从没试」。
        EnsureColumn(connection, "pages", "width", "INTEGER");
        EnsureColumn(connection, "pages", "height", "INTEGER");
        EnsureColumn(connection, "pages", "fps", "REAL");
        EnsureColumn(connection, "pages", "audio_codec", "TEXT");
        EnsureColumn(connection, "pages", "shape_probed", "INTEGER");
        EnsureColumn(connection, "video_codecs", "width", "INTEGER");
        EnsureColumn(connection, "video_codecs", "height", "INTEGER");
        EnsureColumn(connection, "video_codecs", "fps", "REAL");
        EnsureColumn(connection, "video_codecs", "audio_codec", "TEXT");
        EnsureColumn(connection, "video_codecs", "shape_probed", "INTEGER");
        // 引擎实发规格真值（到站/定档那一刻从在途任务 Metadata 拄下来）：Resolution 是替换完
        // 可用性降级后的真实档位（id 116=1080P 60帧），AudioCodec 是实发音频档（id 30251/31251=Hi-Res 系）。
        // 这是比 ffprobe 实测更硬的真相（B 站页面菜单同源），有它就直接照报，没它才退回实测链。
        EnsureColumn(connection, "pages", "quality_id", "INTEGER");
        EnsureColumn(connection, "pages", "quality_name", "TEXT");
        EnsureColumn(connection, "pages", "audio_quality_id", "INTEGER");
        EnsureColumn(connection, "video_codecs", "quality_id", "INTEGER");
        EnsureColumn(connection, "video_codecs", "quality_name", "TEXT");
        EnsureColumn(connection, "video_codecs", "audio_quality_id", "INTEGER");
        // 盘上实测编码（'avc'/'hevc'/'av1'）：与 codec（点单要的编码）分家，专治挂羊头卖狗肉——
        // B 站只给了别的编码、或引擎按可用性降级时，codec 仍是点单值（GetArrivedCodecRel 靠它找文件、不能改），
        // 真实编码只进这一列，与点单不符时同步员留一条 Warning。只在定档探测那一步顺带写，不额外起 ffprobe。
        EnsureColumn(connection, "video_codecs", "measured_codec", "TEXT");
        // 加档撞车让路：引擎判重键不含编码（DuplicateKey.From(cid, NeedDownloadContent)），
        // 同一稿件并发加两个编码时后到的必被判「已存在未完成的同名任务」。这类行不判死：
        // 挂 retry_at_utc（UTC ISO，与 created_at_utc 同格式可比大小）等同步员到期回炉，
        // retry_count 记已经让过几趟（退避长度与「放弃重试」都只看它）。
        EnsureColumn(connection, "video_codecs", "retry_at_utc", "TEXT");
        EnsureColumn(connection, "video_codecs", "retry_count", "INTEGER NOT NULL DEFAULT 0");
        // UP 主名片快照（头像本地化相对路径 / 简介 / 粉丝数），由 LbsUpInfoFetcher 到站时采。
        EnsureColumn(connection, "video_meta", "up_face", "TEXT");
        EnsureColumn(connection, "video_meta", "up_sign", "TEXT");
        EnsureColumn(connection, "video_meta", "up_fans", "INTEGER");
        // UP 档案的认证栏（UP 空间页头 B 站化）：B 站官方认证标题（如「bilibili直播高能主播」），同由 LbsUpInfoFetcher 采。
        EnsureColumn(connection, "video_meta", "up_official", "TEXT");
        // B 站互动数据快照（view 接口 stat 字段真采）：stat_probed=1 表示已尝试过，失败也不重复扰 B 站。
        EnsureColumn(connection, "video_meta", "stat_like", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_coin", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_fav", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_share", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_reply", "INTEGER");
        EnsureColumn(connection, "video_meta", "stat_probed", "INTEGER");
        // 创作团队（联合创作稿的 B 站 staff 名单）：camelCase JSON 数组，读写共用 WatchStaffCodec。
        // 不另开 staff_probed 销账位：团队与互动数据是同一次 view 拿的，要不要再出去由 stat_probed 定。
        EnsureColumn(connection, "video_meta", "staff_json", "TEXT");
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

        // 会话单边销账：我删我的记录，对方账本不动（微信式删聊天）；重新开口说话自动复活。
        using (var dmHidden = connection.CreateCommand())
        {
            dmHidden.CommandText = """
                CREATE TABLE IF NOT EXISTS dm_thread_hidden (
                    member_id      INTEGER NOT NULL,
                    peer_member_id INTEGER NOT NULL,
                    hidden_at_utc  TEXT NOT NULL,
                    PRIMARY KEY (member_id, peer_member_id)
                );
                """;
            dmHidden.ExecuteNonQuery();
        }

        // 聊天记录单边清空（双键墓碑）：盖自己的黑章，对话视图里盖章前的老记录不露面；
        // 双方都盖了章，往来消息才真删（谁也看不见的账，留着没意义）。
        using (var dmCleared = connection.CreateCommand())
        {
            dmCleared.CommandText = """
                CREATE TABLE IF NOT EXISTS dm_thread_cleared (
                    member_id       INTEGER NOT NULL,
                    peer_member_id  INTEGER NOT NULL,
                    cleared_at_utc  TEXT NOT NULL,
                    PRIMARY KEY (member_id, peer_member_id)
                );
                """;
            dmCleared.ExecuteNonQuery();
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
        // 发评人 IP 属地（B 站 content.ip_location）：新抓才带，老行留 NULL 前端不显示（老评论区本来就没这料）。
        EnsureColumn(connection, "dynamic_comments", "ip_location", "TEXT");

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
        // 到站播报（11-03 路线图 🔭 首件）：messages 四列纯文本，结构化卡面载荷无处安放——
        // 照 dm_messages 的 msg_type/dyn_json 先例补两列，老信两列 NULL 渲染照旧，到站播报是头一家用户。
        EnsureColumn(connection, "messages", "msg_type", "TEXT");
        EnsureColumn(connection, "messages", "payload_json", "TEXT");
        // 通知静音（11-03 站长：「有些人嫌烦」）：席位自关到站播报/周报/投稿回执三路定时货，
        // 存逗号分隔频道名（NULL/空＝全开）；只管往后，静音前已寄的信留在手边不追溯。
        EnsureColumn(connection, "members", "notify_mutes", "TEXT");
        // 素净席位自改名（11-08 站长拍板）：昵称自己说了算那一刻起打锁，
        // 日后补绑 B 站号与每次重登都不再拿 B 站昵称盖它（不锁就会发生「改了名、下次登录又变回去」）。
        EnsureColumn(connection, "members", "nickname_locked", "INTEGER");
        // 公告撤回（11-08）：同一次播送共用一个批次号，撤回只打时间戳不进删除——
        // 成员已读没读、铃铛数多少都能复原（把 revoked_at_utc 拍回 NULL 即捐回）。
        EnsureColumn(connection, "messages", "batch_id", "TEXT");
        EnsureColumn(connection, "messages", "revoked_at_utc", "TEXT");
        using (var batchIdx = connection.CreateCommand())
        {
            // 撤回与「站长室列历史播送」都按批次号捣行，没索引就是全表扫。
            batchIdx.CommandText = "CREATE INDEX IF NOT EXISTS idx_messages_batch ON messages(batch_id) WHERE batch_id IS NOT NULL;";
            batchIdx.ExecuteNonQuery();
        }
        // 单删弹幕进回收站（11-08 站长拍板）：不挤 videos 那张回收站——它的形状是「一部片」
        // （video_id/bvid NOT NULL + 一堆文件账目），硬塑一条弹幕要伪造空 Video 字典、还要绕开文件搬运。
        // 不存 id：捐回时重新入队拿新 id，旧 id 只当审计用（弹幕没有对外引用，不必像整片回收那样保号）。
        using (var dmRecycle = connection.CreateCommand())
        {
            dmRecycle.CommandText = """
                CREATE TABLE IF NOT EXISTS danmaku_recycle (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    dm_id INTEGER NOT NULL,
                    video_id INTEGER NOT NULL,
                    bvid TEXT,
                    page_idx INTEGER NOT NULL,
                    t_sec REAL NOT NULL,
                    text TEXT NOT NULL,
                    sender_dmid TEXT,
                    sender_owner TEXT,
                    member_id INTEGER,
                    dm_created_at_utc TEXT,
                    deleted_at_utc TEXT NOT NULL,
                    deleted_by TEXT,
                    deleted_by_member_id INTEGER,
                    source TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_danmaku_recycle_video ON danmaku_recycle(video_id);
                """;
            dmRecycle.ExecuteNonQuery();
        }
        using (var uname = connection.CreateCommand())
        {
            // 大小写不敏感判重（NOCASE 对 ASCII 生效，汉字本无大小写）；登录也按这把尺子认人。
            uname.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_members_username ON members(username COLLATE NOCASE) WHERE username IS NOT NULL;";
            uname.ExecuteNonQuery();
        }

        // UP 空间要按 mid 捞稿：video_meta.up_mid 先前一个索引都没有（全站那几条建索引语句里没它），
        // 新 WHERE 是实打实的全表扫 —— 这行是新端口的必要配套，不是可选优化。
        using (var upIdx = connection.CreateCommand())
        {
            upIdx.CommandText = "CREATE INDEX IF NOT EXISTS idx_video_meta_up_mid ON video_meta(up_mid);";
            upIdx.ExecuteNonQuery();
        }

        // 头图栏已于 11-06 站长拍板整体废除（功能下线，认证保留）：老库里那一列连同旧值一并拍掉。
        // 幂等：PRAGMA 查到列存在才 DROP（新库本没这列，重跑也不响）；这列从没进过索引/视图/触发器，DROP COLUMN 不会撞限制。
        DropColumn(connection, "video_meta", "up_head");

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
    /// <see cref="EnsureColumn"/> 的反向件：只在 <c>PRAGMA table_info</c> 真查到那列时才 DROP，所以新库首开与重跑都安静（幂等）。
    /// 只给「功能整体废除、连旧值也不留」的列用；这类列不得带索引/视图/触发器，否则 SQLite 会直接拒 DROP（站内废除列守这条）。
    /// </summary>
    private static void DropColumn(SqliteConnection connection, string table, string column)
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
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// watch_history 主键的一次性迁移（仿 <see cref="EnsureColumn"/> 的幂等风格）：老库的主键是
    /// (member_id, video_id)——一人一片一行，多 P 片子后报的 P 盖掉前一个 P 的续播位。重建为
    /// (member_id, video_id, page_idx) 一 P 一坑；老表同一部片只有一行，搬过去天然无撞键。
    /// 新库首开建的已是新表（DDL 里主键带着 page_idx），检测即返回；四步重建走一把事务，中途炸了老行原样躺着。
    /// </summary>
    private static void MigrateWatchHistoryPerPage(SqliteConnection connection)
    {
        string? schema;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'watch_history';";
            schema = check.ExecuteScalar() as string;
        }

        // 列定义各占一行，「member_id, video_id, page_idx」这个连珠只在主键句里出现：查它等于查主键形状。
        if (schema is null || schema.Contains("member_id, video_id, page_idx", StringComparison.Ordinal))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = """
                CREATE TABLE watch_history_paged (
                    member_id    INTEGER NOT NULL,
                    video_id     INTEGER NOT NULL,
                    page_idx     INTEGER NOT NULL,
                    position_sec REAL NOT NULL,
                    duration_sec REAL NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (member_id, video_id, page_idx)
                );
                INSERT INTO watch_history_paged (member_id, video_id, page_idx, position_sec, duration_sec, updated_at_utc)
                    SELECT member_id, video_id, page_idx, position_sec, duration_sec, updated_at_utc FROM watch_history;
                DROP TABLE watch_history;
                ALTER TABLE watch_history_paged RENAME TO watch_history;
                """;
            rebuild.ExecuteNonQuery();
        }

        // 索引随老表一起被 DROP 了，同名重建（IF NOT EXISTS：新库路径上它也已被建过，重跑安静）。
        using (var index = connection.CreateCommand())
        {
            index.Transaction = transaction;
            index.CommandText = "CREATE INDEX IF NOT EXISTS idx_watchhistory_member ON watch_history (member_id, updated_at_utc);";
            index.ExecuteNonQuery();
        }

        transaction.Commit();
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

    /// <summary>卡阵（含没到货的行）：尺子只滤可见性，不筛到站与否（下载面板与成员空间吃同一张嘴）。</summary>
    public IReadOnlyList<VideoEntry> ListVideos(VisibilityScope? scope = null)
    {
        var (visSql, bindVis) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "v");
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id, v.url, v.bvid, v.avid, {EffTitleExpr}, v.status, v.created_at_utc, v.cover_url, v.last_error,
                   m.up_name, m.view_count, p.duration_text, v.published_at_utc, m.publish_text,
                   (SELECT COUNT(1) FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'danmaku'),
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   (SELECT p.rel_path FROM previews p WHERE p.video_id = v.id AND p.status = 'Arrived' LIMIT 1),
                   v.visibility, m.up_mid,
                   -- 封面缩略档位清单（第 20 列）：已落盘的文件名串，供前端拼 /thumbs 地址；没切过回 NULL。
                   (SELECT group_concat(t.rel_path, ',') FROM cover_thumbs t WHERE t.video_id = v.id AND t.status = 'arrived')
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            LEFT JOIN pages p ON p.video_id = v.id
                 AND p.idx = (SELECT MIN(idx) FROM pages WHERE video_id = v.id)
            WHERE {visSql}
            ORDER BY v.id DESC;
            """;
        bindVis(command);

        using var reader = command.ExecuteReader();
        return ReadEntries(reader);
    }

    /// <summary>
    /// 全站搜索：只搜已到站的视频（搜索是为了看，不是为了查队列）。
    /// 空格分词后词间 AND，每个词在标题/UP主/BV号/分区/简介任一命中即算；LIKE 通配符按字面处理。
    /// sort="plays" 按播放数降序（见 ViewPowerSql），默认新到站优先。
    /// </summary>
    public IReadOnlyList<VideoEntry> SearchVideos(string query, string? sort, int limit = 50, VisibilityScope? scope = null)
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
            // 标题 / 分区 / 简介三栏都走有效值那张嘴（站内覆写与改派优先；这条查询给 video_meta 起的是 m，
            // 表达式本体在 VideoSettings.cs，别名递进去——卡阵、放映厅、分区页与搜索从此同一张嘴）：
            // 改派后的新户口搜得着、旧户口不再命中，覆写后的新简介搜得着、原句不再命中（拿原值搜就是「改了却搜不到自己改的那句」）。
            // UP 主名与 BV 号本来就只有一份（video_meta.up_name / videos.bvid），照旧直接比。
            conditions.Add($"({EffTitleExpr} LIKE {name} ESCAPE {esc} OR m.up_name LIKE {name} ESCAPE {esc} OR v.bvid LIKE {name} ESCAPE {esc} OR {EffZoneExprWith("m")} LIKE {name} ESCAPE {esc} OR {EffDescExprWith("m")} LIKE {name} ESCAPE {esc})");
        }

        // sort 是白名单拼接，不取用户自由输入进 ORDER BY。
        var order = sort == "plays" ? ViewPowerSql + " DESC, v.id DESC" : "v.id DESC";
        var (visSql, bindVis) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "v");

        command.CommandText = $"""
            SELECT v.id, v.url, v.bvid, v.avid, {EffTitleExpr}, v.status, v.created_at_utc, v.cover_url, v.last_error,
                   m.up_name, m.view_count, p.duration_text, v.published_at_utc, m.publish_text,
                   (SELECT COUNT(1) FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'danmaku'),
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   (SELECT p.rel_path FROM previews p WHERE p.video_id = v.id AND p.status = 'Arrived' LIMIT 1),
                   v.visibility, m.up_mid,
                   -- 封面缩略档位清单（与 ListVideos / 相关推荐同形，ReadEntries 共用一套列下标）。
                   (SELECT group_concat(t.rel_path, ',') FROM cover_thumbs t WHERE t.video_id = v.id AND t.status = 'arrived')
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            LEFT JOIN pages p ON p.video_id = v.id
                 AND p.idx = (SELECT MIN(idx) FROM pages WHERE video_id = v.id)
            WHERE v.status = $status AND {visSql} AND {string.Join(" AND ", conditions)}
            ORDER BY {order}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        command.Parameters.AddWithValue("$limit", limit);
        bindVis(command);

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

    /// <summary>把列表查询的固定列形状读成 VideoEntry，并补扫弹幕条数。
    /// 列下标写死的：加一列要同步<b>四处</b> SELECT（ListVideos / SearchVideos / 相关推荐 / UP 空间 ListVideosByUpMid），
    /// 漏一处就是那一页报锈（曾经注释只写了三处，UP 空间那一处靠单测拖出来的）。列数与下标对不上时，
    /// 现成长得最像的错是 <c>IsDBNull(19)</c> 抛 ordinal 越界。</summary>
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
                PreviewUrl: reader.IsDBNull(16) ? null : "/previews/" + reader.GetString(16),
                // 可见性：能走到这一行的都是尺子放行过的，带出来只给前端画锁（认不出的脏值当公开读，不抛）。
                Visibility: VisibilityCodec.FromColumn(reader.IsDBNull(17) ? null : reader.GetString(17)),
                // UP 的 mid（第 19 列，末尾追加）：卡上的 UP 名要能点进站内 UP 空间。
                UpMid: reader.IsDBNull(18) ? null : reader.GetInt64(18),
                // 封面缩略档位清单（第 20 列）：逗号相接的 /thumbs 文件名；一条都没切过留 null。
                // 与 PreviewUrl 同一副脾性：没切过就是 null，前端自己退回原图，不在这条 SELECT 里使绊子。
                CoverThumbs: reader.IsDBNull(19) ? null : reader.GetString(19)));
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

    /// <summary>批量入库：库内已有 bvid 与批内重复都计入 Duplicated，单事务提交。
    /// 撞重那支只补 cover/时间，visibility 一根手指不碰——早就被主人藏好的稿子，
    /// 导入源（UP 投稿列表/收藏夹）无权把它翻回公开。</summary>
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
                INSERT INTO videos (url, bvid, title, cover_url, published_at_utc, status, created_at_utc, visibility)
                VALUES ($url, $bvid, $title, $cover, $pub, $status, $now, $visibility);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$url", video.Url);
            insert.Parameters.AddWithValue("$bvid", video.Bvid);
            insert.Parameters.AddWithValue("$title", (object?)video.Title ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cover", (object?)video.CoverUrl ?? DBNull.Value);
            insert.Parameters.AddWithValue("$pub", (object?)video.PublishedAtUtc ?? DBNull.Value);
            insert.Parameters.AddWithValue("$status", VideoStatus.Pending.ToString());
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$visibility", VisibilityCodec.ToColumn(video.Visibility));
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
        command.CommandText = "SELECT url, title, status, force_redownload FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(2) != VideoStatus.Pending.ToString())
        {
            return null;
        }

        return new SubmissionCandidate(id, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(3) == 1);
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
            DELETE FROM danmaku_recycle;
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
                force_redownload = 0,
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
    /// <param name="expectFrom">非 null 时只有当前状态在集内才改（多写手交错时的乐观守卫）；返 false = 状态已被别人抢先改走。</param>
    public bool MarkArrived(long id, VideoStatus? expectFrom = null)
        => ExecuteUpdate(id, VideoStatus.Arrived, null, null, expectFrom is { } f ? [f] : null) > 0;

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
    public bool MarkSkipped(long id, string reason, VideoStatus? expectFrom = null)
        => ExecuteUpdate(id, VideoStatus.Skipped, null, reason, expectFrom is { } f ? [f] : null) > 0;

    /// <summary>提交失败，可由站长在站长室重试。</summary>
    public bool MarkFailed(long id, string error, VideoStatus? expectFrom = null)
        => ExecuteUpdate(id, VideoStatus.Failed, null, error, expectFrom is { } f ? [f] : null) > 0;

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
    /// <param name="expectFrom">非 null 时只有当前状态在集内才改；返 false = 状态已被别人抢先改走，调用方别再往下推。</param>
    public bool ResetToPending(long id, IReadOnlyCollection<VideoStatus>? expectFrom = null)
        => ExecuteUpdate(id, VideoStatus.Pending, null, null, expectFrom) > 0;

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
    public IReadOnlyList<DownloadQueueItem> GetPendingQueueItems() => GetQueueItemsByStatus(VideoStatus.Pending);

    /// <summary>已取消的遗留行（Canceled）：面板取消后留在首页卡阵里的「已取消」卡片，供站长一键清理。</summary>
    public IReadOnlyList<DownloadQueueItem> GetCanceledQueueItems() => GetQueueItemsByStatus(VideoStatus.Canceled);

    /// <summary>已取消遗留计数：清理按钮上的那个数。</summary>
    public int CountCanceledVideos()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM videos WHERE status = $status;";
        command.Parameters.AddWithValue("$status", VideoStatus.Canceled.ToString());
        return (int)(long)command.ExecuteScalar()!;
    }

    private IReadOnlyList<DownloadQueueItem> GetQueueItemsByStatus(VideoStatus status)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, bvid, title, cover_url FROM videos WHERE status = $status ORDER BY id;";
        command.Parameters.AddWithValue("$status", status.ToString());

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

    /// <summary>
    /// 状态机唯一改状态入口：连带把 force_redownload 清零——那枚旗只活一次提交，
    /// 无论这轮是到站、失败还是回炉重投，都不能让它搭到下一趟顺风车。
    /// expectFrom 非 null 时追加「当前状态在集内」守卫（照 CancelPendingDownloads 的既有先例），
    /// 返影响行数：巡视快照读到半路、另一写手已抢改时返 0，调用方据此跳过后续副作用（通知/归档），
    /// 免得把已被重投的馆藏盖成假到站。
    /// </summary>
    private int ExecuteUpdate(long id, VideoStatus status, string? taskIdsJson, string? error, IReadOnlyCollection<VideoStatus>? expectFrom = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        var sql = "UPDATE videos SET status = $status, engine_task_ids = $taskIds, last_error = $error, force_redownload = 0 WHERE id = $id";
        if (expectFrom is { Count: > 0 })
        {
            var names = new List<string>(expectFrom.Count);
            var slot = 0;
            foreach (var from in expectFrom)
            {
                var name = "$from" + slot.ToString();
                names.Add(name);
                command.Parameters.AddWithValue(name, from.ToString());
                slot++;
            }

            sql += " AND status IN (" + string.Join(",", names) + ")";
        }

        command.CommandText = sql + ";";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$taskIds", (object?)taskIdsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery();
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
    /// Skipped 出口给站长按的那一下：给这馆挂上「这次真要重下」的旗，下一轮提交会带 RedownloadCompleted 入引擎。
    /// 只挂牌不动状态（状态回 Pending 与入队由调用方一手包办），免得一个写口分两处、中间挂个半拉子状态。
    /// </summary>
    public void MarkForceRedownload(long id)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET force_redownload = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 销档前递绐引擎的对账料：这馆在引擎那边留下过的全部任务号 + 全部 cid。
    /// 任务号三处取并集：videos.engine_task_ids（到站那一刻被 MarkArrived 清成 NULL，单靠它必漏）、
    /// pages.task_id（提交时逐分 P 落账、NOT NULL，最稳的一路）、video_codecs.engine_task_ids（加档历史与在途都算）。
    /// cid 只从 pages 取（引擎历史行的键就是它）；脏 JSON 当没得销，不拦删档。
    /// </summary>
    public (IReadOnlyList<string> TaskIds, IReadOnlyList<long> Cids) GetEngineTraceForVideo(long id)
    {
        var taskIds = new List<string>();
        var cids = new List<long>();
        using var connection = Open(_dbPath);

        using (var tasks = connection.CreateCommand())
        {
            tasks.CommandText = """
                SELECT engine_task_ids FROM videos WHERE id = $id
                UNION ALL SELECT engine_task_ids FROM video_codecs WHERE video_id = $id;
                """;
            tasks.Parameters.AddWithValue("$id", id);
            using var reader = tasks.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(0));
                    foreach (var taskId in parsed ?? new List<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(taskId))
                        {
                            taskIds.Add(taskId);
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // 账本里那入不了手的 JSON 就当没留过任务号
                }
            }
        }

        using (var pages = connection.CreateCommand())
        {
            // 分 P 行上的 task_id 就是提交时逐路落账的引擎任务号（NOT NULL）：到站后 videos 那一列被清空，
            // 只剩这里还能把历史行的主键对上号，所以它必须进并集。
            pages.CommandText = "SELECT task_id, cid FROM pages WHERE video_id = $id ORDER BY idx;";
            pages.Parameters.AddWithValue("$id", id);
            using var reader = pages.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && !string.IsNullOrWhiteSpace(reader.GetString(0)))
                {
                    taskIds.Add(reader.GetString(0));
                }

                if (!reader.IsDBNull(1))
                {
                    var cid = reader.GetInt64(1);
                    if (cid > 0)
                    {
                        cids.Add(cid);
                    }
                }
            }
        }

        return (
            taskIds.Distinct(StringComparer.Ordinal).ToList(),
            cids.Distinct().ToList());
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
            foreach (var table in new[] { "artifacts", "pages", "video_meta", "video_codecs", "cover_thumbs", "videos" })
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
    /// 「重拉封面与媒体」的窄口回写：只动 <c>video_meta</c> 的 zone / description / view_count / up_name / up_mid / publish_text
    /// 六栏，外加 <c>videos.cover_url</c>（新链非空才顶）。回的两个布尔分开报：MetaWritten = 档案那六栏真写到行上了，
    /// CoverWritten = 封面链真写到行上了（拿它们各自拼 updated / kept，不拿一个笼统的「成功」冒充两件事）。
    /// <b>绝不走 <see cref="SaveSubmissionMetadata"/>：它是 DELETE FROM video_meta + INSERT 七栏的首提交口径</b>——
    /// 同一行里 UP 名片（up_face / up_sign / up_fans）、互动数据（stat_* 与 stat_probed）、创作团队（staff_json）
    /// 都是另几拨采集员（LbsUpInfoFetcher / 互动补采）慢慢攒出来的家底，走那条会被整行抹成 NULL，
    /// pages 也会连 task_id / base_path 一起重插。重取只许改上面这几栏，这条口径就长在 SQL 里。
    /// 站内覆写那三栏（site_title / site_desc / site_zone）这里一个字节不碰（有效值表达式自会挑该念哪份）；
    /// <b>videos.title 也不重取</b>：标题正是站长能在稿件设置里覆写的那一栏，跟着 B 站跑会把他改过的字冲掉
    /// （他勾的是「重拉封面与媒体」，不是「重命名」）。
    /// 七栏一律 COALESCE：B 站这次没给（null / 空串 / 纯空白）就当这栏没新东西，不拿空值涂旧账。
    /// </summary>
    public (bool MetaWritten, bool CoverWritten) UpdateArchiveFromView(long videoId, ArchiveRefreshPatch patch)
    {
        var zone = BlankToNull(patch.Zone);
        var description = BlankToNull(patch.Description);
        var viewCount = BlankToNull(patch.ViewCountText);
        var upName = BlankToNull(patch.UpName);
        var publishText = BlankToNull(patch.PublishText);
        var coverUrl = BlankToNull(patch.CoverUrl);
        // B 站 owner.mid 缺档时给 0：0 不是 mid，当没给处理（COALESCE 自会留着旧值）。
        var upMid = patch.UpMid is > 0 ? patch.UpMid : null;

        using var connection = Open(_dbPath);

        var metaWritten = false;
        if (zone is not null || description is not null || viewCount is not null || upName is not null
            || publishText is not null || upMid is not null)
        {
            using var meta = connection.CreateCommand();
            meta.CommandText = """
                UPDATE video_meta
                   SET zone = COALESCE($zone, zone),
                       description = COALESCE($desc, description),
                       view_count = COALESCE($views, view_count),
                       up_name = COALESCE($upName, up_name),
                       up_mid = COALESCE($upMid, up_mid),
                       publish_text = COALESCE($publish, publish_text)
                 WHERE video_id = $id;
                """;
            AddText(meta, "$zone", zone);
            AddText(meta, "$desc", description);
            AddText(meta, "$views", viewCount);
            AddText(meta, "$upName", upName);
            AddText(meta, "$publish", publishText);
            meta.Parameters.AddWithValue("$upMid", (object?)upMid ?? DBNull.Value);
            meta.Parameters.AddWithValue("$id", videoId);
            metaWritten = meta.ExecuteNonQuery() > 0;
        }

        var coverWritten = false;
        if (coverUrl is not null)
        {
            using var video = connection.CreateCommand();
            video.CommandText = "UPDATE videos SET cover_url = COALESCE($cover, cover_url) WHERE id = $id;";
            AddText(video, "$cover", coverUrl);
            video.Parameters.AddWithValue("$id", videoId);
            coverWritten = video.ExecuteNonQuery() > 0;
        }

        return (metaWritten, coverWritten);
    }

    /// <summary>去首尾空白后空白一律 null：COALESCE 那一侧靠它分辨「B 站给了」与「B 站这次没说」。</summary>
    private static string? BlankToNull(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void AddText(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

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
                // 手动重试算重新开局：让路历史（retry_count）与上一轮探测的账（实测编码/码率/防抖戳）一并归零，
                // 不然老账一上来就踩满放弃上限，或新文件还没探就先挂着上一轮的实测编码骗人。
                retry.CommandText = "UPDATE video_codecs SET status = 'Downloading', last_error = NULL, engine_task_ids = NULL, retry_at_utc = NULL, retry_count = 0, measured_codec = NULL, video_kbps = NULL, audio_kbps = NULL, stream_probed = NULL, shape_probed = NULL, width = NULL, height = NULL, fps = NULL, audio_codec = NULL, quality_id = NULL, quality_name = NULL, audio_quality_id = NULL WHERE video_id = $id AND page_idx = $page AND codec = $codec;";
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
        // 真拿到引擎任务了，让路 saga 就此结束：排队标记与让路次数一起销账（不然下次重新排队又从老账算起）。
        command.CommandText = "UPDATE video_codecs SET engine_task_ids = $taskIds, base_path = $base, retry_at_utc = NULL, retry_count = 0 WHERE id = $id;";
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

    public IReadOnlyList<(long Id, int PageIdx, string Codec, string Status, string? RelPath, string? LastError, int RetryCount)> GetCodecVersions(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, page_idx, codec, status, rel_path, last_error, retry_count FROM video_codecs WHERE video_id = $id ORDER BY page_idx, codec;";
        command.Parameters.AddWithValue("$id", videoId);

        using var reader = command.ExecuteReader();
        var list = new List<(long, int, string, string, string?, string?, int)>();
        while (reader.Read())
        {
            list.Add((
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
        }

        return list;
    }

    /// <summary>
    /// 撞车让路：行保持在途（不判死），只挂一个到期回炉时间 + 累加让路次数，把引擎给的原因留在 last_error 供站长室看。
    /// 只动还没分到引擎任务的行（engine_task_ids IS NULL）——已挂真任务的行归同步员盯，不该被这里插一脚。
    /// </summary>
    public void MarkCodecDeferred(long id, DateTimeOffset retryAtUtc, string reason)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE video_codecs
            SET retry_at_utc = $at, retry_count = retry_count + 1, last_error = $reason
            WHERE id = $id AND status = 'Downloading' AND engine_task_ids IS NULL;
            """;
        command.Parameters.AddWithValue("$at", retryAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 本轮真要提交了：销掉排队标记（站长室队列快照的 waiting 就认这一列），retry_count 留着算退避。
    /// 销成 NULL 后这行对 <see cref="GetDueCodecRetries"/> 隐身，提交途中不会被重复拾起。
    /// </summary>
    public void ClearCodecRetry(long videoId, string codec)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video_codecs SET retry_at_utc = NULL WHERE video_id = $id AND codec = $codec AND status = 'Downloading';";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$codec", codec.Trim().ToLowerInvariant());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 到期该回炉的加档行（在途 + 有排队标记且时间已到）；nowUtc 要传 UTC（比较走 ISO 文本字典序）。
    /// 拾到后要拿 <see cref="TakeCodecRetryLease"/> 抢租约，防同一条被相邻两轮连投两次。
    /// </summary>
    public IReadOnlyList<(long Id, long VideoId, int PageIdx, string Codec)> GetDueCodecRetries(DateTimeOffset nowUtc)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, video_id, page_idx, codec
            FROM video_codecs
            WHERE status = 'Downloading' AND retry_at_utc IS NOT NULL AND retry_at_utc <= $now
            ORDER BY retry_at_utc, id;
            """;
        command.Parameters.AddWithValue("$now", nowUtc.ToString("O"));

        using var reader = command.ExecuteReader();
        var list = new List<(long, long, int, string)>();
        while (reader.Read())
        {
            list.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return list;
    }

    /// <summary>
    /// 抢回炉租约：把到期时间前移到 leaseUntilUtc（典型为 now + 90s），抢不到（已被别的轮次接走 / 已销账）返回 false。
    /// 租约期内这行不会再次到期，提交途中也就不会被重复入队。
    /// </summary>
    public bool TakeCodecRetryLease(long id, DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE video_codecs
            SET retry_at_utc = $until
            WHERE id = $id AND status = 'Downloading' AND retry_at_utc IS NOT NULL AND retry_at_utc <= $now;
            """;
        command.Parameters.AddWithValue("$until", leaseUntilUtc.ToString("O"));
        command.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 加档行的分轨码率落库：probed=false 时只销防抖戳（留着下次重探），探不到值也置 probed=true 不再扰盘。
    /// measuredCodec 写的是「盘上这个文件实测是什么编码」，与 codec（点单值）分家，COALESCE 只补不擦。
    /// </summary>
    public void SetCodecStreams(
        long id,
        int? videoKbps,
        int? audioKbps,
        bool probed,
        string? measuredCodec = null,
        int? width = null,
        int? height = null,
        double? fps = null,
        string? audioCodec = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE video_codecs
            SET video_kbps = COALESCE($vkbps, video_kbps),
                audio_kbps = COALESCE($akbps, audio_kbps),
                measured_codec = COALESCE($measured, measured_codec),
                width = COALESCE($width, width),
                height = COALESCE($height, height),
                fps = COALESCE($fps, fps),
                audio_codec = COALESCE($audioCodec, audio_codec),
                stream_probed = $probed,
                shape_probed = $probed
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$vkbps", (object?)videoKbps ?? DBNull.Value);
        command.Parameters.AddWithValue("$akbps", (object?)audioKbps ?? DBNull.Value);
        command.Parameters.AddWithValue("$measured", (object?)measuredCodec ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", (object?)width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)height ?? DBNull.Value);
        command.Parameters.AddWithValue("$fps", (object?)fps ?? DBNull.Value);
        command.Parameters.AddWithValue("$audioCodec", (object?)audioCodec ?? DBNull.Value);
        command.Parameters.AddWithValue("$probed", probed ? 1 : (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 这一稿里「正片已落盘但还没探过分轨码率」的分 P：到站归档那一刻给 worker 取一次。
    /// 存量没人探的行另有补员兼顶：见 <see cref="GetPagesAwaitingStreamProbeBackfill"/>。
    /// </summary>
    public IReadOnlyList<(int PageIdx, string Rel)> GetPagesAwaitingStreamProbe(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT idx, local_video_path
            FROM pages
            WHERE video_id = $id AND local_video_path IS NOT NULL
              AND (stream_probed IS NULL OR shape_probed IS NULL)
            ORDER BY idx;
            """;
        command.Parameters.AddWithValue("$id", videoId);

        using var reader = command.ExecuteReader();
        var list = new List<(int, string)>();
        while (reader.Read())
        {
            list.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        return list;
    }

    /// <summary>补员巡视用：全库「正片已落盘但从没探过分轨码率」的行（探过的都盖了 stream_probed 戳）。
    /// 新货优先，每轮封顶免得扰盘风暴。</summary>
    public IReadOnlyList<(long VideoId, int PageIdx, string Rel)> GetPagesAwaitingStreamProbeBackfill(int limit)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT video_id, idx, local_video_path
            FROM pages
            WHERE local_video_path IS NOT NULL AND local_video_path != ''
              AND (stream_probed IS NULL OR shape_probed IS NULL)
            ORDER BY video_id DESC
            LIMIT $n;
            """;
        command.Parameters.AddWithValue("$n", limit);

        using var reader = command.ExecuteReader();
        var list = new List<(long, int, string)>();
        while (reader.Read())
        {
            list.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2)));
        }

        return list;
    }

    /// <summary>补员巡视用：全库「加档已定档但从没探过分轨码率」的行（同样靠 stream_probed 防抖）。</summary>
    public IReadOnlyList<(long Id, long VideoId, int PageIdx, string Codec, string Rel)> GetCodecsAwaitingStreamProbeBackfill(int limit)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, video_id, page_idx, codec, rel_path
            FROM video_codecs
            WHERE rel_path IS NOT NULL AND rel_path != ''
              AND (stream_probed IS NULL OR shape_probed IS NULL)
            ORDER BY id DESC
            LIMIT $n;
            """;
        command.Parameters.AddWithValue("$n", limit);

        using var reader = command.ExecuteReader();
        var list = new List<(long, long, int, string, string)>();
        while (reader.Read())
        {
            list.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4)));
        }

        return list;
    }

    /// <summary>
    /// 主版本的分轨实测落库（到站那一刻由 worker 探完写）：codec 只在探到时补（COALESCE，
    /// 不拿 null 去擦已有的编码名——播放页版本区块拿它决定整块显不显），双轨码率照写，
    /// 无论如何都盖 stream_probed=1：一个文件只扰盘一趟，探不到就留 null。
    /// </summary>
    public void SetPageStreams(
        long videoId,
        int pageIdx,
        string? codec,
        int? videoKbps,
        int? audioKbps,
        int? width = null,
        int? height = null,
        double? fps = null,
        string? audioCodec = null)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pages
            SET codec = COALESCE($codec, codec),
                video_kbps = COALESCE($vkbps, video_kbps),
                audio_kbps = COALESCE($akbps, audio_kbps),
                width = COALESCE($width, width),
                height = COALESCE($height, height),
                fps = COALESCE($fps, fps),
                audio_codec = COALESCE($audioCodec, audio_codec),
                stream_probed = 1,
                shape_probed = 1
            WHERE video_id = $id AND idx = $idx;
            """;
        command.Parameters.AddWithValue("$codec", (object?)codec ?? DBNull.Value);
        command.Parameters.AddWithValue("$vkbps", (object?)videoKbps ?? DBNull.Value);
        command.Parameters.AddWithValue("$akbps", (object?)audioKbps ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", (object?)width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)height ?? DBNull.Value);
        command.Parameters.AddWithValue("$fps", (object?)fps ?? DBNull.Value);
        command.Parameters.AddWithValue("$audioCodec", (object?)audioCodec ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$idx", pageIdx);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 引擎实发画质档真值落库（到站那一刻由同步员从在途任务 Metadata 拄下来）：
    /// COALESCE 只补不擦——引擎报过的真相比后补的实测硬，但也不拿 null 去擦已有真值。
    /// 一份稿全部 P 同档（提交时整稿一个清晰度选项），逐页同值盖。
    /// </summary>
    public void SetPageQuality(long videoId, int? qualityId, string? qualityName, int? audioQualityId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pages
            SET quality_id = COALESCE($qid, quality_id),
                quality_name = COALESCE($qname, quality_name),
                audio_quality_id = COALESCE($aqid, audio_quality_id)
            WHERE video_id = $id;
            """;
        command.Parameters.AddWithValue("$qid", (object?)qualityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$qname", (object?)qualityName ?? DBNull.Value);
        command.Parameters.AddWithValue("$aqid", (object?)audioQualityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", videoId);
        command.ExecuteNonQuery();
    }

    /// <summary>加档行的引擎实发规格落库：与 <see cref="SetPageQuality"/> 同一口径，COALESCE 只补不擦。</summary>
    public void SetCodecQuality(long id, int? qualityId, string? qualityName, int? audioQualityId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE video_codecs
            SET quality_id = COALESCE($qid, quality_id),
                quality_name = COALESCE($qname, quality_name),
                audio_quality_id = COALESCE($aqid, audio_quality_id)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$qid", (object?)qualityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$qname", (object?)qualityName ?? DBNull.Value);
        command.Parameters.AddWithValue("$aqid", (object?)audioQualityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 站长室队列快照用的加档行：在途的加档账都在 video_codecs，不在 videos，
    /// 原来那块面板只认 videos 表、所以一直看不见它们。waiting=true 是「判重让路、排队等回炉」，
    /// measured 是盘上实测编码（还没探过给 null）——与点单的 codec 呲牙时一眼就能看出来。
    /// 尾巴四个字段给面板透实况：让路次数/让路缘由/引擎任务清单/回炉倒计时（元组超 7 元自动嵌进 Rest）。
    /// </summary>
    public IReadOnlyList<(long VideoId, string Bvid, string? Title, string Codec, int PageIdx, bool Waiting, string? Measured, (int RetryCount, string? LastError, string? TaskIdsJson, DateTimeOffset? RetryAtUtc) Extra)> GetCodecQueueRows()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.video_id, v.bvid, v.title, c.codec, c.page_idx, c.retry_at_utc, c.measured_codec,
                   c.retry_count, c.last_error, c.engine_task_ids
            FROM video_codecs c
            JOIN videos v ON v.id = c.video_id
            WHERE c.status = 'Downloading'
            ORDER BY c.video_id, c.page_idx, c.codec;
            """;

        using var reader = command.ExecuteReader();
        var list = new List<(long, string, string?, string, int, bool, string?, (int, string?, string?, DateTimeOffset?))>();
        while (reader.Read())
        {
            DateTimeOffset? retryAt = null;
            if (!reader.IsDBNull(5)
                && DateTimeOffset.TryParse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
            {
                retryAt = parsed;
            }

            list.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                !reader.IsDBNull(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                (reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    retryAt)));
        }

        return list;
    }

    /// <summary>全站台账引用过的引擎任务清单（不分相位）：开机孤儿清扫用——
    /// 引擎判重不看相位，没主的人（重试点单时行里旧任务号被清过）躺在在途表里
    /// 会被当成「未完成的同名任务」拦掉同视频的重试点单。</summary>
    public IReadOnlyList<string> GetReferencedEngineTaskIds()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT engine_task_ids FROM videos WHERE engine_task_ids IS NOT NULL
            UNION
            SELECT engine_task_ids FROM video_codecs WHERE engine_task_ids IS NOT NULL;
            """;

        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
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

    /// <summary>
    /// UP 主名片登记：face 存本地化路径（或热链），fans 可空；失败重试时覆盖式更新。
    /// official（B 站官方认证标题）同走这一口径：
    /// <b>一栏不传就写 NULL</b>（与 face/sign/fans 对 null 的现行为对齐，不是「null 则不动列」），
    /// 所以「失败销账」由调用方递空串、采到就递真值；老调用点不传那一栏时它同被销成 NULL，
    /// 由补采轮重填（读侧是「该 mid 最新一条非空」，一行被抹不等于整位丢了）。
    /// </summary>
    public void SetUpInfo(long videoId, string? face, string? sign, long? fans, string? official = null)
    {
        using var connection = Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE video_meta SET up_face = $face, up_sign = $sign, up_fans = $fans, up_official = $official WHERE video_id = $id;";
        cmd.Parameters.AddWithValue("$face", (object?)face ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sign", (object?)sign ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fans", (object?)fans ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$official", (object?)official ?? DBNull.Value);
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

    /// <summary>同站可蹭的名片快照：同一位 UP 的另一条稿子把头像化到了本地且盘上真有件。</summary>
    public sealed record UpCardCopy(string Face, string? Sign, long? Fans);

    /// <summary>
    /// 该不该去蹭：本行脸是 null/空串/本地件丢链（早年 up-{mid}.jpg 命名那一代）就算该蹭；
    /// 热链按站长方针算能用，不蹭（白嫖图床本来就是第一选择）。
    /// </summary>
    private bool ShouldBorrowFace(string? face)
    {
        if (string.IsNullOrWhiteSpace(face))
        {
            return true;
        }

        var value = face.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsLocalFaceOnDisk(value);
    }

    /// <summary>/avatars/... 落盘验货：前缀对、leaf 不穿目录、文件真在。其余一律 not usable。</summary>
    private bool IsLocalFaceOnDisk(string value)
    {
        if (!value.StartsWith("/avatars/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var leaf = value["/avatars/".Length..]
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (leaf.Length == 0 || leaf.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(leaf))
        {
            return false;
        }

        return File.Exists(Path.Combine(_paths.Avatars, leaf));
    }

    /// <summary>
    /// 同站蹭脸：同一 mid 的其它稿子里挑一张本地化且落盘的名片（新账优先）。
    /// 只认本地件：热链不配当被蹭源（那是每次都要去敲图床的门），空串/缺件行也不算现货。
    /// </summary>
    public UpCardCopy? FindSharedUpCardByMid(long mid, long exceptVideoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT up_face, up_sign, up_fans FROM video_meta
            WHERE up_mid = $mid AND video_id <> $except AND up_face LIKE '/avatars/%'
            ORDER BY video_id DESC;
            """;
        command.Parameters.AddWithValue("$mid", mid);
        command.Parameters.AddWithValue("$except", exceptVideoId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var face = reader.GetString(0);
            if (!IsLocalFaceOnDisk(face))
            {
                continue;
            }

            return new UpCardCopy(
                face,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2));
        }

        return null;
    }

    /// <summary>
    /// 存量对账（开局一次，零出门）：把 mid 已知却脸没落盘的老账，从同站现货里蹭回来落账；
    /// 签名/粉丝只在原行空着时垫，不把本行已有的数抹掉。回蹭成几条。
    /// </summary>
    public int BackfillUpCardsFromShared()
    {
        var repaired = 0;
        using var connection = Open(_dbPath);

        var stale = new List<(long VideoId, long Mid)>();
        using (var scan = connection.CreateCommand())
        {
            scan.CommandText = "SELECT video_id, up_mid, up_face FROM video_meta WHERE up_mid IS NOT NULL;";
            using var reader = scan.ExecuteReader();
            while (reader.Read())
            {
                var face = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (ShouldBorrowFace(face))
                {
                    stale.Add((reader.GetInt64(0), reader.GetInt64(1)));
                }
            }
        }

        foreach (var (videoId, mid) in stale)
        {
            var shared = FindSharedUpCardByMid(mid, videoId);
            if (shared is null)
            {
                continue;
            }

            using var fix = connection.CreateCommand();
            fix.CommandText = """
                UPDATE video_meta
                SET up_face = $face, up_sign = COALESCE(up_sign, $sign), up_fans = COALESCE(up_fans, $fans)
                WHERE video_id = $id;
                """;
            fix.Parameters.AddWithValue("$face", shared.Face);
            fix.Parameters.AddWithValue("$sign", (object?)shared.Sign ?? DBNull.Value);
            fix.Parameters.AddWithValue("$fans", (object?)shared.Fans ?? DBNull.Value);
            fix.Parameters.AddWithValue("$id", videoId);
            repaired += fix.ExecuteNonQuery();
        }

        return repaired;
    }

    /// <summary>
    /// 钉子户名单：mid 已知、脸没落盘、且同站没现货可蹭（单稿 UP）的行。这些只能出门重采，
    /// 但早已 Arrived、不再走重新排队重采，交给巡捕零散补采（限流防扰 B 站；无采集号时 TryFetchUpAsync 自己会空转）。
    /// </summary>
    public IReadOnlyList<long> ListUpCardsBeyondSalvage(int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var stuck = new List<long>();
        using var connection = Open(_dbPath);
        var scan = connection.CreateCommand();
        scan.CommandText = "SELECT video_id, up_mid, up_face FROM video_meta WHERE up_mid IS NOT NULL ORDER BY video_id;";
        using var reader = scan.ExecuteReader();
        while (reader.Read() && stuck.Count < limit)
        {
            var face = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (!ShouldBorrowFace(face))
            {
                continue;
            }

            var videoId = reader.GetInt64(0);
            // 能蹭到现货的不算钉子户——那部分开局对账已修好（或本轮不会出门）。
            if (FindSharedUpCardByMid(reader.GetInt64(1), videoId) is not null)
            {
                continue;
            }

            stuck.Add(videoId);
        }

        return stuck;
    }

    /// <summary>
    /// 认证的欠账名单：mid 已知、但 <c>up_official</c> 还没落的<b>去重 mid</b>
    /// （新稿的 UP 排前面先补）。给开局那一次性存量回填用（<c>StatusSyncWorker</c>）：
    /// 脸早已本地化的老 UP，<c>LbsUpInfoFetcher.NeedsRefetch</c>（在 Engine 层）判能用、补采轮永不登门，
    /// 这一栏对老用户等于不存在，得专门跑一轮去敲 B 站。
    /// <b>这张表不自动收敛</b>：无认证的人永远采不到认证，跑一轮它们照旧在名单里 ——
    /// 要不要再跑由 settings 哨兵 <c>up-official-migrated</c> 说了算，这里只照实报欠账。
    /// </summary>
    public IReadOnlyList<long> ListMidsLackingOfficial()
    {
        var mids = new List<long>();
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT up_mid FROM video_meta
            WHERE up_mid IS NOT NULL AND up_official IS NULL
            GROUP BY up_mid
            ORDER BY MAX(video_id) DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            mids.Add(reader.GetInt64(0));
        }

        return mids;
    }

    /// <summary>
    /// 认证的<b>窄口</b>：一根 UPDATE 只动 <c>up_official</c> 一列，同一位 UP 的所有行全量刷新。
    /// 为什么不走 <see cref="SetUpInfo"/>：它无条件写名片那几列，而存量回填手边没有 face/sign/fans 的现值，
    /// 拿 null 去传就是把名片抹平（与当年 SaveSubmissionMetadata 埋名片同一类事故）。
    /// 这一列<b>只补不抹</b>（COALESCE，11-05 深夜交叉验证抓到 P0 后收口）：旧版「采到什么写什么」在空手时
    /// 把已到位的现值全行写成 NULL——全行写场景没有读侧「最新非空」兑底可仗（它只救得了个别行被抹，
    /// 救不了全行被抹），叠上哨兵后不可自愈，只能写入口自己守。
    /// 重复跑幂等；UPDATE 命中即计行（同值也算），「补了 X 位」口径不变。回写到几行。
    /// </summary>
    public int SetUpOfficial(long mid, string? official)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video_meta SET up_official = COALESCE($official, up_official) WHERE up_mid = $mid;";
        command.Parameters.AddWithValue("$official", (object?)official ?? DBNull.Value);
        command.Parameters.AddWithValue("$mid", mid);
        return command.ExecuteNonQuery();
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

    /// <summary>创作团队归档（与互动数据同一次 view 采回）：无团队/采挂都写 null，不影响 stat_probed 的销账。
    /// 重提交（销档重下）会把整行 DELETE+INSERT 重开，staff 跟着清零 → 下一轮回炉重采，正是想要的行为。</summary>
    public void SetVideoStaff(long videoId, string? staffJson)
    {
        using var connection = Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE video_meta SET staff_json = $json WHERE video_id = $id;";
        cmd.Parameters.AddWithValue("$json", (object?)staffJson ?? DBNull.Value);
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
    /// 接下来播放：同 UP 主优先、层内随机抽取，排除自身；列形状与列表一致，前端卡片直接复用。
    /// 层内为什么走 RANDOM() 而不是 v.id DESC：馆藏里有 143 支同 UP 主稿件，按 id 倒序取 8 支就永远是
    /// 最新那 8 支，刷新多少次都是同几张脸，本地分站最该干的「把沉在库底的旧稿递到手边」一件没干。
    /// 只打乱层内顺序，同 UP 优先这一层留着（一屏里掺进不相干的 UP 反而不像「接下来播放」）。
    /// 补位也只从这位看得着的库里挑：尺子不递进去，别人的私有稿就会从随机那条缝里溜到访客眼前。
    /// </summary>
    public List<VideoEntry> GetRelatedVideos(long videoId, int take = 8, VisibilityScope? scope = null)
    {
        var (visSql, bindVis) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "v");
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id, v.url, v.bvid, v.avid, {EffTitleExpr}, v.status, v.created_at_utc, v.cover_url, v.last_error,
                   m.up_name, m.view_count, p.duration_text, v.published_at_utc, m.publish_text,
                   (SELECT COUNT(1) FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'danmaku'),
                   (SELECT a.rel_path FROM artifacts a WHERE a.video_id = v.id AND a.kind = 'cover' ORDER BY a.id LIMIT 1),
                   (SELECT p.rel_path FROM previews p WHERE p.video_id = v.id AND p.status = 'Arrived' LIMIT 1),
                   v.visibility, m.up_mid,
                   -- 封面缩略档位清单（三处 SELECT 同形：ReadEntries 列下标写死，少一处就是整页报锈）。
                   (SELECT group_concat(t.rel_path, ',') FROM cover_thumbs t WHERE t.video_id = v.id AND t.status = 'arrived')
            FROM videos v
            LEFT JOIN video_meta m ON m.video_id = v.id
            LEFT JOIN pages p ON p.video_id = v.id
                 AND p.idx = (SELECT MIN(idx) FROM pages WHERE video_id = v.id)
            WHERE v.status = $status AND v.id <> $id AND {visSql}
            ORDER BY (CASE WHEN $mid IS NOT NULL AND m.up_mid = $mid THEN 0
                           WHEN $up IS NOT NULL AND m.up_name = $up THEN 0
                           ELSE 1 END),
                     RANDOM()
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", VideoStatus.Arrived.ToString());
        command.Parameters.AddWithValue("$id", videoId);
        bindVis(command);

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

    /// <summary>这条稿当前记着的 B 站封面热链（重拉封面时 B 站没给新链就拿它原地重抓）；查无此行也回 null。</summary>
    public string? GetCoverUrl(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cover_url FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>推封面件目录与基名用的那条产物 rel（正片行优先，与 <see cref="ListVideosMissingCover"/> 的 BaseRel 同一条子查询）；
    /// 一条产物行都没有时回 null（没处落盘，重拉封面这一步只能原样留着）。</summary>
    public string? GetCoverBaseRel(long videoId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.rel_path FROM artifacts a WHERE a.video_id = $id
             ORDER BY (a.kind = 'video') DESC, a.id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", videoId);
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

    /// <summary>观看页数据：档案 + 分 P + 本地文件。查不到 bvid 或这稿对这位藏着 → 都回 null（同一张面孔，不告诉门外它存在）。</summary>
    public WatchData? GetWatchData(string bvid, VisibilityScope? scope = null)
    {
        var (visSql, bindVis) = VisibilityFilter(scope ?? VisibilityScope.PublicOnly, "v");
        using var connection = Open(_dbPath);

        long id;
        string url, primaryBvid;
        string? title, coverUrl, createdAtText;
        long? aid;
        VideoStatus status;
        VideoVisibility visibility;
        bool titleOverridden, descOverridden, zoneOverridden;
        using (var video = connection.CreateCommand())
        {
            // 标题走有效值那张嘴（站内覆写优先）；三枚覆写标给稿件设置面板回显（NULLIF 与上面两式一字同源，见 VideoSettings.cs）。
            video.CommandText = $"""
                SELECT v.id, v.url, v.bvid, {EffTitleExpr}, v.status, v.cover_url, v.created_at_utc, v.avid, v.visibility,
                       {OverriddenExpr("site_title")}, {OverriddenExpr("site_desc")}, {OverriddenExpr("site_zone")}
                FROM videos v WHERE v.bvid = $bvid AND {visSql};
                """;
            video.Parameters.AddWithValue("$bvid", bvid);
            bindVis(video);
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
            visibility = VisibilityCodec.FromColumn(reader.IsDBNull(8) ? null : reader.GetString(8));
            titleOverridden = reader.GetInt64(9) != 0;
            descOverridden = reader.GetInt64(10) != 0;
            zoneOverridden = reader.GetInt64(11) != 0;
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
        string? staffJson = null;
        using (var meta = connection.CreateCommand())
        {
            // 分区与简介走有效值表达式（EffZoneExpr / EffDescExpr）：改派与本地覆写在放映厅也照得住，
            // 与分区页 / 卡阵同一张嘴（以前只吃 vm.zone：站长改了派，分区页换了、开片那张还顶户口旧名）。
            // 从 videos 起 LEFT JOIN（不是从 video_meta 起）：老稿没档案时改派过的户口照样得报，不被 INNER JOIN 抹掉。
            meta.CommandText = $"""
                SELECT vm.up_name, {EffZoneExpr}, vm.publish_text, {EffDescExpr}, vm.view_count, vm.up_mid,
                       vm.up_face, vm.up_sign, vm.up_fans, vm.stat_like, vm.stat_coin, vm.stat_fav, vm.stat_share, vm.stat_reply, vm.staff_json
                FROM videos v LEFT JOIN video_meta vm ON vm.video_id = v.id
                WHERE v.id = $id;
                """;
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
                staffJson = reader.IsDBNull(14) ? null : reader.GetString(14);
            }
        }

        // 同站蹭脸（展示层兜底，不动账；存量老账由开局 BackfillUpCardsFromShared 批量改库）：
        // 本行脸没落盘（早年 up-{mid}.jpg 命名换 sha1 后丢件那一代）就从同一 UP 的别稿搭一张现货，别让 UpCard 吃 404 顶灰占位。
        if (upMid is long faceMid && ShouldBorrowFace(upFace) && FindSharedUpCardByMid(faceMid, id) is { } sharedFace)
        {
            upFace = sharedFace.Face;
            upSign ??= sharedFace.Sign;
            upFans ??= sharedFace.Fans;
        }

        var pages = new List<(int Index, string Title, long? Cid, string? Duration, string? VideoRel, string? Codec, int? VideoKbps, int? AudioKbps, int? Height, double? Fps, string? AudioCodec, string? QualityName, int? AudioQualityId)>();
        using (var pageCmd = connection.CreateCommand())
        {
            pageCmd.CommandText = "SELECT idx, title, cid, duration_text, local_video_path, codec, video_kbps, audio_kbps, height, fps, audio_codec, quality_name, audio_quality_id FROM pages WHERE video_id = $id ORDER BY idx;";
            pageCmd.Parameters.AddWithValue("$id", id);
            using var reader = pageCmd.ExecuteReader();
            while (reader.Read())
            {
                var idx = reader.GetInt32(0);
                var videoRel = reader.IsDBNull(4) ? null : reader.GetString(4);
                var codec = reader.IsDBNull(5) ? null : reader.GetString(5);
                int? videoKbps = reader.IsDBNull(6) ? null : reader.GetInt32(6);
                int? audioKbps = reader.IsDBNull(7) ? null : reader.GetInt32(7);
                int? height = reader.IsDBNull(8) ? null : reader.GetInt32(8);
                double? fps = reader.IsDBNull(9) ? null : reader.GetDouble(9);
                string? audioCodec = reader.IsDBNull(10) ? null : reader.GetString(10);
                string? qualityName = reader.IsDBNull(11) ? null : reader.GetString(11);
                int? audioQualityId = reader.IsDBNull(12) ? null : reader.GetInt32(12);

                // 老档案的编码名懒测（仓库原有行为，原语义不动）：只补 codec 一个字段。
                // 播放页版本区块拿 props.page.codec 决定整块显不显，这条腿不能拆；
                // 但分轨码率不在这里探——开观看页是热路径，不为码率起 ffprobe，
                // 码率只从写入侧来（到站/定档那一刻），探不到就 null，前端按设计省略码率段。
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
                    codec,
                    videoKbps,
                    audioKbps,
                    height,
                    fps,
                    audioCodec,
                    qualityName,
                    audioQualityId));
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

        var codecRows = new List<(int PageIdx, string Codec, string Status, string? Rel, string? Err, int? VideoKbps, int? AudioKbps, int? Height, double? Fps, string? AudioCodec, string? QualityName, int? AudioQualityId)>();
        using (var codecCmd = connection.CreateCommand())
        {
            codecCmd.CommandText = "SELECT page_idx, codec, status, rel_path, last_error, video_kbps, audio_kbps, height, fps, audio_codec, quality_name, audio_quality_id FROM video_codecs WHERE video_id = $id;";
            codecCmd.Parameters.AddWithValue("$id", id);
            using var reader = codecCmd.ExecuteReader();
            while (reader.Read())
            {
                // 码率只吃写入侧（同步员定档那一刻探完落库），读侧不补探：这条是每次开观看页都走的热路径。
                // 历史遗留没探过的就是 null：不回填、不自愈（存量馆藏会整体重新下，不值得扰盘）。
                codecRows.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11)));
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
                SizeOfVideoFile(c.Rel),
                c.Err,
                c.VideoKbps,
                c.AudioKbps,
                AudioCodec: c.AudioCodec,
                Fps: c.Fps,
                Height: c.Height,
                QualityName: c.QualityName,
                AudioQualityId: c.AudioQualityId)).ToList(),
            p.Codec,
            SizeOfVideoFile(p.VideoRel),
            p.VideoKbps,
            p.AudioKbps,
            AudioCodec: p.AudioCodec,
            Fps: p.Fps,
            Height: p.Height,
            QualityName: p.QualityName,
            AudioQualityId: p.AudioQualityId
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
            statReply,
            Staff: WatchStaffCodec.FromJson(staffJson),
            Visibility: VisibilityCodec.ToColumn(visibility),
            // 稿件设置面板那几栏（IsStationOwner 不在这里算：数据层不认身份，与 CanManage 同一分工，归 /api/watch 端点盖）。
            TitleOverridden: titleOverridden,
            DescOverridden: descOverridden,
            ZoneOverridden: zoneOverridden);
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
