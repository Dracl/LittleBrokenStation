using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站内合集：逛站的看公开两口（/api/collections 清单 + /api/collections/{id} 详情），与首页同级不上门；
/// 开集/销集/加减片子挂在 /api/member/collections 前缀下由端点自防——这个前缀不在两张门
/// （AdminGate / MemberGate）的视野里，与 /api/member/history 那三条同一防法：进门先查 MemberGate.Current，
/// 没席位一律 401 中文。
/// 归属口径：集是谁开的谁管，站长（role=owner）兜底代管；非本人非站长一律 403。
/// 拒绕全显式 Results.Json(new { ... }, statusCode:)，严禁 Results.Forbid()（分站没注册认证服务，它会 500）。
/// </summary>
public static class CollectionsEndpoints
{
    internal sealed record CreateCollectionRequest(string? Name, string? Intro);

    internal sealed record CollectionItemRequest(string? Bvid);

    /// <summary>
    /// 动手前的三道核验（端点与测试同一张嘴）：没席位 401、集查无 404、非本人非站长 403；放行回 null。
    /// </summary>
    internal static (int Status, string Message)? CheckOwnership(StationDb db, StationMember? me, long id)
    {
        if (me is null)
        {
            return (StatusCodes.Status401Unauthorized, "先登录，这是要动别人家集的事");
        }

        var owner = db.GetCollectionOwner(id);
        if (owner is null)
        {
            return (StatusCodes.Status404NotFound, "没有这个合集");
        }

        if (owner != me.Id && !MemberGate.IsOwner(me))
        {
            return (StatusCodes.Status403Forbidden, "这集不是你攒的，动不着");
        }

        return null;
    }

    /// <summary>集名校验（端点与测试同一张嘴）：两侧空白掰掉后非空、不超 30 字；违规回文案。</summary>
    internal static bool TryNormalizeName(string? raw, out string name, out string message)
    {
        name = (raw ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            message = "总得给集起个名";
            return false;
        }

        if (name.Length > StationDb.MaxCollectionNameLength)
        {
            message = $"集名 {StationDb.MaxCollectionNameLength} 字封顶，这不是写简介的地方";
            return false;
        }

        message = "集名可用";
        return true;
    }

    private static object Card(StationDb.CollectionItem v) => new
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
    };

    private static object Brief(StationDb.CollectionBrief b) => new
    {
        id = b.Id,
        name = b.Name,
        intro = b.Intro,
        ownerMemberId = b.OwnerMemberId,
        ownerName = b.OwnerName,
        createdAtUtc = b.CreatedAtUtc,
        itemCount = b.ItemCount,
        cover = b.Cover,
    };

    public static void MapCollectionsEndpoints(this WebApplication app)
    {
        // 公开清单：全站集子摆在逛站页，只报简报八件套（计数与封面只认已到站的 item）。
        app.MapGet("/api/collections", (StationDb db) =>
            Results.Ok(new { collections = db.ListCollections().Select(Brief) }));

        // 详情：查无 404（这一口是直链进来的，不像分区页那样容空手）。片单只含已到站的，最新入集的排前头。
        app.MapGet("/api/collections/{id:long}", (long id, StationDb db) =>
        {
            var detail = db.GetCollection(id);
            if (detail is null)
            {
                return Results.Json(new { message = "没有这个合集" }, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(new
            {
                id = detail.Id,
                name = detail.Name,
                intro = detail.Intro,
                ownerMemberId = detail.OwnerMemberId,
                ownerName = detail.OwnerName,
                createdAtUtc = detail.CreatedAtUtc,
                itemCount = detail.ItemCount,
                cover = detail.Cover,
                items = detail.Items.Select(Card),
            });
        });

        // 我的集：带 contains 给前端小勾（这部在不在集里）。bvid 没给就不判，一次拉全集合比对，不给每条集单开一趟。
        app.MapGet("/api/member/collections", (string? bvid, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录，无从谈起你的集" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var wanted = string.IsNullOrWhiteSpace(bvid) ? (long?)null : db.GetVideoIdByBvid(bvid.Trim());
            var collections = db.ListCollections(me.Id).Select(b => new
            {
                id = b.Id,
                name = b.Name,
                intro = b.Intro,
                ownerMemberId = b.OwnerMemberId,
                ownerName = b.OwnerName,
                createdAtUtc = b.CreatedAtUtc,
                itemCount = b.ItemCount,
                cover = b.Cover,
                contains = wanted is long videoId && db.GetCollectionVideoIds(b.Id).Contains(videoId),
            });
            return Results.Ok(new { collections });
        });

        app.MapPost("/api/member/collections", (CreateCollectionRequest? request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录，这集归在谁名下" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!TryNormalizeName(request?.Name, out var name, out var message))
            {
                return Results.Json(new { message }, statusCode: StatusCodes.Status400BadRequest);
            }

            var id = db.CreateCollection(name, request?.Intro, me.Id);
            return Results.Json(new { id, name, intro = string.IsNullOrWhiteSpace(request?.Intro) ? null : request.Intro.Trim() },
                statusCode: StatusCodes.Status201Created);
        });

        // 销集：集行与它的 item 行一起走（一个事务），片本身不动——它是馆藏，不归集管。
        app.MapDelete("/api/member/collections/{id:long}", (long id, StationDb db, HttpContext http) =>
        {
            if (CheckOwnership(db, MemberGate.Current(http), id) is { } denied)
            {
                return Results.Json(new { message = denied.Message }, statusCode: denied.Status);
            }

            return db.DeleteCollection(id)
                ? Results.Ok(new { message = "这个集已销，里面的片子照旧在馆藏" })
                : Results.Json(new { message = "没有这个合集" }, statusCode: StatusCodes.Status404NotFound);
        });

        // 往集里放片：先验归属再验「这部在不在架」。撞主键幂等——同片放两次也只占一坑，回话一样是「已入集」。
        app.MapPost("/api/member/collections/{id:long}/items", (long id, CollectionItemRequest? request, StationDb db, HttpContext http) =>
        {
            if (CheckOwnership(db, MemberGate.Current(http), id) is { } denied)
            {
                return Results.Json(new { message = denied.Message }, statusCode: denied.Status);
            }

            var bvid = request?.Bvid?.Trim();
            if (string.IsNullOrEmpty(bvid))
            {
                return Results.Json(new { message = "得说清往集里放哪一部" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (db.GetVideoIdByBvid(bvid) is not long videoId)
            {
                return Results.Json(new { message = $"馆藏里没有「{bvid}」，先把这部投进来" }, statusCode: StatusCodes.Status404NotFound);
            }

            return db.AddItem(id, videoId)
                ? Results.Ok(new { message = "已入集" })
                : Results.Json(new { message = "没有这个合集" }, statusCode: StatusCodes.Status404NotFound);
        });

        // 挪出集：只删这一对 (集, 片)，片照旧在馆藏里待着。
        app.MapDelete("/api/member/collections/{id:long}/items/{bvid}", (long id, string bvid, StationDb db, HttpContext http) =>
        {
            if (CheckOwnership(db, MemberGate.Current(http), id) is { } denied)
            {
                return Results.Json(new { message = denied.Message }, statusCode: denied.Status);
            }

            if (db.GetVideoIdByBvid(bvid.Trim()) is not long videoId)
            {
                return Results.Json(new { message = $"馆藏里没有「{bvid}」，集里也不会有它" }, statusCode: StatusCodes.Status404NotFound);
            }

            return db.RemoveItem(id, videoId)
                ? Results.Ok(new { message = "这部已从集里挪走" })
                : Results.Ok(new { message = "集里本来就没这部" });
        });
    }
}
