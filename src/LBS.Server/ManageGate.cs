using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 写侧那把钥匙（与读侧的 <see cref="ViewerGate"/> 对称）：这稿子的<b>投稿人本人 ∥ 站长</b>。
/// 归属不明的行（爬工代投 / 老数据反查失败）只有站长握得住，与 MemberMail「无归属不发信」同一口径；
/// 比的是站内席位 id（videos.submitter_member_id），不绕 B 站 mid。
/// </summary>
/// <remarks>正身原先住在 Program.cs 的局部函数 <c>CanManageVideo</c> 里，搬进这里与 <see cref="ViewerGate"/> 同一先例：
/// ① 分部端点也要同一张嘴（局部函数跨不过文件）；② 它能被测试直接调——「revive / requeue / 稿件设置那几口到底谁握得住」
/// 这件事本仓没有 HTTP harness，只能钉在判定函数本身（照 SubmitterManageTests 那族测门判据的做法）。
/// Program.cs 里照样留着 <c>CanManageVideo</c> 同名入口（一句话转发），读代码的人在原地就能找到。</remarks>
internal static class ManageGate
{
    /// <summary>这稿的自管钥匙在不在这位手里（从会话认人；与 <see cref="ViewerGate.CanView(HttpContext,StationDb,long)"/> 同形，别另起口径）。</summary>
    public static bool CanManage(HttpContext http, StationDb db, long videoId) => CanManage(db, videoId, MemberGate.Current(http));

    /// <summary>
    /// 同上，但<b>身份由调用方递</b>：站长短路为真（不问有没有这一条——「查无此片」归端点自己那趟回查接住）；
    /// 没席位一律假；有席位不是站长，就只认「这稿的投稿人席位 id == 我的席位 id」那一行，无主行谁也拿不到。
    /// </summary>
    public static bool CanManage(StationDb db, long videoId, StationMember? member)
    {
        if (member is null)
        {
            return false;
        }

        if (MemberGate.IsOwner(member))
        {
            return true;
        }

        return db.GetVideoSubmitterMember(videoId) is { } submitter && submitter == member.Id;
    }
}
