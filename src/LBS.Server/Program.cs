using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System.Net;
using System.Threading.RateLimiting;

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
builder.Services.AddSingleton<SubtitleArchiveService>(); // 稿件设置窗那枚「重拉字幕存档」（全轨回拉 + 重扫登账）
builder.Services.AddSingleton<MediaRefreshService>(); // 稿件设置窗那枚「重拉封面与媒体」（档案重取 + 封面重抓 + 缺件回炉）
builder.Services.AddSingleton<LbsCollectPolicy>();
// 加档前的「预估」探子（playurl 标称码率与体积）：无状态，单例就够；出号与节流都交给现成那一套。
builder.Services.AddSingleton<CodecEstimateService>();
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
// 封面缩略补员：把有封面却没缩略的稿件（包括存量全部老货）按档位本地裁出来。
// 开机 15 秒后开切、一轮 40 条，还有欠账就连轴转，切干净了才退到每 5 分钟看一眼（站长 11-03 拍板要全量回填）。
builder.Services.AddHostedService<CoverThumbBackfillWorker>();
// 动态配图补员：九宫格没落盘（pics_local_json 为 NULL 或含 null 洞）的动态，每小时一轮从 pics_json 补下原图
// （每轮封顶 40、条间 300ms，只写 pics_local_json 一列）。整轮挂采集号出号上下文，理由见类注释。
builder.Services.AddHostedService<DynamicPicBackfillWorker>();
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
// 分站 AI（成员自带干粮版）：每人自己的 key/model 存 member_ai 表，站级设定（agent-* KV）已退役；
// LlmClient 单例共享一张嘴（静态 HttpClient 流式与试炮共用），门只一色：MemberGate。
builder.Services.AddSingleton<LlmClient>();
// 台账备份员：station.db 在线快照到 data/backups，周期巡工 + 站长室手动补拍。
builder.Services.AddSingleton<LedgerBackup>();
// 数据交换：勾选式导出打包（大包走内存任务牌）+ 导入暂存（开机换库）。
builder.Services.AddSingleton<StationExporter>();
builder.Services.AddSingleton<ExportJobBoard>();
// 缺件回炉闸门：先建档（默认关），开机换库真换了才拨闩——builder.Build() 之后集合只读，不能再 AddSingleton。
builder.Services.AddSingleton<MediaReconcileGate>();
builder.Services.AddHostedService<LedgerBackupWorker>();
builder.Services.AddHostedService<WeeklyReportWorker>(); // 周报发报员：周一 09:00（UTC+8）后给全站播一条周报公告
builder.Services.AddHostedService<ArrivalReportWorker>(); // 到站播报员：每 10 分钟看一眼，有无归属的新货到站就播一条通报
builder.Services.AddHostedService<UpFaceSweepWorker>(); // UP 巡脸员：跨周逐位去 card 口取一张脸，存 up_faces 区与台账（没采集号整轮欠着）
// 分站海马体：业务侧 Record 一下就走，抄录员后台攒批落 events 表，供站长与未来 AI 分析。
builder.Services.AddSingleton<EventRecorder>();
builder.Services.AddHostedService<EventRecorderWorker>();
// 扫码登录代理：B 站 qrcode 协议的服务端握手（前端只见状态，cookie 只进不出）。
builder.Services.AddSingleton<LbsQrLogin>();
// 成员门闸：放映厅与橱窗的分界线（与站长锁同一套哲学，门在数据上）。
builder.Services.AddSingleton<MemberGate>();
// 对外形状：稿件可见性一律小写字符串（枚举直发会变成 0/1，前端拿不到 "private" 就不显那枚药丸）。
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new VideoVisibilityJsonConverter());
});

// 下载引擎（移植自 downkyicore 的 headless 运行时）：启动时拉起下载 runtime 并自检 ffmpeg
builder.Services.AddLbsDownloadEngine(new EngineOptions(EngineOptions.DefaultUserAgent));

// 静态前端：开发期直接指向 web/dist（pnpm build 产物），发布镜像里拷进 wwwroot
// 注意：仅赋 WebRootPath 不足以让 StaticFileMiddleware 换目录，必须同时换 WebRootFileProvider
var devWebDist = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "web", "dist"));
// 应用本体的 wwwroot（镜像里是 /app/wwwroot，发布时把前端拷进去）：content root 跟 cwd 走，
// 容器 WORKDIR=/data 时相对定位会去伺服一个当场新建的空目录（Release 换 cwd 实测：/ 与 /admin 全 404）。
// 实到 exe 老家这一腿，把镜像里的前端接住；开发期 web/dist 优先的次序不变。
var sidecarWeb = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var webRoot = File.Exists(Path.Combine(devWebDist, "index.html"))
    ? devWebDist
    : File.Exists(Path.Combine(sidecarWeb, "index.html"))
        ? sidecarWeb
        : Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(webRoot);
builder.Environment.WebRootPath = webRoot;
builder.Environment.WebRootFileProvider = new PhysicalFileProvider(webRoot);

var urls = Environment.GetEnvironmentVariable("LBS_URLS") ?? "http://0.0.0.0:8090";
builder.WebHost.UseUrls(urls);

// 全局限流闸：只治端点（/api/*），静态区（封面/正片/弹幕 json）不走端点管线不受限。
// 匿名薄、登录宽：管理员室轮询密集不能误伤，匿名写路径只够正经人手速。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("""{"error":"rate-limited","message":"手速太快了，歇一会儿再试"}""", token);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // 「登录宽」那一档得拿真实席位当凭据，不能看 cookie 名字：lbs_dmid 是任何人都能自己
        // 在浏览器里塑一张的访客弹幕签（lbs_admin 同理），拿它当登录凭据等于把
        // 「匿名写 20/分钟」的闸门交给攻击者自己拧到 300/分钟。
        // 盖章中间件（成员门）就挂在限流闸之前且只对 /api/ 生效，走到这里的请求要么已被
        // MemberGate.Stamp 塞过 Items，要么就是真访客——Current 只读 Items，零额外库查。
        var authed = MemberGate.Current(http) is not null;
        var write = HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method)
            || HttpMethods.IsDelete(http.Request.Method) || HttpMethods.IsPatch(http.Request.Method);
        var (permit, queue) = (authed, write) switch
        {
            (false, true) => (20, 0),     // 匿名写：弹幕私信投稿的钓鱼脚本在这里碰壁
            (false, false) => (300, 20),  // 匿名逛：首屏卡阵 + 无限滚动的余量
            (true, true) => (300, 20),    // 登录写：站长室批量设置手速如飞
            (true, false) => (1200, 50),  // 登录读：队列/进度轮询密集，别拿它开刀
        };
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{ip}|{(authed ? "a" : "g")}|{(write ? "w" : "r")}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1), QueueLimit = queue, AutoReplenishment = true });
    });
});

var app = builder.Build();

// 反代身后（nginx/Caddy）全站 IP 判定（小黑屋、登录限次、扫码票、限流）看到的都是代理机：
// 不配信任区，小黑屋变成「任何人输错 6 次锁全站」，扫码 IP 绑定形同虚设。
// 默认完全不信任（局域网直连照旧）；挂反代请配 LBS_TRUSTED_PROXY_CIDRS（逗号分隔，如 127.0.0.1/32,192.168.0.0/16）。
var trustedProxyCidrs = (Environment.GetEnvironmentVariable("LBS_TRUSTED_PROXY_CIDRS") ?? "").Trim();
if (trustedProxyCidrs.Length > 0)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    foreach (var piece in trustedProxyCidrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        try
        {
            // .NET 10 口径：KnownNetworks(IpNetwork) 已废，新闸眼 KnownIPNetworks 吃 System.Net.IPNetwork（地址+前缀长）。
            var slash = piece.LastIndexOf('/');
            if (slash <= 0)
            {
                throw new FormatException("缺 /前缀长");
            }

            forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(
                IPAddress.Parse(piece[..slash]), int.Parse(piece[(slash + 1)..])));
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning("LBS_TRUSTED_PROXY_CIDRS 条目 {Cidr} 不是合法 CIDR（如 192.168.0.0/16），已跳过：{Message}", piece, ex.Message);
        }
    }

    app.UseForwardedHeaders(forwarded);
    app.Logger.LogInformation("反代信任区已启用：{Cidrs}（客户端 IP 取自 X-Forwarded-For）", trustedProxyCidrs);
}

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

app.MapGet("/api/health", () => Results.Ok(new HealthInfo(
    Ok: true,
    Name: "小破分站",
    Version: Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0",
    ServerTimeUtc: DateTimeOffset.UtcNow)));

app.MapEngineSettingsApi();
app.MapShareLinkEndpoints();
app.MapAdminRecycleEndpoints();
app.MapPreviewApi();
// 分站 AI：成员侧 status/chat（SSE）+ 站长室 settings/test（/api/admin/ 前缀由站长门盖兵）。
app.MapAgentEndpoints();

// 卡阵与搜索都递看客尺子：站长全见 / 席位看公开 + 自己的 / 访客只看公开（递错了就是 fail-closed：私有稿连本人与站长都看不见）。
app.MapGet("/api/videos", (StationDb db, HttpContext http) =>
    Results.Ok(TagManageable(db, db.ListVideos(ViewerScope(http)), http)));

