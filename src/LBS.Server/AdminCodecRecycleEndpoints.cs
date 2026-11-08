using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站长室的加档回收站（11-08 站长拍板「删掉的加档版本也进回收站，方便站长回滚」）：
/// 一版加档（一个编码，可能横跨多个分 P）删掉后连行带件封在这里，可整组捐回、可整组销毁。
/// </summary>
/// <remarks>
/// 整段挂在 <c>/api/admin/</c> 下吃 AdminGate 现成的站长门（普通成员与访客一律 401/403），与馆藏回收站、
/// 弹幕回收站同一个哲学：<b>删除动作投稿人握得住，反悔的话语权只握在站长手里</b>。
/// 所以这里三枚口都不核归属——捐回会把一件别人可能不想要的版本请回货架，销毁会在站长看见之前把证据链抹平，
/// 两件都不该交给投稿人。判据本体在库层（<see cref="StationDb"/> 的 CodecRecycle 那一段），这里只把结果翻成 HTTP。
/// </remarks>
public static class AdminCodecRecycleEndpoints
{
    public static void MapAdminCodecRecycleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/codec-recycle", (int? limit, long? videoId, StationDb db) => Results.Ok(new
        {
            total = db.CountCodecRecycle(),
            items = db.ListCodecRecycle(videoId, Math.Clamp(limit ?? 300, 1, 2000)).Select(item => new
            {
                id = item.Id,
                // 同一次删除共用一组：点单是整片点的，捐回也按组一次搬齐，不让站长按 N 次钮。
                groupId = item.GroupId,
                videoId = item.VideoId,
                bvid = item.Bvid,
                pageIdx = item.PageIdx,
                codec = item.Codec,
                relPath = item.RelPath,
                bytes = item.Bytes,
                // 件到底在不在回收目录里：false = 删的时候盘上本没这一版（只销了账），捐回去也只是补一行账。
                fileStored = item.FileStored,
                deletedAtUtc = item.DeletedAtUtc,
                deletedBy = item.DeletedBy,
                deletedByMemberId = item.DeletedByMemberId,
                // 谁动的手：member = 投稿人自管、admin = 站长。台账里分得清，前端才有脸说「这位删的」。
                source = item.Source,
                // 片子还在不在架：不在架时捐不动，前端据此把这组的捐回钮摆灰并说明原因。
                inStation = item.VideoInStation,
            }),
        }));

        app.MapPost("/api/admin/codec-recycle/{groupId}/restore",
            (string groupId, HttpContext http, StationDb db, DataPaths paths, EventRecorder events) =>
            {
                var me = MemberGate.Current(http);
                var outcome = db.RestoreCodecGroup(groupId, paths.Videos, paths.CodecRecycle);
                events.RecordWithPayload(EventActors.Admin, me?.Mid, EventNames.CodecRecycleRestore,
                    "codec-recycle", groupId,
                    new { ok = outcome.Ok, rows = outcome.Rows, files = outcome.Files, message = outcome.Message },
                    ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: me?.Id);
                return outcome.Ok
                    ? Results.Ok(new { message = outcome.Message, rows = outcome.Rows, files = outcome.Files })
                    : Results.Json(new { message = outcome.Message }, statusCode: StatusCodes.Status409Conflict);
            });

        app.MapDelete("/api/admin/codec-recycle/{groupId}",
            (string groupId, HttpContext http, StationDb db, DataPaths paths, EventRecorder events) =>
            {
                var me = MemberGate.Current(http);
                var purged = db.PurgeCodecGroup(groupId, paths.CodecRecycle);
                // 销毁是站里唯一一次真删文件，先记再回话：万一事后要查「那 3GB 谁清的」，这条台账是唯一的凭据。
                events.RecordWithPayload(EventActors.Admin, me?.Mid, EventNames.CodecRecyclePurge,
                    "codec-recycle", groupId, new { ok = purged },
                    ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: me?.Id);
                return purged
                    ? Results.Ok(new { message = "这一组已从回收站彻底销毁，文件跟着没了" })
                    : Results.NotFound(new { message = "回收站里没有这一组" });
            });
    }
}
