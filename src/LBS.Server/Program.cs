using System.Reflection;
using System.Text.Json;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Services.Download;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// 数据目录：容器内 LBS_DATA_DIR=/data，开发期落到 ./data
var dataPaths = DataPaths.Resolve();
builder.Services.AddSingleton(dataPaths);
builder.Services.AddSingleton(sp =>
{
    var db = new StationDb(sp.GetRequiredService<DataPaths>());
    db.Initialize();
    return db;
});
builder.Services.AddSingleton<DownloadQueue>();
// 全站唯一的 B 站 API 节流闸：随机间隔 + 风控冷却 + 会话预热，挂在 LbsQueryClient 入口一处治全局。
builder.Services.AddSingleton<LbsApiThrottle>();
builder.Services.AddSingleton<LbsCommentArchiver>();
builder.Services.AddSingleton<LbsUpInfoFetcher>();
builder.Services.AddSingleton<LbsAccountRouter>();
// 分流 cookie 供给必须赶在 AddLbsDownloadEngine 之前占位，让上游的 TryAddSingleton 自动让位：
// 从此引擎内所有经 IBilibiliApiClient 的请求（含 playurl/view info）都按视频归属号现取 cookie。
builder.Services.AddSingleton<DownKyi.Application.Bilibili.IBilibiliCookieProvider, LbsRoutedCookieProvider>();
builder.Services.AddSingleton<LbsQueryClient>();
builder.Services.AddSingleton<CommentArchiveService>();
builder.Services.AddSingleton<DanmakuArchiveService>();
builder.Services.AddSingleton<LbsCollectPolicy>();
builder.Services.AddHostedService<DownloadWorker>();
// 分轨码率探子：起进程的事集中在这一个类，同步员在「主片到站」与「加档定档」两个事件点叫它，
// StationDb 只管写台账、不起进程，热路径（开观看页）也不探。
builder.Services.AddSingleton<MediaStreamProber>();
// worker 供号：下载 worker 是 runtime 启动时的常驻循环，AsyncLocal 不跨队列——
// 账号分流上线后 worker 里第一个带凭据请求判无号、异常炸工无日志（10-26 晚后引擎零完成的根因），
// 每个任务的执行包进采集号上下文才能带着凭据跑。
builder.Services.AddSingleton<IDownloadWorkerAmbient, LbsDownloadWorkerAmbient>();
builder.Services.AddHostedService<StatusSyncWorker>();
// 订阅爬工：单例注册以便站长室手动触发拿到同一个实例（串行闸在它手里）。
builder.Services.AddSingleton<SubscriptionCrawler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SubscriptionCrawler>());
// 动态巡捕（M6）：取数员单例；巡捕也单例注册，站长室「立即巡一轮」拿同一个实例吃同一把重入闸。
builder.Services.AddSingleton<LBS.Engine.LbsDynamicFetcher>();
builder.Services.AddSingleton<DynamicFetchWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DynamicFetchWorker>());
// 封面补员：到站时没勾封面的视频，每小时一轮从 cover_url 补下封面原图（每轮封顶 40、条间 300ms，先文件后 artifacts 行）。
builder.Services.AddHostedService<CoverBackfillWorker>();
builder.Services.AddHostedService<StreamBackfillWorker>(); // 分轨码率补员：把探子上岗前落盘的老货逐轮补探
// 悬停预览补员：ffmpeg 从本地成片转低码率无声短片到 data/previews（不进视频库、不进 artifacts）。
// 设定全存 settings KV（preview-*），与引擎设置互不串门；补员员单例注册，站长室催一轮与总览拿的是同一个实例。
builder.Services.AddSingleton<PreviewSettings>();
builder.Services.AddSingleton<PreviewBackfillWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PreviewBackfillWorker>());
// 崩溃遗留的僵尸下载：StatusSyncWorker 负责发现（停滞销案），Reviver 负责救活（手动端点 + 启动自愈共用）。
builder.Services.AddSingleton<LbsTaskReviver>();
builder.Services.AddHostedService<StartupReviverWorker>();
// 下载暂停板：站长室总闸（暂停/继续/取消排队），提交闸与自愈/同步都认这块牌子。
builder.Services.AddSingleton<DownloadPauseBoard>();
// 站长室锁闸：管理类端点的口令门，朋友共用分站前上锁；访客路由不受影响。
builder.Services.AddSingleton<AdminGate>();
// 台账备份员：station.db 在线快照到 data/backups，周期巡工 + 站长室手动补拍。
builder.Services.AddSingleton<LedgerBackup>();
// 数据交换：勾选式导出打包（大包走内存任务牌）+ 导入暂存（开机换库）。
builder.Services.AddSingleton<StationExporter>();
builder.Services.AddSingleton<ExportJobBoard>();
// 缺件回炉闸门：先建档（默认关），开机换库真换了才拨闩——builder.Build() 之后集合只读，不能再 AddSingleton。
builder.Services.AddSingleton<MediaReconcileGate>();
builder.Services.AddHostedService<LedgerBackupWorker>();
builder.Services.AddHostedService<WeeklyReportWorker>(); // 周报发报员：周一 09:00（UTC+8）后给全站播一条周报公告
// 分站海马体：业务侧 Record 一下就走，抄录员后台攒批落 events 表，供站长与未来 AI 分析。
builder.Services.AddSingleton<EventRecorder>();
builder.Services.AddHostedService<EventRecorderWorker>();
// 扫码登录代理：B 站 qrcode 协议的服务端握手（前端只见状态，cookie 只进不出）。
builder.Services.AddSingleton<LbsQrLogin>();
// 成员门闸：放映厅与橱窗的分界线（与站长锁同一套哲学，门在数据上）。
builder.Services.AddSingleton<MemberGate>();

// 下载引擎（移植自 downkyicore 的 headless 运行时）：启动时拉起下载 runtime 并自检 ffmpeg
builder.Services.AddLbsDownloadEngine(new EngineOptions(EngineOptions.DefaultUserAgent));

// 静态前端：开发期直接指向 web/dist（pnpm build 产物），发布镜像里拷进 wwwroot
// 注意：仅赋 WebRootPath 不足以让 StaticFileMiddleware 换目录，必须同时换 WebRootFileProvider
var devWebDist = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "web", "dist"));
var webRoot = File.Exists(Path.Combine(devWebDist, "index.html"))
    ? devWebDist
    : Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(webRoot);
builder.Environment.WebRootPath = webRoot;
builder.Environment.WebRootFileProvider = new PhysicalFileProvider(webRoot);

var urls = Environment.GetEnvironmentVariable("LBS_URLS") ?? "http://0.0.0.0:8090";
builder.WebHost.UseUrls(urls);

var app = builder.Build();

// 数据交换·开机换库：必须赶在 StationDb 首次打开之前（DI 工厂是懒的，workers 也还没起，此刻盘上只有刚 Resolve 的目录）。
// pending.json 在 = 要换进刚导入的包：换台账 + 回写登录态 + 解媒体/缓存 + 清引擎台账；无论成败都删标记，绝不带标记死循环。
var swappedThisBoot = PendingImportSwapper.ApplyIfNeeded(dataPaths, app.Logger);
// 缺件回炉闸门：只在本次开机真换过库时才允许 DownloadWorker 点名磁盘实物，普通开机不点名（防仓挂载瞬断误判满仓缺件）。
app.Services.GetRequiredService<MediaReconcileGate>().ReconcileOnBoot = swappedThisBoot;

// SPA 缓存纪律：index.html 每次必须回源验新（它决定加载哪个哈希 JS，缓存了就等于发布后用户还开着旧壳），
// 带内容哈希的 /assets 产物则相反，永久缓存也无害。html 的统一在 OnStarting 里盖（覆盖静态伺服与 fallback 两条路）。
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.Headers.CacheControl = "no-cache";
        }

        return Task.CompletedTask;
    });
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
        {
            ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
    },
});

app.MapGet("/api/health", (DataPaths paths) => Results.Ok(new HealthInfo(
    Ok: true,
    Name: "小破分站",
    Version: Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0",
    DataDir: paths.Root,
    ServerTimeUtc: DateTimeOffset.UtcNow)));

app.MapEngineSettingsApi();
app.MapAdminRecycleEndpoints();
app.MapPreviewApi();

app.MapGet("/api/videos", (StationDb db, HttpContext http) => Results.Ok(TagManageable(db, db.ListVideos(), http)));

// 站内搜索：只命中已到站视频，空格分词 AND；空查询给空结果让前端少一条错误分支。
// 长度截断防滥用，排序白名单（plays / 缺省新到站）在 StationDb 里控制。
app.MapGet("/api/search", (string? q, string? sort, StationDb db, EventRecorder events, HttpContext http) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.Ok(Array.Empty<VideoEntry>());
    }

    var trimmed = q.Trim();
    if (trimmed.Length > 64)
    {
        trimmed = trimmed[..64];
    }

    var hits = db.SearchVideos(trimmed, sort);
    // 卡阵那两枚自钮（补采/删档）只在服务端算好的 canManage 为真时露面，与放映厅同一口径。
    hits = TagManageable(db, hits, http);
    // 搜了什么、有没有搜到，服务端盖手记（不拿前端上报，不给伪造留门）。
    var member = MemberGate.Current(http);
    events.RecordWithPayload(member is null ? EventActors.Guest : EventActors.Member, member?.Mid,
        EventNames.SearchQuery, "query", trimmed, new { hits = hits.Count, sort },
        http.Connection.RemoteIpAddress?.ToString(), member?.Id);
    return Results.Ok(hits);
});

