namespace LBS.Domain;

/// <summary>
/// 事件草稿：EventRecorder 入队用，TsUtc 由记录方在发生瞬间填好（别等落库才盖戳）。
/// payload 是 JSON 字符串，形状随事件名自便，AI 好啃即可；cookie/令牌永不入此形。
/// </summary>
public sealed record EventDraft(
    string ActorType,
    long? ActorMid,
    string EventType,
    string? SubjectType,
    string? SubjectId,
    string? PayloadJson,
    string? Ip,
    string TsUtc);

/// <summary>事件台账一行（查询回形）。</summary>
public sealed record EventRow(
    long Id,
    string TsUtc,
    string ActorType,
    long? ActorMid,
    string Event,
    string? SubjectType,
    string? SubjectId,
    string? Payload,
    string? Ip);

/// <summary>事件主角的四种身份。</summary>
public static class EventActors
{
    public const string Guest = "guest";
    public const string Member = "member";
    public const string Admin = "admin";
    public const string System = "system";
}

/// <summary>
/// 事件名字典：指令化命名 域.动作，集中在此防散逸。
/// 新事件先在这里挂号，AI 侧按前缀聚合分析。
/// </summary>
public static class EventNames
{
    public const string Visit = "site.visit";
    public const string SearchQuery = "search.query";
    public const string CardClick = "card.click";

    public const string WatchOpen = "watch.open";
    public const string WatchHeartbeat = "watch.heartbeat";
    public const string WatchSeek = "watch.seek";
    public const string WatchBlocked = "watch.blocked";

    public const string DanmakuSend = "danmaku.send";
    public const string DownloadLink = "download.link";

    public const string VideoSubmit = "video.submit";
    public const string VideoArrived = "video.arrived";
    public const string VideoFailed = "video.failed";
    public const string VideoRemoved = "video.removed";

    public const string LoginStart = "login.qr.start";
    public const string LoginScanned = "login.qr.scanned";
    public const string LoginSuccess = "login.qr.success";
    public const string LoginExpired = "login.qr.expired";
    public const string LoginDenied = "login.qr.denied";
    public const string LoginLogout = "login.logout";

    public const string AdminAction = "admin.action";
}
