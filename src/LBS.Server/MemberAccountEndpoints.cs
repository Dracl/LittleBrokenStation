using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站内账户端点（M5.4）：注册、账密登录、改密、站长代重置——分站身份从 B 站 mid 手里独立出来，
/// 账密是真身份的入口，扫码降级为可选绑定（扫码链路一字未动，两把钥匙开同一张门）。
/// 账密登录按 IP 限流（仿事件上报口：5 分钟窗口最多 10 次失败尝试），防字典慢磨。
/// </summary>
public static class MemberAccountEndpoints
{
    private static readonly Regex UsernamePattern =
        new(@"^[\u4E00-\u9FFFA-Za-z0-9_]{2,24}$", RegexOptions.Compiled);

    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(5);
    private const int MaxFailsPerWindow = 10;

    private sealed record RegisterRequest(string? Code, string? Username, string? Password);

    private sealed record PasswordLoginRequest(string? Username, string? Password);

    private sealed record ChangePasswordRequest(string? OldPassword, string? NewPassword);

    private sealed record AdminResetPasswordRequest(string? Password);

    public static void MapMemberAccountEndpoints(this WebApplication app)
    {
        var failBoard = new IpFailBoard(FailWindow, MaxFailsPerWindow);

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
            var now = DateTimeOffset.UtcNow;
            var slot = failBoard.Snapshot(ip);
            if (slot.Blocked)
            {
                return Results.Json(new { message = "试太多次了，歇会儿再来" },
                    statusCode: StatusCodes.Status429TooManyRequests);
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

            if (member.Status == MemberStatus.Banned)
            {
                return Results.Json(new { message = "这位已被站长请出分站" }, statusCode: StatusCodes.Status403Forbidden);
            }

            db.TouchMemberLoginById(member.Id);
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

    /// <summary>按 IP 记密码失败账的黑板（仿事件上报口的节流）：窗口内满额即封，成功登录不清账也不影响。</summary>
    internal sealed class IpFailBoard(TimeSpan window, int max)
    {
        private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Start)> _board = new();

        public (bool Blocked, int Count) Snapshot(string ip)
        {
            var now = DateTimeOffset.UtcNow;
            var slot = _board.TryGetValue(ip, out var old) && now - old.Start <= window
                ? old
                : (Count: 0, Start: now);
            return (slot.Count >= max, slot.Count);
        }

        public void RegisterFailure(string ip)
        {
            var now = DateTimeOffset.UtcNow;
            _board.AddOrUpdate(ip,
                _ => (1, now),
                (_, old) => now - old.Start > window ? (1, now) : (old.Count + 1, old.Start));
        }
    }

    /// <summary>发会话 cookie——与扫码链路同一张票同一姿势（HttpOnly/Lax/30 天）。</summary>
    internal static void AppendSessionCookie(HttpContext http, string token) =>
        http.Response.Cookies.Append(MemberGate.Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = MemberGate.SessionTtl,
        });
}
