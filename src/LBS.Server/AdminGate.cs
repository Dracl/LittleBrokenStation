using System.Security.Cryptography;
using System.Text;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站长室锁闸：给管理类端点上一道口令门（朋友共用分站时用），访客路由不受影响。
/// 口令哈希存 settings KV（"admin-key"），未设即敞开；新锁走 PBKDF2 加盐（<see cref="MemberPasswords"/>），
/// 存里那枚无盐 SHA256 旧哈希照旧能验，验对那一刻透明换铸（现网真站点不至于因为加固被锁死）；
/// 解锁凭 HttpOnly cookie 里的会话令牌，
/// 有效期 7 天，重启分站后需重新解锁（内存会话不落盘，免得令牌跟着数据目录被复制）。
/// 忘了口令：删掉 station.db 里 settings 表的 admin-key 行即可回到敞开态。
/// </summary>
public sealed class AdminGate(StationDb db, ILogger<AdminGate> logger)
{
    private const string CookieName = "lbs_admin";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(5);
    private const int MaxFailsPerWindow = 6;

    /// <summary>
    /// 站长口令长度下限：与站内席位口令同一档（<see cref="MemberPasswords.MeetsPolicy"/> 也是 8）。
    /// 四位口令在公网等于没锁——小黑屋只限住同一来源的并发速度，挡不住分布式慢摇。
    /// </summary>
    public const int MinPasswordLength = 8;

    /// <summary>整段前缀归站长管的路径。</summary>
    private static readonly string[] AdminPrefixes =
    [
        "/api/admin/",
        // /api/settings 本体已公开（分享链接门面地址要给访客读，见 ShareLinkEndpoints）；
        // 引擎设置仍吃站长门，点名必须写全：前缀表是 StartsWith，光写 /api/settings 会把两者一锅端，
        // 光摘掉又会把引擎门放掉（11-15 做分享链接设定时在此踩过）。
        "/api/settings/engine",
        "/api/downloads/",
        // /api/login/ 随站级登录态一起拆了：分站不再存任何站级 B 站 cookie，
        // 成员配自己的号走 /api/member/accounts/*（那里按席位归属自管）。
        "/api/subscriptions",
        "/api/backups",
        "/api/preview/",
    ];

    /// <summary>零散点名：/api/station/profile 是访客的站名牌，throttle 才是站长情报。</summary>
    private static readonly string[] AdminPaths =
    [
        "/api/station/throttle",
        // 预览总览会报站内囤了多少预览、谁转挂了，也是站长情报，一并收进门里。
        "/api/stats/preview",
        // 采集号报的是「此刻在用谁的 B 站号、借的是哪位成员的号」，属于站长情报，不对外露。
        "/api/stats/collect",
    ];

    private readonly object pad = new();
    private readonly Dictionary<string, DateTimeOffset> sessions = [];
    private readonly Dictionary<string, List<DateTimeOffset>> fails = [];

    public bool IsLocked => !string.IsNullOrEmpty(db.GetSetting("admin-key"));

