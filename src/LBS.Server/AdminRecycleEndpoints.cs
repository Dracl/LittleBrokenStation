using LBS.Domain;
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

        // 一键清理「已取消」遗留：卡阵里的 Canceled 行整批抖进回收站——和手动删档同一条船，站长可从回收站反悔。
        // 带引擎台账的行（极少数 Failed→重排→再取消的路径残留）先跳过不碰：清理不该动引擎的账，宁可留下不误搬。
        app.MapPost("/api/admin/canceled/purge", (StationDb db, DataPaths paths, HttpContext http) =>
        {
            var items = db.GetCanceledQueueItems();
            if (items.Count == 0)
            {
                return Results.Ok(new { purged = 0, skipped = 0, message = "没有已取消的馆藏需要清理" });
            }

            var deleter = MemberGate.Current(http);
            var who = deleter?.Nickname ?? deleter?.Username ?? "站长";
            var purged = 0;
            var skipped = 0;
            foreach (var item in items)
            {
                var row = db.GetVideoForDelete(item.Id);
                if (row is null || row.Value.Status != VideoStatus.Canceled || !string.IsNullOrWhiteSpace(row.Value.TaskIdsJson))
                {
                    skipped++;
                    continue;
                }

                var commentCount = 0;
                if (db.GetArtifactRel(item.Id, "comments") is { } commentsRel)
                {
                    var snapshotPath = Path.Combine(paths.Videos, commentsRel.Replace('/', Path.DirectorySeparatorChar));
                    if (LBS.Engine.CommentSnapshotOps.Load(snapshotPath) is { } snapshot)
                    {
                        commentCount = snapshot.Replies.Count + (snapshot.Top is null ? 0 : 1);
                    }
                }

                var recycled = db.MoveVideoToRecycle(item.Id, paths.Videos, paths.Recycle, who, commentCount);
                if (recycled.Ok)
                {
                    purged++;
                }
                else
                {
                    skipped++;
                }
            }

            var tail = skipped > 0 ? $"，{skipped} 件跳过（引擎账未清或文件占用）" : string.Empty;
            return Results.Ok(new
            {
                purged,
                skipped,
                message = purged > 0 ? $"清理了 {purged} 件已取消馆藏（存进了回收站）{tail}" : $"{skipped} 件都没清动（引擎账未清或文件占用）",
            });
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
