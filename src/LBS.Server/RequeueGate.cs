using LBS.Domain;

namespace LBS.Server;

/// <summary>
/// 重投与认领的判据（纯函数）。端点只负责把判据翻成 HTTP，判据本身住在这里——
/// 本仓无 HTTP harness，判据只有抽成纯函数才钉得住真值表（SubmissionGate / ManageGate 同一家族）。
/// </summary>
public static class RequeueGate
{
    /// <summary>重投这一脚的四种下场。</summary>
    public enum Verdict
    {
        /// <summary>可以打回待提交重排。</summary>
        Requeue,

        /// <summary>这状态本就不该重投（排队中、下载中、已到站）。</summary>
        NotRequeueable,

        /// <summary>失败且没有投稿人席位，且来人是站长：得先认领（补归属），否则重排出去在
        /// DownloadWorker 那里照样判无号，白跑一趟还再记一次失败。</summary>
        NeedClaim,

        /// <summary>失败、无归属、来人又不是站长：这条轮不到你救。</summary>
        NoAccess,
    }

    /// <summary>
    /// 能不能重投。Skipped / Canceled 是旧世界就有的两路（原样保留）；
    /// Failed 是今夜新开的门——失败本来就是「等人来救」的状态，以前却只有被取消的才准重排，
    /// 一句「只有被取消的馆藏才需要重新排队」把失败行堵成死档。
    /// 有归属的失败行照旧归归属门管（上游 CanManageVideo 已把非本人非站长挡过，这里不再重查）；
    /// 无归属的失败行只有站长救得动，且必须先认领。
    /// </summary>
    public static Verdict JudgeRequeue(VideoStatus status, long? submitterMemberId, bool isOwner) => status switch
    {
        VideoStatus.Skipped or VideoStatus.Canceled => Verdict.Requeue,
        VideoStatus.Failed => submitterMemberId is not null
            ? Verdict.Requeue
            : isOwner ? Verdict.NeedClaim : Verdict.NoAccess,
        _ => Verdict.NotRequeueable,
    };

    /// <summary>
    /// 认领（给无归属的稿子补投稿人席位）的门槛。回 null = 放行，否则是要贴给前端的拒词。
    /// 四条：只站长能做（归属是账本，占谁的配额算谁的功劳，不能让成员自己给自己挂）、
    /// 只补空归属（已有归属的不许抢改）、目标席位得真存在、而且得是个还在站里的席（活席）。
    /// targetSeatStatus 递的是席位当前 status（null = 查无此席）：判据拿事实自己判断，
    /// 不拿调用方已经消化过的 bool——那样「除名席能不能接稿」这条新规就只能在端点里默默写死，钉不住真值表。
    /// </summary>
    public static string? JudgeClaim(bool isOwner, long? currentSubmitterMemberId, string? targetSeatStatus)
    {
        if (!isOwner)
        {
            return "只有站长能给稿子补归属";
        }

        if (currentSubmitterMemberId is not null)
        {
            return "这条已经有归属了，不重复认领";
        }

        if (targetSeatStatus is null)
        {
            return "要挂过去的那个席位不存在";
        }

        // 归属决定「谁烧自己的号下这条、谁能自管这条」，挂到一个不进得来的席位上
        // 等于把活派给一个已经不在站里的人（DownloadWorker 拿不到他的号，只会再失败一次）。
        return MemberStatus.AllowsAdmission(targetSeatStatus)
            ? null
            : "要挂过去的席位不在站里（除名、封禁或还没兑现），换个活着的";
    }
}
