using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 分站周报的公开窗口：GET /api/weekly?week=0..4，0=本地本周（至今），1..4=往前数完整周，越界夹回。
/// /api/weekly 不在 MemberGate 的会员门禁清单里（那是数据面清单，周报是新开辟的读口），门在端点自己防：
/// 前置 middleware 会给所有 /api/ 请求盖章成员，没章的就是没登录——显式 Results.Json 401 中文回词，
/// 严禁 Results.Forbid()（分站没注册认证服务，Forbid 会 500，DanmakuReportEndpoints 有先例注释）。
/// 数字全部出自 StationDb.GetWeeklyReport（聚合与口径焊在 DB 层，这里只做防御与整形）。
/// </summary>
public static class WeeklyReportEndpoints
{
    /// <summary>缺省看本周：进站问周报，先给「这周到现在」，想考古再往回翻。</summary>
    private const int DefaultWeekBack = 0;

    /// <summary>week 参数夹校（纯函数直测）：null 落缺省，越界夹回 0..4。</summary>
    internal static int ClampWeek(int? week) => Math.Clamp(week ?? DefaultWeekBack, 0, StationDb.WeeklyMaxWeekBack);

    public static void MapWeeklyReportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/weekly", (int? week, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is null)
            {
                return Results.Json(new { message = "登录后才能看周报" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var report = db.GetWeeklyReport(ClampWeek(week));
            return Results.Ok(new
            {
                weekLabel = report.WeekLabel,
                sinceUtc = report.SinceUtc,
                untilUtc = report.UntilUtc,
                news = new
                {
                    count = report.NewsCount,
                    items = report.News.Select(n => new { bvid = n.Bvid, title = n.Title, coverUrl = n.CoverUrl }),
                },
                hottest = report.Hottest.Select(h => new { bvid = h.Bvid, title = h.Title, coverUrl = h.CoverUrl, viewers = h.Viewers }),
                danmakuAdded = report.DanmakuAdded,
                totals = new
                {
                    videos = report.Totals.Videos,
                    members = report.Totals.Members,
                    danmaku = report.Totals.Danmaku,
                    bytes = report.Totals.Bytes,
                },
            });
        });
    }
}
