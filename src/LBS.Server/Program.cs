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
builder.Services.AddSingleton<LbsAltLoginManager>();
builder.Services.AddSingleton<LbsAccountRouter>();
// 分流 cookie 供给必须赶在 AddLbsDownloadEngine 之前占位，让上游的 TryAddSingleton 自动让位：
// 从此引擎内所有经 IBilibiliApiClient 的请求（含 playurl/view info）都按视频归属号现取 cookie。
builder.Services.AddSingleton<DownKyi.Application.Bilibili.IBilibiliCookieProvider, LbsRoutedCookieProvider>();
builder.Services.AddSingleton<LbsQueryClient>();
builder.Services.AddSingleton<CommentArchiveService>();
builder.Services.AddSingleton<LbsOwnerPolicy>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<StatusSyncWorker>();
// 订阅爬工：单例注册以便站长室手动触发拿到同一个实例（串行闸在它手里）。
builder.Services.AddSingleton<SubscriptionCrawler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SubscriptionCrawler>());
// 动态巡捕（M6）：取数员单例；巡捕也单例注册，站长室「立即巡一轮」拿同一个实例吃同一把重入闸。
builder.Services.AddSingleton<LBS.Engine.LbsDynamicFetcher>();
builder.Services.AddSingleton<DynamicFetchWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DynamicFetchWorker>());
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

app.MapGet("/api/videos", (StationDb db) => db.ListVideos());

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
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPaths.Videos),
    RequestPath = "/media",
    ContentTypeProvider = mediaTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
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
            await context.Response.WriteAsJsonAsync(new { error = "member-required", message = "扫码登录后才能看视频、弹幕与评论区" });
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
    // 已上锁时换口令也得先递口令：否则任何人一发请求就能换掉站长钥匙。
    if (gate.IsLocked && !gate.HasValidSession(http.Request))
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

    // 除锁必须持有效会话：不然路过的访客一发请求就把锁卸了。
    if (!gate.HasValidSession(http.Request))
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

// 数据交换：导出打包/档案柜/导入暂存与换库（全在 /api/admin/** 站长门下）。
app.MapDataExchangeApi();
app.MapDynamicEndpoints();
app.MapAdminDynamicEndpoints();

// 旧档案的后补：有 mid 但名片从未采过（up_face 为 NULL）时，开页顺手异步采一次，下次刷新就有。
// 失败也销账（置空串）；并发重复触发用 in-flight 集合挡掉。
System.Collections.Concurrent.ConcurrentDictionary<long, byte> upCardInFlight = new();
System.Collections.Concurrent.ConcurrentDictionary<long, byte> statInFlight = new();

app.MapGet("/api/watch/{bvid}", (string bvid, StationDb db, LbsUpInfoFetcher upInfo, LbsOwnerPolicy policy, ILoggerFactory loggerFactory) =>
{
    // BV 号是大小写敏感的 base62，先原样精确查，再补一记大写化兜底（照顾手打习惯）。
    var input = bvid.Trim();
    var data = db.GetWatchData(input) ?? db.GetWatchData(input.ToUpperInvariant());
    if (data is not null)
    {
        BackfillUpCard(db, upInfo, policy, loggerFactory, data);
        BackfillStat(db, upInfo, policy, loggerFactory, data);
    }

    return data is null
        ? Results.NotFound(new { message = "馆藏里没有这个 BV 号" })
        : Results.Ok(data);
});

// 接下来播放：同 UP 主优先、新入站补齐，卡片形状与列表接口一致。
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
        CreatedAt = v.CreatedAtUtc,
    }).ToList());
});

