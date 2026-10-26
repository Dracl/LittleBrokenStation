using LBS.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LBS.Tests;

/// <summary>
/// 反爬节流闸（LbsApiThrottle）：随机间隔、风控熔断翻倍、冷却等待、连续健康销案、CDN 豁免。
/// 用手动时钟让时间随等待前进，测试不真睡觉也不碰网络。
/// </summary>
public sealed class ApiThrottleTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // 假表：到点即发，时间随等待前进。
            Delays.Add(dueTime);
            Now += dueTime;
            callback(state);
            return new NullTimer();
        }

        private sealed class NullTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class NoWarmupThrottle(ManualTimeProvider clock, LbsThrottleOptions options)
        : LbsApiThrottle(clock, NullLogger<LbsApiThrottle>.Instance, options)
    {
        public int WarmupCount { get; private set; }

        protected override Task EnsureWarmupAsync(CancellationToken cancellationToken)
        {
            WarmupCount++;
            return Task.CompletedTask;
        }
    }

    private static readonly Uri ApiUri = new("https://api.bilibili.com/x/space/wbi/arc/search");
    private static readonly Uri CdnUri = new("https://i0.hdslb.com/bfs/face/abc.jpg");

    private static NoWarmupThrottle Create(ManualTimeProvider clock) =>
        new(clock, new LbsThrottleOptions(MinIntervalMs: 350, MaxIntervalMs: 800, BaseCooldownSeconds: 60, MaxCooldownSeconds: 150, SuccessStreakReset: 20));

    [Fact]
    public async Task 两次过闸之间隔随机最小间隔()
    {
        var clock = new ManualTimeProvider();
        var throttle = Create(clock);

        await throttle.EnterAsync(ApiUri, CancellationToken.None);
        await throttle.EnterAsync(ApiUri, CancellationToken.None);

        Assert.Single(clock.Delays);
        Assert.InRange(clock.Delays[0], TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(800));
        // 预热每次过闸都查 TTL，假实现只数调用次数：两次过闸 = 两次查岗。
        Assert.Equal(2, throttle.WarmupCount);
    }

    [Fact]
    public void 风控命中进冷却且时长翻倍封顶()
    {
        var clock = new ManualTimeProvider();
        var throttle = Create(clock);

        throttle.ReportResult(ApiUri, "{\"code\":-352,\"message\":\"风控拦截\"}", null);
        Assert.True(throttle.IsCoolingDown);
        Assert.Equal(60, throttle.CooldownRemaining!.Value.TotalSeconds);

        throttle.ReportResult(ApiUri, null, 412);
        Assert.Equal(120, throttle.CooldownRemaining!.Value.TotalSeconds);

        throttle.ReportResult(ApiUri, null, 429);
        Assert.Equal(150, throttle.CooldownRemaining!.Value.TotalSeconds); // 封顶
    }

    [Fact]
    public async Task 冷却期过闸先等冷却再等间隔()
    {
        var clock = new ManualTimeProvider();
        var throttle = Create(clock);
        throttle.ReportResult(ApiUri, "{\"code\":-352}", null);

        await throttle.EnterAsync(ApiUri, CancellationToken.None);

        // 第一段等满冷却（60s 内某刻），第二段过完随机间隔；总时长 ≥ 冷却起点。
        Assert.True(clock.Delays.Sum(d => d.TotalSeconds) >= 60);
        Assert.False(throttle.IsCoolingDown);
    }

    [Fact]
    public void 连续健康请求攒够销案_下次命中从基础档重开()
    {
        var clock = new ManualTimeProvider();
        var throttle = Create(clock);

        throttle.ReportResult(ApiUri, null, 412); // 第 1 次命中
        for (var i = 0; i < 20; i++)
        {
            throttle.ReportResult(ApiUri, "{\"code\":0}", 200);
        }

        throttle.ReportResult(ApiUri, null, 412);
        Assert.Equal(60, throttle.CooldownRemaining!.Value.TotalSeconds); // 不是翻倍后的 120
    }

    [Fact]
    public async Task CDN与静态资源不占闸也不计风控()
    {
        var clock = new ManualTimeProvider();
        var throttle = Create(clock);

        await throttle.EnterAsync(CdnUri, CancellationToken.None);
        throttle.ReportResult(CdnUri, "{\"code\":-352}", 412);

        Assert.Empty(clock.Delays);
        Assert.False(throttle.IsCoolingDown);
        Assert.Equal(0, throttle.WarmupCount);
    }
}
