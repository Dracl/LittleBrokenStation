using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 投稿闸门（M5.4）：素净席位（无未过期 B 站号）可逛可看可私信，唯独不能投稿——
/// 提交链要烧投稿人自己的 B 站登录态，没号就没得烧。站长本人不受此闸：
/// 站长拿引擎登录态（主/副槽）投稿，那是分站自己的家底。
/// 单拎成静态函数：端点与测试驱动的是同一张嘴。
/// </summary>
public static class SubmissionGate
{
    public static bool Allows(StationDb db, StationMember member) =>
        member.Role == MemberRoles.Owner || db.PickMemberAccountForDownload(member.Id) is not null;
}
