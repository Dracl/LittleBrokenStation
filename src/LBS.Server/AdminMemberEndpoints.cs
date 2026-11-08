using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站长室的成员管理：席位清单（含每人账号池与入站账本）、发邀请码、封禁/解封、撤席位。
/// 全程走 owner 会话门（AdminGate.IsAdminRequest 前缀命中，middleware 已把关），
/// 站长对自己这位头名免疫封禁与删除——不能把自己赶出家门。
/// </summary>
public static class AdminMemberEndpoints
{
    private sealed record InviteRequest(string? Note);

    private sealed record MemberIdRequest(long Id);

    private sealed record AccountIdRequest(long Id);

    public static void MapAdminMemberEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/members", (StationDb db) =>
        {
            var statsByMember = db.ListMemberIngestStats().ToDictionary(s => s.SubmitterMemberId);
            var accountsByMember = db.ListMemberAccounts().GroupBy(a => a.MemberId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var ownerMid = db.ListMembers().SingleOrDefault(m => m.Role == MemberRoles.Owner)?.Mid;

            return Results.Ok(db.ListMembers().Select(m => new
            {
                id = m.Id,
                mid = m.Mid,
                username = m.Username,
                nickname = m.Nickname,
                faceUrl = m.FaceUrl,
                note = m.Note,
                status = m.Status,
                role = m.Role,
                createdAtUtc = m.CreatedAtUtc,
                boundAtUtc = m.BoundAtUtc,
                lastLoginAtUtc = m.LastLoginAtUtc,
                accounts = accountsByMember.GetValueOrDefault(m.Id, [])
                    .Select(a => new
                    {
                        id = a.Id,
                        accountMid = a.AccountMid,
                        accountName = a.AccountName,
                        faceUrl = a.FaceUrl,
                        isPrimary = a.IsPrimary,
                        expired = a.Expired,
                        lastSeenUtc = a.LastSeenUtc,
                    }),
                submitted = statsByMember.GetValueOrDefault(m.Id)?.VideoCount ?? 0,
                bytes = statsByMember.GetValueOrDefault(m.Id)?.TotalBytes ?? 0,
            }));
        });

        // 事件台账查阅（也是未来 AI 分析的取数口）：身份/事件名前缀/条数都可筛。
        app.MapGet("/api/admin/events", (int? limit, string? actor, string? prefix, StationDb db) =>
            Results.Ok(new
            {
                total = db.CountEvents(),
                events = db.QueryEvents(Math.Clamp(limit ?? 200, 1, 2000),
                    string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(), null,
                    string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim()),
            }));

        app.MapPost("/api/admin/members/invite", (InviteRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            var (id, code) = db.CreateMemberInvite(string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim());
            events.RecordWithPayload(EventActors.Admin, OwnerMid(db), EventNames.MemberInvite, "member", id.ToString(),
                new { note = request.Note }, ip: http.Connection.RemoteIpAddress?.ToString(),
                actorMemberId: MemberGate.Current(http)?.Id);
            // 「永久」是实话而非许愿：兑现判据 FindInvitedByCode 只看 invite_code 与 status='invited'，
            // 库里也没有过期列——以前这句「 7 天内有效」是口头上锁、手上没门，发出去的码隔月照样灵。
            return Results.Ok(new { id, code, message = $"邀请码 {code} 已生成，一次性、永久有效" });
        });

