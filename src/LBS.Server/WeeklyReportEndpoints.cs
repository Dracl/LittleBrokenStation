using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 分站周报的公开窗口：GET /api/weekly?week=0..4，**全是已过完的整周**：0=上一个刚过完的周，1..4=再往前数，越界夹回。
/// /api/weekly 不在 MemberGate 的会员门禁清单里（那是数据面清单，周报是新开辟的读口），门在端点自己防：
/// 前置 middleware 会给所有 /api/ 请求盖章成员，没章的就是没登录——显式 Results.Json 401 中文回词，
/// 严禁 Results.Forbid()（分站没注册认证服务，Forbid 会 500，DanmakuReportEndpoints 有先例注释）。
/// 数字全部出自 StationDb.GetWeeklyReport（聚合与口径焊在 DB 层，这里只做防御与整形）。
/// </summary>
public static class WeeklyReportEndpoints
{
    /// <summary>缺省看最近一个已过完的整周：进站问周报，给的全是盖棺定论、不会再变来变去的数。</summary>
    private const int DefaultWeekBack = 0;

    /// <summary>week 参数夹校（纯函数直测）：null 落缺省，越界夹回 0..4。</summary>
    internal static int ClampWeek(int? week) => Math.Clamp(week ?? DefaultWeekBack, 0, StationDb.WeeklyMaxWeekBack);

    public static void MapWeeklyReportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/weekly", (int? week, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "登录后才能看周报" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var report = db.GetWeeklyReport(ClampWeek(week), me.Id, StationDb.WeeklyNewsPagedTake);
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
                dayBars = report.DayBars.Select(d => new { localDate = d.LocalDate, opens = d.Opens, plays = d.Plays, heads = d.Heads, guestShots = d.GuestShots, guestHeads = d.GuestHeads }),
                deltas = report.Deltas.Select(d => new { key = d.Key, current = d.Current, last = d.Last }),
                staleUps = report.StaleUps.Select(s => new
                {
                    mid = s.Mid,
                    nickname = s.Nickname,
                    faceUrl = s.FaceUrl,
                    dynThisWeek = s.DynThisWeek,
                    dynTotal = s.DynTotal,
                    lastDays = s.LastDays,
                    lastAtUtc = s.LastAtUtc,
                }),
                danmakuAdded = report.DanmakuAdded,
                duration = new
                {
                    seconds = report.Duration.Seconds,
                    shots = report.Duration.Shots,
                    videos = report.Duration.Videos,
                    finished = report.Duration.Finished,
                    avgOpenSeconds = report.Duration.AvgOpenSeconds,
                    topVideos = report.Duration.TopVideos.Select(v => new { bvid = v.Bvid, title = v.Title, coverUrl = v.CoverUrl, seconds = v.Seconds, shots = v.Shots, finishPermille = v.FinishPermille }),
                    topUps = report.Duration.TopUps.Select(u => new { key = u.Key, seconds = u.Seconds, shots = u.Shots, videos = u.Videos, finished = u.Finished }),
                    topZones = report.Duration.TopZones.Select(z => new { key = z.Key, seconds = z.Seconds, shots = z.Shots, videos = z.Videos, finished = z.Finished }),
                },
                topUps = report.TopUps.Select(u => new { upName = u.UpName, viewers = u.Viewers, plays = u.Plays, videos = u.Videos }),
                topZones = report.TopZones.Select(z => new { zone = z.Zone, viewers = z.Viewers, plays = z.Plays, videos = z.Videos }),
                personal = report.Personal is null ? null : new
                {
                    opens = report.Personal.Opens,
                    plays = report.Personal.Plays,
                    videosWatched = report.Personal.VideosWatched,
                    searches = report.Personal.Searches,
                    submissions = report.Personal.Submissions,
                    logins = report.Personal.Logins,
                    danmakuSent = report.Personal.DanmakuSent,
                    dmSent = report.Personal.DmSent,
                    commentsPosted = report.Personal.CommentsPosted,
                },
                personalVsAll = report.PersonalVsAll is null ? null : new
                {
                    sharePermille = report.PersonalVsAll.SharePermille,
                    rank = report.PersonalVsAll.Rank,
                    headCount = report.PersonalVsAll.HeadCount,
                },
                personalDuration = report.PersonalDuration is null ? null : new
                {
                    seconds = report.PersonalDuration.Seconds,
                    shots = report.PersonalDuration.Shots,
                    videos = report.PersonalDuration.Videos,
                    finished = report.PersonalDuration.Finished,
                    avgOpenSeconds = report.PersonalDuration.AvgOpenSeconds,
                },
                activity = new
                {
                    watchPlays = report.WatchPlays,
                    watchOpens = report.WatchOpens,
                    beams = report.Play.Beams,
                    uniqueViewers = report.UniqueViewers,
                    searches = report.Searches,
                    submissions = report.Submissions,
                    logins = report.Logins,
                    newMembers = report.NewMembers,
                    commentsArchived = report.CommentsArchived,
                    dynamicsArchived = report.DynamicsArchived,
                    dmSent = report.DmSent,
                },
                guest = new
                {
                    heads = report.Guest.Heads,
                    opens = report.Guest.Opens,
                    plays = report.Guest.Plays,
                    searches = report.Guest.Searches,
                    blockedWatch = report.Guest.BlockedWatch,
                    blockedDanmaku = report.Guest.BlockedDanmaku,
                    loginTries = report.Guest.LoginTries,
                },
                totals = new
                {
                    videos = report.Totals.Videos,
                    members = report.Totals.Members,
                    danmaku = report.Totals.Danmaku,
                    bytes = report.Totals.Bytes,
                    dynamics = report.Totals.Dynamics,
                    dynamicComments = report.Totals.DynamicComments,
                    dmMessages = report.Totals.DmMessages,
                },
            });
        });
    }
}
