using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using LBS.Engine;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LBS.Server;

/// <summary>
/// 数据交换：勾选式导出打包（导出档案柜 data/exports/）+ 暂存式导入（data/imports/，启动时换库）。
/// 包格式 lbs-export：station.db（在线快照，登录态按勾选洗白）+ manifest.json + 可选的媒体/评论图/缓存镜像与登录态文件。
/// 导入走「上传暂存 → 校验 → 应用（自救快照 + 现站保底 + 写 pending 标记）→ 停机 → 下次开机换库」，
/// 换库必须赶在 StationDb 首次打开之前（Program.cs 挂在 builder.Build() 之后第一件事）。
/// </summary>
/// <remarks>
/// 导出勾选里的 WithCreds 是个已失效的历史开关：成员号池（member_accounts）的 cookie 随台账件走，
/// 出包前一律被洗成 revoked-on-export（见 <c>ScrubAndCount</c>），递 true 不再有任何效果；
/// 字段保留只为老导出包/老前端反序列化不报错，并且 manifest 里的 Options 仍能原样回读。
/// </remarks>
public sealed record ExportOptions(bool WithCreds, bool WithMedia, bool WithCmtImg, bool WithCaches, bool WithPreviews = true);

public sealed record ExportManifestFile(string Path, long Bytes);

public sealed record ExportCounts(long Videos, long Members, long Events);

/// <summary>包清单：媒体量大不逐个列，只给 mediaCount/mediaBytes 汇总；files 仅非媒体类。</summary>
public sealed record ExportManifest(
    string Format,
    int Version,
    string StationName,
    string ExportedAtUtc,
    ExportOptions Options,
    ExportCounts Counts,
    IReadOnlyList<ExportManifestFile> Files,
    long MediaCount,
    long MediaBytes);

public sealed record ExportArchiveInfo(
    string Name,
    long Bytes,
    string CreatedAtUtc,
    ExportOptions? Options,
    string? StationName,
    string? ExportedAtUtc);

public sealed record ExportStats(long Videos, long CmtImg, long Avatars, long Emotes, long Cache, long DbBytes, long Previews);

public sealed record ExportRunResult(string Name, long Bytes);

public sealed record ImportPreview(string? StationName, string? ExportedAtUtc, ExportOptions? Options, ExportCounts? Counts);

public sealed record StageOutcome(bool Ok, string? Error, string? Receipt, ImportPreview? Preview, IReadOnlyList<string>? Warnings)
{
    public static StageOutcome Failed(string error) => new(false, error, null, null, null);
}

public sealed record PendingMarker(string Dir);

/// <summary>包名与目录布局的守门人：档案柜只认白名单名，路径花活一律拒。</summary>
public static class DataExchangeLayout
{
    private static readonly Regex ExportName = new(@"^lbs-export-[A-Za-z0-9._-]+-\d{8}-\d{6}\.zip$", RegexOptions.Compiled);
    private static readonly Regex Receipt = new(@"^\d{8}-\d{6}(?:-\d+)?$", RegexOptions.Compiled);

    public static bool IsSafeExportName(string name) => ExportName.IsMatch(name);

    public static bool IsSafeReceipt(string receipt) => Receipt.IsMatch(receipt);

    /// <summary>分站名进文件名：只留 ASCII 字母数字与 - _，清洗后为空回 "station"。</summary>
    public static string SafeStationName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "station";
        }

        var cleaned = new string(raw.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (cleaned.Length == 0)
        {
            return "station";
        }

        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    public static string ExportsDir(DataPaths paths) => Path.Combine(paths.Root, "exports");

    public static string ImportsDir(DataPaths paths) => Path.Combine(paths.Root, "imports");

    public static string PendingFile(DataPaths paths) => Path.Combine(ImportsDir(paths), "pending.json");

    public static string LastErrorFile(DataPaths paths) => Path.Combine(ImportsDir(paths), "last-error.log");

    public static string PreImportDir(DataPaths paths, string stamp) => Path.Combine(paths.Root, "pre-import", stamp);

    // （原 AltLoginFile / DownkyiLoginFile 两个路径随站级主备号一起退役：分站不再存任何
    // 站级 B 站登录态，成员号池的 cookie 本就在台账件 member_accounts 里，跟着 station.db 一起备份与换库。）
}

