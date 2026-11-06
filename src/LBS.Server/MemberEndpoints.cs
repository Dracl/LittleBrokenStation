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

    /// <summary>同一台来路同时能摊开几张码。抬到 6 是给「换一张试试」留出余地；真超了先踢自己最旧那张，不锁人。</summary>
    private const int MaxOpenQrPerIp = 6;

    /// <summary>全站活票总闸：挡的是陌生人脚本刷码，只在这一条真撞上时才说「等着的人太多」。</summary>
    private const int MaxOpenQrTotal = 64;

    private sealed record QrNewRequest(string? Invite, string? Mode);

    /// <summary>退票只递 key：票是谁的、还在不在，都由分站对着票池核，外面问不出存在性。</summary>
    private sealed record QrCancelRequest(string? Key);

    private sealed record AccountIdRequest(long Id);

    public static void MapMemberEndpoints(this WebApplication app)
    {
        var jar = new QrTicketJar();

        // 公开探针：登录页用它挂“首位扫码者继位站长”的告示，不需要任何身份。
        app.MapGet("/api/member/status", (StationDb db) => Results.Ok(new { hasOwner = db.HasOwner() }));

        app.MapPost("/api/member/qr/new", async (QrNewRequest? request, LbsQrLogin qr, StationDb db,
            EventRecorder events, HttpContext http, CancellationToken cancellationToken) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            jar.SweepStale(DateTimeOffset.UtcNow, QrTtl);

            // 同人换码不该吃 429：per-IP 超限改成踢他最旧那张腾坑，429 只留给总闸真满。
            if (jar.Total >= MaxOpenQrTotal)
            {
                return Results.Json(new { message = QrMessages.TooManyWaiting }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            // bind 模式：已登录成员往自己账号池里再接一个 B 站号，不建新席不发新会话。
            var mode = string.Equals(request?.Mode, "bind", StringComparison.OrdinalIgnoreCase) ? "bind" : "admit";
            var binder = MemberGate.Current(http);
            if (mode == "bind" && binder is null)
            {
                return Results.Json(new { message = "先以会员身份进站，才能往自己池里加号" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var challenge = await qr.NewChallengeAsync(cancellationToken);
            if (challenge is null)
            {
                return Results.Json(new { message = "B 站没给二维码，稍后再试" }, statusCode: StatusCodes.Status502BadGateway);
            }

            // 坑到这一步才真腾：取码没成就把人家手里那张好票顶掉，等于帮倒忙。
            if (mode == "bind")
            {
                jar.RemoveOthersOfMember(binder!.Id);
            }
            else
            {
                while (jar.CountByIp(ip) >= MaxOpenQrPerIp && jar.RemoveOldestByIp(ip))
                {
                }
            }

            jar.Add(challenge.QrcodeKey, ip, NormalizeInvite(request?.Invite), mode, mode == "bind" ? binder!.Id : null,
                DateTimeOffset.UtcNow);
            events.Record(EventActors.Guest, null, EventNames.LoginStart, "qr", challenge.QrcodeKey, ip: ip);
            return Results.Ok(new { url = challenge.Url, key = challenge.QrcodeKey });
        });

        // 退票口：关窗、换一张、扫成功都来这儿把坑还掉。匿名可达（登录页本来就在门外），
        // 且四种结局（不存在 / 不是你的 / 已经销了 / 真销掉了）回的是同一个响应，免得拿它探谁的码还在。
        app.MapPost("/api/member/qr/cancel", (QrCancelRequest? request, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Key) || !jar.TryGet(request.Key, out var session) || session is null)
            {
                return Results.Ok(new { ok = true });
            }

            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!string.Equals(session.SourceIp, ip, StringComparison.Ordinal))
            {
                return Results.Ok(new { ok = true });
            }

            // bind 的票只有发起人那个席位销得动，别人拿着 key 也还不了他的坑。
            if (session.Mode == "bind" && MemberGate.Current(http)?.Id != session.OwnerMemberId)
            {
                return Results.Ok(new { ok = true });
            }

            jar.Remove(request.Key);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/member/qr/poll", async (string? key, LbsQrLogin qr, StationDb db, MemberGate gate,
            EventRecorder events, ILoggerFactory loggerFactory, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(key) || !jar.TryGet(key, out var session) || session is null)
            {
                // 票池在内存里，分站一重启就空了——这话得说实话，别让人以为 B 站把码废了。
                return Results.Json(new { message = QrMessages.TicketGone }, statusCode: StatusCodes.Status410Gone);
            }

            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!string.Equals(session.SourceIp, ip, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            // bind 模式的码只能发起人自己消——拿别人刚扫完的码偷号不行。
            // 钥匙用席位 id（票上那枚 OwnerMemberId），不拿 mid 当门：站长新开的空位 mid 还是 NULL，
            // 拿 mid 卡人恰好把「给空位绑第一个号」这一最该成的情形关在门外（401 空 body，前端只能报作废）。
            if (session.Mode == "bind")
            {
                var binderSeat = MemberGate.Current(http);
                if (binderSeat is null || binderSeat.Id != session.OwnerMemberId)
                {
                    return Results.Unauthorized();
                }
            }

            if (DateTimeOffset.UtcNow - session.CreatedUtc > QrTtl)
            {
                jar.Remove(key);
                events.Record(EventActors.Guest, null, EventNames.LoginExpired, "qr", key, ip: ip);
                return Results.Ok(new { phase = "expired", message = QrMessages.TtlExpired });
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
                    jar.Remove(key);
                    events.Record(EventActors.Guest, null, EventNames.LoginExpired, "qr", key, ip: ip);
                    return Results.Ok(new { phase = "expired", message = result.Message ?? QrMessages.UpstreamExpired });

                case QrPhase.Success when result.CookieHeader is { Length: > 0 } cookie:
                {
                    var identity = await qr.ProbeIdentityAsync(cookie, cancellationToken);
                    if (identity is null)
                    {
                        jar.Remove(key);
                        events.Record(EventActors.Guest, null, EventNames.LoginDenied, "qr", key,
                            "{\"reason\":\"B 站不认这份登录回执\"}", ip);
                        return Results.Json(new { message = "B 站没认下这次登录，重新扫一次试试" },
                            statusCode: StatusCodes.Status401Unauthorized);
                    }

                    if (session.Mode == "bind")
                    {
                        var binder = db.GetMemberById(session.OwnerMemberId!.Value);
                        if (binder is null)
                        {
                            // 码还挂在手上时席位被逐了：不拿 null 去碰账，说清楚让他重发一次。
                            jar.Remove(key);
                            return Results.Json(new { message = "发起绑定的席位已经不在了，重新发起一次" },
                                statusCode: StatusCodes.Status410Gone);
                        }

                        db.UpsertMemberAccount(binder.Id, identity.Mid, identity.Name, identity.Face, cookie);
                        jar.Remove(key);
                        events.Record(EventActors.Member, identity.Mid, EventNames.LoginSuccess, "member",
                            identity.Mid.ToString(), "{\"mode\":\"bind\"}", ip, binder.Id);
                        return Results.Ok(new
                        {
                            phase = "success",
                            account = new { accountMid = identity.Mid, nickname = identity.Name, faceUrl = identity.Face },
                        });
                    }

                    var outcome = AdmitMember(db, gate, identity, session.Invite, cookie,
                        loggerFactory.CreateLogger("MemberMail"));
                    jar.Remove(key);
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
                        // 扫码登录这一口也必须上 Secure：它与 AppendSessionCookie 发的是同一张席位票，
                        // 两边口径不一致就等于「换机登录比注册更容易被扫走」。
                        // .NET 10 已无 CookieOptions.SecurePolicy，SameAsRequest 自己算（反代身后靠 X-Forwarded-Proto）。
                        Secure = http.Request.IsHttps,
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
                    // 头像以前就漏在这次投影里：ListMemberAccounts 的 SQL 早把 face_url 读出来了，
                    // 前端那行想画也没得画（账号池一行不能把 cookie 带上，故只交这三样人旺的）。
                    faceUrl = a.FaceUrl,
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
