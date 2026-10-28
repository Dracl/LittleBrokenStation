using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站长室回收站端点：删掉的馆藏在这里点名、恢复、彻底销毁。
/// 整段挂在 /api/admin/ 下，吃 AdminGate 现成的站长门（普通成员与访客一律 401/403）。
/// 投稿人那边删完就是删完——回收站的话语权只握在站长手里。
/// </summary>
public static class AdminRecycleEndpoints
{
    public static void MapAdminRecycleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/recycle", (StationDb db, DataPaths paths) => Results.Ok(
            db.ListRecycle().Select(item => new
            {
                id = item.Id,
                videoId = item.VideoId,
                bvid = item.Bvid,
                title = item.Title,
                upName = item.UpName,
                // 封面文件已经不在 /media 下了，只能隔着站长室的门递出去。
                coverUrl = $"/api/admin/recycle/{item.Id}/cover",
                deletedAtUtc = item.DeletedAtUtc,
                deletedBy = item.DeletedBy,
                fileCount = item.FileCount,
                bytes = item.Bytes,
                pageCount = item.PageCount,
                danmakuCount = item.DanmakuCount,
                commentCount = item.CommentCount,
            })));

        app.MapPost("/api/admin/recycle/{id:long}/restore", (long id, StationDb db, DataPaths paths) =>
        {
            var outcome = db.RestoreFromRecycle(id, paths.Videos, paths.Recycle);
            return outcome.Ok
                ? Results.Ok(new { message = outcome.Message, files = outcome.Files, rows = outcome.Rows })
                : Results.Conflict(new { message = outcome.Message });
        });

        app.MapDelete("/api/admin/recycle/{id:long}", (long id, StationDb db, DataPaths paths) =>
            db.PurgeRecycle(id, paths.Recycle)
                ? Results.Ok(new { message = "这一件已从回收站彻底销毁" })
                : Results.NotFound(new { message = "回收站里没有这一件" }));

        app.MapDelete("/api/admin/recycle", (StationDb db, DataPaths paths) =>
        {
            var cleared = db.PurgeAllRecycle(paths.Recycle);
            return Results.Ok(new { message = cleared > 0 ? $"回收站已清空，带走 {cleared} 件" : "回收站本来就是空的", cleared });
        });

        // 回收站里的封面：清单说哪个是封面就递哪个，路径越界一律不给。
        app.MapGet("/api/admin/recycle/{id:long}/cover", (long id, StationDb db, DataPaths paths) =>
        {
            var bytes = db.ReadRecycleCover(id, paths.Recycle);
            return bytes is null
                ? Results.NotFound(new { message = "这一件没存下封面" })
                : Results.File(bytes, "image/jpeg");
        });
    }
}