/// <summary>导出打包员：LedgerBackup 拍快照 → 快照洗白 → 流式 zip 进档案柜。</summary>
public sealed class StationExporter(DataPaths paths, LedgerBackup backup, StationDb db, ILogger<StationExporter> logger)
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ManifestReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Tally
    {
        public long Count;
        public long Bytes;
    }

    public ExportRunResult Run(ExportOptions options)
    {
        Directory.CreateDirectory(DataExchangeLayout.ExportsDir(paths));

        // 必含件：站库走在线快照（写盘中也能拍），拍完就地洗白，不动现站分毫。
        var snapshotName = backup.Run();
        var snapshot = Path.Combine(paths.Root, "backups", snapshotName);
        var zipPath = "";
        try
        {
            var counts = ScrubAndCount(snapshot);
            // 洗白用的是读写连接，连接池会替快照扣着句柄——不清掉，后面打包读不动它
            SqliteConnection.ClearAllPools();
            var stationName = DataExchangeLayout.SafeStationName(db.GetSetting("station-name"));
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var zipName = $"lbs-export-{stationName}-{stamp}.zip";
            zipPath = Path.Combine(DataExchangeLayout.ExportsDir(paths), zipName);

            var files = new List<ExportManifestFile>();
            var tally = new Tally();
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddFile(zip, snapshot, "station.db", files);
                // 登录态不再入包：分站已无站级 B 站 cookie，成员号池（member_accounts）跟着台账件走，
                // 出包前已被洗成 revoked-on-export，不存在「一份 cookie 跟着包满天飞」的老风险。
                // options.WithCreds 这个开关就此失去意义（前端勾选项已拆，写死洗于 ScrubAndCount），留着只为老导入包认得这个字段。

                if (options.WithMedia)
                {
                    AddTree(zip, paths.Videos, "media/", tally);
                }

                if (options.WithCmtImg)
                {
                    AddTree(zip, paths.CmtImg, "cmtimg/", tally);
                }

                if (options.WithCaches)
                {
                    // 三座缓存并进 caches/ 下，保留子目录名：导入时按名字分回原座。
                    AddTree(zip, paths.Avatars, "caches/avatars/", tally);
                    AddTree(zip, paths.Emotes, "caches/emotes/", tally);
                    AddTree(zip, paths.Cache, "caches/cache/", tally);
                    // UP 历史脸（周巡脸员的存件）同属缓存一族：台账（up_faces）跟着 station.db 走，件就得跟着 caches 走，
                    // 两边分家导入就会把对不上号的幽灵地址递给人家。
                    AddTree(zip, paths.UpFaces, "caches/up_faces/", tally);
                }

                if (options.WithPreviews)
                {
                    // 预览成片单独一勾：它是派生物，有人想瘦包就只把它去掉，不必连着三座缓存一起丢。
                    AddTree(zip, paths.Previews, "previews/", tally);
                }

                var manifest = new ExportManifest(
                    "lbs-export", 1, stationName, DateTimeOffset.UtcNow.ToString("O"),
                    options, counts, files, tally.Count, tally.Bytes);
                var entry = zip.CreateEntry("manifest.json");
                using var stream = entry.Open();
                JsonSerializer.Serialize(stream, manifest, ManifestJson);
            }

            var bytes = new FileInfo(zipPath).Length;
            logger.LogInformation("数据交换出包 {Name}（{Kb} KB：台账件 {Files}、媒体件 {Media}）", zipName, bytes / 1024, files.Count, tally.Count);

            // 打包成功临时快照就退场，档案柜归周期备份员管；失败时留着，它仍是有效备份。
            TryDelete(snapshot);
            return new ExportRunResult(zipName, bytes);
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }
    }

    public IReadOnlyList<ExportArchiveInfo> List()
    {
        var dir = DataExchangeLayout.ExportsDir(paths);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var list = new List<ExportArchiveInfo>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (!DataExchangeLayout.IsSafeExportName(name))
            {
                continue;
            }

            var manifest = TryReadManifest(file);
            list.Add(new ExportArchiveInfo(
                name,
                new FileInfo(file).Length,
                new DateTimeOffset(File.GetCreationTimeUtc(file), TimeSpan.Zero).ToString("O"),
                manifest?.Options,
                manifest?.StationName,
                manifest?.ExportedAtUtc));
        }

        return list.OrderByDescending(x => x.Name, StringComparer.Ordinal).ToList();
    }

    public ExportStats GetStats() => new(
        DirBytes(paths.Videos),
        DirBytes(paths.CmtImg),
        DirBytes(paths.Avatars),
        DirBytes(paths.Emotes),
        DirBytes(paths.Cache),
        File.Exists(paths.DatabaseFile) ? new FileInfo(paths.DatabaseFile).Length : 0,
        DirBytes(paths.Previews));

    /// <summary>销包：只认包名白名单，路径花活一律拒。</summary>
    public bool Delete(string name)
    {
        if (!DataExchangeLayout.IsSafeExportName(name))
        {
            return false;
        }

        var file = Path.Combine(DataExchangeLayout.ExportsDir(paths), name);
        if (!File.Exists(file))
        {
            return false;
        }

        File.Delete(file);
        return true;
    }

    /// <summary>快照洗白：会话整表作废、成员号池的 cookie 一律洗成固定串（列 NOT NULL 不能置 NULL），顺带从快照查 counts。cookie 那一手不给开关：客户端在导出请求体里递什么 withCreds 都不能把全站成员的 B 站登录态（SESSDATA/bili_jct）原样带进 zip。</summary>
    private static ExportCounts ScrubAndCount(string snapshot)
    {
        using var connection = new SqliteConnection($"Data Source={snapshot};Default Timeout=15");
        connection.Open();
        using (var wipe = connection.CreateCommand())
        {
            wipe.CommandText = "DELETE FROM member_sessions;";
            wipe.ExecuteNonQuery();
            // 无条件洗：导出包会被拿去分享/备份/换机，cookie 跟着包走就是拿全站成员的号做人情。
            wipe.CommandText = "UPDATE member_accounts SET cookie = 'revoked-on-export';";
            wipe.ExecuteNonQuery();
        }

        return new ExportCounts(
            ScalarLong(connection, "SELECT COUNT(*) FROM videos;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM members;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM events;"));
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void AddFile(ZipArchive zip, string source, string entryName, List<ExportManifestFile> files)
    {
        zip.CreateEntryFromFile(source, entryName, CompressionLevel.Fastest);
        files.Add(new ExportManifestFile(entryName, new FileInfo(source).Length));
    }

    private static void AddTree(ZipArchive zip, string root, string prefix, Tally tally)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, prefix + relative, CompressionLevel.Fastest);
            tally.Count++;
            tally.Bytes += new FileInfo(file).Length;
        }
    }

    private ExportManifest? TryReadManifest(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            return JsonSerializer.Deserialize<ExportManifest>(stream, ManifestReadJson);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // 包坏了也别把清单页拖倒：这行照常列出，manifest 字段给 null
            logger.LogWarning(ex, "档案柜里的 {Zip} 读不出 manifest", Path.GetFileName(zipPath));
            return null;
        }
    }

    private static long DirBytes(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
            // 删不动交给下一轮巡班
        }
    }
}

