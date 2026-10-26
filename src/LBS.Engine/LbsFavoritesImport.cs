using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.BiliApi.Favorites.Models;
using LBS.Domain;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>收藏夹链接导入的结果：Success=false 时 Error 是给站长看的中文说明。</summary>
public sealed record FavoritesImportOutcome(bool Success, string? Error, IReadOnlyList<NewVideo> Videos, int Ignored)
{
    public static FavoritesImportOutcome Fail(string error) => new(false, error, Array.Empty<NewVideo>(), 0);
}

/// <summary>
/// 收藏夹链接导入：吃 favlist 分享链接或纯收藏夹 ID，走 downkyicore 的收藏夹接口整架拉回，
/// 产出可直接入站的 NewVideo 列表（去重与入队归 StationDb/DownloadQueue 管，这里不碰库）。
/// 非视频条目（番剧链接等）计入 Ignored，不硬塞。
/// </summary>
public sealed class LbsFavoritesImport(LbsQueryClient client, ILogger<LbsFavoritesImport> logger)
{
    public async Task<FavoritesImportOutcome> ImportAsync(string urlOrId, CancellationToken cancellationToken)
    {
        var input = NormalizeToFavoritesId((urlOrId ?? string.Empty).Trim());
        if (string.IsNullOrEmpty(input))
        {
            return FavoritesImportOutcome.Fail("得给我一个收藏夹链接（形如 space.bilibili.com/…/favlist?fid=123）或收藏夹 ID。");
        }

        if (!ParseEntrance.IsFavoritesUrl(input) && !ParseEntrance.IsFavoritesId(input))
        {
            return FavoritesImportOutcome.Fail("这不像一个收藏夹链接，也不是收藏夹 ID。");
        }

        var mediaId = ParseEntrance.GetFavoritesId(input);
        if (mediaId <= 0)
        {
            return FavoritesImportOutcome.Fail("认不出这个收藏夹 ID，检查下链接是不是复制全了。");
        }

        try
        {
            var medias = await client.GetAllFavoritesMediaAsync(mediaId, cancellationToken);
            if (medias.Count == 0)
            {
                return FavoritesImportOutcome.Fail("这个收藏夹是空的，或者要登录才看得到内容。");
            }

            var videos = PickVideos(medias, out var ignored);
            if (videos.Count == 0)
            {
                return FavoritesImportOutcome.Fail($"收藏夹里 {medias.Count} 个条目都没有可用的普通视频（下架、番剧或无 BV 号），分站这次没收。");
            }

            logger.LogInformation("收藏夹 {MediaId} 拉回 {Total} 个条目，可入站 {Count} 条", mediaId, medias.Count, videos.Count);
            return new FavoritesImportOutcome(true, null, videos, ignored);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "拉取收藏夹 {MediaId} 失败", mediaId);
            return FavoritesImportOutcome.Fail($"拉收藏夹没成功：{ex.Message}");
        }
    }

    /// <summary>
    /// 从收藏夹条目里拣出可入站的普通视频。
    /// v3 资源接口的 link 字段可能是 bilibili://video/&lt;aid&gt; 这种 app 深链，不能当 URL 使；
    /// 正身是 bv_id/bvid，两个字段谁有用谁，link 只作兑底。
    /// attr 非 0 是上游标了下架/失效的，直接进 Ignored，不收进来再等下载失败。
    /// </summary>
    public static IReadOnlyList<NewVideo> PickVideos(IEnumerable<FavoritesMedia> medias, out int ignored)
    {
        var videos = new List<NewVideo>();
        ignored = 0;
        foreach (var media in medias)
        {
            if (media.Attr != 0)
            {
                ignored++;
                continue;
            }

            string? bvid = null;
            foreach (var candidate in new[] { media.Bvid, media.LegacyBvid, media.Link })
            {
                if (!string.IsNullOrWhiteSpace(candidate) && BilibiliLink.TryExtractBvid(candidate, out var extracted))
                {
                    bvid = extracted;
                    break;
                }
            }

            if (bvid == null)
            {
                ignored++;
                continue;
            }

            // link 能当正常网页链接使就原样存，否则按 BV 号合成标准地址
            var link = Uri.TryCreate(media.Link, UriKind.Absolute, out var uri)
                       && BilibiliLink.IsBilibiliHost(uri.Host)
                ? media.Link
                : $"https://www.bilibili.com/video/{bvid}";

            // 投稿时间取 pubtime，老接口没给就退 ctime（都远好于入站时间）。
            var pubUnix = media.Pubtime > 0 ? media.Pubtime : media.Ctime;
            videos.Add(new NewVideo(link, bvid,
                string.IsNullOrWhiteSpace(media.Title) ? null : media.Title,
                string.IsNullOrWhiteSpace(media.Cover) ? null : media.Cover,
                pubUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(pubUnix).ToString("O") : null));
        }

        return videos;
    }

    /// <summary>
    /// 把站长能从浏览器地址栏复制到的各种写法归一成上游 ParseEntrance 认得的 ml&lt;数字&gt; 形态：
    /// 纯数字 fid、space.bilibili.com/…/favlist?fid=123（或 ml123）都在这里翻译，
    /// 其余原样交给上游自己判断（www.bilibili.com/medialist/… 这类链接它本来就认）。
    /// </summary>
    public static string NormalizeToFavoritesId(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        // 纯数字 fid：直接补 ml 前缀
        if (long.TryParse(input, out _))
        {
            return "ml" + input;
        }

        // favlist 链接：取查询串里的 fid 参数（地址栏复制就是这种）
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2 || !kv[0].Equals("fid", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fid = kv[1].Trim();
                if (fid.StartsWith("ml", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(fid.AsSpan(2), out _))
                {
                    return fid.ToLowerInvariant();
                }

                if (long.TryParse(fid, out _))
                {
                    return "ml" + fid;
                }
            }
        }

        return input;
    }
}
