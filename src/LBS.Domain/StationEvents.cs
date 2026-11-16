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
    string TsUtc,
    // M5.4：成员事件另挂站内身份（素净席位无 mid，全靠这列认人）；访客事件留 NULL。
    long? ActorMemberId = null);

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

    // 观映族 11-15 改名 watch.* → playback.*：与投稿族 video.* 分家（同前缀会在台账筛族里混一锅）。
    // 台账老行不改写：统计读侧的新老双吃钉在 StationDb.Weekly.cs 的 Legacy* 常量与 IN 判据里。
    public const string PlaybackOpen = "playback.open";
    public const string HomeOpen = "home.open"; // 进首页一枪（周报七日动静的「主页」柱）：subject_type=page，不混观映账
    public const string PlaybackHeartbeat = "playback.heartbeat";
    public const string PlaybackSeek = "playback.seek";
    public const string PlaybackBlocked = "playback.blocked";

    public const string DanmakuSend = "danmaku.send";
    public const string DownloadLink = "download.link";

    public const string VideoSubmit = "video.submit";
    public const string VideoArrived = "video.arrived";
    public const string VideoFailed = "video.failed";
    public const string VideoRemoved = "video.removed";

    public const string MemberInvite = "member.invite";
    public const string MemberBan = "member.ban";
    public const string MemberUnban = "member.unban";
    public const string MemberDelete = "member.delete";
    // 除名与召回（11-08）：与 ban/unban 分家写账——封禁是「暂时不认你」，除名是「这位不往回来了」，
    // 两者对同一个人的后果与可逆路径不同，混成一枚事件名以后想查「谁被踢过站」就分不开了。
    public const string MemberExile = "member.exile";
    public const string MemberRestore = "member.restore";
    public const string MemberNickname = "member.nickname";
    public const string MemberInviteRevoke = "member.invite.revoke";
    public const string AnnounceRevoke = "admin.announce.revoke";
    public const string DanmakuRecycle = "danmaku.recycle";
    // 稿件设置（标题 / 简介 / 可见性 / 分区）从此有账：这一口早就是投稿人自管，而改名、改简介、
    // 把稿子转私有都是能损别人的事，后台得说得出「谁在什么时候动了哪几栏」。
    public const string VideoSettings = "video.settings";
    // 回收站里的三种动作各一枚名字（与 danmaku.recycle 同族）：进回收站 / 恢复 / 销毁是三件不同的事，
    // 合成一枚的话台账里「谁删的」与「谁恢复的」就分不开。销毁只站长握得住，更要单独留痕。
    public const string DanmakuRecycleRestore = "danmaku.recycle.restore";
    public const string CodecRecycle = "codec.recycle";
    public const string CodecRecycleRestore = "codec.recycle.restore";
    public const string CodecRecyclePurge = "codec.recycle.purge";
    // 成员回收站弹窗（11-16 站长拍板）的两枚动作：恢复是自己反悔请回架上；
    // 「销毁」只是成员视角的隐藏戳（货与清单都在，站长室照旧捞得起）——台账名字必须与真销毁分家，
    // 不然查账会把藏起来的当没了的。
    public const string VideoRecycleSelfRestore = "video.recycle.self.restore";
    public const string VideoRecycleSelfHide = "video.recycle.self.hide";
    public const string MemberSetPrimary = "member.set_primary";
    public const string MemberDropAccount = "member.drop_account";
    public const string MemberUseFace = "member.use_face";
    public const string MemberRegister = "member.register";
    public const string MemberPasswordChange = "member.password.change";

    public const string LoginStart = "login.qr.start";
    public const string LoginScanned = "login.qr.scanned";
    public const string LoginSuccess = "login.qr.success";
    public const string LoginExpired = "login.qr.expired";
    public const string LoginDenied = "login.qr.denied";
    public const string LoginLogout = "login.logout";
    public const string LoginPassword = "login.password";
    public const string LoginPasswordDenied = "login.password.denied";

    public const string AdminAction = "admin.action";
    public const string AdminAnnounce = "admin.announce";
    public const string AdminBlocked = "admin.blocked";

    // 分站 AI（M1）：每轮对话一行，日额度就从这些行数出来（账即是额度，不养第二本计数器）。
    // M2 的工具调用（投稿/推荐）同口记账：工具那一发也是模型往返，共吃日额度，不再另立名字。
    public const string AgentChat = "agent.chat";
    // 一个字都没从上游吐回来的失败发（地址不通/key 作废这类）：记行留痕但不进额度账——
    // AI 宕机不该烧成员的额度，额度反的是滥用不是意外（数额度的 CountAgentChatsToday 只认 agent.chat）。
    public const string AgentChatFail = "agent.chat.fail";

    /// <summary>站长改写看板娘性格卡（11-16 站长令：默认 prompt 暴露可改）：每次存卡一行，改了什么字数进 payload。</summary>
    public const string AdminAgentPersona = "admin.agent.persona";
}
