namespace LBS.Domain;

/// <summary>
/// 成员席位（邀请制）：站长先建空位发邀请码，朋友扫码绑定 B 站 mid 后转 active。
/// 封禁即 banned，登录与会话同时作废。cookie 不在此形——见 MemberAccountCredential。
/// </summary>
public sealed record StationMember(
    long Id,
    long? Mid,
    string? Nickname,
    string? FaceUrl,
    string? Note,
    string Status,
    string Role,
    string CreatedAtUtc,
    string? BoundAtUtc,
    string? LastLoginAtUtc);

/// <summary>成员账号池一行（不含 cookie；cookie 只走凭证形内部取用）。</summary>
public sealed record MemberAccount(
    long Id,
    long MemberId,
    long AccountMid,
    string? AccountName,
    string? FaceUrl,
    bool IsPrimary,
    bool Expired,
    string CreatedAtUtc,
    string? LastSeenUtc);

/// <summary>账号 cookie 凭证：只给下载/查询链内部用，任何 HTTP 端点不得返回。</summary>
public sealed record MemberAccountCredential(long Id, long MemberId, long AccountMid, string Cookie);

/// <summary>某成员的入站概览（站长室与未来 AI 都吃这口）。</summary>
public sealed record MemberIngestStats(long SubmitterMid, int VideoCount, long TotalBytes);

/// <summary>成员状态三字经。</summary>
public static class MemberStatus
{
    public const string Invited = "invited";
    public const string Active = "active";
    public const string Banned = "banned";
}

/// <summary>成员角色：头名即站长（建表后第一个扫码认主的），其余皆用户。</summary>
public static class MemberRoles
{
    public const string Owner = "owner";
    public const string Member = "member";
}
