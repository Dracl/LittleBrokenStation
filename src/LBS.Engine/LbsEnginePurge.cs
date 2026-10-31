using Microsoft.Data.Sqlite;

namespace LBS.Engine;

/// <summary>
/// 引擎台账清空（危险区一键清空用）：清掉 downkyicore 自己 Data.db 的四张任务表，
/// 保留 schema 与 Settings 目录（下载设置里的 UA/并发等站长配置不动）。
/// 不清这四张表的话，清完站重新投稿会被引擎的「已存在」记录拦下。
/// </summary>
public static class LbsEnginePurge
{
    public static int PurgeEngineStorage(string downkyiRoot)
    {
        var dbFile = Path.Combine(downkyiRoot, "Storage", "Data.db");
        if (!File.Exists(dbFile))
        {
            return 0;
        }

        using var connection = new SqliteConnection($"Data Source={dbFile};Default Timeout=5");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM downloading;
            DELETE FROM download_base;
            DELETE FROM download_history;
            DELETE FROM download_quarantine;
            """;
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// 按 cid 销单部片子的引擎历史（删档与重投前对账用）：引擎准入判重读的就是这张表，
    /// 键是 (cid, 需要下载的内容)（见 DownKyi.Worker/Services/Download/DownloadTaskAdmissionService.cs:64-85），
    /// 不清它的话删完重投会被判「已下过」，分站台账落到无人认领的 Skipped 终态。
    /// 与危险区一键清空同一把钥匙（直开引擎自己的 Data.db），区别只在把 DELETE 绑到 cid 上；
    /// cid&lt;=0 一律不当数：旧库迁移行可能没填 cid（schema 里 DEFAULT 0），拿 0 去删会把别人的历史一并捣掉。
    /// 只动 download_history：在途任务由调用方走引擎接口销（那边会顺带把 download_base 一并带走），
    /// 分站不自己伸手删在途表，免得绕过它的内存态。
    /// </summary>
    public static int PurgeEngineHistoryByCids(string downkyiRoot, IEnumerable<long> cids)
    {
        var wanted = cids.Where(static cid => cid > 0).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return 0;
        }

        var dbFile = Path.Combine(downkyiRoot, "Storage", "Data.db");
        if (!File.Exists(dbFile))
        {
            return 0;
        }

        using var connection = new SqliteConnection($"Data Source={dbFile};Default Timeout=5");
        connection.Open();
        var deleted = 0;
        foreach (var cid in wanted)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM download_history WHERE cid = $cid;";
            command.Parameters.AddWithValue("$cid", cid);
            deleted += command.ExecuteNonQuery();
        }

        return deleted;
    }
}
