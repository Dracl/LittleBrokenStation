using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站长室的弹幕回收站（11-08 站长拍板「单删弹幕进回收站」）：被删掉的站内弹幕在这里点名、捐回、销毁。
/// 整段挂在 /api/admin/ 下吃 AdminGate 现成的站长门（普通成员与访客一律 401/403），
/// 与馆藏回收站（AdminRecycleEndpoints）同一个哲学：删完就是删完了，反悔的话语权只握在站长手里——
/// 发送者自己删的也进这张表（source='self'），但捐回只由站长动手，不开「自己撤回删除」的口。
/// 判据本体在库层（StationDb.DanmakuRecycle），这里只把结果翻成 HTTP。
/// </summary>
public static class AdminDanmakuRecycleEndpoints
{
    private sealed record RecycleIdRequest(long Id);

    public static void MapAdminDanmakuRecycleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/danmaku-recycle", (int? limit, StationDb db) => Results.Ok(new
        {
            total = db.CountDanmakuRecycle(),
            items = db.ListDanmakuRecycle(Math.Clamp(limit ?? 200, 1, 2000)).Select(item => new
            {
                id = item.Id,
                dmId = item.DmId,
                videoId = item.VideoId,
                bvid = item.Bvid,
                pageIdx = item.PageIdx,
                tSec = item.TSec,
                text = item.Text,
                senderDmid = item.SenderDmid,
                senderOwner = item.SenderOwner,
                memberId = item.MemberId,
                senderName = item.SenderName,
                dmCreatedAtUtc = item.DmCreatedAtUtc,
                deletedAtUtc = item.DeletedAtUtc,
                deletedBy = item.DeletedBy,
                // 谁动的手分三种：self = 发送者自己收回、report = 站长按举报单删、admin = 站长直接删。
                source = item.Source,
                // 片子还在不在架：不在架时捐不动，前端据此把这行的捐回钮摆灰并说明原因。
                inStation = item.VideoInStation,
            }),
        }));

        app.MapPost("/api/admin/danmaku-recycle/{id:long}/restore", (long id, StationDb db) =>
        {
            var (ok, message) = db.RestoreDanmakuFromRecycle(id);
            return ok
                ? Results.Ok(new { message })
                : Results.Json(new { message }, statusCode: StatusCodes.Status409Conflict);
        });

        app.MapDelete("/api/admin/danmaku-recycle/{id:long}", (long id, StationDb db) =>
            db.PurgeDanmakuRecycle(id)
                ? Results.Ok(new { message = "这一条已从回收站彻底销毁" })
                : Results.NotFound(new { message = "回收站里没有这一条" }));

        app.MapPost("/api/admin/danmaku-recycle/clear", (StationDb db) =>
        {
            var cleared = db.PurgeAllDanmakuRecycle();
            return Results.Ok(new
            {
                cleared,
                message = cleared > 0 ? $"弹幕回收站已清空，带走 {cleared} 条" : "弹幕回收站本来就是空的",
            });
        });
    }
}