// UP 空间：放映厅名片上那颗蓝钮的去向。只给已到货的行（没缓存的不给人看），
// 页头吃 video_meta 的站内快照 —— 这一页不替站长出网：老稿没采过名片就画默认小人与「还没有签名」，
// 要补齐走稿件设置里现成的「重拉封面与媒体」。门禁在 MemberGate 点名（与 /api/collections/{id} 同权），
// 前端路由同向挂 guestBlocked —— 两端必须同向，不然就是「访客点得到钮却吃 401」。
app.MapGet("/api/up/{mid:long}/videos", (long mid, StationDb db, HttpContext http) =>
    Results.Ok(new
    {
        up = db.GetUpProfileByMid(mid),
        items = TagManageable(db, db.ListVideosByUpMid(mid, ViewerScope(http)), http),
    }));

// UP 历史脸档案：这位在本站存过的历张脸（周巡脸员攒下的），按再见时刻新者在前。
// 台账说有过 ≠ 盘上还有：站长手删过 data/up_faces、导包只带了台账都会留下幽灵行，所以逐张验货
// （File 在位且 Length>0 才算有件），无档案回空数组（前端当「没历史」，不是一条错误）。门禁已在 MemberGate 点名 /api/up/ 前缀 GET。
app.MapGet("/api/up/{mid:long}/faces", (long mid, StationDb db, DataPaths paths) =>
    Results.Ok(db.ListUpFaces(mid).Where(f => HasUpFace(paths.UpFaces, f.FaceRel)).ToList()));

/// <summary>历史脸验货（端点专用）：件名只认 <c>/up_faces/{裸文件名}</c> 这一种形状 —— 越界（.. / 绝对路径 / 子目录）与认不出的一律当没件，
/// 0 字节的半截壳也当没件（与巡脸侧「0 字节归缺件」同一把尺）。</summary>
static bool HasUpFace(string dir, string faceRel)
{
    const string prefix = "/up_faces/";
    if (string.IsNullOrEmpty(faceRel) || !faceRel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var leaf = faceRel[prefix.Length..];
    if (leaf.Length == 0 || leaf.Contains('/') || leaf.Contains('\\') || leaf.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(leaf))
    {
        return false;
    }

    var file = new FileInfo(Path.Combine(dir, leaf));
    return File.Exists(file.FullName) && file.Length > 0;
}

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

    var hits = db.SearchVideos(trimmed, sort, 50, ViewerScope(http));
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
// 缩略件用 webp，不赌框架默认字典（这台机器上没能直接验成），万一映射不上会退成 octet-stream。
// mediaTypes 是五个静态区共用的实例，这里补一行，五个前缀一起好。
mediaTypes.Mappings[".webp"] = "image/webp";

// 中转半成品一律不许从静态区溜出去。实测（本轮）：往 data/videos 丢一个 probe.part，
// GET /media/probe.part → 200 并能拿到整份字节；同位的 .hidden.mp4 倒是 404（中间件不答理点开头）。
// 而这类件是真会出现在途时窗里的：封面补员在视频库内写 <名>.jpg.part（CoverBackfillWorker:80），
// 弹幕刷新在原地摆 <名>.json.refreshing（DanmakuArchiveService:346），预览转码写 <bvid>.mp4.part。
// 这一道闸摆在所有静态区之前（/media 的 UseStaticFiles 在后面，后面的闸拦不住它），各区的 OnPrepareResponse 作补保。
// 静态区反查的「件 → 稿件 id」缓存（进程内，只服这一道闸与两处补保）：首页一屏上百张封面，逐条问库不划算。
var mediaOwnerCache = new MediaOwnerCache();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var onStatic = path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/previews/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/thumbs/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/cache/", StringComparison.OrdinalIgnoreCase);
    if (onStatic && IsStaleTransientPath(path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // 私有稿件的字节这期一起挡（与上面那道半成品闸并列，各判各的，半成品那条的语义一根手指不碰）。
    if (PrivateMediaBlocked(context))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // 封面图的 revalidation（两道闸都放行之后才轮得着它）：命中 If-None-Match 就地 304。
    // 只对 /media 下的图片扩展名生效，正片与音轨一个字节都不掺（那边有分轨 range 请求）。
    if (MediaCachePolicy.TryHandleNotModified(context, dataPaths))
    {
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
            return;
        }

        // 补保：统一闸若被绕过（将来有人再添一处伺服 data/videos 的 UseStaticFiles），这里照样挡。
        if (PrivateMediaBlocked(ctx.Context))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentType = null;
            ctx.Context.Response.Headers.ContentLength = null;
            return;
        }

        // 封面图走 revalidation（与上面两处是叠加：它们把响应打成 404 就直接 return，不会给拒掉的响应贴缓存头）。
        // 文件名是确定性推导的（换内容不换 URL），不贴 no-cache 浏览器会一直给你旧图；视频与音频一律不动。
        MediaCachePolicy.PrepareResponse(ctx.Context, ctx.File);
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

// UP 历史脸档案区（周巡脸员落件，切脸列表的相册与本机钦选都从这里取图）：
// 与 /avatars 同一把尺——台账说有过 ≠ 盘上还有，死链由端点验货与前端 @error 双层兑底。
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPaths.UpFaces),
    RequestPath = "/up_faces",
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
            return;
        }

        // 补保（与 /media 那侧同形）：私有稿的悬停预览件也不许从静态区溜出去。
        if (PrivateMediaBlocked(ctx.Context))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentType = null;
            ctx.Context.Response.Headers.ContentLength = null;
        }
    },
});

// 封面缩略件区（站长 11-03「缩略图要本站自己从本地原图裁，不许靠 B 站 CDN 缩放」）：data/thumbs → /thumbs。
// 与 /previews 同形（派生物、独立目录、两道闸补保），差别只有两处：
// 一、缓存走 immutable 长缓存。文件名里带原图版本戳（<bvid>_<档>-<版本>.<后缀>），原图重拉过一次 URL 就变，
//     同一个地址永不改内容，所以不必像 /media 的封面那样每次进站都补一轮 revalidation（首页一屏 40 张就是 40 个来回）。
// 二、私有稿件必须挡住。缩略件根本不在 artifacts 里，归属由 cover_thumbs 台账那条腿认（见 MediaRelTables 第四条），
//     忘了补前缀分支就是 100% fail-open，所以这道分支与生成器同一次提交，不许拆开发车。
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPaths.Thumbs),
    RequestPath = "/thumbs",
    ContentTypeProvider = mediaTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    OnPrepareResponse = ctx =>
    {
        if (IsStaleTransientPath(ctx.Context.Request.Path.Value ?? ""))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentType = null;
            ctx.Context.Response.Headers.ContentLength = null;
            return;
        }

        if (PrivateMediaBlocked(ctx.Context))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentType = null;
            ctx.Context.Response.Headers.ContentLength = null;
            return;
        }

        ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    },
});

// 静态区私有件判定（只管 /media/、/previews/、/thumbs/ 三支，其余前缀一概放行）。
// 判定链：剥前缀 → 百分号解码（中文文件名不解码对不上台账，白挡）→ 反斜杠归一成 / → 认稿件 id →
// 台账不认这个件（头像/表情/评论图那类根本不在这些表里）就放行 —— null 绝不能当私有，否则全站公共件一起变黑；
// 认出来了才问 CanViewVideo，不过就 404（与半成品闸同一副面孔，不说「私有」二字）。
// 反查走 mediaOwnerCache（参数与「转私后最多 60 秒内必须挡住」的硬约束写在类注释里）。
bool PrivateMediaBlocked(HttpContext context)
{
    var path = context.Request.Path.Value ?? "";
    string rel;
    if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
    {
        rel = path["/media/".Length..];
    }
    else if (path.StartsWith("/previews/", StringComparison.OrdinalIgnoreCase))
    {
        rel = path["/previews/".Length..];
    }
    else if (path.StartsWith("/thumbs/", StringComparison.OrdinalIgnoreCase))
    {
        // 缩略件与悬停预览同等待遇：都是派生物，都住在视频库外，私有稿一样不许透。
        rel = path["/thumbs/".Length..];
    }
    else
    {
        return false;
    }

    // Kestrel 递给 Path 的串本已解过一道码；这一手是防哪一环把中文/空格原样透传过来（不解就匹配不上台账，白挡）。
    // 文件名里真带 % 的件不多，且解不动的转义序列 Uri 会原样留着，不为这个把挡门拆了。
    rel = Uri.UnescapeDataString(rel).Replace('\\', '/');
    var db = context.RequestServices.GetRequiredService<StationDb>();
    if (mediaOwnerCache.Resolve(rel, db.GetVideoIdByMediaRel) is not { } videoId)
    {
        return false;
    }

    // 身份得自己从 cookie 认：这道闸跑在会员盖章（下面那道 gate.Stamp）之前，MemberGate.Current 在这儿永远是 null——
    // 直接拿 Current 问就等于把所有人都当访客，私有稿的主人连自己的封面与正片都取不回（实测踩过：站长与投稿人本人一律 404）。
    var member = context.RequestServices.GetRequiredService<MemberGate>().Resolve(context.Request);
    return !ViewerGate.CanView(db, videoId, member);
}

// 静态区只认成品：在途中转件（.part/.refreshing/.tmp）与点开头的隐藏件都不算「本站有的东西」。
// 正片、封面、弹幕 json、字幕、预览件的真名字永远不会长成这些样子（预览区只认 <bvid>.mp4），所以误伤面为零。
static bool IsStaleTransientPath(string path) =>
    path.Contains("/.", StringComparison.Ordinal)
    || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".refreshing", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

