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

    /// <summary>
    /// 快照名的尺子：正形 <c>station-yyyyMMdd-HHmmss.db</c>，同秒连拍靠 <c>-1/-2</c> 数字后缀错开
    /// （字典序还能排），数字位也被人占了（同刻并发抢同一枚）就上 8 位十六进制随机。
    /// <b>三个口径共用这一把尺</b>（<see cref="List"/> / <see cref="FullPathOf"/> / <see cref="Delete"/>），
    /// 新增后缀形状必须同时进这里，不然抢出来那张快照会成「柜里看不见、也销不掉」的幽灵件。
    /// </summary>
    private static readonly Regex SafeName = new(@"^station-\d{8}-\d{6}(?:-\d+|-[0-9a-f]{8})?\.db$", RegexOptions.Compiled);

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
        var target = ClaimSlot(dir, stamp);
        var name = Path.GetFileName(target);
        // 备份 API 与并发写者抢锁会给 BUSY：Default Timeout 就是 SQLite 的忙等窗口，
        // 不设的话高负载下间歇性炸雷（测试并行里撞过一回，生产上 worker 常年写着，周期快照同理）。
        var sourceCs = $"Data Source={paths.DatabaseFile};Mode=ReadOnly;Default Timeout=10";
        var destCs = $"Data Source={target};Default Timeout=10";
        using (var source = new SqliteConnection(sourceCs))
        using (var destination = new SqliteConnection(destCs))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        // 连接池会替刚拍完的快照扣着文件句柄，不清掉的话紧接着的 Prune 想删也删不动。
        // 只清本方法自己那两池：ClearAllPools 是进程级副作用，会把全站 worker 正复用的
        // station.db 主池一起掠平（每小时巡班 + 每次导出都抢一遗，白白制造一波重连风暴）。
        ClearOwnPool(sourceCs);
        ClearOwnPool(destCs);

        // 尺寸先记账：后头的修剪万一把它当陈旧件盯上，日志也不会读到没的文件
        var bytes = new FileInfo(target).Length;

        Prune(protect: name);
        db.SetSetting("last-backup-utc", DateTimeOffset.UtcNow.ToString("O"));
        logger.LogInformation("台账快照落柜 {Name}（{Kb} KB）", name, bytes / 1024);
        return name;
    }

    /// <summary>
    /// 抢一个快照位，交回绝对路径。以前是「<c>while (File.Exists)</c> 看一眼、再开写」——两个备份员
    /// （整点巡班与站长导出）同秒抢同一枚时，各自看那一刻都不存在，然后一起往同一个文件备份：
    /// 后写的把先写的盖掉，导出那侧还会把对方正在打包的快照删掉。
    /// 现在交绨给操作系统拍板：<see cref="FileMode.CreateNew"/> 只能由一个人成功，输了就换下一个名字重试。
    /// 名字兜底次序：正形 → -1..-98 数字后缀（同秒连拍的常态，字典序还能排）→ 8 位随机 hex。
    /// </summary>
    private static string ClaimSlot(string dir, string stamp)
    {
        for (var attempt = 1; attempt <= 200; attempt++)
        {
            var name = attempt switch
            {
                1 => $"station-{stamp}.db",
                <= 99 => $"station-{stamp}-{attempt - 1}.db",
                _ => $"station-{stamp}-{Guid.NewGuid().ToString("N")[..8]}.db",
            };
            var target = Path.Combine(dir, name);
            try
            {
                // 只预占不写：建完就关门，背写本体交给 SQLite。
                using var claim = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return target;
            }
            catch (IOException)
            {
                // 这一枚已被人占了（同秒并发）或是上次炸下的残件：换下一个名字再来
            }
        }

        throw new IOException($"档案柜里 {stamp} 这一秒的快照位都占满了，稍后再拍");
    }

    /// <summary>清一条连接串自己的池（不碰别人的）。没建过池就是空操作，异常不拦拍快照。</summary>
    private static void ClearOwnPool(string connectionString)
    {
        try
        {
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
        }
        catch (SqliteException)
        {
            // 清不动只是 Prune 本轮删不掉它，整点巡班还会补刀
        }
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
