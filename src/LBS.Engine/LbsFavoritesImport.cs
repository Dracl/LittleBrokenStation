using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Favorites;
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
public sealed class LbsFavoritesImport(IBilibiliApiClient client, ILogger<LbsFavoritesImport> logger)
{
    public async Task<FavoritesImportOutcome> ImportAsync(string urlOrId, CancellationToken cancellationToken)
    {
        var input = (urlOrId ?? string.Empty).Trim();
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

            var videos = new List<NewVideo>();
            var ignored = 0;
            foreach (var media in medias)
            {
                if (Uri.TryCreate(media.Link, UriKind.Absolute, out var uri)
                    && BilibiliLink.IsBilibiliHost(uri.Host)
                    && BilibiliLink.TryExtractBvid(media.Link, out var bvid))
                {
                    videos.Add(new NewVideo(media.Link, bvid, string.IsNullOrWhiteSpace(media.Title) ? null : media.Title));
                }
                else
                {
                    ignored++;
                }
            }

            if (videos.Count == 0)
            {
                return FavoritesImportOutcome.Fail($"收藏夹里 {medias.Count} 个条目都不是普通视频链接，分站这次没收。");
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
}