// 成员门闸（先盖章后验门）：访客只逛橱窗（首页卡阵/搜索/封面），放映厅、弹幕、投稿一律要会员会话；
// 被拦访客的欲看而不得记 playback.blocked 供 AI 分析。
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    // 路由匹配默认忽略大小写，门径点名必须跟着忽略，否则 /API/... 绕过盖章与会员门（发布前实测坐实）。
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
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
                .Record(EventActors.Guest, null, EventNames.PlaybackBlocked, "path", path,
                    ip: context.Connection.RemoteIpAddress?.ToString());
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "member-required", message = "用站名密码登录后才能看视频、弹幕与评论区" });
            return;
        }
    }

    await next();
});

// 站长室门牌：身份即钥匙——只有站长（owner）的会员会话放行，口令锁依约退役（AdminGate 留档待公网复位）。
// 普通成员误打也拦下并指路个人空间；探头探脑的撞门声记 admin.blocked。
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
                message = forbidden ? "这里是站长室，普通成员请进个人空间" : "站长室仅限站长，请先扫码登录",
            });
            return;
        }
    }

    await next();
});

// 限流闸在两道门之后、所有端点之前：小黑屋与限流共用同一套 IP 口径（含上面的反代信任区）。
app.UseRateLimiter();

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
        // 与会员会话同一张尺：TLS 下上 Secure（防中间人把站长会话 cookie 扫走），局域网 http 直连不误伤。
        // .NET 10 的 CookieOptions 已没有 SecurePolicy，SameAsRequest 得自己算；反代身后 Request.IsHttps
        // 由 X-Forwarded-Proto 撑腰（信任区见 LBS_TRUSTED_PROXY_CIDRS），不会把 https 站点误判成明文。
        Secure = http.Request.IsHttps,
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

    // 口令闸门交给 AdminGate 自己把守（下限八位、PBKDF2 落库），这里只负责把不合格挡成 400，
    // 不让 ArgumentException 裸奔到客户端（全站无 exception handler，500 回栈不是风格）。
    if (!gate.TrySetPassword(request.Password, out var lockError))
    {
        return Results.BadRequest(new { message = lockError ?? "口令不合格" });
    }

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
// 弹幕回收站（11-08）：单删的弹幕在这里点名、恢复、销毁（与馆藏回收站同属站长话语权）。
app.MapAdminDanmakuRecycleEndpoints();
app.MapAdminCodecRecycleEndpoints(); // 加档版本回收站（11-08 站长拍板）：删掉的加档连行带件封在这里，整组恢复 / 整组销毁都只站长按得动
app.MapMemberDanmakuRecycleEndpoints(); // 成员回收站「我收回的弹幕」段：自己收回的那几条自己恢复，不必求站长（入口已搬进头像菜单的回收站弹窗）
app.MapMemberRecycleEndpoints(); // 成员回收站弹窗（11-16 站长拍板）：自己删的视频在这里点名/恢复/销毁（销毁=盖隐藏戳，真销毁只站长握）
app.MapWeeklyReportEndpoints(); // 分站周报：成员登录后 /api/weekly 看本周战报（/weekly 页）
app.MapVideoHistoryEndpoints(); // 观看历史：报看片进度、续播点位、个人空间历史列表
app.MapMemberWallApi(); // 会员墙（馆藏壁）：投稿+历史聚合，喂个人空间顶上的画布窗口
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

app.MapGet("/api/video/{bvid}", (string bvid, StationDb db, LbsUpInfoFetcher upInfo, LbsCollectPolicy policy, ILoggerFactory loggerFactory, HttpContext http) =>
{
    // BV 号是大小写敏感的 base62，先原样精确查，再补一记大写化兜底（照顾手打习惯）。
    // 尺子递到这里：私有稿在别人那一趟就是 null，与「查无此片」同一张 404 脸、同一句话（不许回 403，
    // 不给「有这稿但不给你看」的信号）；本人/站长拿到的载荷顶层带 visibility（放映厅那枚「私有」药丸读它）。
    var input = bvid.Trim();
    var scope = ViewerScope(http);
    var data = db.GetPlaybackData(input, scope) ?? db.GetPlaybackData(input.ToUpperInvariant(), scope);
    if (data is not null)
    {
        BackfillUpCard(db, upInfo, policy, loggerFactory, dataPaths, data);
        BackfillStat(db, upInfo, policy, loggerFactory, data);
    }

    return data is null
        ? Results.NotFound(new { message = "馆藏里没有这个 BV 号" })
        // 能不能重拉快照/删档，服务端算好一个布尔带下去（与站内弹幕 canDelete 同一范式：
        // 前端拿不到投稿人身份，藏钮只是体验，端点里那一遭才是真门）。
        : Results.Ok(data with
        {
            // 能不能重拉快照/删档，服务端算好一个布尔带下去（与站内弹幕 canDelete 同一范式：
            // 前端拿不到投稿人身份，藏钮只是体验，端点里那一遣才是真门）。
            // IsStationOwner 同样在端点算（数据层不认身份）：稿件设置面板拿它决定显不显站长那一组（重新排队 / 强制重下 / 加档）。
            CanManage = CanManageVideo(http, db, data.Id),
            IsStationOwner = MemberGate.IsOwner(MemberGate.Current(http)),
        });
});

