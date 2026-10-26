using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LBS.Tests;

/// <summary>台账备份员：在线快照、档案柜修剪与销单守门。</summary>
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
    public void Prune_KeepsOnlyNewest()
    {
        Assert.True(_backup.SetKeep(7));
        for (var i = 0; i < 9; i++)
        {
            // 同秒连拍靠 -N 后缀错开，命名仍单调可排
            _backup.Run();
            Thread.Sleep(30);
        }

        Assert.Equal(7, _backup.List().Count);
        // 白名单外的份数拒收，免得站长一挥手把档案柜清空
        Assert.False(_backup.SetKeep(2));
    }

    [Fact]
    public void Delete_RejectsSneakyNames()
    {
        Assert.False(_backup.Delete("../../station.db"));
        Assert.False(_backup.Delete("station-20261026-999999.db.bak"));
        Assert.False(_backup.Delete("not-a-snapshot.db"));
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