// 本地媒体区：data/videos 映射到 /media，StaticFileMiddleware 自带 Range 支持，mp4 拖进度条没问题。
// .ass/.srt 不在默认 MIME 字典里，不补映射会被中间件 404 掉（弹幕看不见的真凶之一）。
var mediaTypes = new FileExtensionContentTypeProvider();
mediaTypes.Mappings[".ass"] = "text/plain";
mediaTypes.Mappings[".srt"] = "text/plain";
mediaTypes.Mappings[".vtt"] = "text/vtt";
mediaTypes.Mappings[".json"] = "application/json";

// 中转半成品一律不许从静态区溜出去。实测（本轮）：往 data/videos 丢一个 probe.part，
// GET /media/probe.part → 200 并能拿到整份字节；同位的 .hidden.mp4 倒是 404（中间件不答理点开头）。
// 而这类件是真会出现在途时窗里的：封面补员在视频库内写 <名>.jpg.part（CoverBackfillWorker:80），
// 弹幕刷新在原地摆 <名>.json.refreshing（DanmakuArchiveService:346），预览转码写 <bvid>.mp4.part。
// 这一道闸摆在所有静态区之前（/media 的 UseStaticFiles 在后面，后面的闸拦不住它），各区的 OnPrepareResponse 作补保。
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var onStatic = path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/previews/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/cache/", StringComparison.OrdinalIgnoreCase);
    if (onStatic && IsStaleTransientPath(path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPaths.Videos),
    RequestPath = "/media",
    ContentTypeProvider = mediaTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    // 正片/封面/弹幕的名字永远不会以 .part/.refreshing/.tmp 结尾，也不该有点开头的段：误伤面为零。
    OnPrepareResponse = ctx =>
    {
        if (IsStaleTransientPath(ctx.Context.Request.Path.Value ?? ""))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentType = null;
            ctx.Context.Response.Headers.ContentLength = null;
        }
    },
});

// 头像本地缓存区：评论归档时落盘，热链只作下载失败的兜底。
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPaths.Avatars),
    RequestPath = "/avatars",
    ContentTypeProvider = mediaTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

// 评论区表情与图片的本地缓存区，同样由评论归档器落盘。
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPaths.Emotes),
    RequestPath = "/emotes",
    ContentTypeProvider = mediaTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPaths.CmtImg),
    RequestPath = "/cmt-img",
    ContentTypeProvider = mediaTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

// 悬停预览成片区：只摆 ffmpeg 转出来的低码率短片，与视频库分家（不污染 /media 与视频库体积）。
// 半成品与隐藏件已在前面那道统一闸里 404 掉了，这里只作补保（两道都实测过：404 且 0 字节）。
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPaths.Previews),
    RequestPath = "/previews",
    ContentTypeProvider = mediaTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    // 半成品与隐藏件不外泄：路径里含 /. 或以中转后缀结尾的一律 404。
    OnPrepareResponse = ctx =>
    {
        if (IsStaleTransientPath(ctx.Context.Request.Path.Value ?? ""))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentType = null;
            ctx.Context.Response.Headers.ContentLength = null;
        }
    },
});

// 静态区只认成品：在途中转件（.part/.refreshing/.tmp）与点开头的隐藏件都不算「本站有的东西」。
// 正片、封面、弹幕 json、字幕、预览件的真名字永远不会长成这些样子（预览区只认 <bvid>.mp4），所以误伤面为零。
static bool IsStaleTransientPath(string path) =>
    path.Contains("/.", StringComparison.Ordinal)
    || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".refreshing", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

// 成员门闸（先盖章后验门）：访客只逛橱窗（首页卡阵/搜索/封面），放映厅、弹幕、投稿一律要会员会话；
// 被拦访客的欲看而不得记 watch.blocked 供 AI 分析。
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api/", StringComparison.Ordinal))
    {
        var gate = context.RequestServices.GetRequiredService<MemberGate>();
        var member = gate.Resolve(context.Request);
        if (member is not null)
        {
            gate.Stamp(context, member);
        }
        else if (MemberGate.RequiresMember(context.Request))
        {
            context.RequestServices.GetRequiredService<EventRecorder>()
                .Record(EventActors.Guest, null, EventNames.WatchBlocked, "path", path,
                    ip: context.Connection.RemoteIpAddress?.ToString());
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "member-required", message = "用站名密码登录后才能看视频、弹幕与评论区" });
            return;
        }
    }

    await next();
});

// 站长室门牌：身份即钥匙——只有站长（owner）的会员会话放行，口令锁依约退役（AdminGate 留档待公网复位）。
// 普通成员误打也拦下并指路用户控制室；探头探脑的撞门声记 admin.blocked。
app.Use(async (context, next) =>
{
    var adminGate = context.RequestServices.GetRequiredService<AdminGate>();
    if (adminGate.IsAdminRequest(context.Request))
    {
        var member = MemberGate.Current(context);
        if (!MemberGate.IsOwner(member))
        {
            context.RequestServices.GetRequiredService<EventRecorder>()
                .Record(member is null ? EventActors.Guest : EventActors.Member, member?.Mid, EventNames.AdminBlocked, "path",
                    context.Request.Path.Value ?? "",
                    ip: context.Connection.RemoteIpAddress?.ToString(), actorMemberId: member?.Id);
            var forbidden = member is not null;
            context.Response.StatusCode = forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = forbidden ? "owner-required" : "owner-login-required",
                message = forbidden ? "这里是站长室，普通成员请进用户控制室" : "站长室仅限站长，请先扫码登录",
            });
            return;
        }
    }

    await next();
});

// 锁闸的门面：status 告诉前端要不要摆密码门；unlock 发 7 天会话 cookie；
// lock/release 只能在已解锁态操作（否则上了锁就自己把自己锁死了）。
app.MapGet("/api/gate/status", (AdminGate gate, HttpContext http) => Results.Ok(new
{
    locked = gate.IsLocked,
    unlocked = !gate.IsLocked || gate.HasValidSession(http.Request),
}));

app.MapPost("/api/gate/unlock", (GateRequest request, AdminGate gate, HttpContext http) =>
{
    var token = gate.TryUnlock(request.Password, http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    if (token is null)
    {
        return Results.Unauthorized();
    }

    http.Response.Cookies.Append(AdminGate.Cookie, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = TimeSpan.FromDays(7),
    });
    return Results.Ok(new { message = "口令正确，站长室开门" });
});

app.MapPost("/api/gate/lock", (GateRequest request, AdminGate gate, HttpContext http) =>
{
    // 挂牌得是站长本人（或留档的口令会话）：否则未上锁时任何人都能抢设口令，
    // 「公网复位」启用那天这把闸就是别人抢钥匙的门。
    if (!MemberGate.IsOwner(MemberGate.Current(http)) && !gate.HasValidSession(http.Request))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Trim().Length < 4)
    {
        return Results.BadRequest(new { message = "口令至少 4 位" });
    }

    gate.SetPassword(request.Password.Trim());
    gate.RevokeSessions();
    return Results.Ok(new { message = "站长室已上锁，下次进站需输口令" });
});

app.MapPost("/api/gate/release", (AdminGate gate, HttpContext http) =>
{
    if (!gate.IsLocked)
    {
        return Results.BadRequest(new { message = "本来就没锁" });
    }

    // 摘牌同挂牌：站长会话或留档口令会话，二者皆无一律拒。
    if (!MemberGate.IsOwner(MemberGate.Current(http)) && !gate.HasValidSession(http.Request))
    {
        return Results.Unauthorized();
    }

    gate.Release();
    return Results.Ok(new { message = "锁已解除，分站回到敞开态" });
});

// 会员登录面：扫码握手（领码/代轮询/成功发会话）+ 登出 + 我是谁；邀请制在核销那一步把关。
app.MapMemberEndpoints();
app.MapMemberAccountEndpoints();

// 站长室的成员管理：席位清单、发邀请码、封禁/解封、撤席位（owner 会话门已在前置 middleware 把关）。
app.MapAdminMemberEndpoints();

// 前端行为上报：播放心跳、停留、页面浏览——白名单内客户端才能写，身份由服务端盖章。
app.MapEventIngestEndpoints();
app.MapMemberMessageEndpoints();
app.MapDmEndpoints();
app.MapAdminDmEndpoints();

// 站内弹幕举报（最小闭环）：会员对站内弹幕一键举报，站长在站长室删或忽略；不预隐藏，只此一口（B 站弹幕不管）。
app.MapDanmakuReportEndpoints();
app.MapAdminDanmakuReportEndpoints();
app.MapWeeklyReportEndpoints(); // 分站周报：成员登录后 /api/weekly 看本周战报（/weekly 页）
app.MapWatchHistoryEndpoints(); // 观看历史：报看片进度、续播点位、个人空间历史列表
app.MapZonesEndpoints(); // 站内分区：顶级清单与卡阵公开，改派在站长门内
app.MapCollectionsEndpoints(); // 站内合集：清单与详情公开，开集/销集/加减片子在 /api/member/collections 下端点自防

