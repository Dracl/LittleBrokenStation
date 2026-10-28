namespace LBS.Engine;

/// <summary>当前这条调用链在替谁干活。</summary>
public enum LbsAccountUse
{
    /// <summary>不在任何出号上下文里（正常路径不该出现）。</summary>
    None = 0,

    /// <summary>投稿人自己的号：首次入站的提交链。</summary>
    Submitter = 1,

    /// <summary>采集号：评论、弹幕、UP 名片、动态、收藏导入，以及编码加档这些「站里的活」。</summary>
    Collect = 2,
}

/// <summary>
/// 当前出号对象的展示态。只带 mid 与昵称，cookie 本体绝不进这里（任何 API、日志、异常都不携带）。
/// </summary>
/// <param name="MemberId">成员席位 id（采集号也是某位成员的号，站长席位优先）。</param>
/// <param name="AccountMid">B 站 mid。</param>
/// <param name="AccountName">B 站昵称，可空。</param>
/// <param name="Use">这路号的用途。</param>
public sealed record LbsAccountHint(long MemberId, long AccountMid, string? AccountName, LbsAccountUse Use);

/// <summary>
/// 出号路由器：cookie 一律来自成员账号池，按调用链的环境号现取（AsyncLocal 顺着 await 链流下去，
/// 上游客户端每个请求都来这里取，所以 playurl、view info 这些风控敏感请求自动挂上对应的号）。
///
/// 分站不再有站级登录态：原先的「站长主号单槽 + 副号分流」整套已退役，谁的事谁出号——
/// 投稿链吃投稿人自己的号，采集与加档吃采集号（站长席位的活号优先）。取不到号就抛
/// <see cref="LbsNoAccountException"/>，绝不把匿名请求发去 B 站挨风控、也不拿匿名结果冒充数据。
/// </summary>
public sealed class LbsAccountRouter
{
    private static readonly AsyncLocal<Slot?> Ambient = new();

    private sealed record Slot(string Cookie, LbsAccountHint Hint);

    /// <summary>当前调用链的 cookie；不在任何出号上下文里时为 null。</summary>
    public string? CurrentCookie => Ambient.Value?.Cookie;

    /// <summary>当前调用链在替谁干活（只给日志与状态用，不带 cookie）。</summary>
    public LbsAccountHint? CurrentAccount => Ambient.Value?.Hint;

    /// <summary>
    /// 把调用链的 cookie 换成投稿人自己的号（成员账号池）。cookie 只进这条链，不落任何回显。
    /// </summary>
    public Task<T> RunAsMemberAsync<T>(string cookie, long accountMid, Func<Task<T>> action) =>
        RunAsAsync(cookie, new LbsAccountHint(0, accountMid, null, LbsAccountUse.Submitter), action);

    /// <summary>非泛型重载，语义同 <see cref="RunAsMemberAsync{T}"/>。</summary>
    public Task RunAsMemberAsync(string cookie, long accountMid, Func<Task> action) =>
        RunAsAsync(cookie, new LbsAccountHint(0, accountMid, null, LbsAccountUse.Submitter), action);

    /// <summary>
    /// 把调用链钉在采集号上（站里的活）。cookie 由 Server 侧的采集号策略解析出来再交进来，
    /// Engine 不认识成员台账，只管「这条链用哪个 cookie」。
    /// </summary>
    public Task<T> RunAsCollectAsync<T>(string cookie, LbsAccountHint hint, Func<Task<T>> action) =>
        RunAsAsync(cookie, hint with { Use = LbsAccountUse.Collect }, action);

    /// <summary>非泛型重载，语义同 <see cref="RunAsCollectAsync{T}"/>。</summary>
    public Task RunAsCollectAsync(string cookie, LbsAccountHint hint, Func<Task> action) =>
        RunAsAsync(cookie, hint with { Use = LbsAccountUse.Collect }, action);

    /// <summary>
    /// 手动开一段采集号上下文（一趟里多个采集调用共用同一个号时用，比如状态同步员的一轮）。
    /// </summary>
    public IDisposable BeginCollectScope(string cookie, LbsAccountHint hint)
    {
        var previous = Ambient.Value;
        Ambient.Value = new Slot(cookie, hint with { Use = LbsAccountUse.Collect });
        return new Scope(previous);
    }

    private Task RunAsAsync(string cookie, LbsAccountHint hint, Func<Task> action)
    {
        var previous = Ambient.Value;
        Ambient.Value = new Slot(cookie, hint);
        try
        {
            return action();
        }
        finally
        {
            Ambient.Value = previous;
        }
    }

    private Task<T> RunAsAsync<T>(string cookie, LbsAccountHint hint, Func<Task<T>> action)
    {
        var previous = Ambient.Value;
        Ambient.Value = new Slot(cookie, hint);
        try
        {
            return action();
        }
        finally
        {
            Ambient.Value = previous;
        }
    }

    private sealed class Scope(Slot? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
