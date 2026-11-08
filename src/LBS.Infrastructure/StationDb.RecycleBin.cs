using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>回收站里的一件封存馆藏（清单存 recycle_bin.manifest，文件在 {recycle}/{id}/files 下）。</summary>
public sealed record RecycleEntry(
    long Id,
    long VideoId,
    string Bvid,
    string? Title,
    string? UpName,
    DateTimeOffset DeletedAtUtc,
    string? DeletedBy,
    /// <summary>动手那位的<b>席位 id</b>（11-08 补）：光有 <see cref="DeletedBy"/> 那个显示名字符串分不清撞名的两个人，
    /// 也写不出「谁删的」这种查账口径。与弹幕回收站的 deleted_by_member_id 同一形状。
    /// </summary>
    long? DeletedByMemberId,
    int FileCount,
    long Bytes,
    int PageCount,
    int DanmakuCount,
    int CommentCount);

/// <summary>收进回收站的结果：Ok 为假时 Message 说明为什么没搬成（比如文件正被占用）。</summary>
public sealed record RecycleMoveOutcome(bool Ok, string Message, long BinId, int Files);

/// <summary>从回收站捞回来的结果。</summary>
public sealed record RecycleRestoreOutcome(bool Ok, string Message, int Files, int Rows);

/// <summary>
/// 回收站：删馆藏不再就地焚毁，而是「文件搬走 + 台账封存」。
/// videos / video_meta / pages / artifacts / video_codecs / danmaku_local 六张表的行连主键一起
/// 存进 recycle_bin.manifest，文件按原相对路径挪到 data/recycle 下；原表删干净，所以用户那边是真查无此片
/// （列表、搜索、相关推荐、卡阵都不会再瞄到它），而站长随时能原样恢复——连站内弹幕的 id 都不变。
/// 行用字典通用存取，将来这些表加列也不用动回收站。
/// </summary>
public sealed partial class StationDb
{
    private const string RecycleTable = "recycle_bin";

    private static readonly string[] RecycleRowTables = ["video_meta", "pages", "artifacts", "video_codecs", "danmaku_local"];

    private static readonly string[] RecycleFileExtensions =
        [".mp4", ".mkv", ".flv", ".webm", ".ass", ".xml", ".srt", ".vtt", ".json", ".jpg", ".jpeg", ".png", ".webp", ".gif"];

    private sealed record RecycleFileMove(string Rel, string Stored);

    private sealed record RecycleRowStore(string Table, Dictionary<string, object?> Values);

    private sealed record RecycleManifest(
        int Schema,
        Dictionary<string, object?> Video,
        List<RecycleRowStore> Rows,
        List<RecycleFileMove> Files);