// 接下来播放：同 UP 主优先、层内随机（库里同 UP 主上百支，按 id 倒序会永远只递那几张新面孔），卡片形状与列表接口一致。
// 尺子同样递进去：补位不从这位看不着的货里挑（私有稿连「被推荐」这条缝也不留）。
app.MapGet("/api/videos/{id:long}/related", (long id, StationDb db, HttpContext http) =>
{
    var related = db.GetRelatedVideos(id, 8, ViewerScope(http));
    return Results.Ok(related.Select(v => new
    {
        v.Id,
        v.Bvid,
        v.Title,
        CoverUrl = v.CoverUrl,
        // 本地封面与缩略档位一起递：放映厅「接下来播放」过去只能拿远程原图（尺寸 141×80），
        //  SQL 里早就查了本地件却没往外给（站长 11-03「所有缩略图要本地原图裁切」）。
        LocalCover = v.LocalCover,
        CoverThumbs = v.CoverThumbs,
        v.UpName,
        // mid 也得递：这张卡上的 UP 名要能点进站内 UP 空间（缺了它前端只能画成死文本）。
        v.UpMid,
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
void BackfillUpCard(StationDb db, LbsUpInfoFetcher upInfo, LbsCollectPolicy policy, ILoggerFactory loggerFactory, DataPaths paths, PlaybackData data)
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
            // official 不传同老口径（SetUpInfo 对 null 写 NULL）：整趟挂了就销 face 那一笔，下轮重采。
            db.SetUpInfo(
                data.Id,
                profile?.FaceRel ?? string.Empty,
                profile?.Sign,
                profile?.Fans,
                profile?.Official);
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

// 互动数据（点赞/投币/收藏/分享）与创作团队的后补：未销账且没采过时，开页异步采一次（一发 view 拿齐）；
// 成败都销账，不反复扰 B 站。新货入站时 StatusSyncWorker 已先采过一轮，这里只接漏网的。
void BackfillStat(StationDb db, LbsUpInfoFetcher upInfo, LbsCollectPolicy policy, ILoggerFactory loggerFactory, PlaybackData data)
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
                // 同一次 view 顺带带回的创作团队一并落账（联合创作稿才有，普通稿写 null）。
                db.SetVideoStaff(data.Id, PlaybackStaffCodec.ToJson(StaffMap.ToPlayback(stat.Staff)));
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

app.MapDelete("/api/videos/{id:long}", async (long id, HttpContext http, StationDb db, DataPaths paths, IDownloadTaskStateService state, IDownloadTaskLookup lookup, IDownloadTaskQueue taskQueue, IDownloadHistoryService history, EventRecorder events, CancellationToken cancellationToken) =>
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
    var recycled = db.MoveVideoToRecycle(id, paths.Videos, paths.Recycle, deleter?.Nickname ?? deleter?.Username ?? "站长", commentCount, deleter?.Id);
    if (recycled.Ok)
    {
        // 删档从此有账（11-08）：EventNames.VideoRemoved 这枚名字立了好几个月却是全仓零引用——
        // 「投稿人删自己的稿」这件事早就放开了，却一个字没记，只在 recycle_bin.deleted_by 里留了个显示名字符串。
        // 再往投稿人手里放开写侧口之前，先把这条账补直：谁删的、席号几号、带走几个文件、进了哪一号回收站。
        events.RecordWithPayload(MemberGate.IsOwner(deleter) ? EventActors.Admin : EventActors.Member,
            deleter?.Mid, EventNames.VideoRemoved, "video", row.Value.Bvid,
            new { videoId = id, binId = recycled.BinId, files = recycled.Files, status = row.Value.Status.ToString() },
            ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: deleter?.Id);
    }

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
app.MapPost("/api/videos/{id:long}/revive", async (long id, HttpContext http, StationDb db, LbsTaskReviver reviver, CancellationToken cancellationToken) =>
{
    // 归属门（站长已同意收）：这口以前只吃站长门（AdminGate 点名 /revive → 非站长一律 401/403），
    // 端点里本身不核归属——那份点名清单一旦漂走就是匿名可写。现在先核 CanManageVideo：
    // 站长在站长室点照样握得住（IsOwner 短路为真，见 ManageGate），别人多一道门，旧账不再留。
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    var (ok, message) = await reviver.ReviveAsync(id, cancellationToken);
    return ok ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// 重新排队：站长取消过的馆藏（Canceled）从卡阵里放回排队区；被引擎判为「已下过」的（Skipped）
// 走另一条道：挂上重下旗再回炉，否则同一把判重键会在门口再挡一次。其它状态不响应免得误复活。
app.MapPost("/api/videos/{id:long}/requeue", async (long id, HttpContext http, StationDb db, DownloadQueue queue, CancellationToken cancellationToken) =>
{
    // 归属门（站长已同意收）：与 /revive 同一道理——先核钥匙再碰台账，站长室自己那一圈调用不受影响（站长短路）。
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    var brief = db.GetVideoBrief(id);
    if (brief is null)
    {
        return Results.NotFound(new { message = "没有这条馆藏" });
    }

    // 重投判据正身在 RequeueGate（真值表钉在 RequeueClaimTests）： Failed 是新开的门，
    // 以前只有被取消的才准重排，一句「只有被取消的馆藏才需要重新排队」把失败行堵成死档。
    switch (RequeueGate.JudgeRequeue(brief.Value.Status, db.GetVideoSubmitterMember(id),
                MemberGate.IsOwner(MemberGate.Current(http))))
    {
        case RequeueGate.Verdict.NotRequeueable:
            return Results.BadRequest(new { message = "这状态不用重投（到站的、排队中的、下载中的都不在此列）" });
        case RequeueGate.Verdict.NoAccess:
            return Results.Json(new { error = "no-submitter", message = "这条稿子没有投稿人席位，借不到 B 站号，请让站长先把它认领了" },
                statusCode: StatusCodes.Status403Forbidden);
        case RequeueGate.Verdict.NeedClaim:
            // 带 requireClaim 回旗：前端拿它摆「挂你名下重投」那一枚，不拿它当普通错误报一句完事。
            return Results.Json(new { error = "need-claim", requireClaim = true, message = "这条稿子没有投稿人席位，先挂到你名下再重投" },
                statusCode: StatusCodes.Status409Conflict);
    }

    if (brief.Value.Status == VideoStatus.Skipped)
    {
        // Skipped 的成因在引擎那一侧：download_history 里有同名行就判无需下载。光重投还是原样被打回，
        // 所以这路必须带 RedownloadCompleted 走正门（引擎自己给加档重下用的就是这条），而不是伸手改它的库。
        // 挂牌必须在 ResetToPending 之后：那一步走状态机统一入口，会把这枚旗顺手清零。
        // 只从 Skipped 拉回：读到与落笔之间状态被另一笔改走（比如已被重投）就不硬勊。
        if (!db.ResetToPending(id, [VideoStatus.Skipped]))
        {
            return Results.BadRequest(new { message = "状态刚被另一笔操作改走，刷新看看再决定" });
        }

        db.MarkForceRedownload(id);
        await queue.EnqueueAsync(id, cancellationToken);
        return Results.Ok(new { message = "这馆先前被引擎判为已下过，已绕开判重重新排队（正片会重下一遍）" });
    }

    // Canceled 与 Failed 都从这儿拉回，各自带自己的乐观守卫（与上面 Skipped 同一道理，
    // 免得把刚被别处推走的行抢进排队）。Failed 走 ResetToPending 会连 last_error 与引擎任务 id 一并清掉，
    // 重投时判重看不见旧账，也不会把上一轮的错词再贴回去。
    var expectFrom = brief.Value.Status == VideoStatus.Failed
        ? new[] { VideoStatus.Failed }
        : new[] { VideoStatus.Canceled };
    if (!db.ResetToPending(id, expectFrom))
    {
        return Results.BadRequest(new { message = "状态刚被另一笔操作改走，刷新看看再决定" });
    }

    await queue.EnqueueAsync(id, cancellationToken);
    return Results.Ok(new { message = "已重新排队，下一班进站" });
});

// 认领（站长专属）：给一条无归属的稿子补上投稿人席位——DownloadWorker 那句「或由站长给这稿补上归属」
// 以才有门可走。补完顺手把 Failed 行重排（这是认领唯一的用场；别的状态只补归属不动状态）。
// 默认挂站长自己（借站长的号下货）；要挂给别的席位走查询串 ?memberId=。
// 为何 memberId 走查询串而不走 body：body 参数（哪怕声明成可空）会让框架给这条路由挂上「只收 application/json」的门槛，
// 不带 Content-Type 的请求（卡面那枚钮就是裸 POST，与 /revive /requeue 同一姿势）根本匹配不上这条路由，
// 会被下面的 /api/** 兜底吃成 404「没有这个接口」——不报 415 也不报 400，很难查（真机验过：带 CT 一切正常，
// 不带 CT 此口完全不可达）。这一口本来就没多少要传的，改成查询串后与同族两口一字同形，curl 一发即中。
app.MapPost("/api/videos/{id:long}/claim", async (long id, long? memberId, HttpContext http,
    StationDb db, DownloadQueue queue, EventRecorder events, CancellationToken cancellationToken) =>
{
    var me = MemberGate.Current(http);
    var isOwner = MemberGate.IsOwner(me);
    var targetId = memberId ?? me?.Id;
    var target = targetId is long seatId ? db.GetMemberById(seatId) : null;

    // 拒词由判据给（RequeueGate.JudgeClaim）；非站长一律 403，其余不合格 400。
    if (RequeueGate.JudgeClaim(isOwner, db.GetVideoSubmitterMember(id), target?.Status) is { } denial)
    {
        return Results.Json(new { message = denial },
            statusCode: isOwner ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden);
    }

    var brief = db.GetVideoBrief(id);
    if (brief is null)
    {
        return Results.NotFound(new { message = "没有这条馆藏" });
    }

    db.SetVideoSubmitterMember(id, target!.Id);
    var requeued = brief.Value.Status == VideoStatus.Failed && db.ResetToPending(id, [VideoStatus.Failed]);
    if (requeued)
    {
        await queue.EnqueueAsync(id, cancellationToken);
    }

    var whose = target.Username ?? target.Nickname ?? $"席位 {target.Id}";
    events.RecordWithPayload(EventActors.Admin, me!.Mid, EventNames.AdminAction, "video", id.ToString(),
        new { op = "claim", submitterMemberId = target.Id, requeued },
        ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: me.Id);
    return Results.Ok(new
    {
        submitterMemberId = target.Id,
        message = requeued ? $"已挂到 {whose} 名下，重新排队等下一班" : $"已挂到 {whose} 名下",
    });
});

// ─── 稿件设置（放映厅那枚「稿件设置」窗的后端）───────────────────────────────
// 一口 PATCH 改四栏：可见性 / 标题本地覆写 / 简介本地覆写 / 分区改派。
// 语义（与数据层同一克制，测试同一张嘴）：这一栏没递 = 不改；递非空串 = 覆写；递 null 或空串 = 还原
//   （落 NULL，这栏回跟 B 站档案走）。可见性没有「B 站原值」可回，null / 空 就按站内默认档 public 落。
// 权限只用 CanManageVideo（投稿人本人 ∥ 站长）那一把，不新造；分区这一栏按站长拍板开放给投稿人本人。
// 门：两道，与同形状的 DELETE /api/videos/{id}（投稿人自管删稿）一字不差——MemberGate 先挡匿名（RequiresMember 里
//   IsPatch + 纯数字 id 那一条，401 与删稿同一句话；放映厅面板「先登录」那一支就读它），端点里再核 CanManageVideo
//   （席位在、但这稿不是他的 → 403）。归属这一道必须长在端点里：不学 /revive /requeue 只靠 AdminGate 点名清单的旧账——
//   那份清单漂一个词就是匿名可写；而 MemberGate 只认「有没有席位」，投稿人本人能进靠的正是端点这一道（站长拿席位会话进门）。
// 成功只回 { ok = true }，不在这儿拼一份稿件 DTO：前端保存后重拉 /api/video（那里下发的是有效值），
//   两处真相的毛病就是这么留下来的。
app.MapPatch("/api/videos/{id:long}", async (long id, HttpContext http, StationDb db, EventRecorder events) =>
{
    // 先核钥匙再问有没有这一条：不是本人也不是站长的人问什么都是同一句 403（连「这稿存在」都问不出来），
    // 只有真拿钥匙的人才会走到下面碰到 400 / 404（与 NotYourSubmission 那套「藏钮只是体验」同一分工）。
    // 匿名已被 MemberGate 挡在 401（RequiresMember 的 IsPatch 那一条）：这一道管的是「席位在、稿不是他的」。
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    if (http.Request.HasFormContentType)
    {
        return Results.Json(new { message = "稿件设置的请求体得是一段 JSON 对象" }, statusCode: StatusCodes.Status400BadRequest);
    }

    JsonElement root;
    try
    {
        // ParseAsync 不是写法讲究，是必需：Kestrel 不许同步读请求流，JsonDocument.Parse(stream) 会当场抛
        // 「Synchronous operations are disallowed」把这一口整片打成 500（实机第一次 PATCH 就是这么炸的，
        // 而本仓没有 HTTP harness，532 条测试一条也不会报——以这条为证：碰真实流的读法要跑一次才算验过）。
        using var doc = await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { AllowTrailingCommas = true });
        root = doc.RootElement.Clone(); // Clone 一份：它能活得比 doc 久，下面要拿它逐栏取值
    }
    catch (JsonException)
    {
        return Results.Json(new { message = "请求体得是一段 JSON 对象（没递 body 也算）" }, statusCode: StatusCodes.Status400BadRequest);
    }

    if (!TryReadVideoSettings(root, out var settings, out var touched, out var readMessage))
    {
        return Results.Json(new { message = readMessage }, statusCode: StatusCodes.Status400BadRequest);
    }

    if (touched.Count == 0)
    {
        // 没话说不空转（与合集 UpdateCollection 同一克制）：递一个 {} 过来不会改掉任何东西。
        return Results.Json(new { message = "一栏都没递，这回没改任何东西（visibility / title / description / zone 递你要改的那几栏）" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    // 校验链（四栏先全验完再落笔，不给「标题改成了、简介脏了就回炉」那种半套改动留缝）：
    // visibility → VisibilityCodec.TryParse；title / description → StationDb.TryNormalizeTitle / TryNormalizeDesc（长度这一刀在数据层砍）；
    // zone → StationDb.TryNormalizeZone（与站长室那口 POST /api/admin/zones/set 同一张嘴）。认不了一律 400 + 人话。
    var writes = new List<Func<bool>>();
    if (touched.Contains("visibility"))
    {
        if (!VisibilityCodec.TryParse(settings.Visibility, out var visibility))
        {
            return Results.Json(new { message = $"可见性只认 \"{VisibilityCodec.Public}\" / \"{VisibilityCodec.Private}\"" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        writes.Add(() => db.SetVideoVisibility(id, visibility));
    }

    if (touched.Contains("title"))
    {
        if (!StationDb.TryNormalizeTitle(settings.Title, out var title, out var titleMessage))
        {
            return Results.Json(new { message = titleMessage }, statusCode: StatusCodes.Status400BadRequest);
        }

        writes.Add(() => db.SetVideoTitleOverride(id, title));
    }

    if (touched.Contains("description"))
    {
        if (!StationDb.TryNormalizeDesc(settings.Description, out var desc, out var descMessage))
        {
            return Results.Json(new { message = descMessage }, statusCode: StatusCodes.Status400BadRequest);
        }

        writes.Add(() => db.SetVideoDescOverride(id, desc));
    }

    if (touched.Contains("zone"))
    {
        if (!StationDb.TryNormalizeZone(settings.Zone, out var zone, out var zoneMessage))
        {
            return Results.Json(new { message = zoneMessage }, statusCode: StatusCodes.Status400BadRequest);
        }

        writes.Add(() => db.SetVideoZoneOverride(id, zone));
    }

    // 落笔：四栏写的都是 videos 同一行，哪栏回 false 都是「查无此片」——而且只可能是第一栏就回 false，
    // 所以不会留下半套改动（行不在，前面那些 UPDATE 也一行没碰着）。
    foreach (var write in writes)
    {
        if (!write())
        {
            return Results.NotFound(new { message = "馆藏里没有这个编号" });
        }
    }

    // 动过哪几栏要留账（11-08）：这口是投稿人自管口，改名、改简介、把稿子转私有都是能损到别人的事，
    // 后台得说得出「谁在什么时候动了哪几栏」。只记动了哪些栏与可见性/分区的新值，
    // 不把整篇简介拄进事件表（那东西能装几千字，台账不是第二个简介字段）。
    var editor = MemberGate.Current(http);
    events.RecordWithPayload(MemberGate.IsOwner(editor) ? EventActors.Admin : EventActors.Member,
        editor?.Mid, EventNames.VideoSettings, "video", id.ToString(),
        new { touched, visibility = settings.Visibility, zone = settings.Zone },
        ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: editor?.Id);

    return Results.Ok(new { ok = true });
});

// 编码加档：给已到站馆藏点单另一个编码版本（avc/hevc/av1），后台整视频补货，
// 每分 P 一行账，完成后同步员扫盘定档；video 装配接口的 codecVersions 随叫随查。
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
        // 弹幕署名凭据也是凭据：TLS 下上 Secure，局域网 http 不误伤（与站长/会员会话同一口径）。
        // .NET 10 没 SecurePolicy，SameAsRequest 自己算（反代身后靠 X-Forwarded-Proto 认 scheme）。
        Secure = http.Request.IsHttps,
        Expires = DateTimeOffset.UtcNow.AddDays(180),
        Path = "/",
    });
    return dmid;
}

// 自管钥匙：这稿子的投稿人本人或站长。归属不明的行（爬工代投、老数据反查失败）
// 只有站长握得住，与 MemberMail「无归属不发信」同一口径。比的是站内席位 id，不绕 mid。
// 正身在 ManageGate（与读侧的 ViewerGate 对称，测试能直接钉它）；这里留同名入口，老调用点一个字不用改。
static bool CanManageVideo(HttpContext http, StationDb db, long videoId) => ManageGate.CanManage(http, db, videoId);

// 看客尺子：站长全见；会员看公开 + 自己投的；访客只看公开。与 CanManageVideo 同一把钥匙（比站内席位 id，不绕 mid）。
// 各读接口一律 db.Xxx(..., ViewerScope(http))；正身在 ViewerGate（端点分部也要同一张嘴），这里留名就地可读。
static VisibilityScope ViewerScope(HttpContext http) => ViewerGate.Scope(http);

// 能不能看这把钥匙：站长短路；否则「公开」或「本人投稿」；查无此片回 false（与 CanManage 同形，别另起口径）。
// 拿 false 一律按「查无此片」那张 404 脸回（403 就是「有这稿但不给你看」的泄密信号）。
static bool CanViewVideo(HttpContext http, StationDb db, long videoId) => ViewerGate.CanView(http, db, videoId);

// 卡阵/放映厅的自管钮共用的那一句拒绕（分站没注册认证服务，Results.Forbid() 会 500，以前提过）。
static IResult NotYourSubmission() => Results.Json(
    new { error = "not-your-submission", message = "这枚钥匙只有投稿人本人和站长握得住" },
    statusCode: StatusCodes.Status403Forbidden);

// 稿件设置那口 PATCH 的收料：请求体 → （四栏的值, 这次动了哪几栏）。
// 为什么自己掰而不直接绑 VideoSettingsRequest：System.Text.Json 分不出「这栏没递」与「这栏递了 null」，
// 而这两件事在稿件设置窗里正是两种操作（少递一栏 = 不动它，递 null = 还原成 B 站原值）。
// 值只认字串与 null：「title 递个数字 123」那种手滑不当「改成 123」处理；栏名大小写不认生，但认不出的栏名当场 400——
// 默默咽下去的话，前端拼错一栏会以为改成功了，那是最难查的一种「改了没生效」。
static bool TryReadVideoSettings(JsonElement root, out VideoSettingsRequest settings, out HashSet<string> touched, out string message)
{
    touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    message = "";
    string? visibility = null, title = null, description = null, zone = null;
    if (root.ValueKind != JsonValueKind.Object)
    {
        settings = new VideoSettingsRequest(null, null, null, null);
        message = "请求体得是一段 JSON 对象";
        return false;
    }

    foreach (var property in root.EnumerateObject())
    {
        var field = property.Name.ToLowerInvariant();
        if (field is not ("visibility" or "title" or "description" or "zone"))
        {
            settings = new VideoSettingsRequest(null, null, null, null);
            message = $"没有「{property.Name}」这一栏可改（能改的只有 visibility / title / description / zone）";
            return false;
        }

        string? value;
        switch (property.Value.ValueKind)
        {
            case JsonValueKind.Null:
                value = null; // 显式 null = 还原这一栏（与空串同一克制）
                break;
            case JsonValueKind.String:
                value = property.Value.GetString();
                break;
            default:
                settings = new VideoSettingsRequest(null, null, null, null);
                message = $"「{property.Name}」得是给字串（null 表示还原成 B 站原值）";
                return false;
        }

        touched.Add(field);
        switch (field)
        {
            case "visibility":
                visibility = value;
                break;
            case "title":
                title = value;
                break;
            case "description":
                description = value;
                break;
            case "zone":
                zone = value;
                break;
        }
    }

    settings = new VideoSettingsRequest(visibility, title, description, zone);
    return true;
}

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
    // 单片级挡门：藏着的稿子与查无此片同一张 404 脸（弹幕也算内容，不隔这扇门就能从弹幕列表验出「有这稿」）。
    if (!CanViewVideo(http, db, id))
    {
        return Results.NotFound(new { message = "馆藏里没这个视频" });
    }

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
    // 写侧也要看客尺子：以前只验「有这条稿」，谁拿个 id 都能往藏着的稿子上钉弹幕
    // （GET 那一口早就走 CanViewVideo 了，写口得同形同文案）：藏着的稿子与查无同一张 404 脸，
    // 不许回 403（那是「有这条但不给你看」的泄密信号）。
    if (!CanViewVideo(http, db, id))
    {
        return Results.NotFound(new { message = "馆藏里没这个视频" });
    }

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

app.MapDelete("/api/videos/danmaku/{dmId:long}", (long dmId, HttpContext http, StationDb db, EventRecorder events) =>
{
    var sender = db.GetLocalDanmakuSender(dmId);
    if (sender is null)
    {
        return Results.NotFound();
    }

    var dmid = EnsureDmid(http);
    var me = MemberGate.Current(http);
    var isOwner = MemberGate.IsOwner(me);
    // 删除权两个口：发送者本人（凭自己的 lbs_dmid）与站长（会员席位 role=owner）。
    // 无主老弹幕（sender 为空）从此归站长删——以前得另外进一次主人模式才删得动，不合理。
    if (!isOwner && !(sender.Length > 0 && sender == dmid))
    {
        // 分站没注册认证服务，Results.Forbid() 会 500；显式状态码才稳。
        return Results.Json(new { message = "这条弹幕不是你的，站长才能删" }, statusCode: 403);
    }

    // 删从此不是一挥就没（11-08 站长拍板）：行摄进回收站（快照 + 删原行），站长在回收站捐得回来。
    // source 分账：本人收回去记 'self'，站长替人删记 'admin'——日后想只对「自己误删」开自捐，
    // 或在回收站里区分「谁动的手」，都靠这一列，不靠猜。
    var who = me?.Nickname ?? me?.Username ?? (isOwner ? "站长" : null);
    var recycleId = db.MoveDanmakuToRecycle(dmId, who, me?.Id, isOwner ? "admin" : "self");
    if (recycleId == 0)
    {
        return Results.NotFound();
    }

    events.RecordWithPayload(isOwner ? EventActors.Admin : EventActors.Member, me?.Mid, EventNames.DanmakuRecycle,
        "danmaku", dmId.ToString(), new { recycleId, source = isOwner ? "admin" : "self" },
        ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: me?.Id);
    return Results.Ok(new { message = "弹幕已收进回收站，站长在回收站里还能恢复" });
});

// 访客下载：把正在看的版本存本地（codec 缺省给主版本）。开 Range 支持断点续传，
// 文件名带标题/分 P/编码；路径死锁在 videos 库内，隔不过目录。
app.MapGet("/api/videos/{id:long}/download", (long id, int pageIdx, string? codec, StationDb db, DataPaths paths, HttpContext http) =>
{
    // 这条直接发字节，是静态区之外最要紧的一道门：藏着的稿子一律「没东西可搬」那张 404 脸（与下面查无同话）。
    if (!CanViewVideo(http, db, id))
    {
        return Results.NotFound(new { message = "这个版本分站还没存下，没东西可搬" });
    }

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

// 加档前的「预估」规格（放映厅那排「点单加档」用）：一个字节不下，只问 B 站要各编码的标称码率与大概体积。
// 两道门：MemberGate 先挡匿名（这一口替分站出去打 playurl，烧的是采集号，不能让访客使），
// 再过看客钥匙 —— 私有稿的规格也是内容情报，与查无此片同一张 404 脸。
app.MapGet("/api/videos/{id:long}/codecs/estimates", async (long id, int? page, StationDb db,
    CodecEstimateService estimates, HttpContext http, CancellationToken cancellationToken) =>
{
    var brief = db.GetVideoBrief(id);
    if (brief is null || !CanViewVideo(http, db, id))
    {
        return Results.NotFound(new { message = "馆藏里没这个视频" });
    }

    var items = await estimates.GetAsync(id, brief.Value.Bvid, page ?? 1, cancellationToken);
    return Results.Ok(new { items });
});

// 加档实况轻端点（会员可读）：放映厅的版本面板把「进站中…」报成带百分比/字节/速度的实况，
// 与站长室队列同一本账——引擎在途表逐枚现查，进度的真伪以引擎为准。
// 先过看客钥匙：码率版本清单（哪个编码在途、进度多少、下到多少字节）也是内容情报，
// 藏着的稿子与查无此片同一张 404 脸（与 danmaku/download 同一类泄漏，不说「私有」）。
// 写侧（codecs 点单 / cancel / 删版本）归站长门与旧账，这期一根手指不碰。
app.MapGet("/api/videos/{id:long}/codecs/progress", async (long id, StationDb db, IDownloadTaskLookup lookup, HttpContext http, CancellationToken cancellationToken) =>
{
    if (!CanViewVideo(http, db, id))
    {
        return Results.NotFound(new { message = "馆藏里没这个视频" });
    }

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

// 取消一件在途加档：销引擎账 + 判 Failed；反悔了回到那支片子的播放器里重新点单。
// 早先面板注释说「加档没取消端点，造个按下去没反应的钮比没有更坑」——现在引擎账销得动，钮按得动了。
// 11-08 从站长门摘给投稿人自管：自己那单的加档自己cancel得动（归属在端点里核，不靠身份直觉）。
// 烧资源的「点单」仍留在站长门里——取消只是止损，点单才是花钱，两者不该同权。
app.MapPost("/api/videos/{id:long}/codecs/cancel", async (long id, CancelCodecRequest request, HttpContext http, StationDb db, IDownloadTaskStateService state, IDownloadTaskLookup lookup, IDownloadTaskQueue taskQueue, EventRecorder events, CancellationToken cancellationToken) =>
{
    var codec = (request.Codec ?? string.Empty).Trim().ToLowerInvariant();
    if (!StationDb.SupportedCodecs.Contains(codec, StringComparer.Ordinal))
    {
        return Results.BadRequest(new { message = "不认的编码，只收 avc / hevc / av1" });
    }

    // 归属门：只准站长与这稿的投稿人本人动手，否则任何会员都能拿别人的 id 把在途任务拍掉。
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
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

    var canceller = MemberGate.Current(http);
    var byOwner = MemberGate.IsOwner(canceller);
    var who = canceller?.Nickname ?? canceller?.Username ?? (byOwner ? "站长" : "会员");
    db.MarkCodecFailed(targetRow.Value, $"{who}取消了这次加档，回到那支片子的播放器里可重新点单");
    // 谁取消的要能查得出来：投稿人自己止损与站长代他止损，在台账里是两件事。
    events.RecordWithPayload(byOwner ? EventActors.Admin : EventActors.Member, canceller?.Mid, EventNames.AdminAction,
        "codec", $"{id}:{codec}", new { action = "cancel", videoId = id, codec, pageIdx = request.PageIdx, byOwner },
        ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: canceller?.Id);
    return Results.Ok(new { message = $"已取消 {codec.ToUpperInvariant()} 加档（反悔回到播放器里重新点单）" });
});

// 删一版加档（一个分 P 的一个编码）：从此不再「销账 + 删文件」一步到死，
// 连行带件封进加档回收站（11-08 站长拍板「删掉的加档版本也进回收站，方便站长回滚」）。
// 与上面取消那口同形：从站长门摘到「会员门 + 端点归属门」，投稿人删得动自己那版的件，恢复与销毁仍只在站长手里。
// 两条安全闸住在库层：rel_path 指的是那一 P 的主件就拒删（以前连这个都不问）、文件被占用就整笔作废。
app.MapDelete("/api/videos/{id:long}/codecs/{pageIdx:int}/{codec}",
    (long id, int pageIdx, string codec, HttpContext http, StationDb db, DataPaths paths, EventRecorder events) =>
    {
        var rejected = ValidateCodecDeleteRequest(http, db, id, codec);
        if (rejected is not null)
        {
            return rejected;
        }

        var actor = MemberGate.Current(http);
        var byOwner = MemberGate.IsOwner(actor);
        var outcome = db.MoveCodecVersionsToRecycle(id, codec.Trim().ToLowerInvariant(), pageIdx,
            paths.Videos, paths.CodecRecycle,
            actor?.Nickname ?? actor?.Username ?? (byOwner ? "站长" : "会员"), actor?.Id, byOwner ? "admin" : "member");
        return FinishCodecDelete(events, http, actor, byOwner, id, pageIdx, codec, outcome);
    });

// 删整版加档（一个编码跨所有分 P）：一次调用 = 一个回收站组，恢复与销毁都按组走。
// 放映厅里那枚「删」钮走的是这一口：一版 hevc 往往是 N 个分 P 一起下的，逐 P 删要按 N 下，
// 还会给同一版留下 N 个互不相干的回收组，恢复时拼不回当初那一次点单。
app.MapDelete("/api/videos/{id:long}/codecs/{codec}",
    (long id, string codec, HttpContext http, StationDb db, DataPaths paths, EventRecorder events) =>
    {
        var rejected = ValidateCodecDeleteRequest(http, db, id, codec);
        if (rejected is not null)
        {
            return rejected;
        }

        var actor = MemberGate.Current(http);
        var byOwner = MemberGate.IsOwner(actor);
        var outcome = db.MoveCodecVersionsToRecycle(id, codec.Trim().ToLowerInvariant(), null,
            paths.Videos, paths.CodecRecycle,
            actor?.Nickname ?? actor?.Username ?? (byOwner ? "站长" : "会员"), actor?.Id, byOwner ? "admin" : "member");
        return FinishCodecDelete(events, http, actor, byOwner, id, null, codec, outcome);
    });

// 两口删加档共用的前置：编码名得认识、钥匙得在手里（只认站长与这稿的投稿人）。
static IResult? ValidateCodecDeleteRequest(HttpContext http, StationDb db, long id, string codec)
{
    if (!StationDb.SupportedCodecs.Contains((codec ?? string.Empty).Trim().ToLowerInvariant(), StringComparer.Ordinal))
    {
        return Results.BadRequest(new { message = "不认的编码，只收 avc / hevc / av1" });
    }

    return CanManageVideo(http, db, id) ? null : NotYourSubmission();
}

// 删完的收摊：进回收站要留账（谁动的手、搬了几个件、哪一组），状态码分三张脸。
static IResult FinishCodecDelete(EventRecorder events, HttpContext http, StationMember? actor, bool byOwner,
    long id, int? pageIdx, string codec, LBS.Infrastructure.CodecRecycleMoveOutcome outcome)
{
    events.RecordWithPayload(byOwner ? EventActors.Admin : EventActors.Member, actor?.Mid, EventNames.CodecRecycle,
        "codec", $"{id}:{codec}",
        new { videoId = id, pageIdx, codec, ok = outcome.Ok, groupId = outcome.GroupId, rows = outcome.Rows, files = outcome.Files, message = outcome.Message },
        ip: http.Connection.RemoteIpAddress?.ToString(), actorMemberId: actor?.Id);
    if (outcome.Ok)
    {
        return Results.Ok(new { message = outcome.Message, groupId = outcome.GroupId, rows = outcome.Rows, files = outcome.Files });
    }

    return outcome.Message == LBS.Infrastructure.StationDb.CodecRecycleMissingMessage
        ? Results.NotFound(new { message = outcome.Message })
        : Results.Conflict(new { message = outcome.Message });
}

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

// 重拉字幕存档（稿件设置窗那枚钮）：回 B 站把这份稿件的字幕轨全拉一遍，整档刷新。
// 背景：引擎当年被语言偏好筛剩过轨（盘上缺的轨销档重下以外没有第二条路），偏好闸 11-11 已拆，这枚钮给老稿补上回拉口。
// 门与 danmaku-refresh 同形：MemberGate 的 POST 点名清单里有它（挡匿名 401），归属归这里的 CanManageVideo（403）。
app.MapPost("/api/videos/{id:long}/subtitles-refresh", async (long id, HttpContext http, StationDb db, SubtitleArchiveService service, CancellationToken cancellationToken) =>
{
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    var outcome = await service.TryRefreshAsync(id, cancellationToken);
    return outcome.Ok
        ? Results.Ok(new { message = outcome.Message, tracks = outcome.Tracks, pages = outcome.Pages })
        : Results.BadRequest(new { message = outcome.Message });
});

// 重拉封面与媒体（稿件设置窗那枚钮，站长批的是全量：含档案重取）。一趟三件事：
//   ① 向 B 站要一份档案 → 只走窄口 UPDATE（绝不走 SaveSubmissionMetadata：它是 DELETE 重插，会把 UP 名片/
//      互动数据/创作团队抹成 NULL）；标题不重取（那是站长可以站内覆写的那一栏）；
//   ② 重抓封面件原地覆盖（与封面补员共用 CoverFileFetcher，抓不着就旧件旧行原样留着）；
//   ③ 盘上真缺件才回炉（判据与开机全库扫同源），一件不缺就一个下载都不触发。
// 门与上面的 danmaku-refresh 同形：MemberGate 的 POST 点名清单里有它（挡匿名 401），归属归这里的 CanManageVideo（403）。
app.MapPost("/api/videos/{id:long}/media-refresh", async (long id, HttpContext http, StationDb db, MediaRefreshService service, CancellationToken cancellationToken) =>
{
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    var outcome = await service.TryRefreshAsync(id, cancellationToken);
    return outcome.Status switch
    {
        MediaRefreshStatus.NotFound => Results.NotFound(new { message = outcome.Message }),
        MediaRefreshStatus.NotArrived => Results.BadRequest(new { message = outcome.Message }),
        _ => Results.Ok(new
        {
            ok = true,
            message = outcome.Message,
            cover = outcome.Cover,
            meta = outcome.Meta,
            media = outcome.Media,
        }),
    };
});

// 重拉视频数据（稿件设置窗的独立钮与卡阵冒泡钮的顺带那件）：只办 media-refresh 的档案半——
// 问 B 站要一份档案、窄口 UPDATE 六栏；封面件与媒体都不碰。门与 media-refresh 同形（MemberGate 点名挡匿名 401，归属归 CanManageVideo）。
app.MapPost("/api/videos/{id:long}/meta-refresh", async (long id, HttpContext http, StationDb db, MediaRefreshService service, CancellationToken cancellationToken) =>
{
    if (!CanManageVideo(http, db, id))
    {
        return NotYourSubmission();
    }

    var outcome = await service.TryRefreshMetaAsync(id, cancellationToken);
    return outcome.Status switch
    {
        MediaRefreshStatus.NotFound => Results.NotFound(new { message = outcome.Message }),
        MediaRefreshStatus.NotArrived => Results.BadRequest(new { message = outcome.Message }),
        _ => Results.Ok(new { ok = true, message = outcome.Message, meta = outcome.Meta }),
    };
});

// 楼中楼按需补采：快照里没预配到的楼层点开时，分站现向 B 站翻这一楼并并回快照。
// 先过看客钥匙：藏着的稿子与查无同一张 404 脸——这口会替分站出去问 B 站，不能谁都能拿着别人的 bvid 使。
app.MapPost("/api/videos/{id:long}/comments/subs", async (long id, CommentArchiveService service, SubsRequest request, HttpContext http, StationDb db, CancellationToken cancellationToken) =>
{
    if (!CanViewVideo(http, db, id))
    {
        return Results.NotFound(new { message = "馆藏里没这个视频" });
    }

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

    // 投稿带可见性：认 public / private / 空（空 = 公开），其余 400 一句人话（宁拒不错档）。
    if (!VisibilityCodec.TryParse(request.Visibility, out var visibility))
    {
        return Results.BadRequest(new { message = $"可见性只认 {VisibilityCodec.Public} / {VisibilityCodec.Private}（不填算公开）" });
    }

    // 投稿受理只有一条顺序：先验人、再验号、最后才落台账（顺序颠倒过的代价写在 SubmissionIntake 的注释里：
    // 闸门拒收时台账已落行，那条 BV 号就被无主空壳永久占死，同链再投永远 409）。
    // 单链投稿也走批量那一口：它是写侧唯一能把可见性正门正写进台账的出口（AddVideo 不带这栏），
    // 手搓 UPDATE 翻档是数据层禁路，这里不绕。撞重照样只补档案，不碰已有行的 visibility。
    var submitter = MemberGate.Current(http);
    if (SubmissionIntake.Deny(db, submitter) is { } denial)
    {
        return denial; // 401 / 403：一行台账都没写
    }

    // Deny 放行即席位非空（空的那张脸就是 401），下面不再判。
    var outcome = SubmissionIntake.Accept(db, submitter!,
        new[] { new NewVideo(request.Url, bvid, null, null, null, visibility) });
    if (outcome.Added == 0)
    {
        return Results.Conflict(new { message = "这个视频已经到过了" });
    }

    var videoId = outcome.InsertedIds[0];

    events.RecordWithPayload(EventActors.Member, submitter!.Mid, EventNames.VideoSubmit, "video", bvid,
        new { url = request.Url, via = "link", visibility = VisibilityCodec.ToColumn(visibility) },
        http.Connection.RemoteIpAddress?.ToString(), submitter.Id);

    await queue.EnqueueAsync(videoId, cancellationToken);
    return Results.Ok(new { videoId, message = $"已加入进站队列，{bvid} 正在赶来" });
});

app.MapPost("/api/favorites/import-url", async (FavoritesUrlImportRequest request, LbsFavoritesImport favorites, LbsCollectPolicy policy, LbsApiThrottle throttle, StationDb db, DownloadQueue queue,
    EventRecorder events, HttpContext http, CancellationToken cancellationToken) =>
{
    // 先校档位再出去翻页：坏值不该先烧一轮 B 站请求再被 400 抵回。
    if (!VisibilityCodec.TryParse(request.Visibility, out var visibility))
    {
        return Results.BadRequest(new { message = $"可见性只认 {VisibilityCodec.Public} / {VisibilityCodec.Private}（不填算公开）" });
    }

    // 整架导入是批量动作，冷却期直接挡回，不等它耐心到天荒地老。
    if (throttle.IsCoolingDown)
    {
        var left = throttle.CooldownRemaining ?? TimeSpan.Zero;
        return Results.Json(new { message = $"分站正在风控冷却中，约 {Math.Ceiling(left.TotalMinutes)} 分钟后自动回暖，稍后再投" }, statusCode: StatusCodes.Status423Locked);
    }

    // 投稿闸放在出去翻页之前：素净席位连 B 站请求都不该替分站烧（这一口整架翻页，一趟几十发）。
    var importer = MemberGate.Current(http);
    if (SubmissionIntake.Deny(db, importer) is { } denial)
    {
        return denial;
    }

    // 整架导入不挂具体视频，按采集偏好出号。
    var outcome = await policy.RunForActivityAsync(() => favorites.ImportAsync(request.Url, cancellationToken));
    if (!outcome.Success)
    {
        return Results.BadRequest(new { message = outcome.Error });
    }

    // 整批共用一个档位：导入器不认可见性（它只管抽链接），入库前在这一行套上（只改内存里的 NewVideo，不碰已有行）。
    var bulk = SubmissionIntake.Accept(db, importer!,
        outcome.Videos.Select(v => v with { Visibility = visibility }).ToList());
    foreach (var videoId in bulk.InsertedIds)
    {
        await queue.EnqueueAsync(videoId, cancellationToken);
    }

    if (bulk.Added > 0)
    {
        events.RecordWithPayload(EventActors.Member, importer!.Mid,
            EventNames.VideoSubmit, "batch", "fav_url",
            new { via = "fav_url", added = bulk.Added, duplicated = bulk.Duplicated, ignored = outcome.Ignored },
            http.Connection.RemoteIpAddress?.ToString(), importer.Id);
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
    // 先校档位再出去翻页：坏值不该先烧一轮 B 站请求再被 400 抵回。
    if (!VisibilityCodec.TryParse(request.Visibility, out var visibility))
    {
        return Results.BadRequest(new { message = $"可见性只认 {VisibilityCodec.Public} / {VisibilityCodec.Private}（不填算公开）" });
    }

    if (throttle.IsCoolingDown)
    {
        var left = throttle.CooldownRemaining ?? TimeSpan.Zero;
        return Results.Json(new { message = $"分站正在风控冷却中，约 {Math.Ceiling(left.TotalMinutes)} 分钟后自动回暖，稍后再投" }, statusCode: StatusCodes.Status423Locked);
    }

    // 投稿闸放在翻整架之前（同收藏夹链接那一口）：素净席位不该替分站翻上几十页空跑一趟。
    var importer = MemberGate.Current(http);
    if (SubmissionIntake.Deny(db, importer) is { } denial)
    {
        return denial;
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
                item.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(item.Created).ToString("O") : null,
                visibility));
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

    var upBulk = SubmissionIntake.Accept(db, importer!, videos);
    foreach (var videoId in upBulk.InsertedIds)
    {
        await queue.EnqueueAsync(videoId, cancellationToken);
    }

    if (upBulk.Added > 0)
    {
        events.RecordWithPayload(EventActors.Member, importer!.Mid,
            EventNames.VideoSubmit, "batch", "up",
            new { via = "up", added = upBulk.Added, duplicated = upBulk.Duplicated },
            http.Connection.RemoteIpAddress?.ToString(), importer.Id);
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

// 快照下载（站长 11-03「现在备份好都没地方下载」）：站长室档案柜那枚下载钮。
// 它天然在站长门内（/api/backups 整段前缀在 AdminGate 的点名表里）；名字走 LedgerBackup.FullPathOf，
// 与「销单」同一把尺——只认快照命名格式，拿 .. 或绝对路径借道 data 以外的目录一概拒。
app.MapGet("/api/backups/{name}/file", (string name, LedgerBackup backup) =>
{
    var file = backup.FullPathOf(name);
    if (file is null)
    {
        return Results.BadRequest(new { message = "没有这个快照" });
    }

    // 整站台账快照可能上百 MB：走流 + Range，不把整份读进内存，浏览器下载器中断了也能续。
    return Results.File(File.OpenRead(file), "application/octet-stream", Path.GetFileName(file), enableRangeProcessing: true);
});

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
    // 加档回收站住在另一座（data/codec_recycle），不点名就会剩下一堆没台账可对的封存件。
    var recycledRows = db.PurgeAllRecycle(paths.Recycle);
    var codecRecycledRows = db.PurgeAllCodecRecycle(paths.CodecRecycle);

    // 4. 资源文件清盘：视频库 + 缓存小图区 + 预览成片区 + e2e 临时件。cookies 与引擎设置不碰。
    var (files, bytes, busy) = PurgeDirectoryTree(paths.Videos);
    foreach (var dir in new[] { paths.Avatars, paths.Emotes, paths.CmtImg, paths.Cache, paths.Previews, paths.UpFaces })
    {
        var (f, b, z) = PurgeDirectoryTree(dir);
        files += f;
        bytes += b;
        busy += z;
    }

    var message = $"站已清空：取消 {cancelledTasks} 个引擎任务，销 {ledgerRows} 行台账、{engineRows} 行引擎历史，清掉 {files} 个文件（{(double)bytes / 1048576:F1} MB）"
        + (recycledRows > 0 ? $"，回收站 {recycledRows} 件一并销毁" : "")
        + (codecRecycledRows > 0 ? $"，加档回收站 {codecRecycledRows} 行一并销毁" : "");
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

app.MapGet("/api/settings", (DataPaths paths, StationDb db, HttpContext http) => Results.Ok(new
{
    // 数据目录只向站长会话报家门：匿名访客没必要知道房子在哪（前端站长室的存储卡照常拿得到）。
    dataDir = MemberGate.IsOwner(MemberGate.Current(http)) ? paths.Root : null,
    // 分享链接的门面地址（站长室「分享链接设置」写）：分享出去的链接本就抄得走，不是站长情报，
    // 访客也读得。没配过回 null，前端（utils/siteLink.js）回退跟随访问地址。
    shareBaseUrl = db.GetSetting(ShareLinkEndpoints.SettingKey),
    skin = new { bare = false, note = "皮肤系统未开工（README 远期项）" },
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
if (dataPaths.RootHint is { } dataHint)
{
    // 数据根是往上翻出来的：把来路说破，别让人把开错库当成数据没了。
    logger.LogWarning("数据目录是推出来的：{Hint}", dataHint);
}
app.Run();

internal sealed record HealthInfo(bool Ok, string Name, string Version, DateTimeOffset ServerTimeUtc);

// 投稿三档（单链 / 收藏夹链接 / UP 主整架）都能选公开或私有，整批共用一个档位；不填 = 公开。
// 字段名跟前端 SubmitDialog 的 body 一字不差（visibility），认不出的值一律 400，不给「默默变成公开」的糊涂账。
internal sealed record AddVideoRequest(string Url, string? Visibility);
internal sealed record FavoritesUrlImportRequest(string Url, string? Visibility);
internal sealed record UpImportRequest(string Url, string? Visibility);

/// <summary>
/// 静态区挡门的「件 → 稿件 id」反查缓存（只给那一道闸与两处补保省腿，不参与任何判据本身）。
/// 时间约束（站长拍的，不许放宽）：稿件转私后最多 60 秒内必须挡住 —— 命中项 TTL 就钉在 60 秒；
/// 认不出（null）只留 5 秒：刚归档的新件不该长时间卡在「台账不认」那一侧（那是放行方向，留久了等于给新私有件发通行证）。
/// 容量上限 4096（首页一屏上百张封面，量级够用），满了整体清一次 —— 台账一直在变，这层只是省腿的，不保真。
/// </summary>
internal sealed class MediaOwnerCache(TimeProvider? clock = null)
{
    private const int Capacity = 4096;

    /// <summary>认到稿件的件：60 秒。这条线就是「转私后多久挡得住」的上限，调它等于调那道闸的脉。</summary>
    private static readonly TimeSpan KnownTtl = TimeSpan.FromSeconds(60);

    /// <summary>台账不认的件：只留 5 秒（新归档件得尽快被认出来）。</summary>
    private static readonly TimeSpan UnknownTtl = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>拿 rel 问属于哪条稿子：没过期直接用缓存，过期/没存过才问一次库。</summary>
    public long? Resolve(string rel, Func<string, long?> readThrough)
    {
        var now = _clock.GetUtcNow();
        if (_entries.TryGetValue(rel, out var hit) && hit.ExpiresUtc > now)
        {
            return hit.VideoId;
        }

        var videoId = readThrough(rel);
        // 满了不逐条逐龄（这条路径上每条静态请求都可能撞上，不值得）：整层掀了重来，代价只是多问几趟库。
        if (_entries.Count >= Capacity)
        {
            _entries.Clear();
        }

        _entries[rel] = new Entry(videoId, now.Add(videoId is null ? UnknownTtl : KnownTtl));
        return videoId;
    }

    private sealed record Entry(long? VideoId, DateTimeOffset ExpiresUtc);
}

/// <summary>
/// 对外一律小写字符串：<see cref="VideoVisibility"/> 是枚举，直接序列化会变成 0/1，
/// 而前端（放映厅那枚「私有」药丸、抽屉角标）拿的契约是 "public" / "private"。
/// 读写两头都从 <see cref="VisibilityCodec"/> 那一个出口出：写侧 ToColumn，读侧 TryParse（认不了当公开，
/// 与数据层 FromColumn 同一克制，不把一列脏值顶成整页 500）。
/// </summary>
internal sealed class VideoVisibilityJsonConverter : JsonConverter<VideoVisibility>
{
    public override VideoVisibility Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => VisibilityCodec.TryParse(reader.GetString(), out var parsed) ? parsed : VideoVisibility.Public,
            // 枚举的数字化旧形（0/1）也收下：存量存档/别人手搓的 body 不该因为少了个引号就 500。
            JsonTokenType.Number => reader.GetInt32() == (int)VideoVisibility.Private ? VideoVisibility.Private : VideoVisibility.Public,
            JsonTokenType.Null => VideoVisibility.Public,
            _ => throw new JsonException($"可见性只认 \"{VisibilityCodec.Public}\" / \"{VisibilityCodec.Private}\""),
        };

    public override void Write(Utf8JsonWriter writer, VideoVisibility value, JsonSerializerOptions options) =>
        writer.WriteStringValue(VisibilityCodec.ToColumn(value));
}

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

/// <summary>
/// 稿件设置窗那口 PATCH /api/videos/{id} 的请求体形状。四栏都可缺省，语义三档：
/// <b>缺省 = 这一栏不改；递非空串 = 覆写；递 null 或空串 = 还原</b>（落 NULL，这栏回跟 B 站档案走）。
/// 可见性用字串（「public」/「private」）而不是枚举：认不出了要能回 400 + 人话，而不是被转换器掰成 public。
/// 实际收料在 <c>TryReadVideoSettings</c>：System.Text.Json 分不出「没递」与「递 null」，得按属性名自己点齐。
/// </summary>
internal sealed record VideoSettingsRequest(string? Visibility, string? Title, string? Description, string? Zone);

internal sealed record CancelCodecRequest(string? Codec, int? PageIdx);
