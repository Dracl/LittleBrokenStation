using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站内账户端点（M5.4）：注册、账密登录、改密、素净席位自改名号、站长代重置——分站身份从 B 站 mid 手里独立出来，
/// 账密是真身份的入口，扫码降级为可选绑定（扫码链路一字未动，两把钥匙开同一张门）。
/// 账密登录按 IP 上阶梯冷却（错一次歇 1 分钟、两次歇 5 分钟、三次起歇 30 分钟），防字典慢磨；
/// 扫码链路不经此闸——站长与朋友永远扫得进站，不会把自己锁在站外。
/// </summary>
public static class MemberAccountEndpoints
{
    private static readonly Regex UsernamePattern =
        new(@"^[\u4E00-\u9FFFA-Za-z0-9_]{2,24}$", RegexOptions.Compiled);

    /// <summary>
    /// 自改名号封顶 24 字（与用户名同宽：卡面、周报、公告都拿它摆，再长就撑破行）。
    /// 为何不像用户名那样上字符集白名单：名号是给人看的，B 站昵称本就带 emoji、空格、日文假名，
    /// 白名单会把人挡在「自己叫什么名字都得听站的」；真正要挡的是控位符与换行（下面逐字查）。
    /// </summary>
    private const int MaxNicknameLength = 24;

