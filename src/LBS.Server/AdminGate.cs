using System.Security.Cryptography;
using System.Text;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站长室锁闸：给管理类端点上一道口令门（朋友共用分站时用），访客路由不受影响。
/// 口令哈希存 settings KV（"admin-key"），未设即敞开；解锁凭 HttpOnly cookie 里的会话令牌，
/// 有效期 7 天，重启分站后需重新解锁（内存会话不落盘，免得令牌跟着数据目录被复制）。
/// 忘了口令：删掉 station.db 里 settings 表的 admin-key 行即可回到敞开态。
/// </summary>
public sealed class AdminGate(StationDb db, ILogger<AdminGate> logger)
{
    private const string CookieName = "lbs_admin";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(5);
    private const int MaxFailsPerWindow = 6;

    /// <summary>整段前缀归站长管的路径。</summary>
    private static readonly string[] AdminPrefixes =
    [
        "/api/admin/",
        "/api/settings",
        "/api/downloads/",
        "/api/login/",
        "/api/subscriptions",
        "/api/backups",
    ];

    /// <summary>零散点名：/api/station/profile 是访客的站名牌，throttle 才是站长情报。</summary>
    private static readonly string[] AdminPaths =
    [
        "/api/station/throttle",
    ];

    private readonly object pad = new();
    private readonly Dictionary<string, DateTimeOffset> sessions = [];
    private readonly Dictionary<string, List<DateTimeOffset>> fails = [];

    public bool IsLocked => !string.IsNullOrEmpty(db.GetSetting("admin-key"));

    /// <summary>这条请求是不是站长事务（需要过门）。</summary>
    public bool IsAdminRequest(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var prefix in AdminPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var exact in AdminPaths)
        {
            if (string.Equals(path, exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 卡阵的删档/续下/重新排队/采评/加档归站长；投稿（POST /api/videos 精确）与
        // 弹幕收发（含 /api/videos/danmaku 删除，自带本人/主人规则）留访客。
        if (path.StartsWith("/api/videos/", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsDelete(request.Method) && !path.StartsWith("/api/videos/danmaku/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (HttpMethods.IsPost(request.Method)
                && (path.Contains("/revive", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/requeue", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/codecs", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/comments", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasValidSession(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (pad)
        {
            if (!sessions.TryGetValue(token, out var expiry))
            {
                return false;
            }

            if (expiry < DateTimeOffset.UtcNow)
            {
                sessions.Remove(token);
                return false;
            }

            return true;
        }
    }

    /// <summary>试锁：口令对就发 7 天会话令牌。同一来源连错 6 次进 5 分钟小黑屋。</summary>
    public string? TryUnlock(string? password, string clientIp)
    {
        var stored = db.GetSetting("admin-key");
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        lock (pad)
        {
            if (fails.TryGetValue(clientIp, out var hits))
            {
                hits.RemoveAll(t => DateTimeOffset.UtcNow - t > LockoutWindow);
                if (hits.Count >= MaxFailsPerWindow)
                {
                    logger.LogWarning("站长室解锁连续失败过多，{Ip} 进小黑屋 {Minutes} 分钟", clientIp, LockoutWindow.TotalMinutes);
                    return null;
                }
            }
        }

        if (string.IsNullOrEmpty(password) || Hash(password) != stored)
        {
            lock (pad)
            {
                if (!fails.TryGetValue(clientIp, out var hits))
                {
                    fails[clientIp] = [];
                    hits = fails[clientIp];
                }

                hits.Add(DateTimeOffset.UtcNow);
            }

            return null;
        }

        lock (pad)
        {
            fails.Remove(clientIp);
            var token = Guid.NewGuid().ToString("N");
            sessions[token] = DateTimeOffset.UtcNow + SessionTtl;
            // 顺手清扫过期会话，字典不至于无限膨胀。
            foreach (var stale in sessions.Where(kv => kv.Value < DateTimeOffset.UtcNow).Select(kv => kv.Key).ToList())
            {
                sessions.Remove(stale);
            }

            return token;
        }
    }

    /// <summary>设/换口令（首次上锁也走这里）。</summary>
    public void SetPassword(string password)
    {
        db.SetSetting("admin-key", Hash(password));
        logger.LogInformation("站长室口令已设置");
    }

    /// <summary>除锁：口令清除 + 全体会话作废，分站回到敞开态。</summary>
    public void Release()
    {
        db.SetSetting("admin-key", "");
        lock (pad)
        {
            sessions.Clear();
            fails.Clear();
        }

        logger.LogInformation("站长室锁已解除");
    }

    public void RevokeSessions()
    {
        lock (pad)
        {
            sessions.Clear();
        }
    }

    public static string Cookie => CookieName;

    public static string Hash(string password)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
    }
}
