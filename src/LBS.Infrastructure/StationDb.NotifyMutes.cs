using Microsoft.Data.Sqlite;

namespace LBS.Infrastructure;

/// <summary>
/// 常规通知静音频道（站长 11-03：「有些人嫌烦」）：席位可自关三路定时货——
/// arrival＝到站播报，weekly＝分站周报，submit＝投稿回执（入档与失败两封同路）。
/// 欢迎信、站长公告、私信不在可关之列：欢迎信一生一封、公告是站务必达、私信是人与人的往来。
/// 静音闸落在「寄出前」：不寄则铃铛不亮、未读数干净；重开不补寄历史（与真实订阅产品同款语义）。
/// </summary>
public static class NotifyMutes
{
    public const string Arrival = "arrival";
    public const string Weekly = "weekly";
    public const string Submit = "submit";

    /// <summary>只存在这三路：端点白名单与入库去污都认这把尺子。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Arrival, Weekly, Submit };

    public static bool IsKnown(string? channel) =>
        !string.IsNullOrEmpty(channel) && All.Contains(channel, StringComparer.Ordinal);

    /// <summary>入库前去污：修边、小写、踢未知、去重——脏值（历史手改、越界请求）无声丢掉。</summary>
    public static IReadOnlyList<string> Sanitize(IEnumerable<string?> raws) =>
        (raws ?? Enumerable.Empty<string?>())
            .Select(c => c?.Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrEmpty(c) && IsKnown(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>席位侧的静音偏好读写 + 发信路上的查闸。</summary>
public sealed partial class StationDb
{
    /// <summary>席位关掉的频道清单：空表＝全开。库里的脏值（历史手改出来的）读取时无声忽略。</summary>
    public IReadOnlyList<string> GetNotifyMutes(long memberId)
    {
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT notify_mutes FROM members WHERE id = $id;";
        command.Parameters.AddWithValue("$id", memberId);
        var raw = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : NotifyMutes.Sanitize(raw.Split(','));
    }

    /// <summary>写静音清单（白名单过滤后入库；逗号连接，全开时落 NULL）。false＝席位不存在。</summary>
    public bool SetNotifyMutes(long memberId, IEnumerable<string?> channels)
    {
        var joined = string.Join(',', NotifyMutes.Sanitize(channels));
        using var connection = Open(_dbPath);
        using var command = connection.CreateCommand();
        // UPDATE 的 changes() 数的是「匹配到的行」而非「值真变了的行」：非 0 即席位在场。
        command.CommandText = "UPDATE members SET notify_mutes = $mutes WHERE id = $id;";
        command.Parameters.AddWithValue("$mutes", joined.Length > 0 ? joined : DBNull.Value);
        command.Parameters.AddWithValue("$id", memberId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 查闸：这个席位关没关这一路？整段精确比对（',a,b,' 含 ',a,'），不用裸 Contains 防子串误伤——
    /// 频道名眼下无包含关系，尺子也要钉死。带连接的重载让发信路径复用同一条连接。
    /// </summary>
    internal static bool IsMuted(SqliteConnection connection, long memberId, string channel)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT notify_mutes FROM members WHERE id = $id;";
        check.Parameters.AddWithValue("$id", memberId);
        var raw = check.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return ("," + raw.Replace(" ", "") + ",").Contains("," + channel + ",", StringComparison.Ordinal);
    }
}
