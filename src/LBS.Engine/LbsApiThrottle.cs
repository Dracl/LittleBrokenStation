using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>节流与熔断的刻度盘，测试可以拧快发条（缩短间隔与冷却）。</summary>
public sealed record LbsThrottleOptions(
    int MinIntervalMs = 350,
    int MaxIntervalMs = 800,
    int BaseCooldownSeconds = 300,
    int MaxCooldownSeconds = 3600,
    int SuccessStreakReset = 20,
    int WarmupTtlMinutes = 30);

/// <summary>
/// 全站唯一的 B 站 API 节流闸（借鉴 RC-WebGallery 的反爬打法，2026-10）：
/// ① 每次对 B 站接口的请求前随机歇 350~800ms——请求节奏从恒速变成人手抖；
/// ② 认出风控信号（HTTP 412/429、body code=-352）就进冷却：5 分钟起步、命中翻倍、封顶 1 小时，
///    连续 20 次健康请求后销案；冷却期爬工跳轮、手动导入吃 423，后台队列耐心等；
/// ③ 每 30 分钟给会话做一次匿名预热（访问首页领指纹）再打接口，等效 RC 的 prime session。
/// 立此存照的规矩：本类与 LbsQueryClient 的 HttpClient 永不挂 CookieContainer 自动回写——
/// buvid3/buvid4 是设备指纹，被匿名响应的 Set-Cookie 覆盖会让 B 站按指纹漂移吊销 SESSDATA（RC 源码注释实录的坑）。
/// </summary>
public class LbsApiThrottle(TimeProvider timeProvider, ILogger<LbsApiThrottle> logger, LbsThrottleOptions? options = null)
{
    // 预热用的匿名客户端：只拿指纹不存 cookie，所以绝不配 CookieContainer。
    private static readonly HttpClient WarmHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly LbsThrottleOptions options = options ?? new LbsThrottleOptions();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object riskLock = new();
    private DateTimeOffset nextAllowedAt;
    private DateTimeOffset? cooldownUntil;
    private int riskHits;
    private int successStreak;
    private DateTimeOffset? lastWarmupAt;

    /// <summary>请求是否算「对 B 站接口」：CDN（hdslb/bilivideo）不吃风控，不占闸。</summary>
    public static bool IsBilibiliApiTarget(Uri uri) =>
        uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "bilibili.com", StringComparison.OrdinalIgnoreCase);

    public bool IsCoolingDown
    {
        get
        {
            lock (riskLock)
            {
                return cooldownUntil is { } until && timeProvider.GetUtcNow() < until;
            }
        }
    }

    public TimeSpan? CooldownRemaining
    {
        get
        {
            lock (riskLock)
            {
                if (cooldownUntil is not { } until)
                {
                    return null;
                }

                var left = until - timeProvider.GetUtcNow();
                return left > TimeSpan.Zero ? left : null;
            }
        }
    }

    /// <summary>
    /// 每次 B 站接口请求前过闸：预热 → 等冷却（若在做客）→ 等最小间隔 → 放行并预约下一班。
    /// 后台工（爬工/下载/评论）耐心等到放行；急性子（手动端点）先查 <see cref="IsCoolingDown"/> 自己走人。
    /// </summary>
    public async Task EnterAsync(Uri target, CancellationToken cancellationToken)
    {
        if (!IsBilibiliApiTarget(target))
        {
            return;
        }

        await EnsureWarmupAsync(cancellationToken);

        await gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var now = timeProvider.GetUtcNow();
                TimeSpan? wait = null;
                lock (riskLock)
                {
                    if (cooldownUntil is { } until && now < until)
                    {
                        wait = until - now;
                    }
                    else if (now < nextAllowedAt)
                    {
                        wait = nextAllowedAt - now;
                    }

                    if (wait is null && cooldownUntil is { } u && timeProvider.GetUtcNow() >= u)
                    {
                        // 冷却已过期，清牌（下轮命中重新从基础时长起步由 ReportRisk 的翻倍逻辑管）。
                        cooldownUntil = null;
                    }

                    if (wait is null)
                    {
                        nextAllowedAt = timeProvider.GetUtcNow() + TimeSpan.FromMilliseconds(
                            Random.Shared.Next(options.MinIntervalMs, options.MaxIntervalMs + 1));
                        return;
                    }
                }

                await WaitAsync(wait.Value, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 请求回来后交卷：认出风控信号就进冷却（时长翻倍递增，封顶），健康响应攒连续成功、攒够销案。
    /// body 传响应文本（可空），statusCode 传 HTTP 状态（可空，主号回落链路拿不到状态码时靠 body 兜 -352）。
    /// </summary>
    public void ReportResult(Uri target, string? body, int? statusCode)
    {
        if (!IsBilibiliApiTarget(target))
        {
            return;
        }

        var risk = statusCode is 412 or 429
            || (body?.Contains("\"code\":-352", StringComparison.Ordinal) ?? false)
            || (body?.Contains("\"code\": -352", StringComparison.Ordinal) ?? false);

        lock (riskLock)
        {
            if (risk)
            {
                riskHits++;
                var seconds = Math.Min(
                    options.BaseCooldownSeconds * (1 << Math.Min(riskHits - 1, 16)),
                    options.MaxCooldownSeconds);
                cooldownUntil = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(seconds);
                successStreak = 0;
                logger.LogWarning(
                    "撞上 B 站风控（{Signal}），分站冷却 {Minutes} 分钟（第 {Hits} 次命中），爬工与导入暂歇",
                    statusCode is null ? "body code=-352" : $"HTTP {statusCode}",
                    seconds / 60, riskHits);
            }
            else
            {
                successStreak++;
                if (successStreak >= options.SuccessStreakReset)
                {
                    riskHits = 0;
                    successStreak = 0;
                }
            }
        }
    }

    /// <summary>跟着注入时钟睡觉：测试换个假表就能快进，生产走系统定时器。</summary>
    private async Task WaitAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = timeProvider.CreateTimer(_ => tcs.TrySetResult(), null, wait, Timeout.InfiniteTimeSpan);
        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            timer.Dispose();
        }
    }

    /// <summary>匿名预热：访问 B 站首页一次，让服务端把设备指纹发过来。TTL 内只热一次，翻车不拦路。测试可接管。</summary>
    protected virtual async Task EnsureWarmupAsync(CancellationToken cancellationToken)
    {
        lock (riskLock)
        {
            if (lastWarmupAt is { } at && timeProvider.GetUtcNow() - at < TimeSpan.FromMinutes(options.WarmupTtlMinutes))
            {
                return;
            }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
            LbsBrowserFingerprints.Apply(req);
            using var _ = await WarmHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "会话预热没热成，照常干活");
        }
        finally
        {
            lock (riskLock)
            {
                lastWarmupAt = timeProvider.GetUtcNow();
            }
        }
    }
}

