using System.Text.Json.Serialization;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站内合集：逛站的看公开两口（/api/collections 清单 + /api/collections/{id} 详情），与首页同级不上门；
/// 开集/销集/加减片子挂在 /api/member/collections 前缀下由端点自防——这个前缀不在两张门
/// （AdminGate / MemberGate）的视野里，与 /api/member/history 那三条同一防法：进门先查 MemberGate.Current，
/// 没席位一律 401 中文。改抽屉（PATCH /api/collections/{id}）也吃同一套自防（不往两张门的名单里加 /api/collections）。
/// 归属口径：集是谁开的谁管，站长（role=owner）兜底代管；非本人非站长一律 403。
/// 可见性口径：公开集人人可见，私有集只对东家与站长露面（读口一律递 ViewerGate.Scope；藏着的集与查无同一句 404）；
/// 看不着的片子也不许挂进自己的集（不堵这缝，任何人都能把别人的私稿借公开集端回全站眼前）。
/// 拒绕全显式 Results.Json(new { ... }, statusCode:)，严禁 Results.Forbid()（分站没注册认证服务，它会 500）。
/// </summary>
public static class CollectionsEndpoints
{
    internal sealed record CreateCollectionRequest(string? Name, string? Intro);

    /// <summary>抽屉设置（PATCH /api/collections/{id}）：三栏都可选，null = 「这一栏没说、不动」；
    /// 档位拿 <see cref="VisibilityCodec.TryParse"/> 校（认不了 400），集名拿 <see cref="TryNormalizeName"/> 校。</summary>
    internal sealed record CollectionSettingsRequest(string? Name, string? Intro, string? Visibility);

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

    /// <summary>档位校验（端点与测试同一张嘴，拿 TryNormalizeName 同一形制）：
    /// 认 public / private / 空（空 = 公开，与不填同档），认不了回 false + 文案（端点据此回 400）。</summary>
    internal static bool TryNormalizeVisibility(string? raw, out VideoVisibility visibility, out string message)
    {
        if (VisibilityCodec.TryParse(raw, out visibility))
        {
            message = $"档位可用（{VisibilityCodec.ToColumn(visibility)}）";
            return true;
        }

        message = $"档位只认 {VisibilityCodec.Public} / {VisibilityCodec.Private}（不填算公开）";
        return false;
    }

    private static object Card(StationDb.CollectionItem v) => new
    {
        id = v.Id,
        bvid = v.Bvid,
        title = v.Title,
        coverUrl = v.CoverUrl,
        localCover = v.LocalCover,
        upName = v.UpName,
        // 卡阵那三件与悬停预览：少一件首页那张卡就给用户一排破折号，跟着字段名逐字对齐前端
        viewCountText = v.ViewCountText,
        durationText = v.DurationText,
        danmakuCount = v.DanmakuCount,
        previewUrl = v.PreviewUrl,
        publishedAtUtc = v.PublishedAtUtc,
        publishText = v.PublishText,
        zone = v.Zone,
    };

    /// <summary>简报对外的形状（清单与 PATCH 回包共用一张脸，免得两处字段错开）：
    /// visibility 一律小写字面量（逛站页的私有角标与抽屉设置的回显都读这一格）；
    /// Message 只在写口回包里有值，公开清单不带它（WhenWritingNull 直接抹掉，不给逛站页添一排 null）。</summary>
    private sealed record BriefDto(
        long Id,
        string Name,
        string? Intro,
        long OwnerMemberId,
        string? OwnerName,
        string CreatedAtUtc,
        long ItemCount,
        string? Cover,
        string Visibility,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);

    private static BriefDto Brief(StationDb.CollectionBrief b, string? message = null) => new(
        b.Id,
        b.Name,
        b.Intro,
        b.OwnerMemberId,
        b.OwnerName,
        b.CreatedAtUtc,
        b.ItemCount,
        b.Cover,
        // 档位随简报一起出（数据层 BriefSelect 一趟带齐）：逛站页的私有角标与抽屉设置的回显都读这一格。
        VisibilityCodec.ToColumn(b.Visibility),
        message);

