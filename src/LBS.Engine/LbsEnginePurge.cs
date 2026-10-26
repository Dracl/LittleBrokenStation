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
}