/// <summary>
/// 浏览器指纹随机池（RC-WebGallery 同款思路）：UA、Accept-Language、Sec-CH-UA 族与 Priority 成套随机，
/// 同一请求内 UA 与 Sec-CH-UA 必须成套（版本/平台错位反而是破绽）。
/// </summary>
public static class LbsBrowserFingerprints
{
    private sealed record Fingerprint(string UserAgent, string ChUa, string Platform);

    private static readonly Fingerprint[] Pool =
    [
        new("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36",
            "\"Chromium\";v=\"136\", \"Google Chrome\";v=\"136\", \"Not.A/Brand\";v=\"24\"", "\"Windows\""),
        new("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36",
            "\"Chromium\";v=\"135\", \"Google Chrome\";v=\"135\", \"Not.A/Brand\";v=\"24\"", "\"macOS\""),
        new("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36",
            "\"Chromium\";v=\"134\", \"Google Chrome\";v=\"134\", \"Not.A/Brand\";v=\"24\"", "\"Linux\""),
    ];

    private static readonly string[] AcceptLanguages =
    [
        "zh-CN,zh;q=0.9,en;q=0.8",
        "zh-CN,zh;q=0.95,en;q=0.75",
        "zh-CN,zh;q=0.9,ja;q=0.7,en;q=0.6",
    ];

    private static readonly string[] Priorities = ["u=1, i", "u=2, i", "u=3"];

    /// <summary>给请求装上一套成套的浏览器头（不动调用方已设的 Cookie/Referer/origin）。</summary>
    public static void Apply(HttpRequestMessage request)
    {
        var fp = Pool[Random.Shared.Next(Pool.Length)];
        request.Headers.UserAgent.ParseAdd(fp.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguages[Random.Shared.Next(AcceptLanguages.Length)]);
        request.Headers.TryAddWithoutValidation("Sec-CH-UA", fp.ChUa);
        request.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile", "?0");
        request.Headers.TryAddWithoutValidation("Sec-CH-UA-Platform", fp.Platform);
        request.Headers.TryAddWithoutValidation("Priority", Priorities[Random.Shared.Next(Priorities.Length)]);
    }
}
