using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Video;

namespace DownKyi.Services.Video;

// LBS fork: this file was restored from the upstream public source
// (https://github.com/crazysmile-PhD/downkyicore, main branch, same path) after the Worker extraction
// accidentally removed the only local copy. It is kept in DownKyi.Desktop because only the desktop
// add-to-download flow (VideoInfoService / DownloadPreparationService) uses it; the download runtime in
// DownKyi.Worker reaches tags through VideoPage.LoadTagsAsync instead.
internal interface IVideoTagProvider
{
    Task<IReadOnlyList<string>> GetTagsAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken);
}

internal sealed class VideoTagProvider : IVideoTagProvider
{
    private readonly IBilibiliApiClient _client;

    public VideoTagProvider(IBilibiliApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bvid);
        cancellationToken.ThrowIfCancellationRequested();
        var tags = await _client.GetBiliTagInfoAsync(bvid, cid, cancellationToken)
            .ConfigureAwait(false);
        return tags?
            .Where(static tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
            .Select(static tag => tag.TagName)
            .ToArray()
            ?? Array.Empty<string>();
    }
}
