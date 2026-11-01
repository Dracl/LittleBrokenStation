using LBS.Infrastructure;
using Microsoft.Extensions.FileProviders;

namespace LBS.Server;

/// <summary>
/// 封面图的缓存口径。<c>/media</c> 那条静态管道从不设 <c>Cache-Control</c>（浏览器走启发式缓存），
/// 而封面名是确定性推导的（<c>&lt;基名&gt;.Cover.jpg</c>）——重抓换了内容不换 URL，浏览器就一直给你旧图。
/// 这里只给图片扩展名（.jpg/.jpeg/.png/.webp/.gif）上 <c>no-cache</c> + ETag，能 304 就 304；
/// <b>正片与音轨一律不碰</b>：那边有分轨 range 请求，加 revalidation 会搅坏播放。
/// 两处配合、与同处已有的两道闸是<b>叠加</b>不是覆盖：
/// <see cref="TryHandleNotModified"/> 挂在静态区之前那道 <c>app.Use</c> 的末尾（半成品闸与私有保活都放行之后），
/// 命中 If-None-Match 就就地 304，不必把整张图再端一遍；
/// <see cref="PrepareResponse"/> 挂在 /media 的 <c>OnPrepareResponse</c> 末尾（同样排在两处 404 判定之后），
/// 只给真发出去的响应贴头——被挡掉的响应不配拿缓存头。
/// </summary>
internal static class MediaCachePolicy
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    private const string Prefix = "/media/";

    /// <summary>这条请求是要取封面图吗（只管 GET/HEAD 与 /media 下的图片扩展名；正片与音轨不在话下）。</summary>
    public static bool IsCoverImageRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && IsImage(request.Path.Value);

    /// <summary>是不是 /media 下的图片路径（扩展名白名单，大小写不认生）。</summary>
    public static bool IsImage(string? path) =>
        !string.IsNullOrEmpty(path)
        && path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 带 If-None-Match 且与盘上这份对上了 → 就地 304（回 true = 这趟已答完，别再往下走静态区）。
    /// 件不在、对不上、不是图片都回 false，交给后面的中间件按老路办（包括它们的 404）。
    /// </summary>
    public static bool TryHandleNotModified(HttpContext context, DataPaths paths)
    {
        if (!IsCoverImageRequest(context.Request))
        {
            return false;
        }

        var candidate = context.Request.Headers.IfNoneMatch.ToString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (Resolve(context.Request.Path.Value!, paths.Videos) is not { } file)
        {
            return false;
        }

        var etag = BuildEtag(file.LastWriteTimeUtc.Ticks, file.Length);
        if (!Hits(candidate, etag))
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status304NotModified;
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = etag;
        return true;
    }

    /// <summary>给真发出去的封面图贴 no-cache + ETag（其余扩展名一个头都不加）。用 IFileInfo 的mtime/长度，不再多扰一次盘。</summary>
    public static void PrepareResponse(HttpContext context, IFileInfo file)
    {
        if (!IsImage(context.Request.Path.Value))
        {
            return;
        }

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = BuildEtag(file.LastModified.UtcTicks, file.Length);
    }

    /// <summary>强 ETag：盘上这份的 mtime + 字节数。重抓走 File.Move(overwrite) 原地换主，两个数必变，不用记账。</summary>
    private static string BuildEtag(long lastWriteUtcTicks, long length) => $"\"{lastWriteUtcTicks:x}-{length:x}\"";

    private static bool Hits(string candidate, string etag)
    {
        foreach (var item in candidate.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (item == "*" || string.Equals(item, etag, StringComparison.Ordinal)
                || string.Equals(item.TrimStart('W', '/'), etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把 /media/xxx 还原成 data/videos 下的实物；想越狱出视频库的一律当「没这东西」办（与缺件回炉同一克制）。</summary>
    private static FileInfo? Resolve(string requestPath, string videosRoot)
    {
        if (!requestPath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = Uri.UnescapeDataString(requestPath[Prefix.Length..])
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var rootFull = Path.GetFullPath(videosRoot) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(videosRoot, relative));
        return full.StartsWith(rootFull, StringComparison.Ordinal) && File.Exists(full) ? new FileInfo(full) : null;
    }
}