// 数据交换：导出打包/档案柜/导入暂存与换库（全在 /api/admin/** 站长门下）。
app.MapDataExchangeApi();
app.MapDynamicEndpoints();
app.MapAdminDynamicEndpoints();

// 旧档案的后补：有 mid 但名片从未采过（up_face 为 NULL）时，开页顺手异步采一次，下次刷新就有。
// 失败也销账（置空串）；并发重复触发用 in-flight 集合挡掉。
System.Collections.Concurrent.ConcurrentDictionary<long, byte> upCardInFlight = new();
System.Collections.Concurrent.ConcurrentDictionary<long, byte> statInFlight = new();

app.MapGet("/api/watch/{bvid}", (string bvid, StationDb db, LbsUpInfoFetcher upInfo, LbsCollectPolicy policy, ILoggerFactory loggerFactory, HttpContext http) =>
{
    // BV 号是大小写敏感的 base62，先原样精确查，再补一记大写化兜底（照顾手打习惯）。
    var input = bvid.Trim();
    var data = db.GetWatchData(input) ?? db.GetWatchData(input.ToUpperInvariant());
    if (data is not null)
    {
        BackfillUpCard(db, upInfo, policy, loggerFactory, dataPaths, data);
        BackfillStat(db, upInfo, policy, loggerFactory, data);
    }

    return data is null
        ? Results.NotFound(new { message = "馆藏里没有这个 BV 号" })
        // 能不能重拉快照/删档，服务端算好一个布尔带下去（与站内弹幕 canDelete 同一范式：
        // 前端拿不到投稿人身份，藏钮只是体验，端点里那一遭才是真门）。
        : Results.Ok(data with { CanManage = CanManageVideo(http, db, data.Id) });
});

// 接下来播放：同 UP 主优先、层内随机（库里同 UP 主上百支，按 id 倒序会永远只递那几张新面孔），卡片形状与列表接口一致。
app.MapGet("/api/videos/{id:long}/related", (long id, StationDb db) =>
{
    var related = db.GetRelatedVideos(id);
    return Results.Ok(related.Select(v => new
    {
        v.Id,
        v.Bvid,
        v.Title,
        CoverUrl = v.CoverUrl,
        v.UpName,
        v.ViewCountText,
        v.DurationText,
        v.DanmakuCount,
        // 展示用时间走投稿时间（与首页卡阵同一口径：首取 publishedAtUtc，其次归档的 publishText，
        // 两个都没就整段不显）：全站批量下载后 created_at_utc 清一色同一天，那是站长台账概念。
        // createdAt 照旧留着（可能有别的消费方在用），只是前端不再拿它显时间。
        v.PublishedAtUtc,
        v.PublishText,
        CreatedAt = v.CreatedAtUtc,
    }).ToList());
});