        app.MapPost("/api/admin/members/ban", (MemberIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
            SetBanned(db, events, http, request.Id, banned: true));

        app.MapPost("/api/admin/members/unban", (MemberIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
            SetBanned(db, events, http, request.Id, banned: false));

        app.MapPost("/api/admin/members/delete", (MemberIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            var member = db.GetMemberById(request.Id);
            if (member is null)
            {
                return Results.NotFound(new { message = "没有这个席位" });
            }

            if (member.Role == MemberRoles.Owner)
            {
                return Results.Json(new { message = "站长不能把自己逐出自己家" }, statusCode: StatusCodes.Status400BadRequest);
            }

            db.DeleteMember(request.Id);
            events.RecordWithPayload(EventActors.Admin, OwnerMid(db), EventNames.MemberDelete, "member",
                request.Id.ToString(), new { mid = member.Mid, nickname = member.Nickname },
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: OwnerMemberId(db));
            // 措词与「除名」分家：这一口是真销席（连账号池与会话一起拍），名字上再叫「除名」
            // 就和上面那枚 exiled 软删撞成一回事了。
            return Results.Ok(new { message = $"「{member.Nickname ?? "该席位"}」已从分站销席（历史事件与投稿归属保留）" });
        });

        // 除名（软删席位，11-08 站长拍板）：与销席的分别是「行还在」——历史名片、投稿归属、
        // 发过的弹幕都还点得开（真删会让已除名者的弹幕集体掉脸）；与封禁的分别是「意图」——
        // 封禁随时可解禁，除名得走「召回」，不是他自己扫个码就当没事人（判据见 MemberStatus.AllowsAdmission）。
        app.MapPost("/api/admin/members/exile", (MemberIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) => SetExiled(db, events, http, request.Id, exiled: true));

        app.MapPost("/api/admin/members/restore", (MemberIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) => SetExiled(db, events, http, request.Id, exiled: false));

        // 发过的邀请码事后查（11-08）：只回 invited 且码还活着的行，一次性消费完就从这里消失。
        app.MapGet("/api/admin/members/invites", (StationDb db) => Results.Ok(new
        {
            items = db.ListPendingInvites().Select(i => new
            {
                id = i.MemberId,
                code = i.Code,
                note = i.Note,
                createdAtUtc = i.CreatedAtUtc,
            }),
        }));

        // 作废一枚码：两条兑现路（扫码与账密注册）都经 FindInvitedByCode，抽掉码就同时断。
        app.MapPost("/api/admin/members/invite/revoke", (MemberIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            var seat = db.GetMemberById(request.Id);
            if (seat is null)
            {
                return Results.NotFound(new { message = "没有这个席位" });
            }

            if (!db.RevokeMemberInvite(request.Id))
            {
                return Results.Conflict(new { message = "这枚码已经用掉了（或本来就没了），撤不动" });
            }

            events.RecordWithPayload(EventActors.Admin, OwnerMid(db), EventNames.MemberInviteRevoke, "member",
                request.Id.ToString(), new { mid = seat.Mid, note = seat.Note },
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: MemberGate.Current(http)?.Id);
            return Results.Ok(new { message = "这枚码作废了，拿它再也进不了人（空席位还留着，重新发一枚就是）" });
        });

        // 站长代管成员账号池：删失效号（成员自己也能删，这里是站长兜底）
        app.MapPost("/api/admin/members/account/delete", (AccountIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            db.DeleteMemberAccount(request.Id);
            events.Record(EventActors.Admin, OwnerMid(db), EventNames.AdminAction, "account", request.Id.ToString(),
                "{\"action\":\"delete\"}", http.Connection.RemoteIpAddress?.ToString(), OwnerMemberId(db));
            return Results.Ok(new { message = "账号已从池中移除" });
        });
    }

    private static IResult SetExiled(StationDb db, EventRecorder events, HttpContext http, long id, bool exiled)
    {
        var member = db.GetMemberById(id);
        if (member is null)
        {
            return Results.NotFound(new { message = "没有这个席位" });
        }

        // 站长对自己这位头名免疫除名——照抄 ban 那道的同一句话。
        // SetMemberStatus 本身不认角色，防线只在端点里，这里漏一笔就能把自己除成没站长（HasOwner 仍真，
        // 登录页也不会挂「首位继位」告示，全站从此没人进得了站长室）。
        if (member.Role == MemberRoles.Owner)
        {
            return Results.Json(new { message = "站长席位不可除名" }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (exiled && member.Status == MemberStatus.Exiled)
        {
            return Results.Ok(new { message = "这位已经在站外了，不劳再除一次" });
        }

        if (!exiled && member.Status != MemberStatus.Exiled)
        {
            return Results.Json(new { message = "这位不是被除名的席位，召回无从谈起（封禁的走解禁）" },
                statusCode: StatusCodes.Status409Conflict);
        }

        db.SetMemberStatus(id, exiled ? MemberStatus.Exiled : MemberStatus.Active);
        events.RecordWithPayload(EventActors.Admin, OwnerMid(db),
            exiled ? EventNames.MemberExile : EventNames.MemberRestore, "member", id.ToString(),
            new { mid = member.Mid, nickname = member.Nickname },
            ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: OwnerMemberId(db));
        return Results.Ok(new
        {
            message = exiled
                ? "已除名：席位与历史都留着，但他扫不进来了——要召回得回这里点「召回」"
                : "已召回：席位回到站内，他可以重新扫了",
        });
    }

    private static IResult SetBanned(StationDb db, EventRecorder events, HttpContext http, long id, bool banned)
    {
        var member = db.GetMemberById(id);
        if (member is null)
        {
            return Results.NotFound(new { message = "没有这个席位" });
        }

        if (member.Role == MemberRoles.Owner)
        {
            return Results.Json(new { message = "站长席位不可封禁" }, statusCode: StatusCodes.Status400BadRequest);
        }

        // 解禁不许把除名席洗成 active：那等于绕开「召回」这枚钮，除名与封禁两笔账在事件台账里从此对不上。
        if (!banned && member.Status == MemberStatus.Exiled)
        {
            return Results.Json(new { message = "这位是被除名的，得走「召回」，解禁管不着" },
                statusCode: StatusCodes.Status409Conflict);
        }

        db.SetMemberStatus(id, banned ? MemberStatus.Banned : MemberStatus.Active);
        events.RecordWithPayload(EventActors.Admin, OwnerMid(db),
            banned ? EventNames.MemberBan : EventNames.MemberUnban, "member", id.ToString(),
            new { mid = member.Mid, nickname = member.Nickname },
            ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: OwnerMemberId(db));
        return Results.Ok(new { message = banned ? "已封禁：即刻请出分站，再扫码也不收" : "已解禁：欢迎回来" });
    }

    private static long? OwnerMid(StationDb db)
        => db.ListMembers().SingleOrDefault(m => m.Role == MemberRoles.Owner)?.Mid;

    private static long? OwnerMemberId(StationDb db)
        => db.ListMembers().SingleOrDefault(m => m.Role == MemberRoles.Owner)?.Id;
}