void BackfillUpCard(StationDb db, LbsUpInfoFetcher upInfo, LbsOwnerPolicy policy, ILoggerFactory loggerFactory, WatchData data)
{
    if (data.UpMid is not { } mid || data.UpFace is not null)
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
void BackfillStat(StationDb db, LbsUpInfoFetcher upInfo, LbsOwnerPolicy policy, ILoggerFactory loggerFactory, WatchData data)
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

app.MapDelete("/api/videos/{id:long}", async (long id, StationDb db, DataPaths paths, IDownloadTaskStateService state, IDownloadTaskLookup lookup, IDownloadTaskQueue taskQueue, CancellationToken cancellationToken) =>
{
    var row = db.GetVideoForDelete(id);
    if (row is null)
    {
        return Results.NotFound(new { message = "馆藏里没有这个编号" });
    }

    // 先安顿引擎侧：在途/排队任务要先取消并删除，否则删完站它们还会继续往硬盘写。
    if (!string.IsNullOrWhiteSpace(row.Value.TaskIdsJson))
    {
        List<string>? taskIds = null;
        try
        {
            taskIds = JsonSerializer.Deserialize<List<string>>(row.Value.TaskIdsJson);
        }
        catch (JsonException)
        {
            // 脏数据就当没得安顿，继续删站
        }

        foreach (var taskId in taskIds ?? new List<string>())
        {
            var downloadTask = await lookup.FindAsync(new DownloadTaskId(taskId), cancellationToken);
            if (downloadTask is null)
            {
                continue; // 已完成搬走的不用安顿（文件在删站阶段一并清）
            }

            // 先打断队列里可能活着的执行再销账（对齐引擎自家删除编排），不然它会把字节继续往已删的行里写。
            try
            {
                await taskQueue.CancelAsync(new DownloadTaskId(taskId));
            }
            catch (Exception)
            {
                // 本就没在跑，取消不掉无妨
            }

            await state.CancelAsync(new DownloadTaskId(taskId), cancellationToken);
            var remove = await state.DeleteAsync(new DownloadTaskId(taskId), cancellationToken);
            if (!remove.IsSuccess)
            {
                return Results.Conflict(new { message = $"引擎任务删不动（{remove.Error?.Message ?? "原因未知"}），等下载告一段落再试" });
            }
        }
    }

    var deletedFiles = db.DeleteVideoCascade(id, paths.Videos);
    return Results.Ok(new { message = $"「{row.Value.Bvid}」已从分站删除，带走 {deletedFiles} 个文件" });
});

// 续下：救活硬重启遗留的卡死下载（引擎任务挂在途表里纹丝不动）。
// 软续下（Resume+Enqueue）优先，打不动就删行重投，策略见 LbsTaskReviver。
app.MapPost("/api/videos/{id:long}/revive", async (long id, LbsTaskReviver reviver, CancellationToken cancellationToken) =>
{
    var (ok, message) = await reviver.ReviveAsync(id, cancellationToken);
    return ok ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// 重新排队：站长取消过的馆藏（Canceled）从卡阵里捞回排队区；其它状态不响应免得误复活。
app.MapPost("/api/videos/{id:long}/requeue", async (long id, StationDb db, DownloadQueue queue, CancellationToken cancellationToken) =>
{
    var brief = db.GetVideoBrief(id);
    if (brief is null)
    {
        return Results.NotFound(new { message = "没有这条馆藏" });
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
// 主人：data/owner.key 文件存在即启用，认得 key 的即主人；key 只在生成那一刻下发一次，
// 任何 GET 端点都不送 key，重置必须带旧 key。主人进模式后发 lbs_owner cookie（HttpOnly）。
static string? ReadOwnerKey(DataPaths paths)
{
    var file = Path.Combine(paths.Root, "owner.key");
    if (!File.Exists(file))
    {
        return null;
    }

    var key = File.ReadAllText(file).Trim();
    return key.Length > 0 ? key : null;
}

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

static bool IsOwner(HttpContext http, DataPaths paths)
{
    var key = ReadOwnerKey(paths);
    return key is not null
        && http.Request.Cookies.TryGetValue("lbs_owner", out var held)
        && held == key;
}

app.MapPost("/api/owner/key", (OwnerKeyRequest? request, DataPaths paths) =>
{
    var file = Path.Combine(paths.Root, "owner.key");
    var wasConfigured = File.Exists(file);
    if (wasConfigured)
    {
        // 换钥匙的唯一途径是交出旧钥匙：分站公开可达，没这道门主人形同虚设。
        var old = ReadOwnerKey(paths);
        if (old is null || string.IsNullOrWhiteSpace(request?.OldKey) || request.OldKey.Trim() != old)
        {
            return Results.Unauthorized();
        }
    }

    var fresh = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    File.WriteAllText(file, fresh);
    return Results.Ok(new { key = fresh, message = wasConfigured ? "钥匙已换新，旧的作废" : "主人钥匙已生成，只显这一次，存好" });
});

app.MapPost("/api/owner/enter", (OwnerEnterRequest request, HttpContext http, DataPaths paths) =>
{
    var key = ReadOwnerKey(paths);
    if (key is null)
    {
        return Results.BadRequest(new { message = "主人钥匙还没生成，去站长室开一把" });
    }

    if (request.Key.Trim() != key)
    {
        return Results.Unauthorized();
    }

    http.Response.Cookies.Append("lbs_owner", key, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.AddDays(30),
        Path = "/",
    });
    return Results.Ok(new { message = "已进入主人模式" });
});

app.MapPost("/api/owner/leave", (HttpContext http) =>
{
    http.Response.Cookies.Delete("lbs_owner", new CookieOptions { Path = "/" });
    return Results.Ok(new { message = "已退出主人模式" });
});

// 只报两个布尔：配了没、当前浏览器是不是主人。key 本身永不出门。
app.MapGet("/api/owner/state", (HttpContext http, DataPaths paths) =>
    Results.Ok(new { configured = ReadOwnerKey(paths) is not null, isOwner = IsOwner(http, paths) }));

// 删除权：仅发送者本人（凭 lbs_dmid cookie）或主人（凭 lbs_owner cookie）；
// canDelete 由服务端算好随列表下发，发送者身份不外泄。无主老弹幕只有主人能删。
app.MapGet("/api/videos/{id:long}/danmaku", (long id, HttpContext http, StationDb db, DataPaths paths) =>
{
    var dmid = EnsureDmid(http);
    var owner = IsOwner(http, paths);
    return Results.Ok(db.ListLocalDanmaku(id).Select(d => new
    {
        id = d.Id,
        pageIdx = d.PageIdx,
        t = d.TSec,
        text = d.Text,
        canDelete = owner || (d.SenderDmid is { Length: > 0 } s && s == dmid),
        // 主人发的弹幕挂站长头像；身份体系之前的无主老行（无凭证）也当主人手笔，那阵子只有主人能发。
        fromOwner = d.SenderOwner || d.SenderDmid is null,
    }));
});

app.MapPost("/api/videos/{id:long}/danmaku", (long id, LocalDanmakuRequest request, HttpContext http, StationDb db, DataPaths paths) =>
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

    var dmId = db.AddLocalDanmaku(id, Math.Max(0, request.PageIdx), Math.Max(0, request.T), text, EnsureDmid(http), IsOwner(http, paths));
    return Results.Ok(new { id = dmId, message = "弹幕已存站" });
});

app.MapDelete("/api/videos/danmaku/{dmId:long}", (long dmId, HttpContext http, StationDb db, DataPaths paths) =>
{
    var sender = db.GetLocalDanmakuSender(dmId);
    if (sender is null)
    {
        return Results.NotFound();
    }

    var dmid = EnsureDmid(http);
    if (!IsOwner(http, paths) && !(sender.Length > 0 && sender == dmid))
    {
        // 分站没注册认证服务，Results.Forbid() 会 500；显式状态码才稳。
        return Results.Json(new { message = "这条弹幕不是你的，主人才能删" }, statusCode: 403);
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

// 补采评论：到站时自动采过一次，这条给站长手动刷新快照（老馆藏回填也靠它）。
app.MapPost("/api/videos/{id:long}/comments", async (long id, CommentArchiveService service, CancellationToken cancellationToken) =>
{
    var outcome = await service.TryArchiveAsync(id, force: true, cancellationToken);
    return outcome.Ok
        ? Results.Ok(new { message = outcome.Message, count = outcome.Count })
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
    // 投稿闸门（M5.4）：素净席位能看能聊不能投稿——得有一个未过期的 B 站号才烧得起别人的登录态；站长拿引擎登录态投稿，不受此闸。
    if (submitter is not null && !SubmissionGate.Allows(db, submitter))
    {
        return Results.Json(new
        {
            error = "no-bilibili-account",
            message = "还没绑 B 站号，先去何处？用户控制室——绑定后就能投稿，看片私信不耽误",
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (submitter is not null)
    {
        db.SetVideoSubmitterMember(outcome.VideoId, submitter.Id);
    }

    events.RecordWithPayload(submitter is null ? EventActors.Guest : EventActors.Member, submitter?.Mid,
        EventNames.VideoSubmit, "video", bvid, new { url = request.Url, via = "link" },
        http.Connection.RemoteIpAddress?.ToString(), submitter?.Id);

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

app.MapPost("/api/favorites/import-url", async (FavoritesUrlImportRequest request, LbsFavoritesImport favorites, LbsOwnerPolicy policy, LbsApiThrottle throttle, StationDb db, DownloadQueue queue,
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
    LbsOwnerPolicy policy, LbsApiThrottle throttle, StationDb db, DownloadQueue queue,
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
                keys, wts, mid.Value, pn: pn, ps: pageSize, LoginHelper.HasSavedLoginInfo,
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
app.MapGet("/api/downloads/queue", (DownloadPauseBoard board, StationDb db) => Results.Ok(new
{
    paused = board.IsPaused,
    intervalSec = board.IntervalSec,
    downloading = db.GetDownloadingRows()
        .Select(r => new { id = r.Id, bvid = r.Bvid, title = r.Title, coverUrl = r.CoverUrl }),
    pending = db.GetPendingQueueItems()
        .Select(i => new { id = i.Id, bvid = i.Bvid, title = i.Title, coverUrl = i.CoverUrl }),
}));

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

app.MapGet("/api/login/status", async (LbsLoginManager login, StationDb db, CancellationToken cancellationToken) =>
{
    var state = await login.GetStateAsync(cancellationToken);
    if (state is { IsLogin: true, Face: { Length: > 0 } })
    {
        // 嗂到 KV 兜一份：nav 是实时接口不宜被弹幕层频扰，站长头像给 /api/station/profile 吃这份缓存。
        db.SetSetting("station-profile", JsonSerializer.Serialize(new { name = state.Name, face = state.Face }));
    }

    return Results.Ok(new
    {
        isLogin = state.IsLogin,
        name = state.Name,
        mid = state.Mid,
        vipStatus = state.VipStatus,
        hasSavedCookies = state.HasSavedCookies,
        face = state.Face,
    });
});

// 站长公开名片：昵称+头像（B 站 CDN 链，本就公网可见），没登录过回 null。
app.MapGet("/api/station/profile", (StationDb db) =>
{
    var json = db.GetSetting("station-profile");    
    return string.IsNullOrWhiteSpace(json) ? Results.Ok(null) : Results.Ok(JsonSerializer.Deserialize<JsonElement>(json));
});

app.MapPost("/api/login/cookie", async (LoginCookieRequest request, LbsLoginManager login, CancellationToken cancellationToken) =>
{
    // 红线：cookie 只进不出，这里不记录也不回显任何 cookie 内容。
    var outcome = await login.LoginWithCookieAsync(request.Cookie, cancellationToken);
    return outcome.Success
        ? Results.Ok(new { message = $"站长「{outcome.Name}」已进站", name = outcome.Name, mid = outcome.Mid })
        : Results.BadRequest(new { message = outcome.Error });
});

app.MapPost("/api/login/logout", (LbsLoginManager login) =>
{
    var outcome = login.Logout();
    return outcome.Success
        ? Results.Ok(new { message = "站长已离站，登录 cookie 清空" })
        : Results.BadRequest(new { message = outcome.Error });
});

// 副登录（查询分流）：cookie 同样只进不出，回显只有昵称/mid/vip。
app.MapGet("/api/login/alt", (LbsAltLoginManager alt) =>
{
    var state = alt.GetState();
    return Results.Ok(new
    {
        isAlt = state is not null,
        name = state?.Name,
        mid = state?.Mid,
        vipStatus = state?.VipStatus,
        savedAtUtc = state?.SavedAtUtc,
    });
});

app.MapPost("/api/login/alt", async (LoginCookieRequest request, LbsAltLoginManager alt, CancellationToken cancellationToken) =>
{
    var (ok, error, state) = await alt.SetAsync(request.Cookie, cancellationToken);
    return ok
        ? Results.Ok(new { message = $"副号「{state!.Name}」已就位，新视频将两号轮转下载", name = state.Name, mid = state.Mid })
        : Results.BadRequest(new { message = error });
});

app.MapDelete("/api/login/alt", (LbsAltLoginManager alt) =>
{
    alt.Clear();
    return Results.Ok(new { message = "副号已移除，查询流量回到主号" });
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
// 保留：主/副登录（cookies 盘）、采集偏好与下载设置（settings KV + 引擎 json）。
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

    // 4. 资源文件清盘：视频库 + 缓存小图区 + e2e 临时件。cookies 与引擎设置不碰。
    var (files, bytes, busy) = PurgeDirectoryTree(paths.Videos);
    foreach (var dir in new[] { paths.Avatars, paths.Emotes, paths.CmtImg, paths.Cache })
    {
        var (f, b, z) = PurgeDirectoryTree(dir);
        files += f;
        bytes += b;
        busy += z;
    }

    var message = $"站已清空：取消 {cancelledTasks} 个引擎任务，销 {ledgerRows} 行台账、{engineRows} 行引擎历史，清掉 {files} 个文件（{(double)bytes / 1048576:F1} MB）";
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

// 采集偏好：mixed 跟随视频归属号；main/alt 强制单号（副号未配自动回落主号）。存分站库，重启不丢。
app.MapGet("/api/settings/collect", (StationDb db) =>
    Results.Ok(new { mode = db.GetSetting("collect.mode") ?? "mixed" }));

app.MapPost("/api/settings/collect", (CollectModeRequest request, StationDb db, LbsAccountRouter router) =>
{
    if (!Enum.TryParse<LbsCollectMode>(request.Mode, ignoreCase: true, out var mode))
    {
        return Results.BadRequest(new { message = "采集模式只认 mixed / main / alt" });
    }

    db.SetSetting("collect.mode", mode.ToString().ToLowerInvariant());
    router.CollectMode = mode;
    return Results.Ok(new { message = "采集偏好已更新", mode = mode.ToString().ToLowerInvariant() });
});

app.MapGet("/api/settings", (DataPaths paths) => Results.Ok(new
{
    dataDir = paths.Root,
    skin = new { bare = false, note = "裸分站皮肤 M4 到站" },
}));

if (File.Exists(Path.Combine(webRoot, "index.html")))
{
    app.MapFallbackToFile("index.html");
}

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LBS");

// 采集偏好回填：上次站长室选的模式跟着进程复活。
var stationRouter = app.Services.GetRequiredService<LbsAccountRouter>();
if (Enum.TryParse<LbsCollectMode>(app.Services.GetRequiredService<StationDb>().GetSetting("collect.mode") ?? "Mixed", ignoreCase: true, out var collectMode))
{
    stationRouter.CollectMode = collectMode;
}

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
internal sealed record LoginCookieRequest(string Cookie);
internal sealed record CollectModeRequest(string Mode);
internal sealed record PurgeRequest(bool Confirm);
internal sealed record SubscriptionItem(string Kind, string Target, string AddedAtUtc);
internal sealed record SubscriptionRequest(string Kind, string Target);
internal sealed record LocalDanmakuRequest(int PageIdx, double T, string Text);
internal sealed record OwnerKeyRequest(string? OldKey, string? NewKey);
internal sealed record OwnerEnterRequest(string Key);
internal sealed record SubsRequest(long Rpid, int Page);
internal sealed record CodecVersionRequest(string? Codec);
