using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站内分区（逛站的门脸）：顶级分区清单 + 分区卡阵两口只读，与首页同级公开不上会员门；
/// 站长改派一口挂在 /api/admin/ 前缀下，自动吃站长门（身份即钥匙）。
/// 拒绕全显式 Results.Json(statusCode:)，严禁 Results.Forbid()（分站没注册认证服务，它会 500）。
/// </summary>
public static class ZonesEndpoints
{
    internal sealed record ZoneSetRequest(long? VideoId, string? Zone);

    public static void MapZonesEndpoints(this WebApplication app)
    {
        // hot = 分区总览入口卡的底图一摄（按次数的冠军分区 + 区内最火那条的封面）。
        // 只是封面，与分区清单同口公开不上会员门；首页拉分区时顺带走，不为它另开一口。
        app.MapGet("/api/zones", (StationDb db, HttpContext http) =>
        {
            // 三处共用同一把看客尺子：别人藏着的货既不占分区那个数字，也不当入口卡底图。
            var scope = ViewerGate.Scope(http);
            var hot = db.GetZoneEntryCover(DateTimeOffset.UtcNow, scope);
            return Results.Ok(new
            {
                zones = db.ListZones(scope).Select(z => new { name = z.Name, count = z.Count }),
                hot = new { zone = hot.Zone, bvid = hot.Bvid, cover = hot.Cover },
            });
        });

        // name 空/查无回空 items：分区页换瓷砖不该挨 404。
        app.MapGet("/api/zones/videos", (string? name, StationDb db, HttpContext http) =>
            Results.Ok(new
            {
                items = db.ListZoneVideos(name, ViewerGate.Scope(http)).Select(v => new
                {
                    id = v.Id,
                    bvid = v.Bvid,
                    title = v.Title,
                    coverUrl = v.CoverUrl,
                    localCover = v.LocalCover,
                    upName = v.UpName,
                    publishedAtUtc = v.PublishedAtUtc,
                    publishText = v.PublishText,
                    zone = v.Zone,
                }),
            }));

        app.MapPost("/api/admin/zones/set", (ZoneSetRequest? request, StationDb db) =>
        {
            if (request?.VideoId is not long videoId)
            {
                return Results.Json(new { message = "没带来稿件 id，这派没法改" }, statusCode: StatusCodes.Status400BadRequest);
            }

            // 校验与规范化全走 StationDb.TryNormalizeZone（同一张嘴，测试直捣）。
            if (!StationDb.TryNormalizeZone(request.Zone, out var zone, out var message))
            {
                return Results.Json(new { message }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!db.SetSiteZone(videoId, zone))
            {
                return Results.Json(new { message = "馆藏里没有这个 id，改派给谁看" }, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(new { message });
        });
    }
}
