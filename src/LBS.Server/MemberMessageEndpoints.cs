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
    private sealed record NotifyPrefsRequest(IReadOnlyList<string>? Mutes);

    public static void MapMemberMessageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/member/messages", (string? unreadOnly, int? limit, StationDb db, HttpContext http) =>
        {
            // M5.4：信箱挂在席位 id 上——素净席位无 mid 也有自己的信箱。
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var wantUnread = string.Equals(unreadOnly, "true", StringComparison.OrdinalIgnoreCase)
                             || unreadOnly == "1";
            var items = db.ListMessages(me.Id, limit ?? 50, wantUnread)
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
                    // 归档回执里的片链死了没：没带片链的信（公告/欢迎）给 null，前端不去判。
                    videoGone = m.VideoInStation == false,
                    // 结构化卡面（11-03 到站播报）：msgType 认卡型，payloadJson 原样递给渲染器自解析；老信两列 null。
                    msgType = m.MsgType,
                    payloadJson = m.PayloadJson,
                });
            return Results.Ok(new { items, unread = db.CountUnread(me.Id) });
        });

        app.MapPost("/api/member/messages/{id:long}/read", (long id, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // MarkRead 自带 to_member_id 条件：递别人的信号等于空手而归，且幂等不报错。
            db.MarkRead(id, me.Id);
            return Results.Ok(new { message = "已阅" });
        });

        app.MapPost("/api/member/messages/read-all", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var read = db.MarkAllRead(me.Id);
            return Results.Ok(new { message = $"已阅 {read} 封", read });
        });

        // 通知偏好（11-03 站长：「有些人嫌烦」）：席位自关到站播报/周报/投稿回执三路定时货。
        // GET 回当前静音清单与全部频道（前端渲染开关用）；PUT 存清单，白名单外的频道顶回去。
        // 欢迎信、站长公告、私信不在此列——它们本就没有可关的频道。
        app.MapGet("/api/member/notify-prefs", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(new { mutes = db.GetNotifyMutes(me.Id), channels = NotifyMutes.All });
        });

        app.MapPut("/api/member/notify-prefs", (NotifyPrefsRequest request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // 白名单闸在库层 Sanitize 之外再道明文的：越界频道直接 400，不无声吞掉——
            // 前端递错频道是 bug，得让它响；修边/小写/去重这种脏活库层已经干完。
            var wanted = (request.Mutes ?? Array.Empty<string>())
                .Select(c => c?.Trim().ToLowerInvariant())
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .ToList();
            var illegal = wanted.Where(c => !NotifyMutes.IsKnown(c)).ToList();
            if (illegal.Count > 0)
            {
                return Results.Json(new { message = $"没这个通知频道：{string.Join("、", illegal)}" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!db.SetNotifyMutes(me.Id, wanted))
            {
                return Results.Json(new { message = "席位不在，招呼没处打" }, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(new { mutes = db.GetNotifyMutes(me.Id), message = "已打好招呼" });
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
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: MemberGate.Current(http)?.Id);
            return Results.Ok(new { sent, message = $"已送达 {sent} 席" });
        });
    }
}
