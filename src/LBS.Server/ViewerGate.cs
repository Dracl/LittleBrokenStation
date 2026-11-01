using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 看客那一侧的两把钥匙（读侧唯一出口，与写侧那把 CanManageVideo 同一张嘴）：
/// ① <see cref="Scope"/> —— 把「谁在问」掰成数据层那把 <see cref="VisibilityScope"/>：站长全见 / 席位看公开 + 自己投的 / 访客只看公开；
/// ② <see cref="CanView"/> —— 单片级「这位能不能看」：站长短路，否则「公开」或「本人投稿」，查无此片回 false。
/// 两条都只比站内席位 id（videos.submitter_member_id），不绕 B 站 mid——与 CanManageVideo 同一枚钥匙。
/// </summary>
/// <remarks>正身放在这里而不是 Program.cs 的局部函数里：ZonesEndpoints / CollectionsEndpoints / DmEndpoints 这些分部
/// 也要同一张嘴（局部函数跨不过文件），两处口径一旦错开就是「列表看得见、点进去 404」那种拆牌。
/// Program.cs 里照样留着 <c>ViewerScope</c> / <c>CanViewVideo</c> 两个同名入口（一句话转发），读代码的人在原地就能找到。</remarks>
internal static class ViewerGate
{
    /// <summary>看客尺子：站长全见；会员看公开 + 自己投的；访客只看公开。与 CanManageVideo 同一把钥匙（比站内席位 id，不绕 mid）。</summary>
    public static VisibilityScope Scope(HttpContext http)
    {
        var member = MemberGate.Current(http);
        if (member is null)
        {
            return VisibilityScope.PublicOnly;
        }

        return MemberGate.IsOwner(member) ? VisibilityScope.Everything : VisibilityScope.Of(member.Id);
    }

    /// <summary>能不能看这把钥匙：站长短路；否则「公开」或「本人投稿」；查无此片回 false（与 CanManage 同形，别另起口径）。
    /// 拿 false 一律按「查无此片」那张 404 脸回（403 就是「有这稿但不给你看」的泄密信号）。
    /// 「与 CanManage 同形」优先于「查无回 false」：站长短路在查档之前（CanManageVideo 也不问有没有这一条），
    /// 所以「查无回 false」说的是没钥匙的人；伪 id 到站长手里，由端点自己那趟回查接住。</summary>
    public static bool CanView(HttpContext http, StationDb db, long videoId) => CanView(db, videoId, MemberGate.Current(http));

    /// <summary>同上，但**身份由调用方递**：静态区那道闸跑在会员盖章（Program.cs 的 <c>gate.Stamp</c>）之前，
    /// <see cref="MemberGate.Current"/> 在那儿永远是 null —— 不自己从 cookie 认人，私有稿的主人连自己的封面与正片都取不回
    /// （实测踩过：站长与投稿人本人一律 404）。</summary>
    public static bool CanView(StationDb db, long videoId, StationMember? member)
    {
        if (member is not null && MemberGate.IsOwner(member))
        {
            return true;
        }

        // 查无此片（null）与「有这条但藏着」都归 false：两件事在门外必须是同一张面孔。
        switch (db.GetVideoVisibility(videoId))
        {
            case null:
                return false;
            case VideoVisibility.Public:
                return true;
        }

        // 私有：只有投稿人本人握得住。无归属的私有行（爬工代投 / 老数据反查失败）连别人也别想撞着，
        // 与 CanManageVideo 对无主行的处理同一口径。
        return member is not null
               && db.GetVideoSubmitterMember(videoId) is { } submitter
               && submitter == member.Id;
    }
}
