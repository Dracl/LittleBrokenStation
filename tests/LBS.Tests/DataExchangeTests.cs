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
/// 数据交换：导出包 manifest 往返、快照洗白（cookie 无条件洗成固定串 + 会话清空，withCreds 递 true 也一视同仁，
/// 且端点已把它写死 false）、导入安检（zip slip 整包拒、integrity_check 拒收、包名/回执白名单）与开机换库全流程。
/// </summary>
[Collection("DiskBound")]
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
        // 旧时代的站级登录件（站级主备号已退役）：故意留在盘上，出包不该再收它们。
        File.WriteAllText(Path.Combine(_paths.Downkyi, "Config", "Login"), """{"cookies":"owner"}""");
        File.WriteAllText(Path.Combine(_paths.Cookies, "alt-login.json"), """{"Cookie":"alt"}""");
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

        // withCreds 递 false：与端点现在的口径一致（端点已把它写死 false，见 Export_RequestBodyWithCredsTrue_StillScrubsCookies）。
        // 旧包里的 true 仍能回读：Import_ApplyThenStartupSwap 那一条拿 withCreds:true 的老包走暂存与应用。
        var result = _exporter.Run(new ExportOptions(false, true, true, true));

        Assert.True(DataExchangeLayout.IsSafeExportName(result.Name));
        Assert.True(result.Bytes > 0);
        var zipPath = ZipPath(result.Name);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("station.db", names);
            Assert.Contains("manifest.json", names);
            // 站级登录件已随站级主备号退役：就算盘上还躺着老文件也不得入包（成员 cookie 只走台账）。
            Assert.DoesNotContain("cookies/alt-login.json", names);
            Assert.DoesNotContain("login/downkyi-login.json", names);
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
        // 新语义：manifest 只说真话——包内 cookie 永远被洗，withCreds 就得是 false（端点那一步已写死）。
        Assert.False(manifest.Options.WithCreds);
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
    public void Export_WithCreds_StillScrubsCookiesAndWipesSessions()
    {
        SeedStation();
        SeedMediaFiles();

        var result = _exporter.Run(new ExportOptions(true, false, false, false));

        var zipPath = ZipPath(result.Name);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            // 带凭证出包也不再多带站级登录件：成员号池的 cookie 本就在台账件里（下面 QueryStrings 验那条）。
            Assert.Null(zip.GetEntry("cookies/alt-login.json"));
            Assert.Null(zip.GetEntry("login/downkyi-login.json"));
        }

        var packDb = ExtractPackedDb(zipPath);
        // 老断言钉的是「withCreds=true 就原样带走全员 B 站 cookie」，已翻转为「永远洗」：
        // 导出包会被拿去分享/备份/换机，cookie 不能由包外的人在请求体里一句话决定要不要带走。
        // 会话无论如何都作废那一手没变。
        Assert.Equal(["revoked-on-export"], QueryStrings(packDb, "SELECT DISTINCT cookie FROM member_accounts;").OrderBy(x => x).ToArray());
        Assert.Equal(0, QueryCount(packDb, "SELECT COUNT(*) FROM member_sessions;"));
        // 现站分毫未动（洗的是快照副本，不是现站）
        Assert.Equal(["SESSDATA=secret-cookie"], QueryStrings(_paths.DatabaseFile, "SELECT DISTINCT cookie FROM member_accounts;"));
    }

    [Fact]
    public void Export_RequestBodyWithCredsTrue_StillScrubsCookies()
    {
        // 钉的是端点那一条链（DataExchangeEndpoints.cs 的 POST /api/admin/export）：客户端在请求体里递
        // withCreds:true 也只能拿到一份洗干净的包。这里拿端点同一份反序列化形状、拼同一句 ExportOptions，
        // 免得「测试验了洗白、线上漏在端点那一层」那种两处真相。
        SeedStation("SESSDATA=live-cookie");

        var body = """{"withCreds":true,"withMedia":false,"withCmtImg":false,"withCaches":false,"withPreviews":false}""";
        var request = JsonSerializer.Deserialize<DataExchangeEndpoints.ExportRequest>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(request.WithCreds); // 字段仍收得下（老前端不报错），到此为止，不再往下传

        // 与 DataExchangeEndpoints.cs 那句构造一字同行：withCreds 写死 false，其余透传。
        var options = new ExportOptions(false, request.WithMedia, request.WithCmtImg, request.WithCaches, request.WithPreviews ?? true);
        var result = _exporter.Run(options);

        var packDb = ExtractPackedDb(ZipPath(result.Name));
        Assert.Equal(["revoked-on-export"], QueryStrings(packDb, "SELECT DISTINCT cookie FROM member_accounts;").OrderBy(x => x).ToArray());
        // manifest 从此不替客户端那句「要带凭证」作伪证（前端那个「凭证 / 带登录态」徒据此自动消失）
        var manifest = ReadManifest(ZipPath(result.Name));
        Assert.NotNull(manifest.Options);
        Assert.False(manifest.Options.WithCreds);
        // 现站自己那条 cookie 不被洗（洗的是快照副本）
        Assert.Equal(["SESSDATA=live-cookie"], QueryStrings(_paths.DatabaseFile, "SELECT DISTINCT cookie FROM member_accounts;"));
    }

    // ─── 导出撞名与失败清理：同秒第二单不许动第一单的包 ───

    private const string SameSecondStamp = "20261027-010203";
    private static readonly ExportOptions LedgerOnly = new(false, false, false, false);

    private string[] ExportCabinetNames() => Directory
        .GetFiles(Path.Combine(_root, "exports"))
        .Select(Path.GetFileName)
        .Where(n => n is not null)
        .Cast<string>()
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void Export_SameStamp_SecondRunGetsOwnSlot_FirstArchiveBytesUnchanged()
    {
        SeedStation();
        SeedMediaFiles();

        // 固定秒戳连拍两回（旧实现第二回拿同名 ZipFile.Open Create 硬开，失败时还把第一单成包销了）
        var first = _exporter.Run(LedgerOnly, SameSecondStamp);
        Assert.Equal($"lbs-export-station-{SameSecondStamp}.zip", first.Name); // 头一手照旧占正形
        var firstPath = ZipPath(first.Name);
        var firstBytes = File.ReadAllBytes(firstPath);

        var second = _exporter.Run(LedgerOnly, SameSecondStamp);

        // 第二单自立门户：同秒戳让位加序号、过了白名单、报的字节数与盘上实物对得上
        Assert.NotEqual(first.Name, second.Name);
        Assert.StartsWith($"lbs-export-station-{SameSecondStamp}-", second.Name, StringComparison.Ordinal);
        Assert.True(DataExchangeLayout.IsSafeExportName(second.Name), second.Name);
        Assert.Equal(new FileInfo(ZipPath(second.Name)).Length, second.Bytes);
        // 第一单成包逐字节分毫未动：同秒第二单无论成败都不许碰别人的包
        Assert.Equal(firstBytes, File.ReadAllBytes(firstPath));
        // 两包各自是完整 zip，manifest 齐整
        Assert.Equal("lbs-export", ReadManifest(firstPath).Format);
        Assert.Equal("lbs-export", ReadManifest(ZipPath(second.Name)).Format);
        // 档案柜不认幽灵件：List 认得两包，后缀包也销得掉（download/delete 与 List 同一把尺）
        Assert.Equal(2, ExportCabinetNames().Length);
        var listed = _exporter.List().Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { first.Name, second.Name }.OrderBy(n => n, StringComparer.Ordinal).ToArray(), listed);
        Assert.True(_exporter.Delete(second.Name));
        Assert.False(File.Exists(ZipPath(second.Name)));
        Assert.True(File.Exists(firstPath));
    }

    [Fact]
    public async Task Export_SameStamp_ConcurrentRuns_EachGetCompleteIndependentArchives()
    {
        SeedStation();
        SeedMediaFiles();

        const int racers = 6;
        var results = await Task.WhenAll(
            Enumerable.Range(0, racers).Select(_ => Task.Run(() => _exporter.Run(LedgerOnly, SameSecondStamp))));

        // 六单并发抢同一秒：名字互不重叠，正形只有一个赢家，其余全走后缀错开且都过白名单
        var names = results.Select(r => r.Name).ToArray();
        Assert.Equal(racers, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(names, n => n == $"lbs-export-station-{SameSecondStamp}.zip");
        Assert.All(names, n => Assert.True(DataExchangeLayout.IsSafeExportName(n), n));

        // 每包都完整落笔：报的字节数与盘上实物一致，zip 打得开、manifest 读得出，谁也没被截胡
        foreach (var r in results)
        {
            var path = ZipPath(r.Name);
            Assert.Equal(new FileInfo(path).Length, r.Bytes);
            Assert.True(r.Bytes > 0);
            Assert.Equal("lbs-export", ReadManifest(path).Format);
        }

        Assert.Equal(racers, ExportCabinetNames().Length);
    }

    [Fact]
    public void Export_PackagingFault_KeepsExistingArchiveAndSweepsOnlyOwnSlot()
    {
        SeedStation();
        SeedMediaFiles();

        // 档案柜里先有一张同秒成包（事故里的「前一手」受害者）
        var kept = _exporter.Run(LedgerOnly, SameSecondStamp);
        var keptPath = ZipPath(kept.Name);
        var keptBytes = File.ReadAllBytes(keptPath);

        // 同秒第二单：CreateNew 撞名换到后缀位后，在挂上笔、未落条目处埋雷炸掉
        var boom = new IOException("模拟打包半途塌方");
        Assert.Same(boom, Assert.Throws<IOException>(() => _exporter.Run(LedgerOnly, SameSecondStamp, () => throw boom)));

        // 旧包幸存且字节分毫未动（旧 bug 的靶心：catch 无条件 TryDelete(zipPath) 销的就是它）
        Assert.Equal(keptBytes, File.ReadAllBytes(keptPath));
        // 自己抢的那枚随失败清走：柜里不留半截 zip 残件
        Assert.Equal(new[] { kept.Name }, ExportCabinetNames());
        // 旧包仍在档案柜视野内、销得掉（白名单对新旧名同一把尺）
        var listed = Assert.Single(_exporter.List());
        Assert.Equal(kept.Name, listed.Name);
        Assert.True(_exporter.Delete(kept.Name));
        Assert.False(File.Exists(keptPath));
    }

    [Fact]
    public void Export_FirstRunFault_LeavesNoHalfBuiltPackage()
    {
        SeedStation();

        var boom = new IOException("模拟首单半途塌方");
        Assert.Same(boom, Assert.Throws<IOException>(() => _exporter.Run(LedgerOnly, "20261027-010299", () => throw boom)));

        // 首单失败也不留壳：空手而来空手归，档案柜干干净净等下一单
        Assert.Empty(ExportCabinetNames());
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
    // 同秒抢位让名后的新形状：秒后数字序号与 8 位随机 hex（老包新包同一把尺都认）
    [InlineData("lbs-export-station-20261027-010203-1.zip", true)]
    [InlineData("lbs-export-station-20261027-010203-98.zip", true)]
    [InlineData("lbs-export-station-20261027-010203-a1b2c3d4.zip", true)]
    [InlineData("lbs-export-station-20261027-010203-1a.zip", false)]
    [InlineData("lbs-export-station-20261027-010203-zz123456.zip", false)]
    [InlineData("lbs-export-station-20261027-010203-.zip", false)]
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
        Assert.False(File.Exists(Path.Combine(_root, "imports", "20261027-120000", "cookies", "alt-login.json")), "站级登录件不再入包");
        Assert.False(File.Exists(Path.Combine(_root, "imports", "20261027-120000", "login", "downkyi-login.json")), "站级登录件不再入包");

        // 非法回执拒应用
        var (badOk, _) = ImportApplier.Apply("../20261027-120000", _paths, _backup);
        Assert.False(badOk);

        var (ok, error) = ImportApplier.Apply("20261027-120000", _paths, _backup);
        Assert.True(ok, error);
        // 标记已落、现站台账已移入 pre-import 保底（站级登录件已不存在，不再有这一步）
        Assert.True(File.Exists(DataExchangeLayout.PendingFile(_paths)));
        Assert.False(File.Exists(_paths.DatabaseFile));
        // 站级登录件不再参与换库：既不进暂存包，也不被搬进 pre-import 保底（盘上的老文件就让它待着）。
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "pre-import"), "alt-login.json", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "pre-import"), "*", SearchOption.AllDirectories));

        // 开机换库：台账换芯、标记与暂存清空（不再回写任何站级登录件）
        PendingImportSwapper.ApplyIfNeeded(_paths, NullLogger.Instance);

        var errFile = DataExchangeLayout.LastErrorFile(_paths);
        Assert.False(File.Exists(errFile), File.Exists(errFile) ? File.ReadAllText(errFile) : null);

        Assert.False(File.Exists(DataExchangeLayout.PendingFile(_paths)));
        Assert.True(File.Exists(_paths.DatabaseFile));
        // （旧版本这两行断言「登录态回位」；站级登录件已退役，换库不碰它们，不再断言。）
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