    /// <summary>这条请求是不是站长事务（需要过门）。</summary>
    public bool IsAdminRequest(HttpRequest request)
    {
        var raw = request.Path.Value ?? "";
        // 进门那一句必须 IgnoreCase：Minimal API 的路由匹配不吃大小写，门闸按 Ordinal 认字面时
        // /API/admin/members 在路由上照样命中端点、在这里却被当成「不是站长事务」直接放行（活体实测漏过）。
        // 下面 AdminPrefixes / AdminPaths 的点名本来就是 IgnoreCase 的，保持原样，不拓语义。
        if (!raw.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 与 MemberGate 同一把剪子：先去掉尾部多余斜杠再点名，否则 /api/stats/collect/ 这类
        // 精确匹配型的站长情报会被一枚斜杠穿给匿名（实测漏过：throttle/stats/collect/stats preview）。
        var path = raw.TrimEnd('/');

        foreach (var prefix in AdminPrefixes)
        {
            // 剪掉尾斜杠后 /api/admin/ 这类带尾前缀要兼容裸形（/api/admin），点名不能因剪子放宽或收紧。
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || path.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
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
        // 例外：投稿人自管五件事（刷评论快照 / 重拉弹幕 / 删自己的稿 / 续下 / 重新排队）从站长室摘出来，
        // 改由 MemberGate 要求会员 + 端点内自己核归属（少了这一摘一补，要么投稿人进不来，要么匿名敞着）。
        // 续下与重新排队（POST …/revive 与 …/requeue）本轮跟着摘：站长室那圈 IsOwner 短路本来就把
        // 「投稿人想续自己的下载」一起挡死了，摘出来之后归属门落在端点里的 CanManageVideo
        // （ManageGate.CanManage 对没席位的人一律回假，匿名顶多拿 403，不是敞着）。
        // /codecs 里只「点单」仍留在站长门里（一次点单能插几十行任务、烧全站共用的采集号）；
        // 而「取消自己那单」与「删自己那版的件」都是止损型动作，11-08 摘给投稿人自管，
        // 归属落在端点里的 CanManageVideo；恢复与销毁住在 /api/admin/ 下面，只站长握得住。
        // …/media-refresh 早就是「会员门 + 端点归属门」那一张形。
        if (path.StartsWith("/api/videos/", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsDelete(request.Method)
                && !path.StartsWith("/api/videos/danmaku/", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("/codecs", StringComparison.OrdinalIgnoreCase)
                && !MemberGate.IsVideoIdOnlyPath(path))
            {
                return true;
            }

            if (HttpMethods.IsPost(request.Method)
                && ((path.Contains("/codecs", StringComparison.OrdinalIgnoreCase) && !IsCodecCancel(path))
                    || (path.Contains("/comments", StringComparison.OrdinalIgnoreCase) && !IsSelfOwnedCommentsRefresh(path))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// POST …/comments 与 …/comments/subs：投稿人刷自己稿子的评论快照、会员点开楼中楼时的按需补采。
    /// 后者从前只被 Contains("/comments") 连带罩住，普通会员点开就是 403。
    /// </summary>
    private static bool IsSelfOwnedCommentsRefresh(string path) =>
        path.EndsWith("/comments", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/comments/subs", StringComparison.OrdinalIgnoreCase);

    /// <summary>取消在途加档那一口（POST …/codecs/cancel）：它只是止损，与「点单」不同权——
    /// 点单才是烧全站采集号的那一下，所以从上面那条 Contains("/codecs") 里单独剔出来。</summary>
    private static bool IsCodecCancel(string path) =>
        path.EndsWith("/codecs/cancel", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// 试锁：口令对就发 7 天会话令牌。候选口令先 <c>Trim()</c> 再验，与挂牌那一步（<see cref="TrySetPassword"/>）同口径：
    /// 当年落库的是 Trim 过的口令，验的时候不 Trim 就会把「手机上多碰了一个空格」的本人挡在门外；Trim 后为空的候选一律拒。
    /// 同一来源连错 6 次进 5 分钟小黑屋。
    /// </summary>
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

        var candidate = password?.Trim(); // 与设锁同口径：量什么验什么（Trim 后为空即拒，不进比对）
        if (string.IsNullOrEmpty(candidate) || !VerifyPassword(stored, candidate, out var needsUpgrade))
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

        // 存量那枚无盐 SHA256 认对了：验证成功那一刻就地换铸 PBKDF2（现网数据不受影响，老哈希不再继续服役）。
        // 换铸用的是验对的那个候选串（已 Trim），与下面验的串一字不差，不会把自己锁死。
        if (needsUpgrade)
        {
            UpgradeLegacyKey(candidate);
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

    /// <summary>设/换口令（首次上锁也走这里）：不够长的口令一律拒上锁，不给「上了锁等于没锁」留后路。</summary>
    /// <exception cref="ArgumentException">口令短于 <see cref="MinPasswordLength"/>（调用方想回 400 就先用 <see cref="MeetsPasswordPolicy"/> 自己挡）。</exception>
    public void SetPassword(string password)
    {
        if (!TrySetPassword(password, out var error))
        {
            throw new ArgumentException(error, nameof(password));
        }
    }

    /// <summary>不抛体版的设锁：口令不够格时回假与一句人话（端点拿它回 400，不必接异常）。</summary>
    public bool TrySetPassword(string? password, out string? error)
    {
        if (!MeetsPasswordPolicy(password))
        {
            error = $"站长室口令至少 {MinPasswordLength} 位";
            return false;
        }

        db.SetSetting("admin-key", MemberPasswords.Hash(password!.Trim()));
        error = null;
        logger.LogInformation("站长室口令已设置");
        return true;
    }

    /// <summary>口令够不够格上锁（只看长度，无复杂度强制）。Trim 后再量：与挂牌那一步的 Trim 同一口径。</summary>
    public static bool MeetsPasswordPolicy(string? password) =>
        !string.IsNullOrWhiteSpace(password) && password.Trim().Length >= MinPasswordLength;

    /// <summary>
    /// 存量无盐 SHA256 → PBKDF2 的透明升级：只在口令验对那一刻发生。
    /// 落库失败不许反过来把人挡在门外（口令已经验对）：留着旧哈希、记一笔，下次解锁再铸。
    /// </summary>
    private void UpgradeLegacyKey(string password)
    {
        try
        {
            db.SetSetting("admin-key", MemberPasswords.Hash(password));
            logger.LogInformation("站长室口令已从无盐 SHA256 升级为 PBKDF2 存储");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "站长室口令升级 PBKDF2 落库失败，本次仍按旧哈希放行");
        }
    }

    /// <summary>
    /// 口令校验的唯一入口：新铸的走 <see cref="MemberPasswords"/>（PBKDF2 加盐 210000 轮），
    /// settings 里存量那枚 64 位 hex 无盐 SHA256 照旧认（现网真站点的数据不能因为这次加固被锁死），
    /// 认对时通过 <paramref name="needsUpgrade"/> 知会调用方换铸。两条比对都是定时的，不给旁路测密留缝。
    /// </summary>
    private static bool VerifyPassword(string? stored, string candidate, out bool needsUpgrade)
    {
        needsUpgrade = false;
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        if (MemberPasswords.Verify(stored, candidate))
        {
            return true;
        }

        if (!IsLegacySha256(stored))
        {
            return false;
        }

        // 存量那一枚：先定形（十六进制不分大小写）再比；长度不等时 FixedTimeEquals 直接给假，不多泄字数。
        var storedHex = Encoding.UTF8.GetBytes(stored.ToLowerInvariant());
        var candidateHex = Encoding.UTF8.GetBytes(Hash(candidate));
        if (!CryptographicOperations.FixedTimeEquals(storedHex, candidateHex))
        {
            return false;
        }

        needsUpgrade = true;
        return true;
    }

    /// <summary>存量哈希的形状：正好 64 位十六进制。PBKDF2 那形带 $ 分隔与轮数段，两形不会互相冒充。</summary>
    private static bool IsLegacySha256(string stored)
    {
        return stored.Length == 64 && stored.All(Uri.IsHexDigit);
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

    /// <summary>
    /// 存量（无盐 SHA256）口令的算法：只留给 <see cref="VerifyPassword"/> 验旧哈希，新锁一律走 <see cref="MemberPasswords"/>。
    /// 收成 internal 是为了不让业处顺手拿它去设新锁（那等于绕开加盐）；测试经 <c>InternalsVisibleTo</c> 拿它伪造存量哈希。
    /// </summary>
    internal static string Hash(string password)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
    }
}
