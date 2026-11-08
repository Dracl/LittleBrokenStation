using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 加档回收站里躺着的一版：一行 = 一个分 P 的一个编码，<b>同一次删除动作共用一个 GroupId</b>
/// （点单是整片点的，删一版 hevc 会落 N 行；捐回按组一次搬齐，不让站长点 N 次）。
/// </summary>
public sealed record CodecRecycleEntry(
    long Id,
    string GroupId,
    long VideoId,
    string? Bvid,
    int PageIdx,
    string Codec,
    string? RelPath,
    long Bytes,
    bool FileStored,
    string DeletedAtUtc,
    string? DeletedBy,
    long? DeletedByMemberId,
    string Source,
    /// <summary>片子还在不在架：不在架时捐不动，前端据此把捐回钮摆灰并说明原因。</summary>
    bool VideoInStation);

/// <summary>收进回收站的结果：Ok 为假时 Message 说明为什么没搬成（占用、还在下载中、指的是主件）。</summary>
public sealed record CodecRecycleMoveOutcome(bool Ok, string Message, string? GroupId, int Rows, int Files);

/// <summary>捐回一组的结果。</summary>
public sealed record CodecRecycleRestoreOutcome(bool Ok, string Message, int Rows, int Files);

/// <summary>
/// 加档版本（额外编码/码率）的回收站：删一版不再「销账 + File.Delete」一步到死，
/// 而是<b>连行带件一起封进 codec_recycle 与 data/codec_recycle</b>，站长随时整组捐回。
/// </summary>
/// <remarks>
/// <b>为什么另开一张表、而不是给 video_codecs 加状态列</b>：
/// ① <c>UNIQUE(video_id, page_idx, codec)</c> 只许一行，僵尸行会让「重新点单同一版」撞唯一约束
///    （AddCodecVersion 只对 Failed 行走 UPDATE 重置，其余一律当「已有」回 false，连报错都不给）；
/// ② 放映厅装配那条 SELECT 不带 status 过滤（<c>StationDb.cs:3448</c>），行在就列档，读侧全得跟着加判据。
/// 理由与 danmaku_recycle 那张表「为什么不挤 videos 那张回收站」同构。
/// <b>为什么文件必须搬走而不是留下</b>：引擎在目标名被占用时直接 throw DownloadOutputCollisionException
/// （DownloadOutputPathResolver.cs:49，本站 RepeatFileAutoAddNumberSuffix=false），而 DownloadWorker 那句
/// 中文判重只认「未完成的同名任务」，认不出它 → 这一版被记成 Failed 且 last_error 是一句英文。
/// 同卷 File.Move 只改目录项，实测 958 MiB 也不翻倍。另开 data/codec_recycle 一座而不挤 recycle，
/// 是因为 PurgeAllRecycle 会把 recycle 下所有子目录扫一遍删掉，两边混住必然账实不符。
/// </remarks>
public sealed partial class StationDb
{
    private const string CodecRecycleTable = "codec_recycle";

    /// <summary>「这一版本就不在账上」那句拒词。公开成常量是给端点分状态码用的：
    /// 查无此版要回 404，而「文件被占用」「指的是主件」要回 409，不能一刷全回 400。</summary>
    public const string CodecRecycleMissingMessage = "没有这个版本";

    /// <summary>
    /// 一版加档的「一族件」：mp4 本体之外还有弹幕、字幕、封面、nfo 这些附属件。
    /// 它们从来没进过 artifacts 表（ClassifyArtifact 认不得带 " - AVC" 编码段的后缀），
    /// 删一版若只删 mp4，剩下这些就在盘上成了没人认领的孤儿——所以按前缀扫一遍一并带走。
    /// </summary>
    private static readonly string[] CodecRecycleSidecarExtensions =
        [".ass", ".xml", ".srt", ".vtt", ".json", ".jpg", ".jpeg", ".png", ".webp", ".nfo"];

