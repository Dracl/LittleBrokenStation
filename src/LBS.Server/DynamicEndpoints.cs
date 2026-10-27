using System.Text.Json;
using LBS.Domain;
using LBS.Engine;
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

    private sealed record ShareRequest(long? ToMemberId, string? Note);

    public static void MapDynamicEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dynamics", (long? mid, int? limit, long? beforeId, string? beforeTime, StationDb db) =>
        {
            // 多取一条探底：hasMore 就是「这次是不是取满了」，前端「看更多」按钮只看它。
            var rows = db.ListDynamics(mid, (limit ?? 20) + 1, beforeId, beforeTime);
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
                authorFaceUrl = r.AuthorFaceUrl,
                // 卡面取材：馆藏有货优先，没入站的吃抓取定格的 B 站档案。
                title = r.StationTitle ?? r.ArchiveTitle,
                cover = r.StationCover ?? r.ArchiveCover,
                duration = DurationText.Normalize(r.StationDuration ?? r.ArchiveDuration),
                viewCount = r.StatView,
                // 底栏三计数与转发灰块的原作者名片（都随抓取定格）。
                statComment = r.StatComment,
                statForward = r.StatForward,
                statLike = r.StatLike,
                origAuthorMid = r.OrigAuthorMid,
                origAuthorName = r.OrigAuthorName,
                origAuthorFace = r.OrigAuthorFace,
                commentsFetched = r.CommentsFetchedAtUtc is not null,
            });
            return Results.Ok(new { items, hasMore });
        });

        // 评论树：一级楼按抓取序（热评序），子回复挂在楼下；公开只读，与橱窗同权。
        app.MapGet("/api/dynamics/{dynamicId}/comments", (string dynamicId, StationDb db) =>
        {
            var rows = db.ListDynamicComments(dynamicId);
            var items = rows.Where(r => r.RootRpid == 0).Select(top => new
            {
                rpid = top.Rpid,
                name = top.Name,
                face = top.Face,
                message = top.Message,
                like = top.LikeCount,
                ctimeUtc = top.CtimeUtc,
                replies = rows.Where(r => r.RootRpid == top.Rpid).Select(r => new
                {
                    rpid = r.Rpid,
                    name = r.Name,
                    face = r.Face,
                    message = r.Message,
                    like = r.LikeCount,
                    ctimeUtc = r.CtimeUtc,
                }).ToList(),
            }).ToList();
            return Results.Ok(new
            {
                // 总数优先拿巡捕盖章的 page.count（本地只存热评前 20，数条数会低估）。
                total = db.GetStatComment(dynamicId) ?? rows.Count,
                fetched = db.GetCommentsFetchedAt(dynamicId) is not null,
                items,
            });
        });

        // 把动态安利给朋友：定格快照随私信落库，对方会话里直接看到卡，点卡回橱窗定位。
        app.MapPost("/api/dynamics/{dynamicId}/share", (string dynamicId, ShareRequest? request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "登录后才能给朋友安利" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (request?.ToMemberId is not long target)
            {
                return Results.Json(new { message = "得先有个收信人" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (target == me.Id)
            {
                return Results.Json(new { message = "给自己安利动态，那叫自娱自乐" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (db.GetMemberById(me.Id) is not { Status: MemberStatus.Active })
            {
                return Results.Json(new { message = "你的席位不在站上，分享闸落着" }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (db.GetMemberById(target) is not { Status: MemberStatus.Active })
            {
                return Results.Json(new { message = "对方不在站" }, statusCode: StatusCodes.Status404NotFound);
            }

            var note = request.Note?.Trim() ?? string.Empty;
            if (note.Length > 500)
            {
                return Results.Json(new { message = "安利语太长了，500 字以内刚好" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (db.GetDynamicSnapshot(dynamicId) is not { } snap)
            {
                return Results.Json(new { message = "分站橱窗里没有这条动态，安利不了空气" }, statusCode: StatusCodes.Status404NotFound);
            }

            var dynJson = JsonSerializer.Serialize(new
            {
                dynamicId = snap.DynamicId,
                type = snap.Type,
                authorMid = snap.AuthorMid,
                authorName = snap.AuthorName,
                authorFace = snap.AuthorFace,
                text = snap.Text,
                bvid = snap.Bvid,
                publishedAtUtc = snap.PublishedAtUtc,
                cover = snap.Cover,
            });
            var id = db.SendDm(me.Id, target, note, "dynamic", snap.DynamicId, dynJson);
            return Results.Ok(new { id, message = $"已把这条动态塞进对方信箱" });
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
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: MemberGate.Current(http)?.Id);
            return Results.Ok(new { added, message = added ? $"盯上 UP {mid.Value}，下轮巡视见" : "这位早就在盯了" });
        });

        app.MapDelete("/api/admin/dynamics/targets/{mid:long}", (long mid, StationDb db, EventRecorder events,
            HttpContext http) =>
        {
            var removed = db.RemoveDynamicTarget(mid);
            events.RecordWithPayload(EventActors.Admin, MemberGate.Current(http)?.Mid, EventNames.AdminAction,
                "dynamics", mid.ToString(), new { op = "removeTarget", removed },
                ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: MemberGate.Current(http)?.Id);
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
