using System.Text.Json;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 动态橱窗端点（M6）：GET 侧是公开橱窗——动态缓存本来就是 B 站公开内容，页面与端点都不设门；
/// 站长侧登记/撤除盯梢名单与手动催轮挂 /api/admin/ 前缀（门自动把关）。
/// 催轮只点把火：Task.Run 后台跑，busy 时如实相告，绝不排队长队。
/// </summary>
public static class DynamicEndpoints
{
    private sealed record TargetRequest(string? Mid);

    private sealed record RefreshRequest(long? Mid);

    public static void MapDynamicEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dynamics", (long? mid, int? limit, long? beforeId, StationDb db) =>
        {
            // 多取一条探底：hasMore 就是「这次是不是取满了」，前端「看更多」按钮只看它。
            var rows = db.ListDynamics(mid, (limit ?? 20) + 1, beforeId);
            var hasMore = rows.Count > (limit ?? 20);
            var items = rows.Take(limit ?? 20).Select(r => new
            {
                id = r.Id,
                dynamicId = r.DynamicId,
                targetMid = r.TargetMid,
                authorMid = r.AuthorMid,
                authorName = r.AuthorName,
                type = r.Type,
                text = r.Text,
                pics = ParsePics(r.PicsJson),
                bvid = r.Bvid,
                publishedAtUtc = r.PublishedAtUtc,
                inStation = r.InStation,
            });
            return Results.Ok(new { items, hasMore });
        });

        app.MapGet("/api/dynamics/targets", (StationDb db) =>
            Results.Ok(db.ListDynamicTargets().Select(t => new
            {
                mid = t.Mid,
                nickname = t.Nickname,
                faceUrl = t.FaceUrl,
                enabled = t.Enabled,
                count = t.Count,
                lastFetchAtUtc = t.LastFetchAtUtc,
            })));
    }

    public static void MapAdminDynamicEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/dynamics/targets", (TargetRequest request, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            // MID 认三写法直接借订阅爬工的 ParseMid（同程序层，那边就是为复用而 public 的）。
            var mid = SubscriptionCrawler.ParseMid(request.Mid ?? string.Empty);
            if (mid is null)
            {
                return Results.Json(new { message = "认不出这个 UP，填纯数字 MID 或 space.bilibili.com/… 空间链接" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var added = db.AddDynamicTarget(mid.Value);
            events.RecordWithPayload(EventActors.Admin, MemberGate.Current(http)?.Mid, EventNames.AdminAction,
                "dynamics", mid.Value.ToString(), new { op = "addTarget", added },
                ip: http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { added, message = added ? $"盯上 UP {mid.Value}，下轮巡视见" : "这位早就在盯了" });
        });

        app.MapDelete("/api/admin/dynamics/targets/{mid:long}", (long mid, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            var removed = db.RemoveDynamicTarget(mid);
            events.RecordWithPayload(EventActors.Admin, MemberGate.Current(http)?.Mid, EventNames.AdminAction,
                "dynamics", mid.ToString(), new { op = "removeTarget", removed },
                ip: http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { removed, message = removed > 0 ? "撤了这尊佛，已抓的动态留在橱窗" : "名单里没这号人" });
        });

        app.MapPost("/api/admin/dynamics/refresh", (RefreshRequest? request, DynamicFetchWorker worker) =>
            Results.Ok(worker.TryStartOnce(request?.Mid)
                ? new { started = true }
                : new { busy = true }));
    }

    private static IReadOnlyList<string> ParsePics(string? picsJson)
    {
        if (string.IsNullOrWhiteSpace(picsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(picsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