    private static void EnsureCodecRecycleTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {CodecRecycleTable} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id TEXT NOT NULL,
                video_id INTEGER NOT NULL,
                bvid TEXT,
                page_idx INTEGER NOT NULL,
                codec TEXT NOT NULL,
                rel_path TEXT,
                bytes INTEGER NOT NULL DEFAULT 0,
                file_stored INTEGER NOT NULL DEFAULT 0,
                row TEXT,
                deleted_at_utc TEXT NOT NULL,
                deleted_by TEXT,
                deleted_by_member_id INTEGER,
                source TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_codec_recycle_group ON codec_recycle(group_id);
            CREATE INDEX IF NOT EXISTS idx_codec_recycle_video ON codec_recycle(video_id);
            """;
        command.ExecuteNonQuery();
    }

    private static string CodecRecycleGroupDir(string recycleRoot, string groupId) =>
        Path.Combine(recycleRoot, groupId, "files");

    /// <summary>
    /// 把某一版加档（一个编码，<paramref name="pageIdx"/> 为空 = 整片该编码的所有分 P）连行带件收进回收站。
    /// <b>一次调用 = 一组</b>，捐回与销毁都按组走。三道安全闸：正在下载中的行拒删（先取消，别让引擎往回收目录里写）；
    /// rel_path 指的是该 P 主件的行拒删（TakeCodecVersionForDelete 原本连这个都不问，主件文件能被一巴掌拍掉）；
    /// 文件被占用时整笔作废，已搬的原路退回，绝不留「账进了回收站、件还在原地」的半吊子。
    /// </summary>
    public CodecRecycleMoveOutcome MoveCodecVersionsToRecycle(
        long videoId, string codec, int? pageIdx,
        string videosRoot, string recycleRoot,
        string? who, long? whoMemberId, string source)
    {
        var wanted = codec.Trim().ToLowerInvariant();
        using var connection = Open(_dbPath);

        var bvid = QueryBvid(connection, videoId);
        var targets = ReadCodecLedgerRows(connection, videoId, wanted, pageIdx);
        if (targets.Count == 0)
        {
            return new CodecRecycleMoveOutcome(false, CodecRecycleMissingMessage, null, 0, 0);
        }

        if (targets.Any(row => string.Equals(ValueOf(row, "status") as string, "Downloading", StringComparison.OrdinalIgnoreCase)))
        {
            return new CodecRecycleMoveOutcome(false, "这一版还在下载中，先取消这次加档再删", null, 0, 0);
        }

        // 主件保护：这一 P 的 local_video_path 与加档行 rel_path 撞同一个文件时不开枪。
        // 站里从没防过这一手（前端藏钮是唯一防线），而删掉主件等于把整部片子那一 P 打空。
        var mainRelByPage = ReadMainRelByPage(connection, videoId);

        var groupId = Guid.NewGuid().ToString("N");
        var staged = new List<(string From, string To)>();
        var stagedFiles = 0;
        try
        {
            foreach (var row in targets)
            {
                var page = Convert.ToInt32(ValueOf(row, "page_idx") ?? 0);
                var rel = ValueOf(row, "rel_path") as string;
                if (!string.IsNullOrWhiteSpace(rel) && mainRelByPage.TryGetValue(page, out var mainRel)
                    && string.Equals(mainRel, rel, StringComparison.OrdinalIgnoreCase))
                {
                    RollbackStaged(staged);
                    return new CodecRecycleMoveOutcome(false, "这一行指的正是那一 P 的主件文件，删它等于把正片拍掉，分站不接", null, 0, 0);
                }

                if (string.IsNullOrWhiteSpace(rel))
                {
                    continue;
                }

                var groupDir = CodecRecycleGroupDir(recycleRoot, groupId);
                foreach (var file in FamilyOf(videosRoot, rel))
                {
                    // 附属件与本体各按自己的相对路径落进回收目录，捐回时照着同一张表反向搬回原位。
                    var relative = RelativeOf(videosRoot, file.FullName);
                    var to = Path.Combine(groupDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(file.FullName, to, overwrite: true);
                    staged.Add((file.FullName, to));
                    stagedFiles++;
                }
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            using var transaction = connection.BeginTransaction();
            foreach (var row in targets)
            {
                var rel = ValueOf(row, "rel_path") as string;
                var movedForRow = CountStagedForRow(staged, rel);
                InsertCodecRecycleRow(connection, transaction, groupId, videoId, bvid, row, rel,
                    rowBytes: SumStagedForRow(staged, rel),
                    stored: movedForRow > 0,
                    now, who, whoMemberId, source);
                DeleteCodecLedgerRow(connection, transaction, Convert.ToInt64(ValueOf(row, "id") ?? 0L));
            }

            transaction.Commit();
            return new CodecRecycleMoveOutcome(true, $"已清出站 {targets.Count} 行、{stagedFiles} 个文件（进了回收站，站长能整组捐回）", groupId, targets.Count, stagedFiles);
        }
        catch (IOException)
        {
            RollbackStaged(staged);
            return new CodecRecycleMoveOutcome(false, "文件正被占用（播放器里可能正在播这一版），先切走再删；这一版一行都没动", null, 0, 0);
        }
        catch
        {
            RollbackStaged(staged);
            throw;
        }
    }

    /// <summary>回收站里的加档版本（新→旧）。给站长室那张卡用，前端按 GroupId 分组摆。</summary>
    public IReadOnlyList<CodecRecycleEntry> ListCodecRecycle(long? videoId, int limit = 400)
    {
        var drafts = new List<CodecRecycleEntry>();
        var videoIds = new List<long>();
        using (var connection = Open(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, group_id, video_id, bvid, page_idx, codec, rel_path, bytes, file_stored,
                       deleted_at_utc, deleted_by, deleted_by_member_id, source
                FROM {CodecRecycleTable}
                WHERE ($v IS NULL OR video_id = $v)
                ORDER BY deleted_at_utc DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$v", (object?)videoId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                drafts.Add(new CodecRecycleEntry(
                    reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8) != 0,
                    reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.GetString(12),
                    // 先占位 false：读侧不能一边开着 reader 一边再开第二条命令，验盘放到 reader 关掉之后。
                    false));
                videoIds.Add(drafts[^1].VideoId);
            }
        }

        // 片子还在不在架：不在架时捐不动，前端据此摆灰钮并说明原因。
        var arrived = new HashSet<long>();
        if (videoIds.Count > 0)
        {
            using var connection = Open(_dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM videos WHERE status = 'Arrived';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                arrived.Add(reader.GetInt64(0));
            }
        }

        return drafts.Select(item => item with { VideoInStation = arrived.Contains(item.VideoId) }).ToList();
    }

    public long CountCodecRecycle()
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {CodecRecycleTable};";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// 整组捐回：一件都缺就整组不捐。四道判据——清单在 → 片子还在 Arrived → 三元组没被新行占 →
    /// 件真在回收目录里。最后那道与整片回收站<b>刻意不同</b>：整片宁可恢复出缺件的片子也不让它永远捞不回，
    /// 而一版加档缺件等于在播放器菜单上摆一档 404，那不如不捐。
    /// </summary>
    public CodecRecycleRestoreOutcome RestoreCodecGroup(string groupId, string videosRoot, string recycleRoot)
    {
        using var connection = Open(_dbPath);
        var rows = ReadCodecGroup(connection, groupId);
        if (rows.Count == 0)
        {
            return new CodecRecycleRestoreOutcome(false, "回收站里没有这一组", 0, 0);
        }

        var videoId = Convert.ToInt64(rows[0]["video_id"] ?? 0L);
        if (!VideoArrived(connection, videoId))
        {
            return new CodecRecycleRestoreOutcome(false, "片子已经不在架（下架或进了整片回收站），先把稿件捞回来再捐这一版", 0, 0);
        }

        foreach (var row in rows)
        {
            var page = Convert.ToInt32(row["page_idx"] ?? 0);
            var codec = Convert.ToString(row["codec"]) ?? "";
            if (CodecRowExists(connection, videoId, page, codec))
            {
                return new CodecRecycleRestoreOutcome(false, $"P{page + 1} 的这一版后来又被人点回来了，先把那一行清掉再捐，别让两条账抢同一个位置", 0, 0);
            }

            var rel = row["rel_path"] as string;
            if (Convert.ToInt64(row["file_stored"] ?? 0L) != 0 && !string.IsNullOrWhiteSpace(rel))
            {
                // 本体必须在；附属件缺一两样（本来就可能没有）不算拦。
                if (!File.Exists(StagedPathFor(recycleRoot, groupId, rel)))
                {
                    return new CodecRecycleRestoreOutcome(false, "回收目录里找不到这一版的文件，捐回去也只是菜单上一档 404，先去看盘还在不在", 0, 0);
                }
            }
        }

        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var row in rows)
            {
                var rel = row["rel_path"] as string;
                if (Convert.ToInt64(row["file_stored"] ?? 0L) == 0 || string.IsNullOrWhiteSpace(rel))
                {
                    continue;
                }

                foreach (var relative in FamilyNames(rel))
                {
                    var from = StagedPathFor(recycleRoot, groupId, relative);
                    if (!File.Exists(from))
                    {
                        continue;
                    }

                    var to = Path.Combine(videosRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to, overwrite: true);
                    moved.Add((from, to));
                }
            }

            using var transaction = connection.BeginTransaction();
            var restored = 0;
            foreach (var row in rows)
            {
                var snapshot = row["row"] as string;
                if (string.IsNullOrWhiteSpace(snapshot))
                {
                    continue;
                }

                if (InsertRow(connection, "video_codecs", ParseSnapshot(snapshot)))
                {
                    restored++;
                }
            }

            using (var purge = connection.CreateCommand())
            {
                purge.CommandText = $"DELETE FROM {CodecRecycleTable} WHERE group_id = $g;";
                purge.Parameters.AddWithValue("$g", groupId);
                purge.ExecuteNonQuery();
            }

            transaction.Commit();
            DeleteDirectoryQuiet(Path.Combine(recycleRoot, groupId));
            return new CodecRecycleRestoreOutcome(true, $"已捞回 {restored} 行、{moved.Count} 个文件，这一版重新回到货架", restored, moved.Count);
        }
        catch (IOException)
        {
            RollbackStaged(moved);
            return new CodecRecycleRestoreOutcome(false, "文件正被占用，没捐回去；这一组还完整躺在回收站里", 0, 0);
        }
        catch
        {
            RollbackStaged(moved);
            throw;
        }
    }

    /// <summary>销毁一组：站里唯一一次真删，一个判据都没有——与整片回收站同一口径，反悔章只留一次机会。</summary>
    public bool PurgeCodecGroup(string groupId, string recycleRoot)
    {
        using var connection = Open(_dbPath);
        DeleteDirectoryQuiet(Path.Combine(recycleRoot, groupId));
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {CodecRecycleTable} WHERE group_id = $g;";
        command.Parameters.AddWithValue("$g", groupId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 危险区「一键清空分站」连带拍空加档回收站：台账行全销 + 回收目录清空。
    /// 与整片回收站 <c>PurgeAllRecycle</c> 同一口径：站都清了，那些封存留着也无人能对号入座；
    /// 而它住在另一座（<see cref="DataPaths.CodecRecycle"/>），故必须点名一声，否则清完站只剩一堆没人认领的件。
    /// </summary>
    public int PurgeAllCodecRecycle(string recycleRoot)
    {
        using var connection = Open(_dbPath);
        DeleteDirectoryQuiet(recycleRoot);
        Directory.CreateDirectory(recycleRoot); // 清完留个空壳：这座本来由 Resolve() 建，不留下次搬件时才现补
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {CodecRecycleTable};";
        return command.ExecuteNonQuery();
    }

    private static string? QueryBvid(SqliteConnection connection, long videoId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bvid FROM videos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() as string;
    }

    private static bool VideoArrived(SqliteConnection connection, long videoId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM videos WHERE id = $id AND status = 'Arrived';";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() is not null;
    }

    private static bool CodecRowExists(SqliteConnection connection, long videoId, int pageIdx, string codec)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM video_codecs WHERE video_id = $id AND page_idx = $page AND codec = $codec;";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$page", pageIdx);
        command.Parameters.AddWithValue("$codec", codec);
        return command.ExecuteScalar() is not null;
    }

    private static List<Dictionary<string, object?>> ReadCodecLedgerRows(
        SqliteConnection connection, long videoId, string codec, int? pageIdx)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM video_codecs WHERE video_id = $id AND codec = $codec" +
                              (pageIdx is null ? ";" : " AND page_idx = $page;");
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$codec", codec);
        if (pageIdx is not null)
        {
            command.Parameters.AddWithValue("$page", pageIdx.Value);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private static List<Dictionary<string, object?>> ReadCodecGroup(SqliteConnection connection, string groupId)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {CodecRecycleTable} WHERE group_id = $g ORDER BY id;";
        command.Parameters.AddWithValue("$g", groupId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private static Dictionary<string, object?> ReadRow(SqliteDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return row;
    }

    private static Dictionary<int, string> ReadMainRelByPage(SqliteConnection connection, long videoId)
    {
        var map = new Dictionary<int, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx, local_video_path FROM pages WHERE video_id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                map[reader.GetInt32(0)] = reader.GetString(1);
            }
        }

        return map;
    }

    private static void InsertCodecRecycleRow(
        SqliteConnection connection, SqliteTransaction transaction, string groupId, long videoId, string? bvid,
        Dictionary<string, object?> row, string? rel, long rowBytes, bool stored, string now,
        string? who, long? whoMemberId, string source)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {CodecRecycleTable}
                (group_id, video_id, bvid, page_idx, codec, rel_path, bytes, file_stored, row,
                 deleted_at_utc, deleted_by, deleted_by_member_id, source)
            VALUES ($g, $v, $b, $p, $c, $rel, $bytes, $stored, $row, $at, $who, $whoId, $source);
            """;
        command.Parameters.AddWithValue("$g", groupId);
        command.Parameters.AddWithValue("$v", videoId);
        command.Parameters.AddWithValue("$b", (object?)bvid ?? DBNull.Value);
        command.Parameters.AddWithValue("$p", Convert.ToInt32(ValueOf(row, "page_idx") ?? 0));
        command.Parameters.AddWithValue("$c", Convert.ToString(ValueOf(row, "codec")) ?? "");
        command.Parameters.AddWithValue("$rel", (object?)rel ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", rowBytes);
        command.Parameters.AddWithValue("$stored", stored ? 1L : 0L);
        command.Parameters.AddWithValue("$row", JsonSerializer.Serialize(row));
        command.Parameters.AddWithValue("$at", now);
        command.Parameters.AddWithValue("$who", (object?)who ?? DBNull.Value);
        command.Parameters.AddWithValue("$whoId", (object?)whoMemberId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", source);
        command.ExecuteNonQuery();
    }

    private static void DeleteCodecLedgerRow(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM video_codecs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, object?> ParseSnapshot(string json)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Clone 之后才离开 document：JsonElement 本体离开已释放的 JsonDocument 就是空壳。
            values[property.Name] = property.Value.Clone();
        }

        return values;
    }