/// <summary>
/// 导入暂存员：解包到 data/imports/{receipt}/ 并过三关——
/// zip slip（条目名含 ../ 或绝对路径整包拒）、manifest 格式/版本、station.db 的 PRAGMA integrity_check。
/// 任何一关没过：删暂存目录，只回 {error}。
/// </summary>
public static class ImportStager
{
    private static readonly JsonSerializerOptions ManifestReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>条目名安检：拒绝对路径、盘符/备用数据流（冒号）与任何 .. 段。</summary>
    public static bool IsSafeEntryName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return false;
        }

        if (Path.IsPathRooted(fullName) || fullName.Contains(':'))
        {
            return false;
        }

        foreach (var segment in fullName.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    public static StageOutcome Stage(string zipPath, DataPaths paths, Func<string> receiptFactory)
    {
        if (!File.Exists(zipPath))
        {
            return StageOutcome.Failed("包文件不存在");
        }

        var receipt = receiptFactory();
        var staging = Path.Combine(DataExchangeLayout.ImportsDir(paths), receipt);
        try
        {
            Directory.CreateDirectory(staging);
            var rootFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Length == 0 && entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue; // 目录条目交给 ExtractToFile 前的 CreateDirectory 顺带处理
                    }

                    if (!IsSafeEntryName(entry.FullName))
                    {
                        Discard(staging);
                        return StageOutcome.Failed($"包里有非法路径条目，整包拒收：{entry.FullName}");
                    }

                    var target = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(rootFull, StringComparison.Ordinal))
                    {
                        Discard(staging);
                        return StageOutcome.Failed($"包条目想越狱出暂存区，整包拒收：{entry.FullName}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            var manifestPath = Path.Combine(staging, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                Discard(staging);
                return StageOutcome.Failed("包里缺 manifest.json");
            }

            ExportManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(manifestPath), ManifestReadJson);
            }
            catch (JsonException)
            {
                manifest = null;
            }

            if (manifest is null || manifest.Format != "lbs-export" || manifest.Version != 1)
            {
                Discard(staging);
                return StageOutcome.Failed("manifest 格式或版本不认（只收 format=lbs-export / version=1）");
            }

            var dbFile = Path.Combine(staging, "station.db");
            if (!File.Exists(dbFile))
            {
                Discard(staging);
                return StageOutcome.Failed("包里缺 station.db");
            }

            if (!IntegrityOk(dbFile))
            {
                Discard(staging);
                return StageOutcome.Failed("station.db 的 integrity_check 没过，包已损坏");
            }

            var warnings = new List<string>();
            if (manifest.Options is null || !manifest.Options.WithMedia)
            {
                warnings.Add("媒体未随包，导入后缺件视频将自动回炉重下");
            }

            // 无条件提醒：包内 cookie 已被洗成 revoked-on-export（与 manifest 里那个失效的 withCreds 无关），
            // 不提醒就会有人以为导回来就能直接拿成员的号去跑 B 站。
            warnings.Add("成员需重新扫码登录");

            return new StageOutcome(true, null, receipt,
                new ImportPreview(manifest.StationName, manifest.ExportedAtUtc, manifest.Options, manifest.Counts), warnings);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or SqliteException)
        {
            Discard(staging);
            return StageOutcome.Failed($"解包没成：{ex.Message}");
        }
    }

    private static bool IntegrityOk(string dbFile)
    {
        try
        {
            // 探针连接不入池：否则池子扣着句柄，校验失败后的 Discard 删不动暂存架
            using var connection = new SqliteConnection($"Data Source={dbFile};Default Timeout=15;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return command.ExecuteScalar() as string == "ok";
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static void Discard(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
        catch (IOException)
        {
            // 删不动的残架下一单同名暂存会覆盖重建
        }
    }
}

/// <summary>
/// 导入应用员：自救快照 → 现站（台账 + 两份登录态文件）移入 data/pre-import/{stamp}/ 保底 → 写 pending 标记。
/// 真正换库在下次开机（PendingImportSwapper）；停机由端点拿 IHostApplicationLifetime 延迟触发，响应先发出去。
/// </summary>
public static class ImportApplier
{
    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static (bool Ok, string? Error) Apply(string receipt, DataPaths paths, LedgerBackup backup)
    {
        if (!DataExchangeLayout.IsSafeReceipt(receipt))
        {
            return (false, "回执格式不认");
        }

        var staging = Path.Combine(DataExchangeLayout.ImportsDir(paths), receipt);
        if (!File.Exists(Path.Combine(staging, "station.db")))
        {
            return (false, "暂存区里没有 station.db（回执不对，或重启后被清过）");
        }

        // ① 换库前先给现站拍一张自救快照，炸了还能回档
        backup.Run();

        // ② 现站保底：台账（连同 -wal/-shm）整体移进 pre-import，不搞丢
        var keep = DataExchangeLayout.PreImportDir(paths, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(keep);
        SqliteConnection.ClearAllPools(); // 尽量松开连接池扣着的句柄再搬（同卷移动，开着的句柄也拦不住重命名）
        MoveForKeep(paths.DatabaseFile, Path.Combine(keep, "station.db"));
        MoveForKeep(paths.DatabaseFile + "-wal", Path.Combine(keep, "station.db-wal"));
        MoveForKeep(paths.DatabaseFile + "-shm", Path.Combine(keep, "station.db-shm"));

        // ③ 落换库标记，等开机消费
        File.WriteAllText(DataExchangeLayout.PendingFile(paths), JsonSerializer.Serialize(new PendingMarker(staging), MarkerJson));
        return (true, null);
    }

    private static void MoveForKeep(string source, string target)
    {
        if (File.Exists(source))
        {
            File.Move(source, target, overwrite: true);
        }
    }
}

/// <summary>
/// 开机换库员：Program.cs 在 StationDb 首次打开之前调 ApplyIfNeeded。
/// 拿暂存包的 station.db 换掉台账、回写登录态、media/cmtimg/caches 合并解盘、清引擎任务台账；
/// 无论成败最后都删标记——失败记进 last-error.log 给卡上看，绝不带着标记死循环重启。
/// </summary>
public static class PendingImportSwapper
{
    private static readonly JsonSerializerOptions PendingReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool ApplyIfNeeded(DataPaths paths, ILogger logger)
    {
        var pending = DataExchangeLayout.PendingFile(paths);
        if (!File.Exists(pending))
        {
            return false;
        }

        string? staging = null;
        var swapped = false;
        try
        {
            // 标记是 camelCase 写的，读也必须容得下大小写（早期默认区分大小写把 Dir 读成 null，误报「暂存区已丢失」）
            staging = JsonSerializer.Deserialize<PendingMarker>(File.ReadAllText(pending), PendingReadJson)?.Dir;
            if (string.IsNullOrWhiteSpace(staging) || !Directory.Exists(staging) || !File.Exists(Path.Combine(staging, "station.db")))
            {
                throw new DirectoryNotFoundException("暂存区已丢失，无从换库");
            }

            // ① 台账换芯：拷贝即换，旧的 -wal/-shm 一并作废（新库不带陈 WAL）
            File.Copy(Path.Combine(staging, "station.db"), paths.DatabaseFile, overwrite: true);
            foreach (var tail in new[] { "-wal", "-shm" })
            {
                var stale = paths.DatabaseFile + tail;
                if (File.Exists(stale))
                {
                    File.Delete(stale);
                }
            }

            // ② 登录态不再回写：分站已不存站级 B 站 cookie，成员号池跟着台账一起换芯（见 member_accounts）。

            // ③ 媒体与缓存合并解盘（覆盖同名，不碰包里没带的存量）；预览成片同属派生物，跟着一起回座
            CopyMerge(Path.Combine(staging, "media"), paths.Videos);
            CopyMerge(Path.Combine(staging, "cmtimg"), paths.CmtImg);
            CopyMerge(Path.Combine(staging, "caches", "avatars"), paths.Avatars);
            CopyMerge(Path.Combine(staging, "caches", "emotes"), paths.Emotes);
            CopyMerge(Path.Combine(staging, "caches", "cache"), paths.Cache);
            CopyMerge(Path.Combine(staging, "caches", "up_faces"), paths.UpFaces); // UP 历史脸回座（台账在 station.db 里，件得跟着一起换芯）
            CopyMerge(Path.Combine(staging, "previews"), paths.Previews);

            // ④ 引擎任务台账清掉：新库的行在引擎眼里是生面孔，不清会被「已存在」误拦（也正好躲开回炉判重撞车）
            var engineRows = LbsEnginePurge.PurgeEngineStorage(paths.Downkyi);
            swapped = true;
            logger.LogInformation("数据交换换库完成：台账已换芯，登录态与媒体/缓存已就位，引擎台账清了 {Rows} 行", engineRows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "数据交换换库失败");
            try
            {
                Directory.CreateDirectory(DataExchangeLayout.ImportsDir(paths));
                File.WriteAllText(DataExchangeLayout.LastErrorFile(paths), $"[{DateTimeOffset.UtcNow:O}] 换库失败：{ex}");
            }
            catch (IOException)
            {
                // 连错误条都写不进也无妨，标记照删，不让分站陷在重启循环里
            }
        }
        finally
        {
            try
            {
                File.Delete(pending);
            }
            catch (IOException)
            {
                // 删不动标记下个开机周期会再试一次——但这是文件被扣的极端情形，不是失败循环
            }

            if (!string.IsNullOrWhiteSpace(staging))
            {
                DeleteWithRetry(staging);
            }
        }

        return swapped;
    }

    /// <summary>Windows 上刚落盘的件会被杀软/索引服务短暂扣着，删不动重试几拍再放手（LedgerBackup.Prune 同款套路）。</summary>
    private static void DeleteWithRetry(string dir)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                // 残架留给下一单同名暂存覆盖重建
                return;
            }
        }
    }

    private static void SwapFileInto(string source, string target)
    {
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private static void CopyMerge(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

/// <summary>
/// 缺件回炉扫：对完成态视频查磁盘实物——有 artifacts 的按 rel_path 查存在，
/// 一行 artifact 都没有的按 bvid 目录 videos/{bvid}* 兜底；缺件退回 Pending 交给 DownloadWorker 重投。
/// 只在导入换库的那次开机跑（闸门见 <see cref="MediaReconcileGate">），普通开机绝不点名——
/// 否则视频仓挂载瞬断一次，满仓健康件就被误判缺件、整仓误拉重下（引擎台账已随换库清过，不会撞 SkippedDuplicate）。
/// </summary>
/// <summary>闸门开合由 Program 在开机换库后拨闩。</summary>
public sealed class MediaReconcileGate
{
    public bool ReconcileOnBoot { get; set; }
}

public static class MissingMediaReconciler
{
    public static IReadOnlyList<long> Reconcile(StationDb db, DataPaths paths, ILogger? logger)
    {
        var missing = new List<long>();
        foreach (var (id, bvid) in db.GetArrivedVideosForMediaScan())
        {
            if (IsMediaMissing(db, paths, id, bvid))
            {
                missing.Add(id);
            }
        }

        if (missing.Count == 0)
        {
            return missing;
        }

        foreach (var id in missing)
        {
            db.ResetToPending(id);
        }

        logger?.LogInformation("数据交换缺件点名：{Count} 个到站视频磁盘缺件，已回炉重下", missing.Count);
        return missing;
    }

    /// <summary>
    /// 单条版缺件判定（开机全库扫与「重拉封面与媒体」共用这一张嘴）：有产物行就逐条验盘，
    /// 一条产物行都没有才退到按 bvid 目录兼底。预览行天生不在 artifacts 表里（另存 previews），
    /// 以它不会被算进缺件——这条口径由 <see cref="StationDb"/> 的预览台账划界钉着，别往 artifacts 里捣预览。
    /// </summary>
    public static bool IsMediaMissing(StationDb db, DataPaths paths, long videoId, string bvid)
    {
        var relPaths = db.GetArtifactRelPaths(videoId);
        return relPaths.Count > 0
            ? relPaths.Any(rel => !PhysicalExists(paths.Videos, rel))
            : !HasBvidDirectory(paths.Videos, bvid);
    }

    /// <summary>rel_path 相对 data/videos 解析；想越狱出视频库的一律按「缺件」办（不假装看得见）。</summary>
    private static bool PhysicalExists(string videosRoot, string relPath)
    {
        var rootFull = Path.GetFullPath(videosRoot) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(videosRoot, relPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        return full.StartsWith(rootFull, StringComparison.Ordinal) && File.Exists(full);
    }

    private static bool HasBvidDirectory(string videosRoot, string bvid)
    {
        if (!Directory.Exists(videosRoot) || string.IsNullOrWhiteSpace(bvid) || bvid.Contains('*') || bvid.Contains('?'))
        {
            return false;
        }

        return Directory.EnumerateDirectories(videosRoot, bvid + "*").Any();
    }
}
