using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 会员墙（馆藏壁）：把「我的投稿 + 观看历史」两路料聚成一口，喂个人空间顶上的画布窗口。
/// scope 三档：submissions 只放投稿 / history 只放历史 / 其余（含缺省）全部；
/// 同一部既投过又看过只挂一张卡，投稿优先（来源记 submission，不重影）。
/// 分页走 offset/limit（对标 Myriad 的 /api/library：默认 20、封顶 120，不把整库碎兕客户端），
/// 回包携 total 供前端算续拉地板线；顺序稳定（投稿新到站倒序在前、历史最近看过倒序在后），
/// 续页只换切片不改已铺位置，画布端才能拖到哪长到哪。
/// 投稿只认已到站（Arrived，封面必已归档）；历史行本身带 JOIN 的封面现取值，照收。
/// 封顶 400 件兜住别把墙拉爆。/api/member/ 前缀不在两张门的视野里，
/// 端点自己防御式查 MemberGate.Current，没席位一律 401 中文（与 /api/member/history 同一口径）。
/// </summary>
public static class MemberWallEndpoints
{
    /// <summary>一面墙的件数地板：再多也只画四百张卡，画布端再兜一层虚拟化。</summary>
    internal const int WallCap = 400;

    /// <summary>单页默认件数：小批量上桌，画布拖到边缘再续（与前端 PAGE_SIZE 对味）。</summary>
    internal const int DefaultPage = 20;

    /// <summary>单页封顶：再多一口吃不下，也不给刷接口的人留空間。</summary>
    internal const int MaxPage = 120;

    /// <summary>墙上的一张卡：来源 submission = 我投的，history = 我看过的；Preview 与首页卡同形（/previews/ 静态件），没切片留 null 前端不摆预览层。</summary>
    internal sealed record WallItem(
        long VideoId,
        string Bvid,
        string? Title,
        string? LocalCover,
        string? CoverUrl,
        string Source,
        string? Preview = null,
        // 站内自切的封面缩略档位（逗号相接文件名）：有则墙上直接吃 /thumbs，没切过的存量由前端自造小图兑底。
        string? CoverThumbs = null);

    public static void MapMemberWallApi(this WebApplication app)
    {
        app.MapGet("/api/member/wall", (string? scope, int? offset, int? limit, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录，墙上该挂谁的收藏" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var wantSubmissions = scope != "history";
            var wantHistory = scope != "submissions";
            var items = new List<WallItem>();
            var seen = new HashSet<long>();
            // 全表只拉一次：投稿从这里筛，历史行找它补齐悬停预览切片（百来行代价，不单独再问一趟）。
            // 递看客尺子：墙上只挂这位看得着的货（自己的私有稿照挂，别人的私货连封面切片都不许露）。
            // 变量名不叫 scope —— 那个名字已被这口的「只看投稿/只看历史」选择子占走（别拿两把尺子同名混人）。
            var viewer = ViewerGate.Scope(http);
            var allVideos = db.ListVideos(viewer);
            var previewById = new Dictionary<long, string?>();
            foreach (var v in allVideos)
            {
                previewById[v.Id] = v.PreviewUrl;
            }

            // 投稿在前：这是「我带来的」，比「我看过的」更有主人味，先讲它。
            if (wantSubmissions)
            {
                var submitters = db.GetVideoSubmitters();
                foreach (var v in allVideos
                             .Where(v => submitters.GetValueOrDefault(v.Id) == me.Id && v.Status == VideoStatus.Arrived)
                             .OrderByDescending(v => v.CreatedAtUtc))
                {
                    if (seen.Add(v.Id))
                    {
                        items.Add(new WallItem(v.Id, v.Bvid, v.Title, v.LocalCover, v.CoverUrl, "submission", v.PreviewUrl, v.CoverThumbs));
                    }
                }
            }

            // 历史随后：GetVideoHistory 本就按 updated_at 倒序，最近看的排前头。
            if (wantHistory)
            {
                foreach (var r in db.GetVideoHistory(me.Id, WallCap, viewer))
                {
                    if (seen.Add(r.VideoId))
                    {
                        items.Add(new WallItem(r.VideoId, r.Bvid, r.Title, r.LocalCover, r.CoverUrl, "history", previewById.GetValueOrDefault(r.VideoId)));
                    }
                }
            }

            // 封顶 400 兑底：total 也按封顶报，免得前端拿超界件数去要坑位（画布端坑位表只备了 400 个）
            if (items.Count > WallCap) items.RemoveRange(WallCap, items.Count - WallCap);
            var off = Math.Max(0, offset ?? 0);
            var lim = Math.Clamp(limit ?? DefaultPage, 1, MaxPage);
            return Results.Ok(new { items = items.Skip(off).Take(lim), total = items.Count, offset = off, limit = lim });
        });
    }
}
