using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站长室「分享链接设置」API：写全站分享的门面地址（分享链接、海报链接文字、周报码文与图链的链接头）。
/// 值存分站自有 settings KV（与备份节奏同表）；读侧走 GET /api/settings——分享出去的链接本就抄得走，
/// 不是站长情报，公开无妨；写侧只有这一口，落在 /api/admin/ 前缀由站长门自动盖。
/// </summary>
public static class ShareLinkEndpoints
{
    /// <summary>settings 表键名：分享链接门面地址（scheme://host[:port]，不带路径）。</summary>
    public const string SettingKey = "share-base-url";

    public static void MapShareLinkEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/settings/share-base-url", (ShareBaseUrlRequest? request, StationDb db) =>
        {
            var raw = request?.Url;
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 空值即恢复默认：删键，前端读回 null 就回到「跟随访问地址」。
                db.RemoveSetting(SettingKey);
                return Results.Ok(new { shareBaseUrl = (string?)null });
            }

            if (!TryNormalize(raw, out var normalized, out var error))
            {
                return Results.BadRequest(new { message = error });
            }

            db.SetSetting(SettingKey, normalized);
            return Results.Ok(new { shareBaseUrl = normalized });
        });
    }

    public sealed record ShareBaseUrlRequest(string? Url);

    /// <summary>
    /// 只收 scheme://host[:port]：http(s) 协议、不许带路径/查询/片段/用户信息，尾斜杠剃掉。
    /// 判据在前端（AdminPage 分享卡）有一份等样的镜像，这里才是真闸——两边不同字，锅在这边背。
    /// </summary>
    internal static bool TryNormalize(string raw, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        // 尾斜杠是书写习惯不是路径：先剃净再验形（否则 https://host/// 会被 AbsolutePath 判成带路径打回，
        // 而前端同款判据早就把它归一放行了——两边不同字时，被误伤的总是照前端写习惯的人）。
        var trimmed = raw.Trim().TrimEnd('/');
        if (trimmed.Length > 300)
        {
            error = "地址太长了，不像门面";
            return false;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri is null)
        {
            error = "只收 http(s):// 地址，认不出来";
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "只收 http(s):// 地址";
            return false;
        }
        if (uri.UserInfo.Length > 0)
        {
            error = "地址里不许夹用户信息";
            return false;
        }
        if (uri.Host.Length == 0)
        {
            error = "域名没填，光有协议头不行";
            return false;
        }
        // 站点根在 Uri 里 AbsolutePath 就是 "/"；还剩下路径/查询/片段，说明递来的是某页的链接而不是站根。
        if (uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "只填到端口为止，不许带路径、查询或锚点";
            return false;
        }
        normalized = $"{uri.Scheme}://{uri.Authority}";
        return true;
    }
}