// 头像值不能只判非空：早年那一代把 up_face 写成 /avatars/up-{mid}.jpg，命名换 sha1 后盘上文件已经没了，
// 留着就是 UpCard 的 img 永远 404 到「粉底首字」占位。改走 NeedsRefetch 验货（空串仍是失败销账，不回头扰 B 站）。
void BackfillUpCard(StationDb db, LbsUpInfoFetcher upInfo, LbsCollectPolicy policy, ILoggerFactory loggerFactory, DataPaths paths, WatchData data)
{
    if (data.UpMid is not { } mid || !LbsUpInfoFetcher.NeedsRefetch(data.UpFace, paths.Avatars))
    {
        return;
    }

    if (!upCardInFlight.TryAdd(data.Id, 0))
    {
        return;
    }

    var logger = loggerFactory.CreateLogger("UpCardBackfill");
    _ = Task.Run(async () =>
    {
        try
        {
            var profile = await policy.RunForVideoAsync(data.Id, () => upInfo.FetchAsync(mid, CancellationToken.None));
            db.SetUpInfo(data.Id, profile?.FaceRel ?? string.Empty, profile?.Sign, profile?.Fans);
            if (profile is null)
            {
                logger.LogWarning("馆藏 {VideoId} UP 主名片补采未成（已销账）", data.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "馆藏 {VideoId} UP 主名片补采抛出异常（已忽略）", data.Id);
        }
        finally
        {
            upCardInFlight.TryRemove(data.Id, out _);
        }
    });
}

// 互动数据（点赞/投币/收藏/分享）的后补：未销账且没采过时，开页异步采一次；成败都销账，不反复扰 B 站。
void BackfillStat(StationDb db, LbsUpInfoFetcher upInfo, LbsCollectPolicy policy, ILoggerFactory loggerFactory, WatchData data)
{
    if (data.Bvid is null || !db.ShouldBackfillStat(data.Id))
    {
        return;
    }

    if (!statInFlight.TryAdd(data.Id, 0))
    {
        return;
    }

    var logger = loggerFactory.CreateLogger("StatBackfill");
    _ = Task.Run(async () =>
    {
        try
        {
            var stat = await policy.RunForVideoAsync(data.Id, () => upInfo.FetchStatAsync(data.Bvid, CancellationToken.None));
            if (stat is not null)
            {
                db.SetVideoStat(data.Id, stat.Like, stat.Coin, stat.Favorite, stat.Share, stat.Reply);
            }
            else
            {
                db.MarkStatProbed(data.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "馆藏 {VideoId} 互动数据补采抛出异常（已忽略）", data.Id);
        }
        finally
        {
            statInFlight.TryRemove(data.Id, out _);
        }
    });
}

app.MapDelete("/api/videos/{id:long}", async (long id, HttpContext http, StationDb db, DataPaths paths, IDownloadTaskStateService state, IDownloadTaskLookup lookup, IDownloadTaskQueue taskQueue, IDownloadHistoryService history, CancellationToken cancellationToken) =>
{
    var row = db.GetVideoForDelete(id);
    if (row is null)
    {
        return Results.NotFound(new { message = "馆藏里没有这个编号" });
    }

    // 删档烧的是全站馆藏：从站长专属改为投稿人自管后，这道门得自己在手里核一遗。
    // 先验存在再核归属，免得手快双击第二下时报「没钥匙」而不是「已经不在了」。
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    // 先安顿引擎侧：在途/排队任务要先取消并删除，否则删完站它们还会继续往硬盘写。
    // 任务号不再只看 videos.engine_task_ids（到站那一刻已被 MarkArrived 清成 NULL）：加档行的在途任务
    // 从来没人安顿，片子删了它们照样往 data/videos 里补字节。GetEngineTraceForVideo 三处一并取。
    var trace = db.GetEngineTraceForVideo(id);
    var (retired, blocking) = await EngineTaskRetire.RunAsync(
        trace.TaskIds, lookup, state, taskQueue, tolerateFailures: false, cancellationToken);
    if (blocking is not null)
    {
        return Results.Conflict(new { message = $"引擎任务删不动（{blocking}），等下载告一段落再试" });
    }

    // 加档行跟着馆藏一起收：引擎任务上面那一步已销，这里只销分站这侧的账，
    // 免得留一行 Downloading 让同步员去扫一台已经不存在的片子。
    foreach (var codecRow in db.GetDownloadingCodecVersions())
    {
        if (codecRow.VideoId == id)
        {
            db.MarkCodecFailed(codecRow.Id, "馆藏已删档，这次加档随之取消");
        }
    }

    // 引擎历史台账按片销掉：它按 (cid, 需要下载的内容) 判重（DownloadTaskAdmissionService.cs:65），
    // 不销它的话删完重投会被判「已下过」：新行落到无人认领的 Skipped，老行又因 bvid 撞车恢复不了，两头卡死。
    // 先走引擎接口按任务号销（顺带对齐它的内存态），再按 cid 补扫一遍任务号丢失的旧行（到站后 engine_task_ids 会被清）。
    var historyRows = 0;
    try
    {
        foreach (var taskId in trace.TaskIds)
        {
            if ((await history.DeleteAsync(new DownloadTaskId(taskId), cancellationToken)).IsSuccess)
            {
                historyRows++;
            }
        }

        historyRows += LbsEnginePurge.PurgeEngineHistoryByCids(paths.Downkyi, trace.Cids);
    }
    catch (Exception ex)
    {
        // 引擎库锁着/不在不拦删档：站这侧该进的回收站照样进，历史行留给下次或危险区一键清空。
        app.Logger.LogWarning(ex, "馆藏 {VideoId} 的引擎历史没销动（删档继续）", id);
    }

    // 不再就地焚毁：文件抖进 data/recycle，六张表的行连主键封进清单。
    // 用户那边列表/搜索/相关推荐都查无此片，话语权留在站长的回收站卡片里。
    var commentCount = 0;
    if (db.GetArtifactRel(id, "comments") is { } commentsRel)
    {
        var snapshotPath = Path.Combine(paths.Videos, commentsRel.Replace('/', Path.DirectorySeparatorChar));
        if (LBS.Engine.CommentSnapshotOps.Load(snapshotPath) is { } snapshot)
        {
            commentCount = snapshot.Replies.Count + (snapshot.Top is null ? 0 : 1);
        }
    }

    var deleter = MemberGate.Current(http);
    var recycled = db.MoveVideoToRecycle(id, paths.Videos, paths.Recycle, deleter?.Nickname ?? deleter?.Username ?? "站长", commentCount);
    return recycled.Ok
        ? Results.Ok(new
        {
            message = $"「{row.Value.Bvid}」已从分站删除，带走 {recycled.Files} 个文件（存进了回收站）"
                + (historyRows > 0 ? $"，顺手销掉 {historyRows} 行引擎历史（重投不再判重）" : "")
                + (retired > 0 ? $"，安顿 {retired} 个引擎任务" : ""),
            binId = recycled.BinId,
        })
        : Results.Conflict(new { message = recycled.Message });
});

// 续下：救活硬重启遗留的卡死下载（引擎任务挂在途表里纹丝不动）。
// 软续下（Resume+Enqueue）优先，打不动就删行重投，策略见 LbsTaskReviver。
app.MapPost("/api/videos/{id:long}/revive", async (long id, LbsTaskReviver reviver, CancellationToken cancellationToken) =>
{
    var (ok, message) = await reviver.ReviveAsync(id, cancellationToken);
    return ok ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// 重新排队：站长取消过的馆藏（Canceled）从卡阵里捞回排队区；被引擎判为「已下过」的（Skipped）
// 走另一条道：挂上重下旗再回炉，否则同一把判重键会在门口再挡一次。其它状态不响应免得误复活。
app.MapPost("/api/videos/{id:long}/requeue", async (long id, StationDb db, DownloadQueue queue, CancellationToken cancellationToken) =>
{
    var brief = db.GetVideoBrief(id);
    if (brief is null)
    {
        return Results.NotFound(new { message = "没有这条馆藏" });
    }

    if (brief.Value.Status == VideoStatus.Skipped)
    {
        // Skipped 的成因在引擎那一侧：download_history 里有同名行就判无需下载。光重投还是原样被打回，
        // 所以这路必须带 RedownloadCompleted 走正门（引擎自己给加档重下用的就是这条），而不是伸手改它的库。
        // 挂牌必须在 ResetToPending 之后：那一步走状态机统一入口，会把这枚旗顺手清零。
        db.ResetToPending(id);
        db.MarkForceRedownload(id);
        await queue.EnqueueAsync(id, cancellationToken);
        return Results.Ok(new { message = "这馆先前被引擎判为已下过，已绕开判重重新排队（正片会重下一遍）" });
    }

    if (brief.Value.Status != VideoStatus.Canceled)
    {
        return Results.BadRequest(new { message = "只有被取消的馆藏才需要重新排队" });
    }

    db.ResetToPending(id);
    await queue.EnqueueAsync(id, cancellationToken);
    return Results.Ok(new { message = "已重新排队，下一班进站" });
});

// 编码加档：给已到站馆藏点单另一个编码版本（avc/hevc/av1），后台整视频补货，
// 每分 P 一行账，完成后同步员扫盘定档；watch 接口的 codecVersions 随叫随查。
// ─── 站内弹幕 ──────────────────────────────────────────────────────
// 访客发的弹幕存分站自己的库（B 站爬的不掺进来）：刷新不丢，站内互见。
// ─── 身份：匿名访客凭证 + 主人模式 ────────────────────────
// 访客：首访发 lbs_dmid（HttpOnly，180 天），弹幕表存其值为发送者；防误删而非防恶。
// 主人这一路以前另有一把 data/owner.key 钥匙（带上 lbs_owner cookie 就算主人），已摘除：
// 站长室、卡阵自管钮、发弹幕挂金冠、删弹幕全都只认会员席位 role=owner 那张脸，
// 两套「主人」并行只会让人猜不透哪把管用（旧库里残留的 data/owner.key 就此成了死件）。

static string EnsureDmid(HttpContext http)
{
    if (http.Request.Cookies.TryGetValue("lbs_dmid", out var dmid) && dmid is { Length: >= 8 and <= 64 })
    {
        return dmid;
    }

    dmid = Guid.NewGuid().ToString("N");
    http.Response.Cookies.Append("lbs_dmid", dmid, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.AddDays(180),
        Path = "/",
    });
    return dmid;
}

// 自管钥匙：这稿子的投稿人本人或站长。归属不明的行（爬工代投、老数据反查失败）
// 只有站长握得住，与 MemberMail「无归属不发信」同一口径。比的是站内席位 id，不绕 mid。
static bool CanManageVideo(HttpContext http, StationDb db, long videoId)
{
    var member = MemberGate.Current(http);
    if (member is null)
    {
        return false;
    }

    if (MemberGate.IsOwner(member))
    {
        return true;
    }

    return db.GetVideoSubmitterMember(videoId) is { } submitter && submitter == member.Id;
}

// 卡阵/放映厅的自管钮共用的那一句拒绕（分站没注册认证服务，Results.Forbid() 会 500，以前提过）。
static IResult NotYourSubmission() => Results.Json(
    new { error = "not-your-submission", message = "这枚钥匙只有投稿人本人和站长握得住" },
    statusCode: StatusCodes.Status403Forbidden);

// 给卡阵行挂上 canManage：访客一律假，登录者按归属集合一次算完（不逐行问库）。
static List<LBS.Domain.VideoEntry> TagManageable(StationDb db, IEnumerable<LBS.Domain.VideoEntry> entries, HttpContext http)
{
    var member = MemberGate.Current(http);
    var list = entries.ToList();
    if (member is null)
    {
        return list;
    }

    var owner = MemberGate.IsOwner(member);
    var submitters = db.GetVideoSubmitters();
    return list.Select(v => v with
    {
        CanManage = owner || (submitters.TryGetValue(v.Id, out var s) && s == member.Id),
    }).ToList();
}

// （原 /api/owner/key | enter | leave | state 四个端点随主人钥匙一起摘除：站内只认会员席位
// role=owner 这一个身份，不再养第二把能让人当主人的钥匙。旧 data/owner.key 就此失效。）

// 删除权：仅发送者本人（凭 lbs_dmid cookie）或站长（会员席位 role=owner）；
// canDelete 由服务端算好随列表下发，发送者身份不外泄。无主老弹幕只有站长删得掉。
app.MapGet("/api/videos/{id:long}/danmaku", (long id, HttpContext http, StationDb db) =>
{
    var dmid = EnsureDmid(http);
    var owner = MemberGate.IsOwner(MemberGate.Current(http));
    // 主人弹幕但当年没记席位（旧主人钥匙发的、或身份体系之前的老行）：拿站长席位那张脸兼底。
    // 账上就坐着他的席位，不能因为发的时候没带席位会话就让他永远顶个粉小人。
    var ownerSeatFace = db.GetOwnerSeatFace();
    return Results.Ok(db.ListLocalDanmaku(id).Select(d => new
    {
        id = d.Id,
        pageIdx = d.PageIdx,
        t = d.TSec,
        text = d.Text,
        canDelete = owner || (d.SenderDmid is { Length: > 0 } s && s == dmid),
        // 主人发的弹幕挂金冠；身份体系之前的无主老行（无凭证）也当主人手笔，那阵子只有主人能发。
        fromOwner = d.SenderOwner || d.SenderDmid is null,
        // 席位真容：自己的脸优先；主人行没记席位就领站长席位那张脸；都没有就由前端画那颗白圆粉小人。
        face = FaceOf(d, ownerSeatFace),
    }));

    static string? FaceOf((long Id, int PageIdx, double TSec, string Text, string? SenderDmid, bool SenderOwner, long? MemberId, string? MemberFace) d, string? ownerSeatFace)
    {
        if (!string.IsNullOrWhiteSpace(d.MemberFace))
        {
            return d.MemberFace;
        }

        var fromOwner = d.SenderOwner || d.SenderDmid is null;
        return fromOwner && !string.IsNullOrWhiteSpace(ownerSeatFace) ? ownerSeatFace : null;
    }
});

app.MapPost("/api/videos/{id:long}/danmaku", (long id, LocalDanmakuRequest request, HttpContext http, StationDb db) =>
{
    var text = request.Text.Trim();
    if (text.Length is 0 or > 100)
    {
        return Results.BadRequest(new { message = "弹幕得写点个到 100 字之间" });
    }

    if (db.GetVideoFileNameInfo(id) is null)
    {
        return Results.NotFound(new { message = "馆藏里没这个视频" });
    }

    // 主人身份只认一个口：席位里头名那个站长（旧的 lbs_owner 钥匙已摘除，不必再进一次「主人模式」）。
    var member = MemberGate.Current(http);
    var owner = MemberGate.IsOwner(member);
    var dmId = db.AddLocalDanmaku(id, Math.Max(0, request.PageIdx), Math.Max(0, request.T), text, EnsureDmid(http), owner, member?.Id);
    // 把服务端算好的身份一并回过去，前端回显时脸与冠不用自己猜（也没 cookies 可猜）。
    // 老的主人行当年没记席位，脸领站长席位那张。
    var echoFace = string.IsNullOrWhiteSpace(member?.FaceUrl) ? (owner ? db.GetOwnerSeatFace() : null) : member.FaceUrl;
    return Results.Ok(new
    {
        id = dmId,
        message = "弹幕已存站",
        fromOwner = owner,
        face = string.IsNullOrWhiteSpace(echoFace) ? null : echoFace,
    });
});

app.MapDelete("/api/videos/danmaku/{dmId:long}", (long dmId, HttpContext http, StationDb db) =>
{
    var sender = db.GetLocalDanmakuSender(dmId);
    if (sender is null)
    {
        return Results.NotFound();
    }

    var dmid = EnsureDmid(http);
    // 删除权两个口：发送者本人（凭自己的 lbs_dmid）与站长（会员席位 role=owner）。
    // 无主老弹幕（sender 为空）从此归站长删——以前得另外进一次主人模式才删得动，不合理。
    if (!MemberGate.IsOwner(MemberGate.Current(http)) && !(sender.Length > 0 && sender == dmid))
    {
        // 分站没注册认证服务，Results.Forbid() 会 500；显式状态码才稳。
        return Results.Json(new { message = "这条弹幕不是你的，站长才能删" }, statusCode: 403);
    }

    return db.DeleteLocalDanmaku(dmId) ? Results.Ok(new { message = "弹幕已删" }) : Results.NotFound();
});

// 访客下载：把正在看的版本存本地（codec 缺省给主版本）。开 Range 支持断点续传，
// 文件名带标题/分 P/编码；路径死锁在 videos 库内，隔不过目录。
app.MapGet("/api/videos/{id:long}/download", (long id, int pageIdx, string? codec, StationDb db, DataPaths paths) =>
{
    var trimmed = codec?.Trim().ToLowerInvariant();
    var rel = string.IsNullOrEmpty(trimmed)
        ? db.GetPageMainRel(id, pageIdx)
        : db.GetArrivedCodecRel(id, pageIdx, trimmed);
    if (rel is null)
    {
        return Results.NotFound(new { message = "这个版本分站还没存下，没东西可搬" });
    }

    var videosRoot = paths.Videos.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var full = Path.GetFullPath(Path.Combine(videosRoot, rel));
    if (!full.StartsWith(videosRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
    {
        return Results.NotFound(new { message = "台账有账，磁盘没档（可能被手删了）" });
    }

    var info = db.GetVideoFileNameInfo(id);
    var rawName = info is null ? "video" : (info.Value.Title is { Length: > 0 } t ? t : info.Value.Bvid);
    var safe = string.Concat(rawName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
    if (safe.Length > 80)
    {
        safe = safe[..80];
    }

    var ext = Path.GetExtension(full);
    var contentType = ext.ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".m4a" => "audio/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };

    var codecTag = trimmed ?? db.GetPageMainCodec(id, pageIdx);
    var downloadName = $"{safe}_P{pageIdx}{(codecTag is { Length: > 0 } c ? "_" + c.ToUpperInvariant() : string.Empty)}{ext}";
    return Results.File(full, contentType, downloadName, enableRangeProcessing: true);
});

app.MapPost("/api/videos/{id:long}/codecs", async (long id, CodecVersionRequest request, StationDb db, DownloadQueue queue, CancellationToken cancellationToken) =>
{
    var codec = (request.Codec ?? string.Empty).Trim().ToLowerInvariant();
    if (!StationDb.SupportedCodecs.Contains(codec, StringComparer.Ordinal))
    {
        return Results.BadRequest(new { message = "不认的编码，只收 avc / hevc / av1" });
    }

    var brief = db.GetVideoBrief(id);
    if (brief is null)
    {
        return Results.NotFound(new { message = "馆藏不存在" });
    }

    if (brief.Value.Status != VideoStatus.Arrived)
    {
        return Results.Conflict(new { message = "主版本还没到站，加档不开放" });
    }

    var pages = db.GetPageIndexes(id);
    if (pages.Count == 0)
    {
        return Results.Conflict(new { message = "这条馆藏没有分 P 归档记录，加档无从谈起" });
    }

    var added = pages.Count(page => db.AddCodecVersion(id, page, codec));
    if (added == 0)
    {
        return Results.Conflict(new { message = $"{codec.ToUpperInvariant()} 版本已在站或已在途" });
    }

    await queue.EnqueueCodecAsync(id, codec, cancellationToken);
    return Results.Ok(new { message = $"{codec.ToUpperInvariant()} 版本已排队进站（{added} 个分 P）" });
});

// 加档实况轻端点（匿名可读）：放映厅的版本面板把「进站中…」报成带百分比/字节/速度的实况，
// 与站长室队列同一本账——引擎在途表逐枚现查，进度的真伪以引擎为准。
app.MapGet("/api/videos/{id:long}/codecs/progress", async (long id, StationDb db, IDownloadTaskLookup lookup, CancellationToken cancellationToken) =>
{
    var rows = new List<object>();
    foreach (var (rowId, videoId, pageIdx, codec, taskIdsJson, basePath) in db.GetDownloadingCodecVersions())
    {
        if (videoId != id)
        {
            continue;
        }

        foreach (var taskId in StatusSyncWorker.ParseTaskIds(taskIdsJson))
        {
            var task = await lookup.FindAsync(new DownloadTaskId(taskId), cancellationToken);
            if (task is null)
            {
                continue; // 已搬历史库，没得可报
            }

            rows.Add(new
            {
                codec,
                pageIdx,
                phase = task.Phase.ToString(),
                percent = task.Progress.Percentage,
                got = task.Progress.DownloadedSizeText,
                speed = task.Progress.SpeedText,
            });
            break;
        }
    }

    return Results.Ok(rows);
});

// 取消一件在途加档：销引擎账 + 判 Failed；反悔了在首页重新点单即可。
// 早先面板注释说「加档没取消端点，造个按下去没反应的钮比没有更坑」——现在引擎账销得动，钮按得动了。
app.MapPost("/api/videos/{id:long}/codecs/cancel", async (long id, CancelCodecRequest request, StationDb db, IDownloadTaskStateService state, IDownloadTaskLookup lookup, IDownloadTaskQueue taskQueue, CancellationToken cancellationToken) =>
{
    var codec = (request.Codec ?? string.Empty).Trim().ToLowerInvariant();
    if (!StationDb.SupportedCodecs.Contains(codec, StringComparer.Ordinal))
    {
        return Results.BadRequest(new { message = "不认的编码，只收 avc / hevc / av1" });
    }

    long? targetRow = null;
    string? targetTasks = null;
    foreach (var row in db.GetDownloadingCodecVersions())
    {
        if (row.VideoId == id && string.Equals(row.Codec, codec, StringComparison.Ordinal)
            && (request.PageIdx is null || row.PageIdx == request.PageIdx))
        {
            targetRow = row.Id;
            targetTasks = row.TaskIdsJson;
            break;
        }
    }

    if (targetRow is null)
    {
        return Results.NotFound(new { message = $"这条馆藏没有正在进行的 {codec.ToUpperInvariant()} 加档，不用取消" });
    }

    foreach (var taskId in StatusSyncWorker.ParseTaskIds(targetTasks))
    {
        if (await lookup.FindAsync(new DownloadTaskId(taskId), cancellationToken) is null)
        {
            continue;
        }

        try
        {
            await taskQueue.CancelAsync(new DownloadTaskId(taskId));
        }
        catch (Exception)
        {
            // 本就没在跑，取消不掉无妨
        }

        try
        {
            await state.CancelAsync(new DownloadTaskId(taskId), cancellationToken);
            await state.DeleteAsync(new DownloadTaskId(taskId), cancellationToken);
        }
        catch (Exception)
        {
            // 引擎销不动也不拦取消：账先销，残存任务由看门狗下一轮收拾
        }
    }

    db.MarkCodecFailed(targetRow.Value, "站长取消了这次加档，可在首页重新点单");
    return Results.Ok(new { message = $"已取消 {codec.ToUpperInvariant()} 加档（反悔可在首页重新点单）" });
});

// 删单个编码版本：销账 + 删文件；主版本不受影响。
app.MapDelete("/api/videos/{id:long}/codecs/{pageIdx:int}/{codec}", (long id, int pageIdx, string codec, StationDb db, DataPaths paths) =>
{
    var rel = db.TakeCodecVersionForDelete(id, pageIdx, codec);
    if (rel is null)
    {
        return Results.NotFound(new { message = "没有这个版本" });
    }

    try
    {
        var full = Path.Combine(paths.Videos, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }
    catch (IOException)
    {
        // 文件被占用时账已销，文件留给下次删除/重启清理
    }

    return Results.Ok(new { message = $"{codec.ToUpperInvariant()} 版本已清出站" });
});

// 补采评论：到站时自动采过一次，这条给投稿人/站长手动刷新快照（老馆藏回填也靠它）。
// 出号走引擎归属号（站长号），所以只开放给这稿子的投稿人本人与站长，端点里再核一遗。
app.MapPost("/api/videos/{id:long}/comments", async (long id, HttpContext http, StationDb db, CommentArchiveService service, CancellationToken cancellationToken) =>
{
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    var outcome = await service.TryArchiveAsync(id, force: true, cancellationToken);
    return outcome.Ok
        ? Results.Ok(new { message = outcome.Message, count = outcome.Count })
        : Results.BadRequest(new { message = outcome.Message });
});

// 重拉 B 站弹幕快照：只覆写分 P 旁边的 {base}.ass / {base}.xml 与 artifacts 台账，
// 分站朋友发的站内弹幕住在 danmaku_local 表里，这条链路一个字节都不碰。
// 新档比留档薄时默认不覆写（留档不会因为 B 站删弹幕而变薄）；确实要薄那就 ?force=1 表态。
app.MapPost("/api/videos/{id:long}/danmaku-refresh", async (long id, HttpContext http, StationDb db, DanmakuArchiveService service, CancellationToken cancellationToken) =>
{
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    // 没有 force 了：重拉是增量并集，只进不退，B 站抽风空手而归也不会把留档刷薄。
    var outcome = await service.TryRefreshAsync(id, cancellationToken);
    return outcome.Ok
        ? Results.Ok(new { message = outcome.Message, added = outcome.Added, total = outcome.Total, pages = outcome.Pages })
        : Results.BadRequest(new { message = outcome.Message });
});

// 楼中楼按需补采：快照里没预配到的楼层点开时，分站现向 B 站翻这一楼并并回快照。
app.MapPost("/api/videos/{id:long}/comments/subs", async (long id, CommentArchiveService service, SubsRequest request, CancellationToken cancellationToken) =>
{
    var (ok, message, subs, hasMore) = await service.FetchSubsForVideoAsync(id, request.Rpid, request.Page, cancellationToken);
    return ok
        ? Results.Ok(new { replies = subs, hasMore })
        : Results.BadRequest(new { message });
});

app.MapPost("/api/videos", async (AddVideoRequest request, StationDb db, DownloadQueue queue, EventRecorder events,
    HttpContext http, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Url)
        || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
        || !BilibiliLink.IsBilibiliHost(uri.Host))
    {
        return Results.BadRequest(new { message = "这看起来不是一条 B 站视频链接" });
    }

    if (!BilibiliLink.TryExtractBvid(request.Url, out var bvid))
    {
        return Results.BadRequest(new { message = "链接里没有找到 BV 号" });
    }

    var outcome = db.AddVideo(request.Url, bvid);
    if (outcome.Result == AddVideoResult.Duplicate)
    {
        return Results.Conflict(new { message = "这个视频已经到过了" });
    }

    // 投稿定人：谁投的归谁，下载提交时走他自己的账号池（M5.4：定的是站内席位 id）。
    var submitter = MemberGate.Current(http);
    // 投稿必留名：查不出席位的匿名稿一律拒收——门闸按路径点名，历史上尾斜杠就能穿到这里白灌台账；
    // 就算门修好，这道死规也留着：宁可多拒一票，台账不养无主行。
    if (submitter is null)
    {
        return Results.Json(new { error = "member-required", message = "投稿要留名：登录进站再丢链接" },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    // 投稿闸门（M5.4）：素净席位能看能聊不能投稿——得有一个未过期的 B 站号才烧得起别人的登录态；站长拿引擎登录态投稿，不受此闸。
    if (!SubmissionGate.Allows(db, submitter))
    {
        return Results.Json(new
        {
            error = "no-bilibili-account",
            message = "还没绑 B 站号，先去何处？用户控制室——绑定后就能投稿，看片私信不耽误",
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    db.SetVideoSubmitterMember(outcome.VideoId, submitter.Id);

    events.RecordWithPayload(EventActors.Member, submitter.Mid, EventNames.VideoSubmit, "video", bvid,
        new { url = request.Url, via = "link" }, http.Connection.RemoteIpAddress?.ToString(), submitter.Id);

    await queue.EnqueueAsync(outcome.VideoId, cancellationToken);
    return Results.Ok(new { videoId = outcome.VideoId, message = $"已加入进站队列，{bvid} 正在赶来" });
});

app.MapPost("/api/favorites/import", async (FavoritesImportRequest request, StationDb db, DownloadQueue queue,
    EventRecorder events, HttpContext http, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Content) || !NetscapeBookmarkParser.LooksLikeNetscapeBookmarkFile(request.Content))
    {
        return Results.BadRequest(new { message = "这看起来不是浏览器导出的收藏夹文件" });
    }

    var candidates = NetscapeBookmarkParser.Parse(request.Content);
    var videos = new List<NewVideo>();
    var ignored = 0;

    foreach (var candidate in candidates)
    {
        if (Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri)
            && BilibiliLink.IsBilibiliHost(uri.Host)
            && BilibiliLink.TryExtractBvid(candidate.Url, out var bvid))
        {
            videos.Add(new NewVideo(candidate.Url, bvid, candidate.Title));
        }
        else
        {
            ignored++;
        }
    }

    var bookmarkSeat = MemberGate.Current(http)?.Id;
    var outcome = db.AddVideosBulk(videos);
    foreach (var videoId in outcome.InsertedIds)
    {
        if (bookmarkSeat is long seatId)
        {
            db.SetVideoSubmitterMember(videoId, seatId);
        }

        await queue.EnqueueAsync(videoId, cancellationToken);
    }

    if (outcome.Added > 0)
    {
        var actor = MemberGate.Current(http);
        events.RecordWithPayload(actor is null ? EventActors.Guest : EventActors.Member, actor?.Mid,
            EventNames.VideoSubmit, "batch", "bookmarks",
            new { via = "bookmarks", added = outcome.Added, duplicated = outcome.Duplicated, ignored },
            http.Connection.RemoteIpAddress?.ToString(), actor?.Id);
    }

    return Results.Ok(new
    {
        added = outcome.Added,
        duplicated = outcome.Duplicated,
        ignored,
        message = $"导入完成：新增 {outcome.Added} 条，重复 {outcome.Duplicated} 条，忽略 {ignored} 条非视频链接",
    });
});

app.MapPost("/api/favorites/import-url", async (FavoritesUrlImportRequest request, LbsFavoritesImport favorites, LbsCollectPolicy policy, LbsApiThrottle throttle, StationDb db, DownloadQueue queue,
    EventRecorder events, HttpContext http, CancellationToken cancellationToken) =>
{
    // 整架导入是批量动作，冷却期直接挡回，不等它耐心到天荒地老。
    if (throttle.IsCoolingDown)
    {
        var left = throttle.CooldownRemaining ?? TimeSpan.Zero;
        return Results.Json(new { message = $"分站正在风控冷却中，约 {Math.Ceiling(left.TotalMinutes)} 分钟后自动回暖，稍后再投" }, statusCode: StatusCodes.Status423Locked);
    }

    // 整架导入不挂具体视频，按采集偏好出号。
    var outcome = await policy.RunForActivityAsync(() => favorites.ImportAsync(request.Url, cancellationToken));
    if (!outcome.Success)
    {
        return Results.BadRequest(new { message = outcome.Error });
    }

    var favSeat = MemberGate.Current(http)?.Id;
    var bulk = db.AddVideosBulk(outcome.Videos);
    foreach (var videoId in bulk.InsertedIds)
    {
        if (favSeat is long seatId)
        {
            db.SetVideoSubmitterMember(videoId, seatId);
        }

        await queue.EnqueueAsync(videoId, cancellationToken);
    }

    if (bulk.Added > 0)
    {
        var actor = MemberGate.Current(http);
        events.RecordWithPayload(actor is null ? EventActors.Guest : EventActors.Member, actor?.Mid,
            EventNames.VideoSubmit, "batch", "fav_url",
            new { via = "fav_url", added = bulk.Added, duplicated = bulk.Duplicated, ignored = outcome.Ignored },
            http.Connection.RemoteIpAddress?.ToString());
    }

    var message = $"整架签收：收到 {outcome.Videos.Count + outcome.Ignored} 个条目，新进站 {bulk.Added} 条，来过 {bulk.Duplicated} 条";
    if (outcome.Ignored > 0)
    {
        message += $"，{outcome.Ignored} 条非视频链接没理";
    }

    return Results.Ok(new
    {
        total = outcome.Videos.Count + outcome.Ignored,
        added = bulk.Added,
        duplicated = bulk.Duplicated,
        ignored = outcome.Ignored,
        message,
    });
});

app.MapPost("/api/up/import", async (UpImportRequest request,
    LbsQueryClient client, IWbiKeyProvider wbiKeys, TimeProvider timeProvider,
    LbsCollectPolicy policy, LbsApiThrottle throttle, StationDb db, DownloadQueue queue,
    EventRecorder events, HttpContext http,
    CancellationToken cancellationToken) =>
{
    if (throttle.IsCoolingDown)
    {
        var left = throttle.CooldownRemaining ?? TimeSpan.Zero;
        return Results.Json(new { message = $"分站正在风控冷却中，约 {Math.Ceiling(left.TotalMinutes)} 分钟后自动回暖，稍后再投" }, statusCode: StatusCodes.Status423Locked);
    }

    var mid = SubscriptionCrawler.ParseMid(request.Url);
    if (mid is null)
    {
        return Results.BadRequest(new { message = "认不出这个 UP 主，填 space.bilibili.com/… 空间链接或纯数字 MID" });
    }

    // 全量翻页收编（PUBDATE 序 ps=30）：翻页节奏由 LbsApiThrottle 统一管，这里只管止损——
    // 单 UP 封顶 500 条，老货想补第二波再投一次即可（台账去重兼容重复投稿）。
    const int pageSize = 30;
    const int maxVideos = 500;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var videos = new List<NewVideo>();
    for (var pn = 1; videos.Count < maxVideos; pn++)
    {
        var page = await policy.RunForActivityAsync(() => WbiRequestExecutor.ExecuteAsync(
            wbiKeys,
            async (keys, wts) => await client.GetPublicationPageAsync(
                keys, wts, mid.Value, pn: pn, ps: pageSize, policy.HasAccount(),
                order: PublicationOrder.PUBDATE, cancellationToken: cancellationToken),
            timeProvider,
            cancellationToken));

        var list = page?.List.Vlist ?? Array.Empty<SpacePublicationListVideo>();
        var fresh = 0;
        foreach (var item in list)
        {
            var bvid = item.Bvid?.Trim();
            if (string.IsNullOrWhiteSpace(bvid) || !seen.Add(bvid))
            {
                continue;
            }

            videos.Add(new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid,
                string.IsNullOrWhiteSpace(item.Title) ? null : item.Title.Trim(),
                string.IsNullOrWhiteSpace(item.Pic) ? null : item.Pic.Trim(),
                item.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(item.Created).ToString("O") : null));
            fresh++;
        }

        // 止损三连：尾页（不足一页）、空页、整页全是重复（翻页失效兜底）都收手。
        if (list.Count == 0 || list.Count < pageSize || fresh == 0)
        {
            break;
        }
    }

    if (videos.Count == 0)
    {
        return Results.Ok(new { total = 0, added = 0, duplicated = 0, message = "这位 UP 主名下没翻到视频（或空间设了权限）" });
    }

    var upSeat = MemberGate.Current(http)?.Id;
    var upBulk = db.AddVideosBulk(videos);
    foreach (var videoId in upBulk.InsertedIds)
    {
        if (upSeat is long seatId)
        {
            db.SetVideoSubmitterMember(videoId, seatId);
        }

        await queue.EnqueueAsync(videoId, cancellationToken);
    }

    if (upBulk.Added > 0)
    {
        var actor = MemberGate.Current(http);
        events.RecordWithPayload(actor is null ? EventActors.Guest : EventActors.Member, actor?.Mid,
            EventNames.VideoSubmit, "batch", "up",
            new { via = "up", added = upBulk.Added, duplicated = upBulk.Duplicated },
            http.Connection.RemoteIpAddress?.ToString());
    }

    var truncated = videos.Count >= maxVideos ? "，先到 500 条为止（想补老货再投一次）" : string.Empty;
    return Results.Ok(new
    {
        total = videos.Count,
        added = upBulk.Added,
        duplicated = upBulk.Duplicated,
        message = $"UP 主整架签收：翻到 {videos.Count} 件{truncated}，新进站 {upBulk.Added} 条，来过 {upBulk.Duplicated} 条",
    });
});

// ─── 下载控制面板：暂停/继续/取消 + 队列快照 ──────────────────────────
// 暂停是全站闸：提交停投 + 在途引擎任务挂起；取消只动排队区（还没碰引擎的行，零成本）。
app.MapGet("/api/downloads/queue", async (DownloadPauseBoard board, StationDb db, IDownloadTaskLookup lookup, CancellationToken cancellationToken) =>
{
    // 加档行多透三层实况：让路缘由与回炉倒计时（waiting）、引擎在途进度（有任务号才查得到）、
    // 让路次数——站长不用猜「排队中在排什么」，面板直接把账摆开。
    var codecRows = new List<object>();
    foreach (var c in db.GetCodecQueueRows())
    {
        object? progress = null;
        foreach (var taskId in StatusSyncWorker.ParseTaskIds(c.Extra.TaskIdsJson))
        {
            var task = await lookup.FindAsync(new DownloadTaskId(taskId), cancellationToken);
            if (task is null)
            {
                continue; // 已搬历史库，没得可报
            }

            progress = new
            {
                phase = task.Phase.ToString(),
                percentage = task.Progress.Percentage,
                downloadedText = task.Progress.DownloadedSizeText,
                totalBytes = task.Progress.TotalBytes,
                speedText = task.Progress.SpeedText,
            };
            break;
        }

        codecRows.Add(new
        {
            videoId = c.VideoId,
            bvid = c.Bvid,
            title = c.Title,
            codec = c.Codec,
            pageIdx = c.PageIdx,
            waiting = c.Waiting,
            measured = c.Measured,
            retryCount = c.Extra.RetryCount,
            hint = c.Extra.LastError,
            retryInSec = c.Extra.RetryAtUtc is { } retryAt
                ? Math.Max(0, (int)(retryAt - DateTimeOffset.UtcNow).TotalSeconds)
                : (int?)null,
            progress,
        });
    }

    return Results.Ok(new
    {
        paused = board.IsPaused,
        intervalSec = board.IntervalSec,
        downloading = db.GetDownloadingRows()
            .Select(r => new { id = r.Id, bvid = r.Bvid, title = r.Title, coverUrl = r.CoverUrl }),
        pending = db.GetPendingQueueItems()
            .Select(i => new { id = i.Id, bvid = i.Bvid, title = i.Title, coverUrl = i.CoverUrl }),
        // 加档账在 video_codecs 表，上面两块只认 videos 表，所以这块面板一直看不见它们；
        // waiting=true 是「判重让路、排队等回炉」，false 是「已交给提交链进站中」；
        // measured 是盘上实测编码（定档那一刻探回来的，与点单的 codec 对不上就是挂羊头卖狗肉）。
        // 既有四块的形状与语义不动（外面还有别的代码在吃），只往加档行里添新字段。
        codecs = codecRows,
        // 已取消遗留数：面板「清理已取消」按钮上的数，清了进回收站可反悔
        canceledCount = db.CountCanceledVideos(),
    });
});

// 逐件提交间隔：白名单 0/60/300/600/1800 秒，实际等待带 ±10% 随机抖动，改完下一件就生效。
app.MapPost("/api/downloads/interval", (SetDownloadIntervalRequest request, DownloadPauseBoard board) =>
{
    if (!board.SetInterval(request.Seconds))
    {
        return Results.BadRequest(new { message = "间隔只认这些档：极速、1 分钟、5 分钟、10 分钟、30 分钟" });
    }

    var label = request.Seconds == 0 ? "极速（5~15s）" : $"{request.Seconds / 60} 分钟";
    return Results.Ok(new { message = $"逐件间隔切到 {label}，已掺 ±10% 随机抖动" });
});

app.MapPost("/api/downloads/pause", async (DownloadPauseBoard board, CancellationToken cancellationToken) =>
    Results.Ok(new { message = await board.PauseAllAsync(cancellationToken) }));

app.MapPost("/api/downloads/resume", async (DownloadPauseBoard board, CancellationToken cancellationToken) =>
    Results.Ok(new { message = await board.ResumeAllAsync(cancellationToken) }));

app.MapPost("/api/downloads/cancel", (CancelDownloadsRequest request, StationDb db) =>
{
    var canceled = db.CancelPendingVideos(request.VideoIds is { Count: > 0 } ids ? ids : null);
    return Results.Ok(new
    {
        canceled,
        message = canceled > 0 ? $"取消了 {canceled} 件排队馆藏" : "排队区没有可取消的馆藏",
    });
});

// ─── 台账备份：station.db 快照的档案柜 ──────────────────────────────
app.MapGet("/api/backups", (LedgerBackup backup) => Results.Ok(new
{
    intervalHours = backup.IntervalHours,
    keep = backup.Keep,
    lastBackupUtc = backup.LastBackupUtc(),
    files = backup.List().Select(f => new { name = f.Name, bytes = f.Bytes, modifiedUtc = f.ModifiedUtc }),
}));

app.MapPost("/api/backups/run", (LedgerBackup backup) =>
{
    var name = backup.Run();
    return Results.Ok(new { message = $"台账快照已落柜：{name}" });
});

app.MapPost("/api/backups/config", (BackupConfigRequest request, LedgerBackup backup) =>
{
    var okInterval = request.IntervalHours is null || backup.SetInterval(request.IntervalHours.Value);
    var okKeep = request.Keep is null || backup.SetKeep(request.Keep.Value);
    return okInterval && okKeep
        ? Results.Ok(new { message = "备份安排已更新" })
        : Results.BadRequest(new { message = "备份安排只认这些档（6/24/72/168 小时；保 7/14/30 份）" });
});

app.MapDelete("/api/backups/{name}", (string name, LedgerBackup backup) =>
    backup.Delete(name) ? Results.Ok(new { message = "快照已销" }) : Results.BadRequest(new { message = "没有这个快照" }));

// 节流闸状态：站长室展示风控冷却倒计时。
app.MapGet("/api/station/throttle", (LbsApiThrottle throttle) => Results.Ok(new
{
    coolingDown = throttle.IsCoolingDown,
    remainingSeconds = throttle.CooldownRemaining is { } left ? Math.Ceiling(left.TotalSeconds) : (double?)null,
}));

// 站长公开名片：昵称+头像（B 站 CDN 链，本就公网可见），没站长席位回 null。
// 这份数以前由 /api/login/status 顺手从 B 站 nav 嗂进 KV 缓存；站级登录态退役后直接吃站长席位台账，
// 少一层缓存也少一处「站长换了脸、分站还顶着旧头像」的 stale。
app.MapGet("/api/station/profile", (StationDb db) => Results.Ok(db.GetOwnerSeatCard()));

// 采集号情报（站长门内）：站里的活——评论/弹幕归档、UP 名片、动态、编码加档——此刻在用谁的号。
// 分站已无站级号，这份号从成员池里取（站长席位的活号优先），所以要说清是「站长自己的号」还是
// 「借了哪位成员的号」，并把停摆多久一并报给柜台标灰。
app.MapGet("/api/stats/collect", (LbsCollectPolicy collect, StationDb db) =>
{
    var account = collect.CurrentAccount();
    var ownerSeat = db.OwnerSeatMemberId();
    return Results.Ok(new
    {
        hasAccount = account is not null,
        mid = account?.AccountMid,
        name = account?.AccountName,
        face = account?.FaceUrl,
        isOwnerSeat = account is not null && ownerSeat is long seat && account.MemberId == seat,
        stoppedSinceUtc = collect.LastNoAccountUtc,
    });
});

// 站长室页头总览：台账聚合 + 磁盘体积（一次加载扫一遍，不轮询）。
app.MapGet("/api/admin/stats", (StationDb db) => Results.Ok(db.AdminStats()));

// ─── 订阅更新 ──────────────────────────────────────────────────────
// 订阅台账存 settings KV（JSON 数组）：收藏夹链接或博主 MID 登记在此。
// 爬工 SubscriptionCrawler 每小时巡一轮，新 BV 走投稿路径自动进站（去重靠台账）。
app.MapGet("/api/subscriptions", (StationDb db) => Results.Ok(ReadSubscriptions(db)));

app.MapPost("/api/subscriptions", (SubscriptionRequest request, StationDb db) =>
{
    var kind = request.Kind.Trim().ToLowerInvariant();
    var target = request.Target.Trim();
    if (kind is not ("fav" or "up") || target.Length == 0)
    {
        return Results.BadRequest(new { message = "kind 只能是 fav/up，内容不能为空" });
    }

    var list = ReadSubscriptions(db);
    if (list.Any(s => s.Kind == kind && s.Target == target))
    {
        return Results.Conflict(new { message = "这个已经订阅过了" });
    }

    list.Add(new SubscriptionItem(kind, target, DateTimeOffset.UtcNow.ToString("O")));
    db.SetSetting("subscriptions", JsonSerializer.Serialize(list));
    return Results.Ok(list);
});

app.MapDelete("/api/subscriptions", (string? kind, string? target, StationDb db) =>
{
    var list = ReadSubscriptions(db);
    var removed = list.RemoveAll(s => s.Kind == kind && s.Target == target);
    db.SetSetting("subscriptions", JsonSerializer.Serialize(list));
    return Results.Ok(new { removed, list });
});

// 站长室「立刻爬一轮」：与每小时例行共用同一个爬工实例，串行闸保证不会两趟同跑。
app.MapPost("/api/subscriptions/crawl", async (SubscriptionCrawler crawler, CancellationToken cancellationToken) =>
    Results.Ok(await crawler.CrawlOnceAsync(cancellationToken)));

// 最近一轮爬取报告（settings KV），站长室卡片展示用；没爬过回 null。
app.MapGet("/api/subscriptions/crawl-status", (StationDb db) =>
{
    var json = db.GetSetting("subscription-crawl");
    if (string.IsNullOrWhiteSpace(json))
    {
        return Results.Ok(null);
    }

    try
    {
        return Results.Ok(JsonSerializer.Deserialize<SubscriptionCrawlReport>(json));
    }
    catch (JsonException)
    {
        return Results.Ok(null);
    }
});

static List<SubscriptionItem> ReadSubscriptions(StationDb db) =>
    db.GetSetting("subscriptions") is { } json && !string.IsNullOrWhiteSpace(json)
        ? JsonSerializer.Deserialize<List<SubscriptionItem>>(json) ?? new()
        : new();

// ─── 危险区：一键清空分站 ────────────────────────────────────────────────────
// 删视频库 + 缓存图 + 台账 + 引擎任务表，站子回到空仓；
// 保留：成员席位与各自的 B 站账号池（member_accounts）、下载设置（settings KV + 引擎 json）。
// （旧注释里的「主/副登录（cookies 盘）」已随站级登录态摘除，data/cookies 不再存任何站级 cookie。）
app.MapPost("/api/admin/purge", async (PurgeRequest request, StationDb db, DataPaths paths, IDownloadTaskStateService state, IDownloadTaskLookup lookup, IDownloadTaskQueue taskQueue, CancellationToken cancellationToken) =>
{
    if (!request.Confirm)
    {
        return Results.BadRequest(new { message = "要带 confirm=true 才执行：这会把视频库、缓存图、台账、引擎任务记录全清掉，不可撤销" });
    }

    // 1. 先安顿引擎在途/排队的任务，不然清完站它们还会继续往硬盘写字节。
    var cancelledTasks = 0;
    foreach (var task in await lookup.GetUnfinishedAsync(cancellationToken))
    {
        try
        {
            try
            {
                await taskQueue.CancelAsync(task.Id);
            }
            catch (Exception)
            {
                // 没在跑的任务取消不掉无妨
            }

            await state.CancelAsync(task.Id, cancellationToken);
            await state.DeleteAsync(task.Id, cancellationToken);
            cancelledTasks++;
        }
        catch (Exception)
        {
            // 取消不动不拦清站：账已销，孤儿任务重启时自清。
        }
    }

    // 2. 引擎自己的任务台账也清掉，免得清完重新投稿被「已存在」误拦。
    var engineRows = LbsEnginePurge.PurgeEngineStorage(paths.Downkyi);

    // 3. 分站台账五表全销（settings KV 保留）。
    var ledgerRows = db.PurgeAllLedger();

    // 3.5 回收站连带拍空：站都清了，留着封存封也无人能对号入座（恢复回去只会碰上空库）。
    var recycledRows = db.PurgeAllRecycle(paths.Recycle);

    // 4. 资源文件清盘：视频库 + 缓存小图区 + 预览成片区 + e2e 临时件。cookies 与引擎设置不碰。
    var (files, bytes, busy) = PurgeDirectoryTree(paths.Videos);
    foreach (var dir in new[] { paths.Avatars, paths.Emotes, paths.CmtImg, paths.Cache, paths.Previews })
    {
        var (f, b, z) = PurgeDirectoryTree(dir);
        files += f;
        bytes += b;
        busy += z;
    }

    var message = $"站已清空：取消 {cancelledTasks} 个引擎任务，销 {ledgerRows} 行台账、{engineRows} 行引擎历史，清掉 {files} 个文件（{(double)bytes / 1048576:F1} MB）"
        + (recycledRows > 0 ? $"，回收站 {recycledRows} 件一并销毁" : "");
    if (busy > 0)
    {
        message += $"；{busy} 个文件被占用没舍得删，回头再按一次";
    }

    return Results.Ok(new { message });

    static (int Files, long Bytes, int Busy) PurgeDirectoryTree(string root)
    {
        var (files, bytes, busy) = (0, 0L, 0);
        if (!Directory.Exists(root))
        {
            return (0, 0L, 0);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                files++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                busy++;
            }
        }

        // 空壳目录扫干净（里头还有文件的层会自然留下）。
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(static d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 占用中的空目录留给下次
            }
        }

        return (files, bytes, busy);
    }
});

app.MapGet("/api/settings", (DataPaths paths) => Results.Ok(new
{
    dataDir = paths.Root,
    skin = new { bare = false, note = "裸分站皮肤 M4 到站" },
}));

// 认不出的 /api/* 给个正经 404 脸：以前拼错的地址会被下面的兗底兜到 index.html，
// 前端 res.json() 炸得很难看。挂在所有端点之后、SPA 兗底之前，只有没被认领的 /api 才会落这里。
app.Map("/api/{**rest}", () =>
    Results.Json(new { message = "没有这个接口" }, statusCode: StatusCodes.Status404NotFound));

if (File.Exists(Path.Combine(webRoot, "index.html")))
{
    app.MapFallbackToFile("index.html");
}

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LBS");

logger.LogInformation("小破分站开站：{Urls}，数据目录 {DataDir}，前端 {WebRoot}", urls, dataPaths.Root, webRoot);
app.Run();

internal sealed record HealthInfo(bool Ok, string Name, string Version, string DataDir, DateTimeOffset ServerTimeUtc);
internal sealed record AddVideoRequest(string Url);
internal sealed record FavoritesImportRequest(string Content);
internal sealed record FavoritesUrlImportRequest(string Url);
internal sealed record UpImportRequest(string Url);
internal sealed record CancelDownloadsRequest(IReadOnlyList<long>? VideoIds);
internal sealed record SetDownloadIntervalRequest(int Seconds);
internal sealed record GateRequest(string? Password);
internal sealed record BackupConfigRequest(int? IntervalHours, int? Keep);
internal sealed record PurgeRequest(bool Confirm);
internal sealed record SubscriptionItem(string Kind, string Target, string AddedAtUtc);
internal sealed record SubscriptionRequest(string Kind, string Target);
internal sealed record LocalDanmakuRequest(int PageIdx, double T, string Text);
internal sealed record SubsRequest(long Rpid, int Page);
internal sealed record CodecVersionRequest(string? Codec);

internal sealed record CancelCodecRequest(string? Codec, int? PageIdx);
