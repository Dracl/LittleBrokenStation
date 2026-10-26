using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LBS.Infrastructure;

/// <summary>
/// 台账备份员：把 station.db 用 SQLite 在线备份 API 快照到 data/backups/，
/// WAL 正在写也不会拷出坏库；自动周期在 LedgerBackupWorker，这里管手艺与档案柜。
/// 间隔与份数存 settings KV（backup-interval-hours / backup-keep），重启记得住。
/// 恢复姿势：停站 → 拿 backups 里的快照覆盖 station.db（连同 -wal/-shm 一并删掉）→ 起站。
/// </summary>
public sealed class LedgerBackup(DataPaths paths, StationDb db, ILogger<LedgerBackup> logger)
{
    public sealed record BackupFile(string Name, long Bytes, DateTimeOffset ModifiedUtc);

    private static readonly int[] IntervalChoices = [6, 24, 72, 168];
    private static readonly int[] KeepChoices = [7, 14, 30];
    private static readonly Regex SafeName = new(@"^station-\d{8}-\d{6}(?:-\d+)?\.db$", RegexOptions.Compiled);

    public int IntervalHours
    {
        get
        {
            var stored = int.TryParse(db.GetSetting("backup-interval-hours"), out var n) && IntervalChoices.Contains(n) ? n : 24;
            return stored;
        }
    }

    public int Keep
    {
        get
        {
            var stored = int.TryParse(db.GetSetting("backup-keep"), out var n) && KeepChoices.Contains(n) ? n : 14;
            return stored;
        }
    }

    public bool SetInterval(int hours)
    {
        if (!IntervalChoices.Contains(hours))
        {
            return false;
        }

        db.SetSetting("backup-interval-hours", hours.ToString());
        return true;
    }

    public bool SetKeep(int keep)
    {
        if (!KeepChoices.Contains(keep))
        {
            return false;
        }

        db.SetSetting("backup-keep", keep.ToString());
        Prune();
        return true;
    }

    /// <summary>最近一次备份时间（取档案柜里最新的快照），一份都没有返回 null。</summary>
    public DateTimeOffset? LastBackupUtc()
    {
        return List().FirstOrDefault()?.ModifiedUtc;
    }

    /// <summary>来一张新快照，顺手按 Keep 修剪档案柜。返回快照文件名。</summary>
    public string Run()
    {
        var dir = Path.Combine(paths.Root, "backups");
        Directory.CreateDirectory(dir);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var name = $"station-{stamp}.db";
        var suffix = 1;
        while (File.Exists(Path.Combine(dir, name)))
        {
            name = $"station-{stamp}-{suffix}.db";
            suffix++;
        }

        var target = Path.Combine(dir, name);
        using (var source = new SqliteConnection($"Data Source={paths.DatabaseFile};Mode=ReadOnly"))
        using (var destination = new SqliteConnection($"Data Source={target}"))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        Prune();
        db.SetSetting("last-backup-utc", DateTimeOffset.UtcNow.ToString("O"));
        var bytes = new FileInfo(target).Length;
        logger.LogInformation("台账快照落柜 {Name}（{Kb} KB）", name, bytes / 1024);
        return name;
    }

    public IReadOnlyList<BackupFile> List()
    {
        var dir = Path.Combine(paths.Root, "backups");
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return Directory.EnumerateFiles(dir, "station-*.db")
            .Select(f => new BackupFile(Path.GetFileName(f), new FileInfo(f).Length, new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)))
            .OrderByDescending(b => b.Name)
            .ToList();
    }

    /// <summary>销单：只认快照命名格式，路径花活一律拒。</summary>
    public bool Delete(string name)
    {
        if (!SafeName.IsMatch(name))
        {
            return false;
        }

        var file = Path.Combine(paths.Root, "backups", name);
        if (!File.Exists(file))
        {
            return false;
        }

        File.Delete(file);
        return true;
    }

    private void Prune()
    {
        var dir = Path.Combine(paths.Root, "backups");
        if (!Directory.Exists(dir))
        {
            return;
        }

        var stale = Directory.EnumerateFiles(dir, "station-*.db")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(Keep);

        foreach (var file in stale)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 快照正被占用（罕见），下一轮再剪
            }
        }
    }
}
