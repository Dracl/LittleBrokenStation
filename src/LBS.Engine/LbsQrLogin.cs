using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>扫码登录的一次挑战：url 交给前端渲染成二维码，qrcodeKey 是轮询凭证。</summary>
public sealed record QrChallenge(string Url, string QrcodeKey);

/// <summary>轮询阶段（对齐 B 站 poll 的 code：0/86038/86090/86101）。</summary>
public enum QrPhase
{
    Waiting,
    Scanned,
    Expired,
    Success,
    Unknown,
}

/// <summary>轮询结果：成功那一刻才带 cookie（只进不出，交给调用方落库，绝不回显）。</summary>
public sealed record QrPollResult(QrPhase Phase, string? Message, string? CookieHeader, string? RefreshToken);

/// <summary>B 站侧的身份快照（nav 验证的产物）。</summary>
public sealed record BilibiliIdentity(long Mid, string Name, string? Face);

/// <summary>
/// 扫码登录代理（服务端握手，前端只见状态不见 cookie）：
/// generate 拿 url+key → 前端渲染二维码 → 本站代 poll → 成功瞬间从 Set-Cookie 收割登录 cookie。
/// 协议对齐 B 站 web 端（参考 top.colter.bilibili 的 qrCode 流程）：
/// poll 的 data.code 0 成功 / 86038 过期 / 86090 已扫未确认 / 86101 未扫码。
/// </summary>
public sealed class LbsQrLogin
{
    private const string GenerateUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-feader";
    private const string PollUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";
    private const string NavUrl = "https://api.bilibili.com/x/web-interface/nav";

    /// <summary>登录 cookie 里必须收齐的件：缺 SESSDATA 就当没登录。</summary>
    private static readonly string[] RequiredCookie = ["SESSDATA"];
    private static readonly string[] WantedCookies = ["SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid"];

    private readonly HttpClient _http;
    private readonly ILogger<LbsQrLogin> _logger;

    public LbsQrLogin(ILogger<LbsQrLogin> logger) : this(logger, handler: null)
    {
    }

    /// <summary>handler 参数只为单测桩 HTTP；生产走上面的 DI 构造。</summary>
    public LbsQrLogin(ILogger<LbsQrLogin> logger, HttpMessageHandler? handler)
    {
        _logger = logger;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }
            : new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>向 B 站领一张登录二维码（url 给前端渲染，key 留给轮询）。</summary>
    public async Task<QrChallenge?> NewChallengeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await GetJsonAsync(GenerateUrl, null, cancellationToken);
            if (doc is null || !doc.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 0)
            {
                _logger.LogWarning("扫码登录取码被 B 站拒绝");
                return null;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.GetProperty("url").GetString() is not { Length: > 0 } url
                || data.GetProperty("qrcode_key").GetString() is not { Length: > 0 } key)
            {
                return null;
            }

            return new QrChallenge(url, key);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫码登录取码请求没能到达 B 站");
            return null;
        }
    }

    /// <summary>
    /// 代客轮询：前端只报 key，状态由本站翻译（waiting/scanned/expired/success）。
    /// 成功瞬间把 Set-Cookie 里的登录件收拢成 cookie 串（含 refresh_token 供续期用）。
    /// </summary>
    public async Task<QrPollResult> PollAsync(string qrcodeKey, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{PollUrl}?qrcode_key={Uri.EscapeDataString(qrcodeKey)}&source=main-feader";
            using var req = NewRequest(url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("code", out var codeEl))
            {
                return new QrPollResult(QrPhase.Unknown, "B 站轮询响应看不懂", null, null);
            }

            var message = data.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            var refreshToken = data.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

            switch (codeEl.GetInt32())
            {
                case 0:
                {
                    var cookie = HarvestCookies(res.Headers);
                    if (cookie is null)
                    {
                        _logger.LogWarning("扫码说成功了，可 Set-Cookie 里没见到 SESSDATA");
                        return new QrPollResult(QrPhase.Unknown, "登录回执不完整", null, null);
                    }

                    return new QrPollResult(QrPhase.Success, message, cookie, refreshToken);
                }
                case 86038:
                    return new QrPollResult(QrPhase.Expired, message ?? "二维码已失效", null, null);
                case 86090:
                    return new QrPollResult(QrPhase.Scanned, message ?? "已扫码，等待确认", null, null);
                case 86101:
                    return new QrPollResult(QrPhase.Waiting, message, null, null);
                default:
                    return new QrPollResult(QrPhase.Unknown, message, null, null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫码轮询请求没能到达 B 站");
            return new QrPollResult(QrPhase.Unknown, "网络不畅，稍后再试", null, null);
        }
    }

    /// <summary>拿 cookie 到 nav 验明正身（mid/昵称/头像），不认则 null。</summary>
    public async Task<BilibiliIdentity?> ProbeIdentityAsync(string cookieHeader, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await GetJsonAsync(NavUrl, cookieHeader, cancellationToken);
            if (doc is null
                || !doc.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 0
                || !doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("isLogin", out var isLogin) || !isLogin.GetBoolean())
            {
                return null;
            }

            if (!data.TryGetProperty("mid", out var m) || !m.TryGetInt64(out var mid))
            {
                return null;
            }

            var name = data.TryGetProperty("uname", out var u) && u.GetString() is { Length: > 0 } un ? un : "B 站用户";
            string? face = data.TryGetProperty("face", out var f) ? f.GetString() : null;
            return new BilibiliIdentity(mid, name, face);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "nav 验证请求没能到达 B 站");
            return null;
        }
    }

    private static HttpRequestMessage NewRequest(string url, string? cookie = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://www.bilibili.com");
        req.Headers.UserAgent.ParseAdd(EngineOptions.DefaultUserAgent);
        if (cookie != null)
        {
            req.Headers.TryAddWithoutValidation("cookie", cookie);
        }

        return req;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, string? cookie, CancellationToken cancellationToken)
    {
        using var req = NewRequest(url, cookie);
        using var res = await _http.SendAsync(req, cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>从响应的 Set-Cookie 头里挑出登录五件套，拼成可直接发送的 cookie 串。</summary>
    internal static string? HarvestCookies(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Set-Cookie", out var raws))
        {
            return null;
        }

        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in raws)
        {
            var pair = raw.Split(';')[0];
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (value.Length > 0 && WantedCookies.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                picked[name] = value;
            }
        }

        if (!RequiredCookie.All(k => picked.ContainsKey(k)))
        {
            return null;
        }

        return string.Join("; ", picked.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
