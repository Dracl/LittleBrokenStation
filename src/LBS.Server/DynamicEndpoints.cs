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

    /// <summary>配图对象（与 worker 存的 pics_json 同形）：前端拿 url 画图、拿宽高判「长图」角标。</summary>
    private sealed record PicOut(string? Url, int? Width, int? Height);

    /// <summary>worker 存的是 camelCase，这里宽容反序化，两种写法都认。</summary>
    private static readonly JsonSerializerOptions LooseJson = new() { PropertyNameCaseInsensitive = true };

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
                // 本地资产与远程同序对齐（单图失败为 null 元素），优先级归前端：本地优先、热链兑底。
                picsLocal = ParseNullableStrings(r.PicsLocalJson),
                bvid = r.Bvid,
                publishedAtUtc = r.PublishedAtUtc,
                inStation = r.InStation,
                authorFaceUrl = r.AuthorFaceUrl,
                // 卡面取材：馆藏有货优先，没入站的吃抓取定格的 B 站档案；封面前本地只在档案热链上桌时才给（馆藏图自有路径）。
                title = r.StationTitle ?? r.ArchiveTitle,
                cover = r.StationCover ?? r.ArchiveCover,
                coverLocal = r.StationCover is null ? r.CoverLocal : null,
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
                // 正文 [表情] 定格映射（转义符→/emotes/…）：前端 richHtml 同一根管线把 [xx] 换图。
                bodyEmotes = ParseEmoteMap(r.BodyEmotesJson),
            });
            return Results.Ok(new { items, hasMore });
        });

        // 评论树：一级楼按抓取序（热评序），子回复挂在楼下；公开只读，与橱窗同权。
        app.MapGet("/api/dynamics/{dynamicId}/comments", (string dynamicId, StationDb db) =>
        {
            var rows = db.ListDynamicComments(dynamicId);
            // IP 属地有则带、没有给 null（老评论留档没这料，前端不占位）。
            var items = rows.Where(r => r.RootRpid == 0).Select(top => new
            {
                rpid = top.Rpid,
                name = top.Name,
                face = top.Face,
                faceLocal = top.FaceLocal,
                message = top.Message,
                like = top.LikeCount,
                ctimeUtc = top.CtimeUtc,
                level = top.Level,
                ipLocation = top.IpLocation,
                pictures = MergePictures(top.PicturesJson, top.PicturesLocalJson),
                replies = rows.Where(r => r.RootRpid == top.Rpid).Select(r => new
                {
                    rpid = r.Rpid,
                    name = r.Name,
                    face = r.Face,
                    faceLocal = r.FaceLocal,
                    message = r.Message,
                    like = r.LikeCount,
                    ctimeUtc = r.CtimeUtc,
                    level = r.Level,
                    ipLocation = r.IpLocation,
                    pictures = MergePictures(r.PicturesJson, r.PicturesLocalJson),
                }).ToList(),
            }).ToList();
            // 表情定格原样透传（转义符→/emotes/xx.png）；前端 rich() 同一根管线把 [xx] 换图。
            object? emotes = ParseEmoteMap(db.GetDynamicEmotesJson(dynamicId));
            return Results.Ok(new
            {
                // 总数优先拿巡捕盖章的 page.count（本地只存热评前 20，数条数会低估）。
                total = db.GetStatComment(dynamicId) ?? rows.Count,
                fetched = db.GetCommentsFetchedAt(dynamicId) is not null,
                // 留档盖章时刻（UTC 串）：抽屉顶行给分站朋友看「这批评论是哪轮采的」。
                fetchedAtUtc = db.GetCommentsFetchedAt(dynamicId),
                emotes,
                items,
            });
        });

        // 单条动态的评论手动补采（分站成员）：前端抽屉刷新钮走这里，不等巡捕下轮。
        // 增量写走 worker 的同一套 UpsertComments（rpid 主键只更不删，新楼插旧楼留），节流与风控熔断也在闸内。
        app.MapPost("/api/dynamics/{dynamicId}/comments/refresh", async (string dynamicId, DynamicFetchWorker worker, HttpContext http) =>
        {
            if (MemberGate.Current(http) is null)
            {
                return Results.Json(new { message = "登录后才能催评论补采" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (ok, busy, reason, total) = await worker.RefreshCommentsOnceAsync(dynamicId, http.RequestAborted);
            return ok
                ? Results.Ok(new { refreshed = true, total })
                : busy
                    ? Results.Json(new { message = "这条动态的评论正在补采，稍候再点" }, statusCode: StatusCodes.Status409Conflict)
                    : reason is "not_found"
                        ? Results.Json(new { message = "分站还没收过这条动态" }, statusCode: StatusCodes.Status404NotFound)
                        : Results.Json(new { message = reason ?? "补采没成，等巡捕下轮再试" }, statusCode: StatusCodes.Status502BadGateway);
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
                // 图集随身定格（本地优先、热链兑底）：图文动态递到私信里不只剩一张封面。
                pics = ParseSharePics(snap.PicsJson, snap.PicsLocalJson),
                // 视频题与时长：报头标题行的料（老消息没这两字段，前端静默不加行）。
                videoTitle = snap.VideoTitle,
                videoDuration = snap.VideoDuration is { } dur ? DurationText.Normalize(dur) : null,
            });
            var id = db.SendDm(me.Id, target, note, "dynamic", snap.DynamicId, dynJson);
            return Results.Ok(new { id, message = $"已把这条动态塞进对方信箱" });
        });

        // 公开橱窗只发「在看谁」的门面：启用中目标的 mid/昵称/头像/条数（动态页筛选用）；
        // enabled 与巡捕时刻是站长情报，走 /api/admin/dynamics/targets，与 stats/collect 同一口径。
        app.MapGet("/api/dynamics/targets", (StationDb db) =>
            Results.Ok(db.ListDynamicTargets().Where(t => t.Enabled).Select(t => new
            {
                mid = t.Mid,
                nickname = t.Nickname,
                faceUrl = t.FaceUrl,
                count = t.Count,
            })));
    }

    public static void MapAdminDynamicEndpoints(this WebApplication app)
    {
        // 站长室订阅战报：全量目标含停用与巡捕时刻（/api/admin/ 前缀吃站长门，身份即钥匙）。
        app.MapGet("/api/admin/dynamics/targets", (StationDb db) =>
            Results.Ok(db.ListDynamicTargets().Select(t => new
            {
                mid = t.Mid,
                nickname = t.Nickname,
                faceUrl = t.FaceUrl,
                enabled = t.Enabled,
                count = t.Count,
                lastFetchAtUtc = t.LastFetchAtUtc,
            })));

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

    private static IReadOnlyList<PicOut> ParsePics(string? picsJson)
    {
        if (string.IsNullOrWhiteSpace(picsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PicOut>>(picsJson, LooseJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>分享定格的图集：远程链配同序本地链（本地可空），坏 JSON 当没图——私信卡宁缺毋空。</summary>
    private static IReadOnlyList<object> ParseSharePics(string? picsJson, string? picsLocalJson)
    {
        var pics = ParsePics(picsJson);
        if (pics.Count == 0)
        {
            return [];
        }

        var locals = ParseNullableStrings(picsLocalJson);
        return pics.Select((p, i) => (object)new
        {
            url = p.Url,
            local = locals is not null && i < locals.Count ? locals[i] : null,
        }).ToList();
    }

    /// <summary>同序本地路径数组（允许 null 元素）；坏 JSON 当没存，前端全量热链兑底。</summary>
    private static IReadOnlyList<string?>? ParseNullableStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string?>>(json, LooseJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>转义符→图路 映射（坏 JSON 当没存，前端顶多裸显转义符，不拦端点）。</summary>
    private static Dictionary<string, string>? ParseEmoteMap(string? emotesJson)
    {
        if (string.IsNullOrWhiteSpace(emotesJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(emotesJson, LooseJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>评论配图本地优先合并：逐张有本地用本地，否则回远程 url；没图给 null。</summary>
    private static IReadOnlyList<string>? MergePictures(string? picturesJson, string? picturesLocalJson)
    {
        var remote = ParseNullableStrings(picturesJson);
        if (remote is not { Count: > 0 })
        {
            return null;
        }

        var locals = ParseNullableStrings(picturesLocalJson);
        return remote
            .Select((url, i) => locals is not null && i < locals.Count && !string.IsNullOrEmpty(locals[i]) ? locals[i] : url)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .ToList();
    }
}
