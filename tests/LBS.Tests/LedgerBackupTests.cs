using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LBS.Tests;

/// <summary>台账备份员：在线快照、档案柜修剪与销单守门。</summary>
[Collection("DiskBound")]
public sealed class LedgerBackupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly LedgerBackup _backup;

    public LedgerBackupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
        _backup = new LedgerBackup(paths, _db, NullLogger<LedgerBackup>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录留给操作系统
        }
    }

    [Fact]
    public void Run_CopiesQueryableViewer()
    {
        _db.AddVideosBulk([new NewVideo("https://www.bilibili.com/video/BV1BACKUPTEST", "BV1BACKUPTEST", "备份试验品")]);

        var name = _backup.Run();

        var snapshot = Path.Combine(_tempDir, "backups", name);
        Assert.True(File.Exists(snapshot));
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={snapshot};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM videos WHERE bvid = $b;";
        cmd.Parameters.AddWithValue("$b", "BV1BACKUPTEST");
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void FullPathOf_认在册快照_拒路径花活() // 下载钮的后端尺子：与销单同一把，销不掉的也别想读走
    {
        var name = _backup.Run();

        var full = _backup.FullPathOf(name);

        Assert.NotNull(full);
        Assert.Equal(Path.Combine(_tempDir, "backups", name), full);
        Assert.True(File.Exists(full));

        // 名字不是路径：.. 、绝对路径、带斜杠的都拒（哪怕那件真在盘上）
        Assert.Null(_backup.FullPathOf("../../station.db"));
        Assert.Null(_backup.FullPathOf("..\\..\\station.db"));
        Assert.Null(_backup.FullPathOf(Path.Combine(_tempDir, "station.db")));
        Assert.Null(_backup.FullPathOf($"{name}/../../station.db"));
        Assert.Null(_backup.FullPathOf(""));
        // 名字合格式但盘上没这件 → 也是 null，不给空流让前端拿到 0 字节的存档
        Assert.Null(_backup.FullPathOf("station-19990101-000000.db"));
    }

    [Fact]
    public void Prune_KeepsOnlyNewest()
    {
        Assert.True(_backup.SetKeep(7));
        for (var i = 0; i < 9; i++)
        {
            // 同秒连拍靠 -N 后缀错开，命名仍单调可排
            _backup.Run();
            Thread.Sleep(30);
        }

        var shelf = _backup.List();
        // 修剪是最终一致：刚拍完的快照可能被系统短暂扣着，补一轮 SetKeep 触发的修剪就该扫干净
        // （生产里由整点巡班兼任这个兜底）
        shelf = WaitForShelf(7);
        Assert.True(shelf.Count == 7, $"柜里 {shelf.Count} 张：{string.Join(" | ", shelf.Select(f => f.Name))}");
        // 白名单外的份数拒收，免得站长一挥手把档案柜清空
        Assert.False(_backup.SetKeep(2));
    }

    /// <summary>反复补一轮 SetKeep 触发的修剪（等价整点巡班），直到柜里只剩 expect 张或预算（2 秒）用尽。
    /// 刚拍完的快照可能被连接池短暂扣着句柄，删不动就补刀，与生产里整点巡班扫尾同义。</summary>
    private IReadOnlyList<LedgerBackup.BackupFile> WaitForShelf(int expect)
    {
        var shelf = _backup.List();
        for (var round = 0; round < 20 && shelf.Count != expect; round++)
        {
            Thread.Sleep(100);
            _backup.SetKeep(7);
            shelf = _backup.List();
        }
        return shelf;
    }

    [Fact]
    public void Delete_RejectsSneakyNames()
    {
        Assert.False(_backup.Delete("../../station.db"));
        Assert.False(_backup.Delete("station-20261026-999999.db.bak"));
        Assert.False(_backup.Delete("not-a-snapshot.db"));
    }

    [Fact]
    public void SidecarFiles_AreNotArchived()
    {
        // Windows 短名会让 *.db 通配误扫到 WAL 边车文件，档案柜只认 SafeName 正形
        var dir = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "station-20261026-120000.db-wal"), "wal");
        File.WriteAllText(Path.Combine(dir, "station-20261026-120000.db-shm"), "shm");

        _backup.Run();

        Assert.All(_backup.List(), f => Assert.EndsWith(".db", f.Name));
        Assert.Single(_backup.List());
    }

    [Fact]
    public void Config_WhitelistOnly()
    {
        Assert.True(_backup.SetInterval(72));
        Assert.Equal(72, _backup.IntervalHours);
        Assert.False(_backup.SetInterval(13));
        Assert.True(_backup.SetKeep(30));
        Assert.Equal(30, _backup.Keep);
        Assert.False(_backup.SetKeep(9999));
    }
}