    // 阶梯冷却：同一 IP 在 FailWindow 内第 1 次密码错歇 1 分钟、第 2 次歇 5 分钟、第 3 次及以后歇 30 分钟。
    // 判据是「错一次就上锁」而非「攒满 N 次才锁」：分站只给自己与朋友用，账密口挨不着公网字典，
    // 手滑一次只赔一分钟可以忍，拿字典慢磨的每半小时只能试一发。要拉回「错一次即半小时」，
    // 把三档拉平成同一个 30 分钟即可，判据与测格不必动。
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(30);
    private static readonly IReadOnlyList<TimeSpan> CooldownTiers = new[]
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
    };

    private sealed record RegisterRequest(string? Code, string? Username, string? Password);

    private sealed record PasswordLoginRequest(string? Username, string? Password);

    private sealed record NicknameRequest(string? Nickname);

    private sealed record ChangePasswordRequest(string? OldPassword, string? NewPassword);

    private sealed record AdminResetPasswordRequest(string? Password);

    public static void MapMemberAccountEndpoints(this WebApplication app)
    {
        var failBoard = new IpFailBoard(FailWindow, CooldownTiers);

        // ── 注册：有主之站凭码落席，无主之站首位免码继位（与扫码首扫语义对齐）──
        app.MapPost("/api/member/register", (RegisterRequest? request, StationDb db, MemberGate gate,
            EventRecorder events, ILoggerFactory loggerFactory, HttpContext http) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var username = request?.Username?.Trim() ?? "";
            var password = request?.Password ?? "";

            if (ValidateCredentials(username, password) is { } bad)
            {
                return Results.Json(new { message = bad.Message }, statusCode: bad.Status);
            }

            if (db.GetMemberByUsername(username) is not null)
            {
                return Results.Json(new { message = "这个站名被人占了，换一个" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            // 席位来源：持码兑现，或无主之站首位免码建空位（邀请码只有站长能发，无主还索要就是死锁）。
            var (seat, seatError) = ResolveSeatForRegister(db, request?.Code);
            if (seatError is not null)
            {
                return Results.Json(new { message = seatError.Value.Message }, statusCode: seatError.Value.Status);
            }

            var activated = db.ActivateMemberByRegistration(seat!.Value, username, MemberPasswords.Hash(password));
            if (activated is null)
            {
                // 竞速双影：要么用户名刚被抢注，要么这席位已被别的钥匙认走。
                return Results.Json(new { message = db.GetMemberByUsername(username) is not null
                    ? "这个站名被人占了，换一个"
                    : "这席位刚被认走，请让站长重发一枚邀请码" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            var (token, _) = gate.IssueSession(activated.Id);
            AppendSessionCookie(http, token);
            // 首扫建档同款欢迎信（账密注册也是首次进站）；寄失败只 log。
            MemberMail.NotifyWelcome(db, loggerFactory.CreateLogger("MemberMail"), activated.Id);
            events.RecordWithPayload(EventActors.Member, activated.Mid, EventNames.MemberRegister, "member",
                activated.Id.ToString(), new { username }, ip, activated.Id);
            return Results.Ok(new
            {
                memberId = activated.Id,
                username = activated.Username,
                role = activated.Role,
                message = $"站籍落定，欢迎上船，{username}",
            });
        });

        // ── 账密登录：按 IP 记失败账，成功不记账 ──
        app.MapPost("/api/member/login/password", (PasswordLoginRequest? request, StationDb db, MemberGate gate,
            EventRecorder events, HttpContext http) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var slot = failBoard.Snapshot(ip);
            if (slot.Blocked)
            {
                // 把还要歇多久如实报出去：朋友手滑被锁时看得懂要等多久，不至于以为站坏了。
                // 冷却中的撞门不记事件：否则脚本能把事件表当成堆肥堆，本身就成了 DoS。
                var mins = Math.Max(1, (int)Math.Ceiling(slot.RetryAfter.TotalMinutes));
                return Results.Json(new
                {
                    message = $"试太多次了，{mins} 分钟后再来",
                    retryAfterSec = (int)Math.Ceiling(slot.RetryAfter.TotalSeconds),
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var username = request?.Username?.Trim() ?? "";
            var member = username.Length > 0 ? db.GetMemberByUsername(username) : null;
            if (member is null || !db.VerifyMemberPassword(member.Id, request?.Password))
            {
                failBoard.RegisterFailure(ip);
                // 查无此人与密码不对给同一句拒词，不给枚举席位留缝。
                events.Record(EventActors.Guest, null, EventNames.LoginPasswordDenied, "member",
                    string.IsNullOrWhiteSpace(username) ? "?" : username, ip: ip);
                return Results.Json(new { message = "用户名或密码不对" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // 白名单判据：账密这条路也一样只认 active。写成 != Banned 时，除名的人拿旧密码
            // 登录会回 200 与一张新票（后续 API 被 MemberGate 挡，但登录页会跟他说「欢迎回来」）。
            if (!MemberStatus.AllowsAdmission(member.Status))
            {
                return Results.Json(new
                {
                    message = member.Status == MemberStatus.Exiled ? "这位已被分站除名，要回来得请站长召回席位" : "这位已被站长请出分站",
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            db.TouchMemberLoginById(member.Id);
            failBoard.Clear(ip); // 登进去了就销账：不让人背着旧账登录，也不拿成功登录当洗白机器（本站只信席位）
            var (token, _) = gate.IssueSession(member.Id);
            AppendSessionCookie(http, token);
            events.Record(EventActors.Member, member.Mid, EventNames.LoginPassword, "member", member.Id.ToString(),
                ip: ip, actorMemberId: member.Id);
            return Results.Ok(new
            {
                memberId = member.Id,
                username = member.Username,
                role = member.Role,
                message = $"欢迎回来，{member.Username}",
            });
        });

        // ── 自助改密：已设过必须验旧；素净席位首次补设免旧（人都登进来了才算数）──
        app.MapPost("/api/member/password/change", (ChangePasswordRequest? request, StationDb db,
            EventRecorder events, HttpContext http) =>
        {
            var member = MemberGate.Current(http);
            if (member is null)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!MemberPasswords.MeetsPolicy(request?.NewPassword))
            {
                return Results.Json(new { message = "新密码至少 8 位" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (db.HasMemberPassword(member.Id) && !db.VerifyMemberPassword(member.Id, request?.OldPassword))
            {
                return Results.Json(new { message = "旧密码没对上" }, statusCode: StatusCodes.Status400BadRequest);
            }

            // MeetsPolicy 已担保非空，编译器不认账，这里接个实锤。
            db.SetMemberPassword(member.Id, MemberPasswords.Hash(request!.NewPassword!));
            events.Record(EventActors.Member, member.Mid, EventNames.MemberPasswordChange, "member",
                member.Id.ToString(), ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: member.Id);
            return Results.Ok(new { message = "钥匙换过了，下次用新的" });
        });

        // ── 席位自改名号（11-08 站长拍板）──
        // 绑没绑 B 站号都能改：展示名是礼貌名，素净席位本就能自己叫任何名字，只禁绑号那批只会造成不对称。
        // 自改即打锁（nickname_locked）：不锁就会「改了名、下次重登又被 B 站昵称盖回去」（TouchMemberLogin 那一条）。
        app.MapPost("/api/member/nickname", (NicknameRequest? request, StationDb db,
            EventRecorder events, HttpContext http) =>
        {
            var member = MemberGate.Current(http);
            if (member is null)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var wanted = request?.Nickname?.Trim() ?? string.Empty;
            if (wanted.Length == 0)
            {
                return Results.Json(new { message = "名号不能空着，总得有个叫法" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (wanted.Length > MaxNicknameLength)
            {
                return Results.Json(new { message = $"名号封顶 {MaxNicknameLength} 个字，卡片摆不下" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // 控制字符与换行不挡就是 XSS 的旁门（卡面、周报、公告里都要拿这个名字摆）。
            if (wanted.Any(char.IsControl))
            {
                return Results.Json(new { message = "名号里不能夹控位符与换行" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.Equals(wanted, member.Nickname, StringComparison.Ordinal))
            {
                return Results.Ok(new { message = "名字本来就叫这个，没动", nickname = wanted });
            }

            if (!db.SetMemberNickname(member.Id, wanted))
            {
                return Results.Json(new { message = "这席还是一张没认主的空位，本人还没到场，名字先不接" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            events.RecordWithPayload(EventActors.Member, member.Mid, EventNames.MemberNickname, "member",
                member.Id.ToString(), new { from = member.Nickname, to = wanted },
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: member.Id);
            return Results.Ok(new { message = $"往后就叫「{wanted}」了", nickname = wanted });
        });

        // ── 站长代重置（owner 也在内——站长忘密的自救通道，前提是还能进站长室）──
        app.MapPost("/api/admin/members/{id:long}/password", (long id, AdminResetPasswordRequest? request,
            StationDb db, EventRecorder events, HttpContext http) =>
        {
            if (!MemberPasswords.MeetsPolicy(request?.Password))
            {
                return Results.Json(new { message = "密码至少 8 位" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (db.GetMemberById(id) is null)
            {
                return Results.Json(new { message = "查无此席" }, statusCode: StatusCodes.Status404NotFound);
            }

            db.SetMemberPassword(id, MemberPasswords.Hash(request!.Password!));
            events.RecordWithPayload(EventActors.Admin, MemberGate.Current(http)?.Mid, EventNames.AdminAction,
                "member", id.ToString(), new { op = "resetPassword" },
                http.Connection.RemoteIpAddress?.ToString(), MemberGate.Current(http)?.Id);
            return Results.Ok(new { message = "密码已重置，记得让本人尽快改" });
        });
    }

    /// <summary>注册入参格式校验（站规：用户名 2–24 汉字/字母/数字/下划线，密码 ≥8）；重名另查库。</summary>
    internal static (int Status, string Message)? ValidateCredentials(string username, string password)
    {
        if (!UsernamePattern.IsMatch(username ?? ""))
        {
            return (StatusCodes.Status400BadRequest, "用户名得是 2–24 个汉字、字母、数字或下划线");
        }

        if (!MemberPasswords.MeetsPolicy(password))
        {
            return (StatusCodes.Status400BadRequest, "密码至少 8 位，别嫌长——这是你在分站的名字钥匙");
        }

        return null;
    }

    /// <summary>注册席位裁决：凭码兑现邀请；无主之站首位免码建空位；有主无码/坏码拒。错误给状态码与文案。</summary>
    internal static (long? SeatId, (int Status, string Message)? Error) ResolveSeatForRegister(StationDb db, string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        if (normalized is not null && db.FindInvitedByCode(normalized) is { } invited)
        {
            return (invited.Id, null);
        }

        if (!db.HasOwner())
        {
            var (seatId, _) = db.CreateMemberInvite(null);
            return (seatId, null);
        }

        return normalized is not null
            ? (null, (StatusCodes.Status403Forbidden, "邀请码没认出来——可能被用过了，找站长重发一枚"))
            : (null, (StatusCodes.Status403Forbidden, "分站是邀请制：首次进站需要站长发的邀请码"));
    }

    /// <summary>
    /// 按 IP 记密码失败账的黑板：阶梯冷却。同窗内第 1 次错上 1 分钟锁、第 2 次 5 分钟、第 3 次及以后 30 分钟。
    /// 三条硬口径：① 冷却之中再撞门不延长刑期（否则脚本能把封禁无限往后推）；② 窗口一过整段账作废重开；
    /// ③ 成功登录清账。只封作案 IP，别家照进。
    /// 时钟可注（TimeProvider，与节流闸/采集员同一家族）：测格拿假表推时间，不拿 Thread.Sleep 赌调度。
    /// </summary>
    internal sealed class IpFailBoard(TimeSpan window, IReadOnlyList<TimeSpan> cooldownTiers, TimeProvider? clock = null)
    {
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;
        private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart, DateTimeOffset Until)> _board = new();

        public (bool Blocked, int Count, TimeSpan RetryAfter) Snapshot(string ip)
        {
            var now = _clock.GetUtcNow();
            if (!_board.TryGetValue(ip, out var slot) || now - slot.WindowStart > window)
            {
                return (false, 0, TimeSpan.Zero); // 没账，或窗口已过整段作废
            }

            return now < slot.Until ? (true, slot.Count, slot.Until - now) : (false, slot.Count, TimeSpan.Zero);
        }

        public void RegisterFailure(string ip)
        {
            var now = _clock.GetUtcNow();
            _board.AddOrUpdate(ip,
                _ => (1, now, now + Tier(1)),
                (_, old) =>
                {
                    if (now < old.Until)
                    {
                        return old; // 冷却中不延长
                    }

                    var stale = now - old.WindowStart > window;
                    var count = stale ? 1 : old.Count + 1;
                    return (count, stale ? now : old.WindowStart, now + Tier(count));
                });
        }

        public void Clear(string ip) => _board.TryRemove(ip, out _);

        private TimeSpan Tier(int count) => cooldownTiers[Math.Clamp(count - 1, 0, cooldownTiers.Count - 1)];
    }

    /// <summary>发会话 cookie——与扫码链路同一张票同一姿势（HttpOnly/Lax/30 天；TLS 下自动 Secure）。</summary>
    internal static void AppendSessionCookie(HttpContext http, string token) =>
        http.Response.Cookies.Append(MemberGate.Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            // 席位会话能拿全站权限，明文信道上不该走：反代 TLS 下上 Secure，局域网 http 直连不误伤
            // （与扫码那一口、站长锁、弹幕签同一口径）。.NET 10 已无 CookieOptions.SecurePolicy，
            // SameAsRequest 自己算；反代身后 Request.IsHttps 靠 X-Forwarded-Proto（见信任区）。
            Secure = http.Request.IsHttps,
            Path = "/",
            MaxAge = MemberGate.SessionTtl,
        });
}
