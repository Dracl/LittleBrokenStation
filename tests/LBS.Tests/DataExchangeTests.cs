using System.IO.Compression;
using System.Text.Json;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LBS.Tests;

/// <summary>
/// 数据交换：导出包 manifest 往返、快照洗白（cookie 洗成固定串 + 会话清空）、
/// 导入安检（zip slip 整包拒、integrity_check 拒收、包名/回执白名单）与开机换库全流程。
/// </summary>
public sealed class DataExchangeTests : IDisposable
{
    private readonly string _root;
    private readonly DataPaths _paths;
    private readonly StationDb _db;
    private readonly LedgerBackup _backup;
    private readonly StationExporter _exporter;

    public DataExchangeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lbs-exchange-" + Guid.NewGuid().ToString("N"));
        _paths = new DataPaths(
            _root,
            Path.Combine(_root, "videos"),
            Path.Combine(_root, "downkyi"),
            Path.Combine(_root, "cookies"),
            Path.Combine(_root, "cache"));
        foreach (var dir in new[] { _paths.Root, _paths.Videos, _paths.Downkyi, _paths.Cookies, _paths.Cache, _paths.Avatars, _paths.Emotes, _paths.CmtImg })
        {
            Directory.CreateDirectory(dir);
        }

        _db = new StationDb(_paths);
        _db.Initialize();
        _backup = new LedgerBackup(_paths, _db, NullLogger<LedgerBackup>.Instance);
        _exporter = new StationExporter(_paths, _backup, _db, NullLogger<StationExporter>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录留给操作系统
        }
    }

    // ─── 播种 ───

    private void SeedStation(string cookie = "SESSDATA=secret-cookie")
    {
        _db.AddVideosBulk([new NewVideo("https://www.bilibili.com/video/BV1EXPORT01", "BV1EXPORT01", "Export Item")]);
        var gate = new MemberGate(_db);
        var outcome = MemberEndpoints.AdmitMember(_db, gate, new BilibiliIdentity(777L, "成员", null), invite: null, cookie);
        Assert.NotNull(outcome.Token);
    }

    private void SeedMediaFiles()
    {
        var videoDir = Directory.CreateDirectory(Path.Combine(_paths.Videos, "BV1EXPORT01_Export Item")).FullName;
        File.WriteAllText(Path.Combine(videoDir, "p1.mp4"), "video-bytes");
        File.WriteAllText(Path.Combine(_paths.CmtImg, "cmt.png"), "png-bytes");
        File.WriteAllText(Path.Combine(_paths.Avatars, "a.jpg"), "av");
        Directory.CreateDirectory(Path.Combine(_paths.Downkyi, "Config"));
        File.WriteAllText(DataExchangeLayout.DownkyiLoginFile(_paths), """{"cookies":"owner"}""");
        File.WriteAllText(DataExchangeLayout.AltLoginFile(_paths), """{"Cookie":"alt"}""");
    }

    private string ZipPath(string name) => Path.Combine(_root, "exports", name);

    private static ExportManifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonSerializer.Deserialize<ExportManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private string ExtractPackedDb(string zipPath)
    {
        var target = Path.Combine(_root, "pack-station.db");
        using var zip = ZipFile.OpenRead(zipPath);
        zip.GetEntry("station.db")!.ExtractToFile(target, overwrite: true);
        return target;
    }

    private static List<string> QueryStrings(string dbFile, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static long QueryCount(string dbFile, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void AddManifestEntry(ZipArchive zip)
    {
        var manifest = new ExportManifest(
            "lbs-export", 1, "station", DateTimeOffset.UtcNow.ToString("O"),
            new ExportOptions(false, false, false, false), new ExportCounts(0, 0, 0), [], 0, 0);
        var entry = zip.CreateEntry("manifest.json");
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    // ─── 导出 ───

    [Fact]
    public void Export_ManifestRoundTrip()
    {
        SeedStation();
        SeedMediaFiles();

        var result = _exporter.Run(new ExportOptions(true, true, true, true));

        Assert.True(DataExchangeLayout.IsSafeExportName(result.Name));
        Assert.True(result.Bytes > 0);
        var zipPath = ZipPath(result.Name);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("station.db", names);
            Assert.Contains("manifest.json", names);
            Assert.Contains("cookies/alt-login.json", names);
            Assert.Contains("login/downkyi-login.json", names);
            Assert.Contains("media/BV1EXPORT01_Export Item/p1.mp4", names);
            Assert.Contains("cmtimg/cmt.png", names);
            Assert.Contains("caches/avatars/a.jpg", names);
        }

        var manifest = ReadManifest(zipPath);
        Assert.Equal("lbs-export", manifest.Format);
        Assert.Equal(1, manifest.Version);
        Assert.Equal("station", manifest.StationName); // station-name KV 未设 → 清洗回默认
        Assert.False(string.IsNullOrWhiteSpace(manifest.ExportedAtUtc));
        Assert.Equal(1, manifest.Counts.Videos);
        Assert.Equal(1, manifest.Counts.Members);
        Assert.True(manifest.Counts.Events >= 0);
        Assert.NotNull(manifest.Options);
        Assert.True(manifest.Options.WithCreds);
        Assert.True(manifest.Options.WithMedia);
        // files 只列非媒体类：station.db 在册，媒体件走汇总
        Assert.Contains(manifest.Files, f => f.Path == "station.db" && f.Bytes > 0);
        Assert.DoesNotContain(manifest.Files, f => f.Path.StartsWith("media/", StringComparison.Ordinal));
        Assert.Equal(3, manifest.MediaCount);
        Assert.True(manifest.MediaBytes > 0);
    }

    [Fact]
    public void Export_WithoutCreds_ScrubsCookiesAndWipesSessions()
    {
        SeedStation();

        var result = _exporter.Run(new ExportOptions(false, false, false, false));

        var packDb = ExtractPackedDb(ZipPath(result.Name));
        // 包内 cookie 洗成固定串（列 NOT NULL 不置 NULL），会话整表清空
        Assert.Equal(["revoked-on-export"], QueryStrings(packDb, "SELECT DISTINCT cookie FROM member_accounts;").OrderBy(x => x).ToArray());
        Assert.Equal(0, QueryCount(packDb, "SELECT COUNT(*) FROM member_sessions;"));
        // 现站分毫未动：cookie 与会话都在
        Assert.Equal(["SESSDATA=secret-cookie"], QueryStrings(_paths.DatabaseFile, "SELECT DISTINCT cookie FROM member_accounts;"));
        Assert.True(QueryCount(_paths.DatabaseFile, "SELECT COUNT(*) FROM member_sessions;") >= 1);
    }

    [Fact]
    public void Export_WithCreds_KeepsCookiesButStillWipesSessions()
    {
        SeedStation();
        SeedMediaFiles();

        var result = _exporter.Run(new ExportOptions(true, false, false, false));

        var zipPath = ZipPath(result.Name);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.NotNull(zip.GetEntry("cookies/alt-login.json"));
            Assert.NotNull(zip.GetEntry("login/downkyi-login.json"));
        }

        var packDb = ExtractPackedDb(zipPath);
        // 凭证随包：cookie 原样；会话无论如何都作废
        Assert.Equal(["SESSDATA=secret-cookie"], QueryStrings(packDb, "SELECT DISTINCT cookie FROM member_accounts;"));
        Assert.Equal(0, QueryCount(packDb, "SELECT COUNT(*) FROM member_sessions;"));
    }

    // ─── 导入安检 ───

    [Fact]
    public void Import_ZipSlip_IsRejectedWholePackage()
    {
        SeedStation();
        var snapshot = _backup.Run();
        var zipPath = Path.Combine(_root, "slip.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(_root, "backups", snapshot), "station.db");
            AddManifestEntry(zip);
            var evil = zip.CreateEntry("../evil.txt");
            using var stream = evil.Open();
            using var writer = new StreamWriter(stream);
            writer.Write("pwned");
        }

        var outcome = ImportStager.Stage(zipPath, _paths, () => "20261027-010203");

        Assert.False(outcome.Ok);
        Assert.Contains("非法路径", outcome.Error);
        // 暂存目录整体销毁，越狱文件没落上盘
        Assert.False(Directory.Exists(Path.Combine(_root, "imports", "20261027-010203")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil.txt")));
    }

    [Fact]
    public void Import_CorruptDb_IsRejectedByIntegrityCheck()
    {
        var zipPath = Path.Combine(_root, "rotten.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("station.db");
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.Write("这绝不是一份 SQLite 数据库。这绝不是一份 SQLite 数据库。");
            }

            AddManifestEntry(zip);
        }

        var outcome = ImportStager.Stage(zipPath, _paths, () => "20261027-010204");

        Assert.False(outcome.Ok);
        Assert.Contains("integrity_check", outcome.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "imports", "20261027-010204")));
    }

    [Theory]
    // 白名单放行：规范包名
    [InlineData("lbs-export-station-20261027-010203.zip", true)]
    [InlineData("lbs-export-LBS_01-x-20261027-010203.zip", true)]
    // 路径花活与非法字符一律拒
    [InlineData("../lbs-export-station-20261027-010203.zip", false)]
    [InlineData("..\\lbs-export-station-20261027-010203.zip", false)]
    [InlineData("sub/lbs-export-station-20261027-010203.zip", false)]
    [InlineData("lbs-export-sta!on-20261027-010203.zip", false)]
    [InlineData("station-20261027-010203.db", false)]
    [InlineData("lbs-export-station-2026102-010203.zip", false)]
    [InlineData("lbs-export-station-20261027-01020.zip", false)]
    public void ExportName_Whitelist(string name, bool allowed)
    {
        Assert.Equal(allowed, DataExchangeLayout.IsSafeExportName(name));
    }

    [Theory]
    [InlineData("station.db", true)]
    [InlineData("media/BV1EXPORT01/p1.mp4", true)]
    [InlineData("caches/avatars/a.jpg", true)]
    [InlineData("../evil.txt", false)]
    [InlineData("..\\evil.txt", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:\\evil", false)]
    [InlineData("media/../../evil", false)]
    [InlineData("a\\..\\b", false)]
    public void EntryName_Guard(string entryName, bool allowed)
    {
        Assert.Equal(allowed, ImportStager.IsSafeEntryName(entryName));
    }

    [Theory]
    [InlineData(null, "station")]
    [InlineData("", "station")]
    [InlineData("我的分站", "station")]
    [InlineData("LBS 01-x!", "LBS01-x")]
    public void StationName_Sanitized(string? raw, string expected)
    {
        Assert.Equal(expected, DataExchangeLayout.SafeStationName(raw));
    }

    // ─── 应用与开机换库 ───

    [Fact]
    public void Import_ApplyThenStartupSwap_ReplacesLedgerAndKeepsBackup()
    {
        SeedStation();
        SeedMediaFiles();
        var export = _exporter.Run(new ExportOptions(true, true, true, true));
        var upload = Path.Combine(_root, "upload.zip");
        File.Copy(ZipPath(export.Name), upload);

        var staged = ImportStager.Stage(upload, _paths, () => "20261027-120000");
        Assert.True(staged.Ok, staged.Error);
        // 暂存区里该有的件先点名（解包环节自证）
        Assert.True(File.Exists(Path.Combine(_root, "imports", "20261027-120000", "station.db")), "staged station.db");
        Assert.True(File.Exists(Path.Combine(_root, "imports", "20261027-120000", "cookies", "alt-login.json")), "staged alt-login");
        Assert.True(File.Exists(Path.Combine(_root, "imports", "20261027-120000", "login", "downkyi-login.json")), "staged downkyi-login");

        // 非法回执拒应用
        var (badOk, _) = ImportApplier.Apply("../20261027-120000", _paths, _backup);
        Assert.False(badOk);

        var (ok, error) = ImportApplier.Apply("20261027-120000", _paths, _backup);
        Assert.True(ok, error);
        // 标记已落、现站台账与登录态已移入 pre-import 保底
        Assert.True(File.Exists(DataExchangeLayout.PendingFile(_paths)));
        Assert.False(File.Exists(_paths.DatabaseFile));
        Assert.False(File.Exists(DataExchangeLayout.AltLoginFile(_paths)));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "pre-import"), "*", SearchOption.AllDirectories));

        // 开机换库：台账换芯、登录态回位、标记与暂存清空
        PendingImportSwapper.ApplyIfNeeded(_paths, NullLogger.Instance);

        var errFile = DataExchangeLayout.LastErrorFile(_paths);
        Assert.False(File.Exists(errFile), File.Exists(errFile) ? File.ReadAllText(errFile) : null);

        Assert.False(File.Exists(DataExchangeLayout.PendingFile(_paths)));
        Assert.True(File.Exists(_paths.DatabaseFile));
        Assert.True(File.Exists(DataExchangeLayout.AltLoginFile(_paths)));
        Assert.True(File.Exists(DataExchangeLayout.DownkyiLoginFile(_paths)));
        Assert.False(Directory.Exists(Path.Combine(_root, "imports", "20261027-120000")));
        Assert.Equal(1, QueryCount(_paths.DatabaseFile, "SELECT COUNT(*) FROM videos;"));
    }

    [Fact]
    public void StartupSwap_Failure_LeavesLastErrorAndNoMarker()
    {
        // 标记指向一个已丢失的暂存区：不能死循环，失败写 last-error.log，标记照删
        Directory.CreateDirectory(DataExchangeLayout.ImportsDir(_paths));
        File.WriteAllText(
            DataExchangeLayout.PendingFile(_paths),
            """{"dir":"nonexistent-staging-dir"}""");

        PendingImportSwapper.ApplyIfNeeded(_paths, NullLogger.Instance);

        Assert.False(File.Exists(DataExchangeLayout.PendingFile(_paths)));
        Assert.True(File.Exists(DataExchangeLayout.LastErrorFile(_paths)));
    }
}
