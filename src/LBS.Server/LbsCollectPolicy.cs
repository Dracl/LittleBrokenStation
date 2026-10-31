using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站里的活怎么出号。分站不再有站级登录态：评论/弹幕归档、UP 名片、动态、收藏导入、编码加档
/// 统一吃「采集号」——站长席位（members.role='owner'）的活号优先，其次任一支活跳席位的活号（主力优先）。
/// 投稿链不吃这里，那条链必须拿投稿人自己的号（DownloadWorker 里管）。
///
/// 号池空了不装忙：一律抛 <see cref="LbsNoAccountException"/>，由调用方把本轮标成无号并写明原因，
/// 绝不拿匿名请求的 -101 结果冒充「采到了空数据」。
///
/// 方法名沿用旧的 RunForVideoAsync / RunForActivityAsync / BeginForVideo，是为了让六处采集入口
/// 不必跟着改；语义已变——不再按视频分主副号，全站同一时刻只有一个采集号在跑。
/// </summary>
public sealed class LbsCollectPolicy(StationDb db, LbsAccountRouter router, ILogger<LbsCollectPolicy> logger)
{
    /// <summary>此刻能用的采集号（只报昵称/mid/头像，cookie 不出门）；号池空了返回 null。</summary>
    public CollectAccountCredential? CurrentAccount() => db.PickAccountForCollect();

    /// <summary>有没有采集号可用。轮班的 worker 一轮开工前先问一句，省得每支视频都抛一次异常刷日志。</summary>
    public bool HasAccount() => db.PickAccountForCollect() is not null;

    /// <summary>最近一次「判无号」的时刻（UTC，ISO 文本），给站长室柜台报「采集停摆多久了」；从没停过为 null。</summary>
    public string? LastNoAccountUtc { get; private set; }

    /// <summary>视频相关的采集（评论、弹幕、UP 名片与统计）。videoId 只留着日志好查，不再决定用哪个号。</summary>
    public Task<T> RunForVideoAsync<T>(long videoId, Func<Task<T>> action) => RunAsync(action);

    /// <summary>非泛型重载，语义同 <see cref="RunForVideoAsync{T}"/>。</summary>
    public Task RunForVideoAsync(long videoId, Func<Task> action) => RunAsync(action);

    /// <summary>不挂具体视频的采集动作（收藏整架导入、动态巡捕）。</summary>
    public Task<T> RunForActivityAsync<T>(Func<Task<T>> action) => RunAsync(action);

    /// <summary>非泛型重载，给只交出 Task 的执行链用（worker 环境的 ExecuteAsync）。</summary>
    public Task RunForActivityAsync(Func<Task> action) => RunAsync(action);

    /// <summary>
    /// 手动开一段采集号上下文：一趟里多个采集调用共用同一个号时用（状态同步员一轮一个号）。
    /// 出号在此刻定死，中途成员换了号也不改口，免得一趟里换号被 B 站当成漂移。
    /// </summary>
    public IDisposable BeginForVideo(long videoId)
    {
        var account = Require();
        return router.BeginCollectScope(account.Cookie, Hint(account));
    }

    private Task RunAsync(Func<Task> action)
    {
        var account = Require();
        return router.RunAsCollectAsync(account.Cookie, Hint(account), action);
    }

    private Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        var account = Require();
        return router.RunAsCollectAsync(account.Cookie, Hint(account), action);
    }

    private CollectAccountCredential Require()
    {
        var account = db.PickAccountForCollect();
        if (account is null)
        {
            LastNoAccountUtc = DateTimeOffset.UtcNow.ToString("O");
            logger.LogWarning("站里没有可用的采集号（成员号池空了或全被判失效），本轮采集与加档判无号");
            throw new LbsNoAccountException(
                "站里没配采集号：任一成员（站长自己的号优先）到「何处？用户控制室」绑一个未过期的 B 站号，采集与加档就自动恢复。");
        }

        return account;
    }

    private static LbsAccountHint Hint(CollectAccountCredential account) =>
        new(account.MemberId, account.AccountMid, account.AccountName, LbsAccountUse.Collect);
}
