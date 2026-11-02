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
        using (var source = new SqliteConnection($"Data Source={paths.DatabaseFile};Mode=ReadOnly;Default Timeout=10"))
        using (var destination = new SqliteConnection($"Data Source={target};Default Timeout=10"))
        {
            source.Open();
            destination.Open();
            // 备份 API 与并发写者抢锁会给 BUSY：Default Timeout 就是 SQLite 的忙等窗口，
            // 不设的话高负载下间歇性炸雷（测试并行里撞过一回，生产上 worker 常年写着，周期快照同理）。
            source.BackupDatabase(destination);
        }

        // 连接池会替刚拍完的快照扣着文件句柄，不清掉的话紧接着的 Prune 想删也删不动
        SqliteConnection.ClearAllPools();

        // 尺寸先记账：后头的修剪万一把它当陈旧件盯上，日志也不会读到没的文件
        var bytes = new FileInfo(target).Length;

        Prune(protect: name);
        db.SetSetting("last-backup-utc", DateTimeOffset.UtcNow.ToString("O"));
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

        return Directory.EnumerateFiles(dir)
            .Where(f => SafeName.IsMatch(Path.GetFileName(f)))
            .Select(f => new BackupFile(Path.GetFileName(f), new FileInfo(f).Length, new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)))
            .OrderByDescending(b => b.Name)
            .ToList();
    }

    /// <summary>取快照的绝对路径，只服站长室的下载钮：与 <see cref="Delete"/> 同一把尺（只认快照命名格式，
    /// 路径花活一律拒）——销得掉的才允许被读走，两个口才不会一个严一个松。</summary>
    public string? FullPathOf(string name)
    {
        if (!SafeName.IsMatch(name))
        {
            return null;
        }

        var file = Path.Combine(paths.Root, "backups", name);
        return File.Exists(file) ? file : null;
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

    private void Prune(string? protect = null)
    {
        var dir = Path.Combine(paths.Root, "backups");
        if (!Directory.Exists(dir))
        {
            return;
        }

        // 按落笔时间新→旧排，名字只作同刻平手时的垫底：
        // 同秒连拍的 -1/-2 后缀按字典序会把最早那张当成最旧，误删的惊怒测试才冒出来
        var stale = Directory.EnumerateFiles(dir)
            .Where(f => SafeName.IsMatch(Path.GetFileName(f)))
            .Where(f => !string.Equals(Path.GetFileName(f), protect, StringComparison.Ordinal))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .ThenByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(Keep);

        foreach (var file in stale)
        {
            // Windows 上刚写完的文件可能被杀软/索引服务短暂扣着，重试几拍再放手
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Delete(file);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
                catch (IOException)
                {
                    // 快照正被占用，整点巡班再剪
                    break;
                }
            }
        }
    }
}
