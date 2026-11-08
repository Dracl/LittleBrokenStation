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
    string? LastLoginAtUtc,
    // M5.4：站内账户才是真身份——无 B 站号的素净席位也有名有姓。
    string? Username = null);

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

/// <summary>
/// 采集号凭证（站里的活：评论/弹幕归档、UP 名片、动态、收藏导入、编码加档）。
/// 比下载凭证多带昵称与头像，给站长室报「此刻在用谁的号」；cookie 同样只进不出。
/// </summary>
public sealed record CollectAccountCredential(
    long Id,
    long MemberId,
    long AccountMid,
    string? AccountName,
    string? FaceUrl,
    string Cookie);

/// <summary>某成员的入站概览（站长室与未来 AI 都吃这口）。</summary>
public sealed record MemberIngestStats(long SubmitterMemberId, int VideoCount, long TotalBytes, int DanmakuCount = 0);

/// <summary>
/// 站长公开名片（首页顶栏那块）：昵称与头像都取自站长席位台账。
/// 以前它吃的是站级主号从 B 站 nav 拉回来的那一份，站级登录态退役后归一到一个身份口。
/// </summary>
public sealed record StationProfileCard(string? Name, string? Face);

/// <summary>成员状态三字经。</summary>
public static class MemberStatus
{
    public const string Invited = "invited";
    public const string Active = "active";
    public const string Banned = "banned";

    /// <summary>
    /// 除名（软删席位）：人与账都留着，但不再进得来。与封禁的分别是意图——封禁是「暂时不认你」，
    /// 随时可解禁；除名是「这位不往回来了」，历史名片照旧点得开（他发的弹幕、投的稿不会集体掉脸），
    /// 但想回来得由站长动「召回」，不是他自己扫个码就当没事人。真删另有一条路（DeleteMember）。
    /// </summary>
    public const string Exiled = "exiled";

    /// <summary>
    /// 进不进得来只认这一张嘴（扫码入场、账密登录、发起绑定、认领目标全走它）。
    /// 判据必须是「白名单 == Active」而不是「黑名单 != banned」：后者每加一种状态就漏一次——
    /// exiled 落地那一刻，全站五处只挡 banned 的地方会把除名的人当没事人放行。
    /// </summary>
    public static bool AllowsAdmission(string? status) => status == Active;
}

/// <summary>成员角色：头名即站长（建表后第一个扫码认主的），其余皆用户。</summary>
public static class MemberRoles
{
    public const string Owner = "owner";
    public const string Member = "member";
}
