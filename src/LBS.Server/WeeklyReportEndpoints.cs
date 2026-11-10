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

    /// <summary>海报署名头像的转运客户端：B 站图床跨域，直画 canvas 会脏像素（toDataURL 报错），只能同源中转。</summary>
    // 不跟 302：IsBilibiliFaceHost 那道白名单只量了调用方递进来的那个 URL，图床只要肯发一条
    // Location 就能把分站捐去内网（SSRF）——白名单验完不跳，才是真白名单。
    private static readonly HttpClient PosterAvatarHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>week 参数夹校（纯函数直测）：null 落缺省，越界夹回 0..4。</summary>
    internal static int ClampWeek(int? week) => Math.Clamp(week ?? DefaultWeekBack, 0, StationDb.WeeklyMaxWeekBack);

    /// <summary>头像白名单域名（纯函数直测）：只认 B 站系图床，防 SSRF；大小写不敏感。</summary>
    internal static bool IsBilibiliFaceHost(string host) =>
        host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("hdslb.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".biliimg.com", StringComparison.OrdinalIgnoreCase);

    public static void MapWeeklyReportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/weekly", (int? week, string? w, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "登录后才能看周报" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // 绝对周（<c>?w=2026-W44</c>，周报公告那枚按钮跳的就是它）：先把 ISO 周号折成相对偏移，
            // 认不出或超出可回看窗口一律 404；<c>?week=</c> 的老行为一字不变（不传 w 就走原路）。
            var nowUtc = DateTimeOffset.UtcNow;
            int back;
            if (!string.IsNullOrWhiteSpace(w))
            {
                if (StationDb.LocalWeekBackByKey(w, nowUtc) is not { } byKey)
                {
                    return Results.Json(new { message = "那一周不在可回看范围（只有最近 5 个已过完的整周）" }, statusCode: StatusCodes.Status404NotFound);
                }

                back = byKey;
            }
            else
            {
                back = ClampWeek(week);
            }

            // 看客尺子随人：站长全见、席位看公开 + 自家私稿（与首页/分区/馆藏壁同一张嘴）；
            // 周报是对外刊物，别人藏的私稿不得带着标题封面随刊外泄。
            var report = db.GetWeeklyReport(back, nowUtc, me.Id, StationDb.WeeklyNewsPagedTake, ViewerGate.Scope(http));
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
                dayBars = report.DayBars.Select(d => new { localDate = d.LocalDate, opens = d.Opens, plays = d.Plays, heads = d.Heads, guestShots = d.GuestShots, guestHeads = d.GuestHeads, home = d.Home }),
                heatWall = report.HeatWall.Select(h => new { localDate = h.LocalDate, shots = h.Shots }),
                heatMatrix = report.HeatMatrix.Select(c => new { wday = c.Wday, bucket = c.Bucket, shots = c.Shots }),
                sparks = new { news = report.Sparks.News, danmaku = report.Sparks.Danmaku, stock = report.Sparks.Stock },
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
                    home = report.Personal.Home,
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
                    home = report.Play.Home,
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
                    home = report.Guest.Home,
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

        // 海报署名头像的转运站：B 站图床跨域，直画 canvas 会脏像素（toDataURL 直接报错），只能同源中转。
        // 只认 B 站系域名（IsBilibiliFaceHost 白名单，防 SSRF——任意 URL 一律不拉），图不超 512KB，成员才可拉。
        app.MapGet("/api/weekly/poster-avatar", async (string? url, HttpContext http) =>
        {
            if (MemberGate.Current(http) is null)
            {
                return Results.Json(new { message = "登录后才能用海报署名" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp)
                || !IsBilibiliFaceHost(target.Host))
            {
                return Results.BadRequest(new { message = "署名图只认 B 站头像链接" });
            }

            try
            {
                using var resp = await PosterAvatarHttp.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                {
                    return Results.StatusCode((int)resp.StatusCode);
                }

                var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                if (!mime.StartsWith("image/", StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { message = "署名只收图片" });
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(http.RequestAborted);
                if (bytes.Length > 512 * 1024)
                {
                    return Results.BadRequest(new { message = "署名图太大" });
                }

                http.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.File(bytes, mime);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        });
    }
}