    public static void MapCollectionsEndpoints(this WebApplication app)
    {
        // 公开清单：全站集子摆在逛站页，只报简报九件套（计数与封面只认已到站的 item）。
        // 看客尺子：访客 = 公开集；会员 = 公开集 + 自己的私有集（逛全站的抽屉那一页能看见自己的私有集）；站长 = 全部。
        app.MapGet("/api/collections", (StationDb db, HttpContext http) =>
            Results.Ok(new
            {
                collections = db.ListCollections(null, ViewerGate.Scope(http)).Select(b => Brief(b)),
            }));

        // 详情：查无 404（这一口是直链进来的，不像分区页那样容空手）；对这位藏着的私有集也回同一句 404，
        // 不给「集存在但你看不着」那副面孔。片单只含已到站的、且这位看得着的，最新入集的排前头。
        app.MapGet("/api/collections/{id:long}", (long id, StationDb db, HttpContext http) =>
        {
            var detail = db.GetCollection(id, ViewerGate.Scope(http));
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
                // 详情顶层也带档位（简报那一格）：详情页那枚「私有」角标与「抽屉设置」回显都拿它。
                visibility = VisibilityCodec.ToColumn(detail.Visibility),
                items = detail.Items.Select(Card),
            });
        });

        // 我的集：本人的集当然自己全见 —— 递 Of(自己) 而不是访客尺子，别把自己的私有集从自己面前滤掉。
        // 集里的「这部在不在」判定料同样递这把：看不着的别人的私货不从这里被验出来。
        // bvid 没给就不判，一次拉全集合比对，不给每条集单开一趟。
        app.MapGet("/api/member/collections", (string? bvid, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录，无从谈起你的集" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var scope = VisibilityScope.Of(me.Id);
            var wanted = string.IsNullOrWhiteSpace(bvid) ? (long?)null : db.GetVideoIdByBvid(bvid.Trim());
            var collections = db.ListCollections(me.Id, scope).Select(b => new
            {
                id = b.Id,
                name = b.Name,
                intro = b.Intro,
                ownerMemberId = b.OwnerMemberId,
                ownerName = b.OwnerName,
                createdAtUtc = b.CreatedAtUtc,
                itemCount = b.ItemCount,
                cover = b.Cover,
                // 用户控制室那排抽屉要认得出自己的私有集（角标 + 抽屉设置回显）。
                visibility = VisibilityCodec.ToColumn(b.Visibility),
                contains = wanted is long videoId && db.GetCollectionVideoIds(b.Id, scope).Contains(videoId),
            });
            return Results.Ok(new { collections });
        });

        // 抽屉设置（改名/简介/档位）：/api/collections 前缀不在两张门的视野里，这条写口自防 ——
        // 三道核验照 CheckOwnership（没席位 401 / 查无 404 / 非东家非站长 403），集名照 TryNormalizeName 校，
        // 档位照 TryNormalizeVisibility（里子就是 VisibilityCodec.TryParse，认不了回 400 + 人话）。只把真给了值的栏交给数据层（null 那栏不动），
        // 成功回更新后的 brief（与清单同形，前端不用二次拉）。
        app.MapPatch("/api/collections/{id:long}", (long id, CollectionSettingsRequest? request, StationDb db, HttpContext http) =>
        {
            if (CheckOwnership(db, MemberGate.Current(http), id) is { } denied)
            {
                return Results.Json(new { message = denied.Message }, statusCode: denied.Status);
            }

            string? name = null;
            if (request?.Name is { } rawName && !TryNormalizeName(rawName, out name, out var nameMessage))
            {
                return Results.Json(new { message = nameMessage }, statusCode: StatusCodes.Status400BadRequest);
            }

            VideoVisibility? visibility = null;
            if (request?.Visibility is { } rawVisibility)
            {
                if (!TryNormalizeVisibility(rawVisibility, out var parsed, out var visibilityMessage))
                {
                    return Results.Json(new { message = visibilityMessage }, statusCode: StatusCodes.Status400BadRequest);
                }

                visibility = parsed;
            }

            if (!db.UpdateCollection(id, name, request?.Intro, visibility))
            {
                // 归属与存在都已核过（集在），走到 false 只剩一个意思：三栏都没给值，数据层不空转一圈 UPDATE。
                return Results.Json(new { message = "总得说改哪一栏：名字、简介还是档位" }, statusCode: StatusCodes.Status400BadRequest);
            }

            // 回读递同一把看客尺子（与详情一口同形，itemCount/cover 与用户当场看到的那页一个口径）：
            // 能走到这一步的只有东家本人与站长（CheckOwnership 刚核过），这两把尺子都滤不掉这座集。
            var updated = db.GetCollection(id, ViewerGate.Scope(http));
            return updated is null
                ? Results.Json(new { message = "没有这个合集" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(Brief(updated, "抽屉改好了"));
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

            // 加片挡洗白洞：看不见的片子挂不进你的集。不挡这一手，任何人都能把别人的私有稿挂进自己的公开集，
            // 让它合规地重新出现在全站面前（清单、封面、计数都跟着它走）。这条不是「查无」而是「你动不着它」，所以 403。
            if (!ViewerGate.CanView(http, db, videoId))
            {
                return Results.Json(new { message = "这部片子你看不见，也就挂不进你的集" }, statusCode: StatusCodes.Status403Forbidden);
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
