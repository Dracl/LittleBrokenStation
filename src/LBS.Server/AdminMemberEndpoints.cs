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
            var statsByMid = db.ListMemberIngestStats().ToDictionary(s => s.SubmitterMid);
            var accountsByMember = db.ListMemberAccounts().GroupBy(a => a.MemberId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var ownerMid = db.ListMembers().SingleOrDefault(m => m.Role == MemberRoles.Owner)?.Mid;

            return Results.Ok(db.ListMembers().Select(m => new
            {
                id = m.Id,
                mid = m.Mid,
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
                submitted = m.Mid is long mid ? statsByMid.GetValueOrDefault(mid)?.VideoCount ?? 0 : 0,
                bytes = m.Mid is long mid2 ? statsByMid.GetValueOrDefault(mid2)?.TotalBytes ?? 0 : 0,
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
                new { note = request.Note }, ip: http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { id, code, message = $"邀请码 {code} 已生成，一次性、7 天内有效" });
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
                ip: http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { message = $"「{member.Nickname ?? "该席位"}」已从分站除名（历史事件与投稿归属保留）" });
        });

        // 站长代管成员账号池：删失效号（成员自己也能删，这里是站长兜底）
        app.MapPost("/api/admin/members/account/delete", (AccountIdRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            db.DeleteMemberAccount(request.Id);
            events.Record(EventActors.Admin, OwnerMid(db), EventNames.AdminAction, "account", request.Id.ToString(),
                "{\"action\":\"delete\"}", http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { message = "账号已从池中移除" });
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

        db.SetMemberStatus(id, banned ? MemberStatus.Banned : MemberStatus.Active);
        events.RecordWithPayload(EventActors.Admin, OwnerMid(db),
            banned ? EventNames.MemberBan : EventNames.MemberUnban, "member", id.ToString(),
            new { mid = member.Mid, nickname = member.Nickname },
            ip: http.Connection.RemoteIpAddress?.ToString());
        return Results.Ok(new { message = banned ? "已封禁：即刻请出分站，再扫码也不收" : "已解禁：欢迎回来" });
    }

    private static long? OwnerMid(StationDb db)
        => db.ListMembers().SingleOrDefault(m => m.Role == MemberRoles.Owner)?.Mid;
}
