using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站内信箱端点（M5.2）：成员侧读信/标读挂在 /api/member/ 下（与 /api/member/me 同款姿势，
/// 进门先点席位，未登录一律 401）；站长侧全站播送挂在 /api/admin/ 前缀（门自动把关），
/// 播送在事件台账留一枚 admin.announce。
/// </summary>
public static class MemberMessageEndpoints
{
    private sealed record AnnounceRequest(string? Title, string? Body);

    public static void MapMemberMessageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/member/messages", (string? unreadOnly, int? limit, StationDb db, HttpContext http) =>
        {
            // Mid 为 null 的是还没绑定的待邀席位，没有信箱可言，一律按未登录打发。
            if (MemberGate.Current(http)?.Mid is not long mid)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var wantUnread = string.Equals(unreadOnly, "true", StringComparison.OrdinalIgnoreCase)
                             || unreadOnly == "1";
            var items = db.ListMessages(mid, limit ?? 50, wantUnread)
                .Select(m => new
                {
                    id = m.Id,
                    kind = m.Kind,
                    fromMid = m.FromMid,
                    fromName = m.FromName,
                    title = m.Title,
                    body = m.Body,
                    link = m.Link,
                    createdAtUtc = m.CreatedAtUtc,
                    readAtUtc = m.ReadAtUtc,
                });
            return Results.Ok(new { items, unread = db.CountUnread(mid) });
        });

        app.MapPost("/api/member/messages/{id:long}/read", (long id, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http)?.Mid is not long mid)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // MarkRead 自带 to_mid 条件：递别人的信号等于空手而归，且幂等不报错。
            db.MarkRead(id, mid);
            return Results.Ok(new { message = "已阅" });
        });

        app.MapPost("/api/member/messages/read-all", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http)?.Mid is not long mid)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var read = db.MarkAllRead(mid);
            return Results.Ok(new { message = $"已阅 {read} 封", read });
        });

        // 全站公告（站长室）：只向 active 席位播送，台账留痕。
        app.MapPost("/api/admin/messages/announce", (AnnounceRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            var title = request.Title?.Trim();
            var body = request.Body?.Trim();
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body))
            {
                return Results.Json(new { message = "公告要有标题和正文才能播送" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var sent = db.BroadcastToActive(MemberMail.KindAnnounce, title, body);
            events.RecordWithPayload(EventActors.Admin, MemberGate.Current(http)?.Mid, EventNames.AdminAnnounce,
                "message", "broadcast", new { title, sent },
                ip: http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { sent, message = $"已送达 {sent} 席" });
        });
    }
}
