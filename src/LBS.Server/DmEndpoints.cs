using System.Text.Json;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 用户间私信（M5.3，M5.4 起全面挂站内席位 id）：成员对成员往来说，素净席位也能聊——
/// 私信是站内事，不再借 B 站 mid 认人。站长也是 member，走同一条路。
/// 校验全收进 ValidateSend 一个函数——端点 lambda 与测试驱动的是同一张嘴。
/// 管理员视野 GET /api/admin/dm 挂在 /api/admin/ 前缀下，门自动把关。
/// </summary>
public static class DmEndpoints
{
    public const int MaxDmLength = 2000;

    private sealed record DmSendRequest(long? ToMemberId, string? Body);

    private sealed record VideoShareRequest(long? ToMemberId, string? Note);

    /// <summary>安利语封顶（与动态分享同规）。500 字以内刚好。</summary>
    internal const int MaxShareNoteLength = 500;

    /// <summary>
    /// 放映厅安利的校验 + 定格（与动态分享同一张嘴，端点 lambda 与测试直驱的都是这里）：
    /// 错误给状态码与文案，null = 放行；DynJson 是随信落库的视频族卡面（与动态分享 dyn_json 同族），
    /// 带 watchUrl = /watch/bvid，对方点卡直落站内放映厅。
    /// </summary>
    internal static ((int Status, string Message)? Error, long ToMemberId, string Note, string? DynJson) ValidateVideoShare(
        StationDb db, long fromMemberId, long? toMemberId, string bvid, string? rawNote)
    {
        if (toMemberId is not long target)
        {
            return ((StatusCodes.Status400BadRequest, "得先有个收信人"), 0, string.Empty, null);
        }

        if (target == fromMemberId)
        {
            return ((StatusCodes.Status400BadRequest, "给自己安利片子，那叫自娱自乐"), 0, string.Empty, null);
        }

        if (db.GetMemberById(fromMemberId) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status403Forbidden, "你的席位不在站上，分享闸落着"), 0, string.Empty, null);
        }

        if (db.GetMemberById(target) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status404NotFound, "对方不在站"), 0, string.Empty, null);
        }

        var note = rawNote?.Trim() ?? string.Empty;
        if (note.Length > MaxShareNoteLength)
        {
            return ((StatusCodes.Status400BadRequest, "安利语太长了，500 字以内刚好"), 0, string.Empty, null);
        }

        if (db.GetVideoShareSnapshot(bvid) is not { } snap)
        {
            return ((StatusCodes.Status404NotFound, "分站馆藏里没有这个片子，安利不了空气"), 0, string.Empty, null);
        }

        var dynJson = JsonSerializer.Serialize(new
        {
            type = "video",
            bvid = snap.Bvid,
            title = snap.Title,
            cover = snap.Cover,
            upName = snap.UpName,
            durationText = snap.DurationText,
            watchUrl = $"/watch/{snap.Bvid}",
        });
        return (null, target, note, dynJson);
    }

    /// <summary>发送前校验：错误给状态码与文案，null = 放行（body 已 trim）。全程席位 id。</summary>
    internal static ((int Status, string Message)? Error, long ToMemberId, string Body) ValidateSend(
        StationDb db, long fromMemberId, long? toMemberId, string? rawBody)
    {
        var body = rawBody?.Trim() ?? string.Empty;
        if (toMemberId is not long target)
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

        if (target == fromMemberId)
        {
            return ((StatusCodes.Status400BadRequest, "不能给自己写信，信箱就在顶栏"), target, body);
        }

        if (db.GetMemberById(fromMemberId) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status403Forbidden, "你的席位不在站上，私信闸落着"), target, body);
        }

        if (db.GetMemberById(target) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status404NotFound, "对方不在站"), target, body);
        }

        return (null, target, body);
    }

    public static void MapDmEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dm/threads", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var threads = db.ListDmThreads(me.Id).Select(t => new
            {
                peerMemberId = t.PeerMemberId,
                peerName = t.PeerName,
                peerFace = t.PeerFace,
                lastBody = t.LastBody,
                lastAtUtc = t.LastAtUtc,
                unread = t.Unread,
                lastMsgType = t.LastMsgType,
            });
            return Results.Ok(new { threads });
        });

        app.MapGet("/api/dm/unread", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(new { unread = db.CountDmUnread(me.Id) });
        });

        // 可私聊名单：memberId 是钥匙，mid 仅供展示（素净席位没有 mid）。
        app.MapGet("/api/dm/directory", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var people = db.GetActiveMembersForDirectory()
                .Where(m => m.Id != me.Id)
                .Select(m => new
                {
                    memberId = m.Id,
                    mid = m.Mid,
                    username = m.Username,
                    nickname = m.Nickname,
                    displayName = StationDb.DisplayOf(m),
                    faceUrl = m.FaceUrl,
                });
            return Results.Ok(new { people });
        });

        // 拉会话即标读：进会话那一刻，对方发来的就算读过了。
        app.MapGet("/api/dm/thread/{peerMemberId:long}", (long peerMemberId, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (db.GetMemberById(peerMemberId) is not { Status: MemberStatus.Active } peer)
            {
                return Results.Json(new { message = "对方不在站" }, statusCode: StatusCodes.Status404NotFound);
            }

            db.MarkDmRead(me.Id, peerMemberId);
            var messages = db.ListDmConversation(me.Id, peerMemberId).Select(m => new
            {
                id = m.Id,
                fromMemberId = m.FromMemberId,
                toMemberId = m.ToMemberId,
                body = m.Body,
                createdAtUtc = m.CreatedAtUtc,
                readAtUtc = m.ReadAtUtc,
                msgType = m.MsgType,
                dyn = ParseShareCard(m.DynJson),
            });
            return Results.Ok(new
            {
                messages,
                peer = new
                {
                    memberId = peer.Id,
                    mid = peer.Mid,
                    username = peer.Username,
                    nickname = peer.Nickname,
                    displayName = StationDb.DisplayOf(peer),
                    faceUrl = peer.FaceUrl,
                },
            });
        });

        app.MapPost("/api/dm", (DmSendRequest request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (error, target, body) = ValidateSend(db, me.Id, request.ToMemberId, request.Body);
            if (error is not null)
            {
                return Results.Json(new { message = error.Value.Message }, statusCode: error.Value.Status);
            }

            var id = db.SendDm(me.Id, target, body);
            return Results.Ok(new { id });
        });

        // 放映厅安利：动态分享的视频形态——bvid 必查在馆，定格卡面随信落库（dyn_json 视频族），
        // 对方会话里点卡直落 /watch/{bvid}。校验全收进 ValidateVideoShare（与动态分享同规）。
        app.MapPost("/api/videos/{bvid}/share", (string bvid, VideoShareRequest? request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "登录后才能给朋友安利" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (error, target, note, dynJson) = ValidateVideoShare(db, me.Id, request?.ToMemberId, bvid, request?.Note);
            if (error is not null)
            {
                return Results.Json(new { message = error.Value.Message }, statusCode: error.Value.Status);
            }

            var id = db.SendDm(me.Id, target, note, "video", bvid, dynJson!);
            return Results.Ok(new { id, message = "已把这个片子塞进对方信箱" });
        });
    }

    /// <summary>分享卡的定格快照还原：坏 JSON 当普通信处理（body 还在，不拦阅读）。</summary>
    private static JsonElement? ParseShareCard(string? dynJson)
    {
        if (string.IsNullOrWhiteSpace(dynJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(dynJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void MapAdminDmEndpoints(this WebApplication app)
    {
        // 管理员视野：全部私信，或锁一对席位往来（peerA+peerB 同给才锁，两边都是席位 id）。
        app.MapGet("/api/admin/dm", (long? peerA, long? peerB, StationDb db) =>
        {
            var rows = db.ListDmForAdmin(peerA, peerB).Select(r => new
            {
                id = r.Id,
                fromMemberId = r.FromMemberId,
                fromName = r.FromName,
                toMemberId = r.ToMemberId,
                toName = r.ToName,
                body = r.Body,
                createdAtUtc = r.CreatedAtUtc,
                readAtUtc = r.ReadAtUtc,
            });
            return Results.Ok(new { messages = rows });
        });
    }
}
