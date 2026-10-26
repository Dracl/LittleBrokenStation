namespace LBS.Engine;

/// <summary>视频归属号：一个视频的提交、加档、采集全部跟着同一个号走。</summary>
public enum LbsOwner
{
    Main = 0,
    Alt = 1,
}

/// <summary>采集账号偏好：混合=跟随视频归属（无主动作轮转）；主/副=采集一律走指定号。</summary>
public enum LbsCollectMode
{
    Mixed = 0,
    Main = 1,
    Alt = 2,
}

/// <summary>
/// 按视频分号的路由器：提交时轮转分配归属（视频1主、视频2副……），归属沿 AsyncLocal
/// 环境号跟随整条调用链——上游客户端每个请求都现取 cookie，所以只要处在同一个 await 链里，
/// playurl / view info 这些风控敏感请求就自动挂对应号的 cookie。
/// 没有副号时一切（分配、环境、采集）都回到主号；副号中途被摘也即时回落，下载不断。
/// </summary>
public sealed class LbsAccountRouter(LbsAltLoginManager alt)
{
    private static readonly AsyncLocal<LbsOwner?> Ambient = new();
    private int _turn;

    /// <summary>采集偏好。启动时从库回填，设置 API 现改现生效。</summary>
    public LbsCollectMode CollectMode { get; set; } = LbsCollectMode.Mixed;

    /// <summary>当前调用链的归属号；不在任何提交/采集上下文里时按主号对待。</summary>
    public LbsOwner Current => Ambient.Value ?? LbsOwner.Main;

    public bool AltReady => alt.TryGetCookieHeader() is not null;

    /// <summary>把请求号折成实际可用的号：副号没配就一律主号。</summary>
    public LbsOwner Effective(LbsOwner owner) =>
        owner == LbsOwner.Alt && !AltReady ? LbsOwner.Main : owner;

    /// <summary>新视频进站的轮转分配：主、副、主、副……（无副号恒为主）。</summary>
    public LbsOwner AssignNext() =>
        AltReady && Interlocked.Increment(ref _turn) % 2 == 0 ? LbsOwner.Alt : LbsOwner.Main;

    /// <summary>无视频上下文的采集动作（如收藏夹导入）按采集偏好出号。</summary>
    public LbsOwner CollectOwner() => CollectMode switch
    {
        LbsCollectMode.Main => LbsOwner.Main,
        LbsCollectMode.Alt => Effective(LbsOwner.Alt),
        _ => AssignNext(),
    };

    /// <summary>把当前调用链钉在某个号上（using 作用域）。</summary>
    public IDisposable BeginScope(LbsOwner owner)
    {
        var previous = Ambient.Value;
        Ambient.Value = owner;
        return new Scope(previous);
    }

    /// <summary>在指定号的上下文里跑一段异步活（评论采集、UP 卡刷新这类入口用）。</summary>
    public Task RunAsAsync(LbsOwner owner, Func<Task> action)
    {
        using var _ = BeginScope(owner);
        return action();
    }

    public Task<T> RunAsAsync<T>(LbsOwner owner, Func<Task<T>> action)
    {
        using var _ = BeginScope(owner);
        return action();
    }

    private sealed class Scope(LbsOwner? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
