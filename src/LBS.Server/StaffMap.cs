using LBS.Domain;
using LBS.Engine;

namespace LBS.Server;

/// <summary>
/// 引擎采到的创作团队 → 放映厅档案：字段一一对应，换一层皮是为了让落库形态与读侧共用 <see cref="PlaybackStaffCodec"/>
/// 这一张嘴，也让 LBS.Infrastructure 不必认识 LBS.Engine 的类型（依赖方向：Server → Engine / Domain ← Infrastructure）。
/// </summary>
internal static class StaffMap
{
    /// <summary>没名字的条目显不出来，直接不收；角色空白归一成 null，由前端决定显什么。</summary>
    public static IReadOnlyList<PlaybackStaff>? ToPlayback(IReadOnlyList<VideoStaffItem>? staff) =>
        staff is null
            ? null
            : staff
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new PlaybackStaff(
                    s.Mid,
                    s.Name!.Trim(),
                    string.IsNullOrWhiteSpace(s.Role) ? null : s.Role.Trim(),
                    string.IsNullOrWhiteSpace(s.FaceRel) ? null : s.FaceRel.Trim()))
                .ToList();
}
