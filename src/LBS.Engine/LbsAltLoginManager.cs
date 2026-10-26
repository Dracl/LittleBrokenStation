using System.Text.Json;
using LBS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>副登录号的展示态（cookie 本体绝不外出，任何 API/日志都不携带）。</summary>
public sealed record LbsAltState(string Name, long? Mid, int VipStatus, string? Face, DateTimeOffset SavedAtUtc);

/// <summary>
/// 副登录（查询分流）：视频下载仍走站长主号（上游 LoginHelper 单槽，红线不碰），
/// 分站自己发起的查询类请求（UP 名片 / view 统计 / 评论采集）由副号代发，
/// 两个号各扛一路，分散单号风控压力。
/// cookie 只进不出：盘存 data/cookies/alt-login.json，回显与日志只有昵称/mid/vip。
/// </summary>
public sealed class LbsAltLoginManager(DataPaths paths, ILogger<LbsAltLoginManager> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly object gate = new();
    private readonly string storePath = Path.Combine(paths.Cookies, "alt-login.json");
    private Entry? cached;
    private bool loaded;

    private sealed record Entry(string Cookie, string Name, long? Mid, int VipStatus, string? Face, DateTimeOffset SavedAtUtc);

    public LbsAltState? GetState()
    {
        var entry = Load();
        return entry is null ? null : new LbsAltState(entry.Name, entry.Mid, entry.VipStatus, entry.Face, entry.SavedAtUtc);
    }

    /// <summary>给分流链路按号取 cookie：主号返回 null（调用方走上游单槽），副号未配也返回 null。</summary>
    public string? ForOwner(LbsOwner owner) => owner == LbsOwner.Alt ? TryGetCookieHeader() : null;

    /// <summary>给 LbsQueryClient 用的原始 cookie 头；未配置返回 null（调用方回落主号）。</summary>
    public string? TryGetCookieHeader() => Load()?.Cookie;

    public async Task<(bool Ok, string? Error, LbsAltState? State)> SetAsync(string? cookieHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return (false, "粘贴框是空的。先用另一个账号在浏览器登录 www.bilibili.com，再从开发者工具整行复制 Cookie。", null);
        }

        var cookie = cookieHeader.Trim();
        if (!cookie.Contains("SESSDATA", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "这份 cookie 里没有 SESSDATA，代表不了登录态；先在浏览器登录 B 站再复制。", null);
        }

        var verified = await VerifyAsync(cookie, cancellationToken);
        if (verified is null)
        {
            return (false, "B 站没认这份副号 cookie（多半是过期或触发风控），未保存；重新登录那个账号后再试。", null);
        }

        var entry = new Entry(cookie, verified.Value.Name, verified.Value.Mid, verified.Value.Vip, verified.Value.Face, DateTimeOffset.UtcNow);
        lock (gate)
        {
            File.WriteAllText(storePath, JsonSerializer.Serialize(entry));
            cached = entry;
            loaded = true;
        }

        logger.LogInformation("副登录号已就位：「{Name}」（mid={Mid}），查询流量从此分流", entry.Name, entry.Mid);
        return (true, null, new LbsAltState(entry.Name, entry.Mid, entry.VipStatus, entry.Face, entry.SavedAtUtc));
    }

    public void Clear()
    {
        lock (gate)
        {
            cached = null;
            loaded = true;
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }

        logger.LogInformation("副登录号已移除，查询流量回到主号");
    }

    private Entry? Load()
    {
        lock (gate)
        {
            if (loaded)
            {
                return cached;
            }

            loaded = true;
            if (!File.Exists(storePath))
            {
                return null;
            }

            try
            {
                cached = JsonSerializer.Deserialize<Entry>(File.ReadAllText(storePath));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "副登录文件读坏了，按未配置处理");
                cached = null;
            }

            return cached;
        }
    }

    private async Task<(string Name, long? Mid, int Vip, string? Face)?> VerifyAsync(string cookie, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            req.Headers.Referrer = new Uri("https://www.bilibili.com");
            req.Headers.Add("cookie", cookie);
            req.Headers.UserAgent.ParseAdd(EngineOptions.DefaultUserAgent);
            using var res = await Http.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 0)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("isLogin", out var isLogin)
                || !isLogin.GetBoolean())
            {
                return null;
            }

            var name = data.TryGetProperty("uname", out var u) && u.GetString() is { } un ? un : "B 站用户";
            long? mid = data.TryGetProperty("mid", out var m) && m.TryGetInt64(out var mv) ? mv : null;
            int vip = data.TryGetProperty("vipStatus", out var v) && v.TryGetInt32(out var vv) ? vv : 0;
            string? face = data.TryGetProperty("face", out var f) ? f.GetString() : null;
            return (name, mid, vip, face);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "副号验证请求没能到达 B 站");
            return null;
        }
    }
}
