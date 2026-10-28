using System.Collections.Concurrent;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 会员登录面：扫码握手三端点 + 登出 + 我是谁。
/// 握手全程 cookie 只进不出：B 站登录 cookie 直接落台账，前端只拿到状态与昵称头像；
/// 分站会话 cookie（lbs_member）在成功那一刻才签发。
/// 首登必须持站长发的邀请码；老席位重登扫码即可。
/// </summary>
public static class MemberEndpoints
{
    private static readonly TimeSpan QrTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollThrottle = TimeSpan.FromSeconds(1);
    private const int MaxOpenQrPerIp = 3;
    private const int MaxOpenQrTotal = 64;

    private sealed record QrSession(string SourceIp, string? Invite, DateTimeOffset CreatedUtc, string Mode)
    {
        public DateTimeOffset LastPollUtc { get; set; }
        public QrPhase Phase { get; set; } = QrPhase.Waiting;
    }

    private sealed record QrNewRequest(string? Invite, string? Mode);

    private sealed record AccountIdRequest(long Id);

    public static void MapMemberEndpoints(this WebApplication app)
    {
        var sessions = new ConcurrentDictionary<string, QrSession>();

        // 公开探针：登录页用它挂“首位扫码者继位站长”的告示，不需要任何身份。
        app.MapGet("/api/member/status", (StationDb db) => Results.Ok(new { hasOwner = db.HasOwner() }));

        app.MapPost("/api/member/qr/new", async (QrNewRequest? request, LbsQrLogin qr, StationDb db,
            EventRecorder events, HttpContext http, CancellationToken cancellationToken) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            SweepStale();

            if (sessions.Count >= MaxOpenQrTotal
                || sessions.Values.Count(s => string.Equals(s.SourceIp, ip, StringComparison.Ordinal)) >= MaxOpenQrPerIp)
            {
                return Results.Json(new { message = "排队扫码的人太多，稍候再试" }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            // bind 模式：已登录成员往自己账号池里再接一个 B 站号，不建新席不发新会话。
            var mode = string.Equals(request?.Mode, "bind", StringComparison.OrdinalIgnoreCase) ? "bind" : "admit";
            if (mode == "bind" && MemberGate.Current(http) is null)
            {
                return Results.Json(new { message = "先以会员身份进站，才能往自己池里加号" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var challenge = await qr.NewChallengeAsync(cancellationToken);
            if (challenge is null)
            {
                return Results.Json(new { message = "B 站没给二维码，稍后再试" }, statusCode: StatusCodes.Status502BadGateway);
            }

            sessions[challenge.QrcodeKey] = new QrSession(ip, NormalizeInvite(request?.Invite), DateTimeOffset.UtcNow, mode);
            events.Record(EventActors.Guest, null, EventNames.LoginStart, "qr", challenge.QrcodeKey, ip: ip);
            return Results.Ok(new { url = challenge.Url, key = challenge.QrcodeKey });
        });

        app.MapGet("/api/member/qr/poll", async (string? key, LbsQrLogin qr, StationDb db, MemberGate gate,
            EventRecorder events, ILoggerFactory loggerFactory, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(key) || !sessions.TryGetValue(key, out var session))
            {
                return Results.Json(new { message = "二维码不存在或已过期，请重新获取" }, statusCode: StatusCodes.Status410Gone);
            }

            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!string.Equals(session.SourceIp, ip, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            // bind 模式的码只能发起人自己消——拿别人刚扫完的码偷号不行。
            long? binderMid = session.Mode == "bind" ? MemberGate.Current(http)?.Mid : null;
            if (session.Mode == "bind" && binderMid is null)
            {
                return Results.Unauthorized();
            }

            if (DateTimeOffset.UtcNow - session.CreatedUtc > QrTtl)
            {
                sessions.TryRemove(key, out _);
                events.Record(EventActors.Guest, null, EventNames.LoginExpired, "qr", key, ip: ip);
                return Results.Ok(new { phase = "expired", message = "二维码超时，请重新获取" });
            }

            // 上游轮询限流：同一张码 1 秒内只代问一次，第二次直接报当前状态。
            if (DateTimeOffset.UtcNow - session.LastPollUtc < PollThrottle)
            {
                return Results.Ok(new { phase = PhaseName(session.Phase) });
            }

            var result = await qr.PollAsync(key, cancellationToken);
            session.LastPollUtc = DateTimeOffset.UtcNow;
            var previous = session.Phase;
            session.Phase = result.Phase;

            switch (result.Phase)
            {
                case QrPhase.Waiting:
                case QrPhase.Scanned:
                    if (result.Phase == QrPhase.Scanned && previous != QrPhase.Scanned)
                    {
                        events.Record(EventActors.Guest, null, EventNames.LoginScanned, "qr", key, ip: ip);
                    }

                    return Results.Ok(new { phase = PhaseName(result.Phase) });

                case QrPhase.Expired:
                    sessions.TryRemove(key, out _);
                    events.Record(EventActors.Guest, null, EventNames.LoginExpired, "qr", key, ip: ip);
                    return Results.Ok(new { phase = "expired", message = result.Message ?? "二维码已失效" });

                case QrPhase.Success when result.CookieHeader is { Length: > 0 } cookie:
                {
                    var identity = await qr.ProbeIdentityAsync(cookie, cancellationToken);
                    if (identity is null)
                    {
                        sessions.TryRemove(key, out _);
                        events.Record(EventActors.Guest, null, EventNames.LoginDenied, "qr", key,
                            "{\"reason\":\"B 站不认这份登录回执\"}", ip);
                        return Results.Json(new { message = "B 站没认下这次登录，重新扫一次试试" },
                            statusCode: StatusCodes.Status401Unauthorized);
                    }

                    if (session.Mode == "bind")
                    {
                        var binder = db.GetMemberByMid(binderMid!.Value)!;
                        db.UpsertMemberAccount(binder.Id, identity.Mid, identity.Name, identity.Face, cookie);
                        sessions.TryRemove(key, out _);
                        events.Record(EventActors.Member, binderMid, EventNames.LoginSuccess, "member",
                            identity.Mid.ToString(), "{\"mode\":\"bind\"}", ip, binder.Id);
                        return Results.Ok(new
                        {
                            phase = "success",
                            account = new { accountMid = identity.Mid, nickname = identity.Name, faceUrl = identity.Face },
                        });
                    }

                    var outcome = AdmitMember(db, gate, identity, session.Invite, cookie,
                        loggerFactory.CreateLogger("MemberMail"));
                    sessions.TryRemove(key, out _);
                    if (outcome.Error is not null)
                    {
                        events.Record(EventActors.Guest, identity.Mid, EventNames.LoginDenied, "member",
                            identity.Mid.ToString(), $"{{\"reason\":\"{outcome.Error}\"}}", ip);
                        return Results.Json(new { message = outcome.Error }, statusCode: outcome.Status);
                    }

                    http.Response.Cookies.Append(MemberGate.Cookie, outcome.Token!, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Lax,
                        Path = "/",
                        MaxAge = MemberGate.SessionTtl,
                    });

                    events.Record(EventActors.Member, identity.Mid, EventNames.LoginSuccess, "member",
                        identity.Mid.ToString(), ip: ip, actorMemberId: db.GetMemberByMid(identity.Mid)?.Id);
                    return Results.Ok(new
                    {
                        phase = "success",
                        member = new
                        {
                            mid = identity.Mid,
                            nickname = identity.Name,
                            faceUrl = identity.Face,
                            role = AdmitRole(db, identity.Mid),
                        },
                    });
                }

                default:
                    return Results.Ok(new { phase = "unknown", message = result.Message });
            }
        });

        app.MapPost("/api/member/logout", (EventRecorder events, HttpContext http) =>
        {
            var member = MemberGate.Current(http);
            if (member is not null)
            {
                // 退出只拆本端 cookie：朋友可能多端在线，按成员吊销太重；残留令牌 30 天自然过期。
                events.Record(EventActors.Member, member.Mid, EventNames.LoginLogout, "member", member.Id.ToString(),
                    ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: member.Id);
            }

            http.Response.Cookies.Delete(MemberGate.Cookie, new CookieOptions { Path = "/" });
            return Results.Ok(new { message = "已退出登录" });
        });

        app.MapGet("/api/member/me", (MemberGate gate, StationDb db, HttpContext http) =>
        {
            var member = MemberGate.Current(http);
            if (member is null)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var accounts = db.ListMemberAccounts(member.Id);
            var stats = db.ListMemberIngestStats().SingleOrDefault(s => s.SubmitterMemberId == member.Id);
            return Results.Ok(new
            {
                memberId = member.Id,
                mid = member.Mid,
                username = member.Username,
                hasPassword = db.HasMemberPassword(member.Id),
                hasBilibili = db.PickMemberAccountForDownload(member.Id) is not null,
                nickname = member.Nickname,
                faceUrl = member.FaceUrl,
                note = member.Note,
                role = member.Role,
                lastLoginAtUtc = member.LastLoginAtUtc,
                accounts = accounts.Select(a => new
                {
                    id = a.Id,
                    accountMid = a.AccountMid,
                    accountName = a.AccountName,
                    isPrimary = a.IsPrimary,
                    expired = a.Expired,
                }),
                submitted = stats?.VideoCount ?? 0,
                bytes = stats?.TotalBytes ?? 0,
                danmaku = stats?.DanmakuCount ?? 0,
            });
        });

        app.MapPost("/api/member/accounts/primary", (AccountIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            var member = MemberGate.Current(http);
            if (member is null)
            {
                return Results.Unauthorized();
            }

            if (!db.ListMemberAccounts(member.Id).Any(a => a.Id == request.Id))
            {
                return Results.Json(new { message = "这个号不在你的池里" }, statusCode: StatusCodes.Status404NotFound);
            }

            db.SetMemberAccountPrimary(request.Id);
            events.Record(EventActors.Member, member.Mid, EventNames.MemberSetPrimary, "account", request.Id.ToString(),
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: member.Id);
            return Results.Ok(new { message = "主号已切换，下载默认用它" });
        });

        app.MapPost("/api/member/accounts/delete", (AccountIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            var member = MemberGate.Current(http);
            if (member is null)
            {
                return Results.Unauthorized();
            }

            if (!db.ListMemberAccounts(member.Id).Any(a => a.Id == request.Id))
            {
                return Results.Json(new { message = "这个号不在你的池里" }, statusCode: StatusCodes.Status404NotFound);
            }

            db.DeleteMemberAccount(request.Id);
            events.Record(EventActors.Member, member.Mid, EventNames.MemberDropAccount, "account", request.Id.ToString(),
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: member.Id);
            return Results.Ok(new { message = "号已从池中逐出" });
        });

        void SweepStale()
        {
            foreach (var stale in sessions.Where(kv => DateTimeOffset.UtcNow - kv.Value.CreatedUtc > QrTtl))
            {
                sessions.TryRemove(stale.Key, out _);
            }
        }
    }

    internal static (string? Token, string? Error, int Status) AdmitMember(StationDb db, MemberGate gate,
        BilibiliIdentity identity, string? invite, string cookie, ILogger? logger = null)
    {
        var existing = db.GetMemberByMid(identity.Mid);
        if (existing is not null)
        {
            if (existing.Status == MemberStatus.Banned)
            {
                return (null, "这个 B 站号已被站长请出分站", StatusCodes.Status403Forbidden);
            }

            db.UpsertMemberAccount(existing.Id, identity.Mid, identity.Name, identity.Face, cookie);
            db.TouchMemberLogin(identity.Mid, identity.Name, identity.Face);
            return (gate.IssueSession(existing.Id).Token, null, 200);
        }

        // 生面孔：有主之站没邀请码不进人；无主之站首扫免码——邀请码只有站长能发，
        // 无主还索要邀请码就是鸡生蛋死锁，首位扫码者直接继位。
        long seatId;
        if (invite is not null && db.FindInvitedByCode(invite) is { } invited)
        {
            seatId = invited.Id;
        }
        else if (!db.HasOwner())
        {
            // 无主站点给首位建空位（临时码只用来落地席位，绑定时即刻作废）。
            (seatId, _) = db.CreateMemberInvite(null);
        }
        else
        {
            return (null, "分站是邀请制：首次进站需要站长发的邀请码", StatusCodes.Status403Forbidden);
        }

        var bound = db.BindMember(seatId, identity.Mid, identity.Name, identity.Face);
        if (bound is null)
        {
            // 抢码竞速：这号刚被别的席位认走，直接按重登放行
            var raced = db.GetMemberByMid(identity.Mid);
            if (raced is not { Status: MemberStatus.Active })
            {
                return (null, "邀请码兑现失败，请让站长重发一枚", StatusCodes.Status409Conflict);
            }

            db.UpsertMemberAccount(raced.Id, identity.Mid, identity.Name, identity.Face, cookie);
            return (gate.IssueSession(raced.Id).Token, null, 200);
        }

        db.UpsertMemberAccount(bound.Id, identity.Mid, identity.Name, identity.Face, cookie);
        // 首扫建档那一刻寄欢迎信（邀请兑现与无主继位都走这里）；重登不重发。收件人是站内席位 id（M5.4）。
        // 寄信失败只 log warning（MemberMail 内部包裹），绝不阻断进站。
        MemberMail.NotifyWelcome(db, logger, bound.Id);
        return (gate.IssueSession(bound.Id).Token, null, 200);
    }

    private static string AdmitRole(StationDb db, long mid)
        => db.GetMemberByMid(mid)?.Role ?? MemberRoles.Member;

    private static string? NormalizeInvite(string? invite)
        => string.IsNullOrWhiteSpace(invite) ? null : invite.Trim().ToUpperInvariant();

    private static string PhaseName(QrPhase phase) => phase switch
    {
        QrPhase.Waiting => "waiting",
        QrPhase.Scanned => "scanned",
        QrPhase.Expired => "expired",
        QrPhase.Success => "success",
        _ => "unknown",
    };
}