    private static void EnsureRecycleTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {RecycleTable} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                video_id INTEGER NOT NULL,
                bvid TEXT NOT NULL,
                title TEXT,
                up_name TEXT,
                deleted_at_utc TEXT NOT NULL,
                deleted_by TEXT,
                deleted_by_member_id INTEGER,
                file_count INTEGER NOT NULL DEFAULT 0,
                bytes INTEGER NOT NULL DEFAULT 0,
                page_count INTEGER NOT NULL DEFAULT 0,
                danmaku_count INTEGER NOT NULL DEFAULT 0,
                comment_count INTEGER NOT NULL DEFAULT 0,
                manifest TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        // 老库补列：CREATE TABLE IF NOT EXISTS 对已经存在的表一个字节都不动，旧回收站的这一栏得单独补上。
        EnsureColumn(connection, RecycleTable, "deleted_by_member_id", "INTEGER");
    }

    /// <summary>把一条馆藏收进回收站：先点清文件、搬进回收站，再删原表行、落清单。</summary>
    /// <param name="commentCount">评论条数（快照文件的解析归上层，这里只管记账）。</param>
    public RecycleMoveOutcome MoveVideoToRecycle(
        long videoId,
        string videosRoot,
        string recycleRoot,
        string? deletedBy,
        int commentCount,
        long? deletedByMemberId = null)
    {
        using var connection = Open(_dbPath);
        EnsureRecycleTable(connection);

        var video = ReadRows(connection, "SELECT * FROM videos WHERE id = $id;", videoId);
        if (video.Count == 0)
        {
            return new RecycleMoveOutcome(false, "馆藏里没有这个编号", 0, 0);
        }

        var bvid = video[0].TryGetValue("bvid", out var rawBvid) ? rawBvid as string ?? "" : "";
        var title = video[0].TryGetValue("title", out var rawTitle) ? rawTitle as string : null;
        var relPaths = ReadRows(connection, "SELECT rel_path FROM artifacts WHERE video_id = $id;", videoId)
            .Select(r => r.TryGetValue("rel_path", out var v) ? v as string : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();
        var basePaths = ReadRows(connection, "SELECT base_path FROM pages WHERE video_id = $id;", videoId)
            .Select(r => r.TryGetValue("base_path", out var v) ? v as string : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();
        var pageCount = basePaths.Count;
        // 账上记的是站内弹幕条数（danmaku_local）：那才是真正被封进清单、恢复时要回来的行；
        // B 站弹幕住在 ass 文件里，按文件计数，不记在这里。
        var danmakuCount = ListLocalDanmaku(videoId).Count;
        var rows = RecycleRowTables.ToDictionary(table => table, table => ReadRows(connection, $"SELECT * FROM {table} WHERE video_id = $id;", videoId));

        var files = CollectRecycleFiles(videosRoot, relPaths, basePaths);

        // 先落一行拿到 id（回收站目录按 id 分间），搬完再回填清单与账目。
        var insertedId = InsertRecycleShell(connection, videoId, bvid, title, deletedBy, pageCount, danmakuCount, commentCount, deletedByMemberId);
        var staged = new List<(string From, string To)>();
        try
        {
            var payloadRoot = RecyclePayloadDir(recycleRoot, insertedId);
            foreach (var (full, rel) in files)
            {
                var target = Path.Combine(payloadRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(full, target);
                staged.Add((full, target));
            }
        }
        catch (IOException ex)
        {
            // 有文件被播放器/引擎占着：已搬的原样搬回去，这单整笔作废，绝不留半拉子。
            RollbackStaged(staged);
            DeleteRecycleRow(connection, insertedId);
            DeletePayloadQuiet(recycleRoot, insertedId);
            return new RecycleMoveOutcome(false, $"有文件正被占用，没能收进回收站（{ex.Message}）", 0, 0);
        }

        var manifest = new RecycleManifest(
            1,
            video[0],
            RecycleRowTables.SelectMany(table => rows[table].Select(values => new RecycleRowStore(table, values))).ToList(),
            files.Select(file => new RecycleFileMove(file.Rel, file.Rel)).ToList());
        var bytes = staged.Sum(pair => File.Exists(pair.To) ? new FileInfo(pair.To).Length : 0L);
        UpdateRecycleLedger(connection, insertedId, manifest, staged.Count, bytes);

        // 正片目录空了就一并清掉（跟彻底删除一个样，免得留一堆空壳）。
        foreach (var dir in staged.Select(pair => Path.GetDirectoryName(pair.From)).Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            if (dir is not null && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
            {
                try
                {
                    Directory.Delete(dir);
                }
                catch (IOException)
                {
                    // 删不掉就算了
                }
            }
        }

        DeleteVideoRows(connection, videoId);

        // 预览不跟随进回收站：它是 ffmpeg 从成片转出的派生物，可再生、占地不大，回收站不收；
        // 文件与 previews 行就地抹掉，馆藏恢复后什么都不用做——预览补员下一轮发现它欠着，自己再转一段回来。
        DropPreviewForRecycle(connection, videoId);
        // 封面缩略件同理：它们不正片也不进回收站（回收站那位站长是拿 .Cover.jpg 看货的，档位件只是屏上消耗品）。
        // 销账 + 清件一并办完，否则地址还在、盘上没件，墙上就挂一片碎图；恢复后由缩略补员自己切回来。
        DropCoverThumbsForRecycle(connection, videoId, bvid);

        return new RecycleMoveOutcome(true, $"「{bvid}」已收进回收站", insertedId, staged.Count);
    }

    /// <summary>回收站清单，最近删的排前面。</summary>
    public IReadOnlyList<RecycleEntry> ListRecycle()
    {
        using var connection = Open(_dbPath);
        EnsureRecycleTable(connection);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, video_id, bvid, title, up_name, deleted_at_utc, deleted_by, deleted_by_member_id,
                   file_count, bytes, page_count, danmaku_count, comment_count
            FROM {RecycleTable} ORDER BY id DESC;
            """;
        var list = new List<RecycleEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RecycleEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.GetInt32(8),
                reader.GetInt64(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12)));
        }

        return list;
    }

    /// <summary>
    /// 把一件馆藏原样请回分站：文件搬回原位，六张表的行按原主键塞回去。
    /// 文件搬运与台账回写同在一个事务作用域里，中途任何一处炸都整笔作废（行回滚 + 已搬文件退回回收站），
    /// 分站里不会留下「有文件没台账」的半件馆藏。
    /// </summary>
    public RecycleRestoreOutcome RestoreFromRecycle(long id, string videosRoot, string recycleRoot)
    {
        using var connection = Open(_dbPath);
        EnsureRecycleTable(connection);

        RecycleManifest? manifest = null;
        string bvid = "";
        using (var load = connection.CreateCommand())
        {
            load.CommandText = $"SELECT bvid, manifest FROM {RecycleTable} WHERE id = $id;";
            load.Parameters.AddWithValue("$id", id);
            using var reader = load.ExecuteReader();
            if (!reader.Read())
            {
                return new RecycleRestoreOutcome(false, "回收站里没有这一件", 0, 0);
            }

            bvid = reader.GetString(0);
            try
            {
                manifest = JsonSerializer.Deserialize<RecycleManifest>(reader.GetString(1));
            }
            catch (JsonException)
            {
                return new RecycleRestoreOutcome(false, "这一件的清单坏了，捞不回来了（可以直接彻底删掉它）", 0, 0);
            }
        }

        if (manifest?.Video is null)
        {
            return new RecycleRestoreOutcome(false, "这一件的清单缺了馆藏主行，捞不回来了", 0, 0);
        }

        using (var clash = connection.CreateCommand())
        {
            clash.CommandText = "SELECT 1 FROM videos WHERE bvid = $b;";
            clash.Parameters.AddWithValue("$b", bvid);
            if (clash.ExecuteScalar() is not null)
            {
                return new RecycleRestoreOutcome(false, $"「{bvid}」已经回到分站（或有人重新投过），回收站这份就当备份留着吧", 0, 0);
            }
        }

        // SQLite 管不着文件系统，就把搬文件整个放进事务的作用域里：一旦台账回写失败，
        // 已经挪出去的文件还能原路退回回收站。延迟事务（DEFERRED）到第一条写语句才取写锁，
        // 所以搬文件的这段时间不占着库；恢复是站长手点的低频活，锁窗口比老写法（文件全搬完再开事务）还窄。
        var payloadRoot = RecyclePayloadDir(recycleRoot, id);
        var moved = new List<(string From, string To)>();
        var restoredRows = 0;
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var file in manifest.Files)
            {
                var from = Path.Combine(payloadRoot, file.Stored.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(from))
                {
                    continue; // 清单里记了但盘上没有：当它本来就缺，别挡整件恢复
                }

                var to = Path.Combine(videosRoot, file.Rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                // overwrite：上一轮恢复搬到一半炸过、或回滚时那份没搬回去，这一轮得能盖过去接着捞，
                // 否则这一件会永远卡在「文件搬不回去」。回收站里那份才是封存的原件，盖它没错。
                File.Move(from, to, true);
                moved.Add((from, to));
            }

            InsertRow(connection, "videos", manifest.Video);
            restoredRows++;
            foreach (var row in manifest.Rows)
            {
                if (InsertRow(connection, row.Table, row.Values))
                {
                    restoredRows++;
                }
            }

            DeleteRecycleRow(connection, id);
            transaction.Commit();
        }
        catch (Exception ex)
        {
            // Commit 失败时事务还挂在连接上，得先放掉写锁；回滚本身再炸不掩原错（Dispose 会连带回滚）。
            try
            {
                transaction.Rollback();
            }
            catch (SqliteException)
            {
                // 连接都废了，交给 using var connection 的 Dispose
            }

            RollbackStaged(moved);
            restoredRows = 0;

            // 只把「搬不动 / 写不进」这一类当业务失败报给站长，其余异常照原样抛出去：
            // 拿「可能被占用」糊弄真正的 bug，比让它炸出来更误事。
            if (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SqliteException)
            {
                return new RecycleRestoreOutcome(false, $"「{bvid}」没能回到分站，已经搬走的文件都退回回收站了（{ex.Message}）", 0, 0);
            }

            throw;
        }

        DeletePayloadQuiet(recycleRoot, id);
        return new RecycleRestoreOutcome(true, $"「{bvid}」已回到分站，带回 {moved.Count} 个文件", moved.Count, restoredRows);
    }

    /// <summary>彻底销毁回收站里的一件：文件与清单一起没了。</summary>
    public bool PurgeRecycle(long id, string recycleRoot)
    {
        using var connection = Open(_dbPath);
        EnsureRecycleTable(connection);

        DeletePayloadQuiet(recycleRoot, id);
        return DeleteRecycleRow(connection, id) > 0;
    }

    /// <summary>清空回收站，回报清掉几件。</summary>
    public int PurgeAllRecycle(string recycleRoot)
    {
        using var connection = Open(_dbPath);
        EnsureRecycleTable(connection);

        if (Directory.Exists(recycleRoot))
        {
            foreach (var dir in Directory.GetDirectories(recycleRoot))
            {
                DeleteDirectoryQuiet(dir);
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {RecycleTable};";
        return command.ExecuteNonQuery();
    }

    /// <summary>回收站里某件馆藏的封面字节（封面文件已经不在 /media 下，只能隔着站长室的门递出去）。</summary>
    public byte[]? ReadRecycleCover(long id, string recycleRoot)
    {
        using var connection = Open(_dbPath);
        EnsureRecycleTable(connection);

        using var load = connection.CreateCommand();
        load.CommandText = $"SELECT manifest FROM {RecycleTable} WHERE id = $id;";
        load.Parameters.AddWithValue("$id", id);
        if (load.ExecuteScalar() is not string json)
        {
            return null;
        }

        RecycleManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RecycleManifest>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        var payloadRoot = RecyclePayloadDir(recycleRoot, id);
        var cover = manifest?.Files
            .Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(f.Stored), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Stored.Contains(".Cover", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (cover is null)
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(payloadRoot, cover.Stored.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(payloadRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(full);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static List<(string Full, string Rel)> CollectRecycleFiles(string videosRoot, IEnumerable<string> relPaths, IEnumerable<string> basePaths)
    {
        var root = Path.GetFullPath(videosRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var found = new List<(string Full, string Rel)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string full)
        {
            var normalized = Path.GetFullPath(full);
            if (!normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(normalized))
            {
                return;
            }

            var rel = normalized[(root.Length + 1)..].Replace('\\', '/');
            if (seen.Add(normalized))
            {
                found.Add((normalized, rel));
            }
        }

        foreach (var rel in relPaths)
        {
            Add(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        }

        // 产物表之外可能还有未归档的同源文件（重复后缀 (1) 版本、没登记的表情包等），按分 P 基准前缀补扫。
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

            foreach (var file in Directory.GetFiles(directory, baseName + "*"))
            {
                if (RecycleFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    Add(file);
                }
            }
        }

        return found;
    }

    private static string RecyclePayloadDir(string recycleRoot, long id) =>
        Path.Combine(recycleRoot, id.ToString(System.Globalization.CultureInfo.InvariantCulture), "files");

    /// <summary>搬完/销毁后的收尾：文件间连同空了的 id 分间一并抹掉，回收站里不留空壳。</summary>
    private static void DeletePayloadQuiet(string recycleRoot, long id)
    {
        var payload = RecyclePayloadDir(recycleRoot, id);
        DeleteDirectoryQuiet(payload);

        var shell = Path.GetDirectoryName(payload);
        if (shell is not null && Directory.Exists(shell) && Directory.GetFileSystemEntries(shell).Length == 0)
        {
            DeleteDirectoryQuiet(shell);
        }
    }

    private static void RollbackStaged(IReadOnlyList<(string From, string To)> staged)
    {
        for (var i = staged.Count - 1; i >= 0; i--)
        {
            try
            {
                if (File.Exists(staged[i].To) && !File.Exists(staged[i].From))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(staged[i].From)!);
                    File.Move(staged[i].To, staged[i].From);
                }
            }
            catch (IOException)
            {
                // 回滚也搬不动：留给人工，至少清单还在
            }
        }
    }

    private static void DeleteDirectoryQuiet(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (IOException)
        {
            // 占用中的回收站目录留到下次
        }
    }

    private static List<Dictionary<string, object?>> ReadRows(SqliteConnection connection, string sql, long videoId)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static long InsertRecycleShell(
        SqliteConnection connection,
        long videoId,
        string bvid,
        string? title,
        string? deletedBy,
        int pageCount,
        int danmakuCount,
        int commentCount,
        long? deletedByMemberId = null)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO " + RecycleTable +
                " (video_id, bvid, title, up_name, deleted_at_utc, deleted_by, deleted_by_member_id, page_count, danmaku_count, comment_count, manifest)" +
                " VALUES ($video, $bvid, $title, $up, $at, $by, $byId, $pages, $danmaku, $comments, '{}');";
            insert.Parameters.AddWithValue("$video", videoId);
            insert.Parameters.AddWithValue("$bvid", bvid);
            insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            insert.Parameters.AddWithValue("$up", DBNull.Value);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$by", (object?)deletedBy ?? DBNull.Value);
            insert.Parameters.AddWithValue("$byId", (object?)deletedByMemberId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$pages", pageCount);
            insert.Parameters.AddWithValue("$danmaku", danmakuCount);
            insert.Parameters.AddWithValue("$comments", commentCount);
            insert.ExecuteNonQuery();
        }

        using var lastId = connection.CreateCommand();
        lastId.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(lastId.ExecuteScalar());
    }

    private static void UpdateRecycleLedger(SqliteConnection connection, long id, RecycleManifest manifest, int files, long bytes)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {RecycleTable} SET up_name = $up, file_count = $files, bytes = $bytes, manifest = $manifest WHERE id = $id;";
        // 没有 video_meta 行的馆藏（刚投稿还没拉完元信息就被带走）up_name 为空：
        // 这里得送 DBNull，AddWithValue(null) 会让 Microsoft.Data.Sqlite 直接抛「Value must be set」。
        command.Parameters.AddWithValue("$up", (object?)UpNameOf(manifest) ?? DBNull.Value);
        command.Parameters.AddWithValue("$files", files);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$manifest", JsonSerializer.Serialize(manifest));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static string? UpNameOf(RecycleManifest manifest) =>
        manifest.Rows.FirstOrDefault(r => r.Table == "video_meta") is { } meta && meta.Values.TryGetValue("up_name", out var up)
            ? up as string
            : null;

    private static int DeleteRecycleRow(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {RecycleTable} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery();
    }

    /// <summary>删一条馆藏的六张表行（文件不归它管：回收站自己搬）。</summary>
    private static void DeleteVideoRows(SqliteConnection connection, long videoId)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "artifacts", "pages", "video_meta", "video_codecs", "danmaku_local", "videos" })
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = table == "videos"
                ? "DELETE FROM videos WHERE id = $id;"
                : $"DELETE FROM {table} WHERE video_id = $id;";
            delete.Parameters.AddWithValue("$id", videoId);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static bool InsertRow(SqliteConnection connection, string table, Dictionary<string, object?> row)
    {
        if (table == "unknown")
        {
            return false;
        }

        var allowed = TableColumns(connection, table);
        var columns = row.Keys.Where(allowed.Contains).ToList();
        if (columns.Count == 0)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {table} ({string.Join(",", columns.Select(c => "\"" + c + "\""))}) " +
                              $"VALUES ({string.Join(",", columns.Select(c => "@" + c))});";
        foreach (var column in columns)
        {
            var value = row[column];
            if (value is JsonElement element)
            {
                value = JsonValueToDb(element);
            }

            command.Parameters.AddWithValue("@" + column, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
        return true;
    }

    private static HashSet<string> TableColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static object? JsonValueToDb(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole : element.GetDouble(),
        _ => element.GetRawText(),
    };
}
