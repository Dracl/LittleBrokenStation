using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 用户间私信（M5.3）：成员对成员往来说，挂在成员身份上（站长也是 member，走同一条路）。
/// 校验全收进 ValidateSend 一个函数——端点 lambda 与测试驱动的是同一张嘴。
/// 管理员视野 GET /api/admin/dm 挂在 /api/admin/ 前缀下，门自动把关。
/// </summary>
public static class DmEndpoints
{
    public const int MaxDmLength = 2000;

    private sealed record DmSendRequest(long? ToMid, string? Body);

    /// <summary>发送前校验：错误给状态码与文案，null = 放行（body 已 trim）。</summary>
    internal static ((int Status, string Message)? Error, long ToMid, string Body) ValidateSend(
        StationDb db, long fromMid, long? toMid, string? rawBody)
    {
        var body = rawBody?.Trim() ?? string.Empty;
        if (toMid is not long target)
        {
            return ((StatusCodes.Status400BadRequest, "得先有个收信人"), 0, body);
        }

        if (body.Length == 0)
        {
            return ((StatusCodes.Status400BadRequest, "私信总不能写空气"), target, body);
        }

        if (body.Length > MaxDmLength)
        {
            return ((StatusCodes.Status400BadRequest, "私信太长了，分站不是论文站"), target, body);
        }

        if (target == fromMid)
        {
            return ((StatusCodes.Status400BadRequest, "不能给自己写信，信箱就在顶栏"), target, body);
        }

        if (db.GetMemberByMid(fromMid) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status403Forbidden, "你的席位不在站上，私信闸落着"), target, body);
        }

        if (db.GetMemberByMid(target) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status404NotFound, "对方不在站"), target, body);
        }

        return (null, target, body);
    }

    public static void MapDmEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dm/threads", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http)?.Mid is not long mid)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var threads = db.ListDmThreads(mid).Select(t => new
            {
                peerMid = t.PeerMid,
                peerName = t.PeerName,
                peerFace = t.PeerFace,
                lastBody = t.LastBody,
                lastAtUtc = t.LastAtUtc,
                unread = t.Unread,
            });
            return Results.Ok(new { threads });
        });

        app.MapGet("/api/dm/unread", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http)?.Mid is not long mid)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(new { unread = db.CountDmUnread(mid) });
        });

        app.MapGet("/api/dm/directory", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http)?.Mid is not long mid)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var people = db.GetActiveMembersForDirectory()
                .Where(m => m.Mid is long pm && pm != mid)
                .Select(m => new { mid = m.Mid, nickname = m.Nickname, faceUrl = m.FaceUrl });
            return Results.Ok(new { people });
        });

        // 拉会话即标读：进会话那一刻，对方发来的就算读过了。
        app.MapGet("/api/dm/thread/{peerMid:long}", (long peerMid, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http)?.Mid is not long mid)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (db.GetMemberByMid(peerMid) is not { Status: MemberStatus.Active } peer)
            {
                return Results.Json(new { message = "对方不在站" }, statusCode: StatusCodes.Status404NotFound);
            }

            db.MarkDmRead(mid, peerMid);
            var messages = db.ListDmConversation(mid, peerMid).Select(m => new
            {
                id = m.Id,
                fromMid = m.FromMid,
                toMid = m.ToMid,
                body = m.Body,
                createdAtUtc = m.CreatedAtUtc,
                readAtUtc = m.ReadAtUtc,
            });
            return Results.Ok(new
            {
                messages,
                peer = new { mid = peer.Mid, nickname = peer.Nickname, faceUrl = peer.FaceUrl },
            });
        });

        app.MapPost("/api/dm", (DmSendRequest request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http)?.Mid is not long mid)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (error, target, body) = ValidateSend(db, mid, request.ToMid, request.Body);
            if (error is not null)
            {
                return Results.Json(new { message = error.Value.Message }, statusCode: error.Value.Status);
            }

            var id = db.SendDm(mid, target, body);
            return Results.Ok(new { id });
        });
    }

    public static void MapAdminDmEndpoints(this WebApplication app)
    {
        // 管理员视野：全部私信，或锁一对往来（peerA+peerB 同给才锁）。
        app.MapGet("/api/admin/dm", (long? peerA, long? peerB, StationDb db) =>
        {
            var rows = db.ListDmForAdmin(peerA, peerB).Select(r => new
            {
                id = r.Id,
                fromMid = r.FromMid,
                fromName = r.FromName,
                toMid = r.ToMid,
                toName = r.ToName,
                body = r.Body,
                createdAtUtc = r.CreatedAtUtc,
                readAtUtc = r.ReadAtUtc,
            });
            return Results.Ok(new { messages = rows });
        });
    }
}
