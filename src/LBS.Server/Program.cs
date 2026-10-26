using System.Reflection;
using System.Text.Json;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
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
builder.Services.AddSingleton<LbsCommentArchiver>();
builder.Services.AddSingleton<LbsUpInfoFetcher>();
builder.Services.AddSingleton<CommentArchiveService>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<StatusSyncWorker>();

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
app.MapGet("/api/search", (string? q, string? sort, StationDb db) =>
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

    return Results.Ok(db.SearchVideos(trimmed, sort));
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

// 旧档案的后补：有 mid 但名片从未采过（up_face 为 NULL）时，开页顺手异步采一次，下次刷新就有。
// 失败也销账（置空串）；并发重复触发用 in-flight 集合挡掉。
System.Collections.Concurrent.ConcurrentDictionary<long, byte> upCardInFlight = new();
System.Collections.Concurrent.ConcurrentDictionary<long, byte> statInFlight = new();

app.MapGet("/api/watch/{bvid}", (string bvid, StationDb db, LbsUpInfoFetcher upInfo, ILoggerFactory loggerFactory) =>
{
    // BV 号是大小写敏感的 base62，先原样精确查，再补一记大写化兜底（照顾手打习惯）。
    var input = bvid.Trim();
    var data = db.GetWatchData(input) ?? db.GetWatchData(input.ToUpperInvariant());
    if (data is not null)
    {
        BackfillUpCard(db, upInfo, loggerFactory, data);
        BackfillStat(db, upInfo, loggerFactory, data);
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

void BackfillUpCard(StationDb db, LbsUpInfoFetcher upInfo, ILoggerFactory loggerFactory, WatchData data)
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
            var profile = await upInfo.FetchAsync(mid, CancellationToken.None);
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
void BackfillStat(StationDb db, LbsUpInfoFetcher upInfo, ILoggerFactory loggerFactory, WatchData data)
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
            var stat = await upInfo.FetchStatAsync(data.Bvid, CancellationToken.None);
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

app.MapDelete("/api/videos/{id:long}", async (long id, StationDb db, DataPaths paths, IDownloadTaskStateService state, IDownloadTaskLookup lookup, CancellationToken cancellationToken) =>
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

// 编码加档：给已到站馆藏点单另一个编码版本（avc/hevc/av1），后台整视频补货，
// 每分 P 一行账，完成后同步员扫盘定档；watch 接口的 codecVersions 随叫随查。
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

app.MapPost("/api/videos", async (AddVideoRequest request, StationDb db, DownloadQueue queue, CancellationToken cancellationToken) =>
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

    await queue.EnqueueAsync(outcome.VideoId, cancellationToken);
    return Results.Ok(new { videoId = outcome.VideoId, message = $"已加入进站队列，{bvid} 正在赶来" });
});

app.MapPost("/api/favorites/import", async (FavoritesImportRequest request, StationDb db, DownloadQueue queue, CancellationToken cancellationToken) =>
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

    var outcome = db.AddVideosBulk(videos);
    foreach (var videoId in outcome.InsertedIds)
    {
        await queue.EnqueueAsync(videoId, cancellationToken);
    }

    return Results.Ok(new
    {
        added = outcome.Added,
        duplicated = outcome.Duplicated,
        ignored,
        message = $"导入完成：新增 {outcome.Added} 条，重复 {outcome.Duplicated} 条，忽略 {ignored} 条非视频链接",
    });
});

app.MapPost("/api/favorites/import-url", async (FavoritesUrlImportRequest request, LbsFavoritesImport favorites, StationDb db, DownloadQueue queue, CancellationToken cancellationToken) =>
{
    var outcome = await favorites.ImportAsync(request.Url, cancellationToken);
    if (!outcome.Success)
    {
        return Results.BadRequest(new { message = outcome.Error });
    }

    var bulk = db.AddVideosBulk(outcome.Videos);
    foreach (var videoId in bulk.InsertedIds)
    {
        await queue.EnqueueAsync(videoId, cancellationToken);
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

app.MapGet("/api/login/status", async (LbsLoginManager login, CancellationToken cancellationToken) =>
{
    var state = await login.GetStateAsync(cancellationToken);
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
logger.LogInformation("小破分站开站：{Urls}，数据目录 {DataDir}，前端 {WebRoot}", urls, dataPaths.Root, webRoot);
app.Run();

internal sealed record HealthInfo(bool Ok, string Name, string Version, string DataDir, DateTimeOffset ServerTimeUtc);
internal sealed record AddVideoRequest(string Url);
internal sealed record FavoritesImportRequest(string Content);
internal sealed record FavoritesUrlImportRequest(string Url);
internal sealed record LoginCookieRequest(string Cookie);
internal sealed record SubsRequest(long Rpid, int Page);
internal sealed record CodecVersionRequest(string? Codec);