    private static object? ValueOf(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value : null;

    /// <summary>这一行搬走的件数（本体 + 附属件）。零就是盘上本没这一版，<see cref="CodecRecycleEntry.FileStored"/> 据此记假。</summary>
    private static int CountStagedForRow(List<(string From, string To)> staged, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
        {
            return 0;
        }

        var stem = Path.GetFileNameWithoutExtension(rel);
        return staged.Count(entry => entry.From.Contains(stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>那一行搬动的字节数：按它自己那一族在回收目录里的实际大小算。</summary>
    private static long SumStagedForRow(List<(string From, string To)> staged, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
        {
            return 0;
        }

        var stem = Path.GetFileNameWithoutExtension(rel);
        return staged.Where(entry => entry.From.Contains(stem, StringComparison.OrdinalIgnoreCase))
            .Sum(entry => SafeLength(entry.To));
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
    }

    /// <summary>本体 + 那一族的附属件（弹幕、字幕、封面、nfo），只收盘上真有的。</summary>
    private static List<FileInfo> FamilyOf(string videosRoot, string rel)
    {
        var list = new List<FileInfo>();
        var main = new FileInfo(Path.Combine(videosRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!main.Exists)
        {
            return list;
        }

        list.Add(main);
        var directory = main.DirectoryName;
        var baseName = Path.GetFileNameWithoutExtension(main.FullName);
        if (directory is null)
        {
            return list;
        }

        foreach (var path in Directory.GetFiles(directory, baseName + "*"))
        {
            if (!string.Equals(path, main.FullName, StringComparison.OrdinalIgnoreCase)
                && CodecRecycleSidecarExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new FileInfo(path));
            }
        }

        return list;
    }

    /// <summary>捐回时照着搬的那一族相对路径：本体在前，附属件按主干拼出来（盘上没有的会被跳过）。</summary>
    private static List<string> FamilyNames(string rel)
    {
        var list = new List<string> { rel };
        var stem = rel[..^Path.GetExtension(rel).Length];
        foreach (var ext in CodecRecycleSidecarExtensions)
        {
            list.Add(stem + ext);
        }

        return list;
    }

    private static string StagedPathFor(string recycleRoot, string groupId, string relative) =>
        Path.Combine(CodecRecycleGroupDir(recycleRoot, groupId), relative.Replace('/', Path.DirectorySeparatorChar));

    private static string RelativeOf(string root, string full)
    {
        var normalized = Path.GetFullPath(full);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var trimmed = normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : Path.GetFileName(normalized);
        return trimmed.Replace(Path.DirectorySeparatorChar, '/');
    }
}
