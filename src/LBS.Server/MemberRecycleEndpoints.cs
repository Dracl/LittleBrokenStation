using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 成员回收站弹窗（11-16 站长拍板：入口在头像下拉菜单，个人空间里那块弹幕收回柜同日搬入）。
/// 成员在这里看管自己「删过的东西」：自己删的视频（recycle_bin 里 deleted_by_member_id=本席的行；
/// 弹幕收回柜另在 <see cref="MemberDanmakuRecycleEndpoints"/>，两族并排摆进同一张弹窗）。
/// 站长替人删的一律不递——与弹幕回收站「谁的手封进去的，谁有资格原样打开」同一把尺。
/// 成员那头的「销毁」只是盖隐藏戳（<c>HideRecycleForSelf</c>）：成员视角从此没了这一件，
/// 货与清单原样封存，站长室照旧恢复／销毁——真销毁的话语权仍旧只在站长手里（confirmStore 11-11
/// 「回收站的存在不必向成员曝光」的同族哲学：成员问话与措辞里不招供内部机制）。
/// 三道闸一口色：MemberGate.Current 认人 → GetRecycleOwner 核归属（不是自己删的看不见也动不了）→
/// 动作走库层现成口（RestoreFromRecycle / HideRecycleForSelf），封面先核归属再递 ReadRecycleCover（自带越界防）。
/// 恢复与销毁两路 POST 都不带 body、不手挂 Content-Type（claim 那口踩过的路由门槛，弹幕恢复同款口径）。
/// </summary>
public static class MemberRecycleEndpoints
{
    public static void MapMemberRecycleEndpoints(this WebApplication app)
    {
        // 弹窗视频段的清单：只递「自己亲手删的、还没被自己销毁过的」，最近删的排前面。
        app.MapGet("/api/member/recycle", (HttpContext http, StationDb db) =>
        {
            var me = MemberGate.Current(http);
            if (me is null)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var items = db.ListRecycleForSelf(me.Id).Select(item => new
            {
                id = item.Id,
                videoId = item.VideoId,
                bvid = item.Bvid,
                title = item.Title,
                upName = item.UpName,
                // 封面住在回收站的文件间里，只隔着这位席位自己的门递出去。
                coverUrl = $"/api/member/recycle/{item.Id}/cover",
                deletedAtUtc = item.DeletedAtUtc,
                fileCount = item.FileCount,
                bytes = item.Bytes,
                pageCount = item.PageCount,
            }).ToList();
            return Results.Ok(new { total = items.Count, items });
        });

        // 恢复：自己删的才请得回架。归属三道闸：没这行 → 404；被自己「销毁」过 → 404（成员视角它就是没了）；
        // 是别人删的 → 403（这句只可能出现在硬试 id 的路上，正常清单里根本见不到别人的行）。
        app.MapPost("/api/member/recycle/{id:long}/restore", (long id, HttpContext http,
            StationDb db, DataPaths paths, EventRecorder events) =>
        {
            var me = MemberGate.Current(http);
            if (me is null)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var owner = db.GetRecycleOwner(id);
            if (owner is null || owner.Value.Hidden)
            {
                return Results.NotFound(new { message = "回收站里没有这一件" });
            }

            if (owner.Value.DeletedByMemberId != me.Id)
            {
                return Results.Json(new { message = "这一件不是你亲手删的，恢复得站长动手" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var outcome = db.RestoreFromRecycle(id, paths.Videos, paths.Recycle);
            events.RecordWithPayload(EventActors.Member, me.Mid, EventNames.VideoRecycleSelfRestore,
                "video", owner.Value.Bvid, new { recycleId = id, ok = outcome.Ok },
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: me.Id);
            return outcome.Ok
                ? Results.Ok(new { message = outcome.Message })
                : Results.Conflict(new { message = outcome.Message });
        });

        // 「销毁」：成员按下去就是盖隐藏戳——话按真销毁说，货一根毛不动（台账单记 video.recycle.self.hide，
        // 与站长的真销毁 video.recycle.purge 分家，查账不会把藏着的当没了的）。
        app.MapPost("/api/member/recycle/{id:long}/discard", (long id, HttpContext http,
            StationDb db, EventRecorder events) =>
        {
            var me = MemberGate.Current(http);
            if (me is null)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var owner = db.GetRecycleOwner(id);
            if (owner is null || owner.Value.Hidden)
            {
                return Results.NotFound(new { message = "回收站里没有这一件" });
            }

            if (owner.Value.DeletedByMemberId != me.Id)
            {
                return Results.Json(new { message = "这一件不是你亲手删的，销毁得站长动手" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var hidden = db.HideRecycleForSelf(id, me.Id);
            events.RecordWithPayload(EventActors.Member, me.Mid, EventNames.VideoRecycleSelfHide,
                "video", owner.Value.Bvid, new { recycleId = id, ok = hidden },
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: me.Id);
            return hidden
                ? Results.Ok(new { message = "这一件已彻底销毁" })
                : Results.NotFound(new { message = "回收站里没有这一件" });
        });

        // 封面：归属不过关一律 404（连「有这一件」都不泄露）；ReadRecycleCover 自带路径越界防。
        app.MapGet("/api/member/recycle/{id:long}/cover", (long id, HttpContext http, StationDb db, DataPaths paths) =>
        {
            var me = MemberGate.Current(http);
            if (me is null)
            {
                return Results.NotFound();
            }

            var owner = db.GetRecycleOwner(id);
            if (owner is null || owner.Value.Hidden || owner.Value.DeletedByMemberId != me.Id)
            {
                return Results.NotFound();
            }

            var bytes = db.ReadRecycleCover(id, paths.Recycle);
            return bytes is null
                ? Results.NotFound()
                : Results.File(bytes, "image/jpeg");
        });
    }
}
