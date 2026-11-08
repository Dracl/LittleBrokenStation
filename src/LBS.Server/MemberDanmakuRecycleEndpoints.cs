using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 个人空间的「我收回的弹幕」（11-08 站长拍板）：发送者自己收回的那几条，自己就能恢复，不必求站长。
/// </summary>
/// <remarks>
/// 只开 <c>source='self' AND member_id=我</c> 那一小片：站长替人删的（source='admin'/'report'）与别人删的
/// 一律看不见也捐不动——「谁的手封进去的，谁有资格原样打开」，而替人删的那条要不要请回来是站长的判断。
/// 与站长室那张 <see cref="AdminDanmakuRecycleEndpoints"/> 共用库层判据（<c>RestoreDanmakuFromRecycle</c> 的四道闸），
/// 这里只在动手前多核一道归属。路径不带 body，免得吃上「必须 application/json」那道路由门槛（claim 那口踩过的坑）。
/// </remarks>
public static class MemberDanmakuRecycleEndpoints
{
    public static void MapMemberDanmakuRecycleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/member/danmaku-recycle", (HttpContext http, StationDb db) =>
        {
            var me = MemberGate.Current(http);
            if (me is null)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var items = db.ListDanmakuRecycle(200, selfMemberId: me.Id).Select(item => new
            {
                id = item.Id,
                dmId = item.DmId,
                videoId = item.VideoId,
                bvid = item.Bvid,
                pageIdx = item.PageIdx,
                tSec = item.TSec,
                text = item.Text,
                senderName = item.SenderName,
                deletedAtUtc = item.DeletedAtUtc,
                // 片子已经不在架时捐不动，前端据此把钮摆灰并说明原因（与站长室那张同一口径）。
                inStation = item.VideoInStation,
            }).ToList();

            return Results.Ok(new { total = items.Count, items });
        });

        app.MapPost("/api/member/danmaku-recycle/{id:long}/restore",
            (long id, HttpContext http, StationDb db, EventRecorder events) =>
            {
                var me = MemberGate.Current(http);
                if (me is null)
                {
                    return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
                }

                // 归属先于动作：查无此行与不是你自己删的分开回，免得拿一串 id 试探别人删过什么。
                var owner = db.GetDanmakuRecycleOwner(id);
                if (owner is null)
                {
                    return Results.NotFound(new { message = "回收站里没有这一条" });
                }

                if (owner.Value.Source != "self" || owner.Value.MemberId != me.Id)
                {
                    return Results.Json(new { message = "这条不是你自己在放映厅里收回的，恢复得站长动手" },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var (ok, message) = db.RestoreDanmakuFromRecycle(id);
                events.RecordWithPayload(EventActors.Member, me.Mid, EventNames.DanmakuRecycleRestore,
                    "danmaku", owner.Value.DmId.ToString(), new { recycleId = id, ok, source = "self" },
                    ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: me.Id);
                return ok
                    ? Results.Ok(new { message })
                    : Results.Json(new { message }, statusCode: StatusCodes.Status409Conflict);
            });
    }
}
