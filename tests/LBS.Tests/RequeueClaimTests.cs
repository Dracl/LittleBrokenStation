using LBS.Domain;
using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// 重投与认领的判据真值表（RequeueGate）。端点只把判据翻成 HTTP，判据本身钉在这儿——
/// 本仓无 HTTP harness，判据只有抽成纯函数才钉得住。
/// 案起：库里那条 11-01 卡到今天的 Failed 孤稿（id 467）。失败行从前两头堵：
/// ① 没有重投入口（/requeue 只认 Canceled 与 Skipped，Failed 被一句「只有被取消的馆藏才需要重新排队」挡回）；
/// ② 没人能补归属（DownloadWorker 那句「或由站长给这稿补上归属」在站长室根本没有对应的门）。
/// 甲（认领）+ 乙（放开 Failed 重投）合上这两头，判据就长下面这张表。
/// </summary>
public sealed class RequeueClaimTests
{
    // ── 乙：Failed 终于能重投 ──────────────────────────────────

    [Fact]
    public void 重投_Failed有归属_不必是站长也能救()
    {
        // 投稿人本人的失败稿自己就能捣回来（归属门在端点上游已把非本人非站长挡过）。
        Assert.Equal(RequeueGate.Verdict.Requeue, RequeueGate.JudgeRequeue(VideoStatus.Failed, 7L, isOwner: false));
        Assert.Equal(RequeueGate.Verdict.Requeue, RequeueGate.JudgeRequeue(VideoStatus.Failed, 7L, isOwner: true));
    }

    [Fact]
    public void 重投_Failed无归属_站长须先认领()
    {
        // 无归属 → DownloadWorker 判无号，重排出去只配再失败一次。所以先要认领，不白跑。
        Assert.Equal(RequeueGate.Verdict.NeedClaim, RequeueGate.JudgeRequeue(VideoStatus.Failed, null, isOwner: true));
    }

    [Fact]
    public void 重投_Failed无归属_普通成员救不动()
    {
        Assert.Equal(RequeueGate.Verdict.NoAccess, RequeueGate.JudgeRequeue(VideoStatus.Failed, null, isOwner: false));
    }

    [Fact]
    public void 重投_Canceled与Skipped旧路一字不变()
    {
        // 这两路是旧世界就有的，甲乙开工不许把它们带偏（Skipped 还另有强制重下正门）。
        Assert.Equal(RequeueGate.Verdict.Requeue, RequeueGate.JudgeRequeue(VideoStatus.Canceled, 7L, isOwner: false));
        Assert.Equal(RequeueGate.Verdict.Requeue, RequeueGate.JudgeRequeue(VideoStatus.Canceled, null, isOwner: true));
        Assert.Equal(RequeueGate.Verdict.Requeue, RequeueGate.JudgeRequeue(VideoStatus.Skipped, 7L, isOwner: false));
    }

    [Fact]
    public void 重投_到站排队与下载中一律不必重投()
    {
        Assert.Equal(RequeueGate.Verdict.NotRequeueable, RequeueGate.JudgeRequeue(VideoStatus.Arrived, 7L, isOwner: true));
        Assert.Equal(RequeueGate.Verdict.NotRequeueable, RequeueGate.JudgeRequeue(VideoStatus.Pending, 7L, isOwner: true));
        Assert.Equal(RequeueGate.Verdict.NotRequeueable, RequeueGate.JudgeRequeue(VideoStatus.Downloading, null, isOwner: true));
    }

    // ── 甲：认领补归属 ────────────────────────────────────────

    [Fact]
    public void 认领_非站长一律挡回()
    {
        // 归属是账本：占谁的配额、算谁的功劳，不能让成员自己给自己挂。
        Assert.Equal("只有站长能给稿子补归属", RequeueGate.JudgeClaim(isOwner: false, null, targetSeatExists: true));
    }

    [Fact]
    public void 认领_已有归属不许抢改()
    {
        Assert.Equal("这条已经有归属了，不重复认领", RequeueGate.JudgeClaim(isOwner: true, 7L, targetSeatExists: true));
    }

    [Fact]
    public void 认领_目标席位不存在挡回()
    {
        // 挂到不存在的席位上 = 造出一条永远借不到号的稿子，比不挂更坏。
        Assert.Equal("要挂过去的那个席位不存在", RequeueGate.JudgeClaim(isOwner: true, null, targetSeatExists: false));
    }

    [Fact]
    public void 认领_站长补空归属放行()
    {
        Assert.Null(RequeueGate.JudgeClaim(isOwner: true, null, targetSeatExists: true));
    }

    [Fact]
    public void 认领判据与重投判据口径对齐()
    {
        // 同一枚事实（无归属 + 站长）在两张表里必须同进同退：重投说「先认领」，认领就得放行。
        var verdict = RequeueGate.JudgeRequeue(VideoStatus.Failed, null, isOwner: true);
        Assert.Equal(RequeueGate.Verdict.NeedClaim, verdict);
        Assert.Null(RequeueGate.JudgeClaim(isOwner: true, null, targetSeatExists: true));
    }
}
