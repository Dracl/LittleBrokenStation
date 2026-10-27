using DownKyi.Application.Bilibili;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 封面补员：给到站时没勾封面的视频从 cover_url 批量补下封面原图——
/// 开机延时 20 秒补首轮，之后每小时一轮；每轮封顶 40 条、条间 300ms 节流（动态评论巡捕同款节奏，B 站风控友好）。
/// 文件落 data/videos、与 downkyi 产物同族命名（&lt;基目录&gt;/&lt;基名&gt;.Cover.jpg，基名从该视频任一产物行 rel 推导），
/// 文件先落盘、再进 artifacts 行：缺件回炉（MissingMediaReconciler）拿 artifacts 行验盘，先插行会把「有行无件」误判整套回炉。
/// 失败不建行、下轮重试——封面是锦上添花，前端随时有热链兑底，不许挡任何人的路。
/// </summary>
public sealed class CoverBackfillWorker(
    StationDb db,
    LbsQueryClient client,
    DataPaths paths,
    ILogger<CoverBackfillWorker> logger) : BackgroundService
{
    private const int BatchPerRound = 40;
    private const string Referer = "https://www.bilibili.com";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("封面补员到岗，20 秒后补首轮，之后每小时一轮");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            await RunRoundAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "封面补员首轮没跑成，等例行轮");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                await RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "封面补员这轮出错，下一轮再试");
            }
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        var queue = db.ListVideosMissingCover(BatchPerRound);
        if (queue.Count == 0)
        {
            return;
        }

        var filled = 0;
        foreach (var candidate in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (NormalizeUrl(candidate.CoverUrl) is not { } url || DeriveCoverRel(candidate.BaseRel) is not { } rel)
                {
                    continue; // 封面链认不出或基名退化：这单今天办不了，不算失败也不建行
                }

                var dest = Path.Combine(paths.Videos, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // 临时件落盘、验过货再换正主：半成品永远不会被 artifacts 行（和缺件回炉）看见。
                var part = dest + ".part";
                try
                {
                    await client.DownloadFileAsync(new BilibiliHttpRequest(url, Referer, includeCredentials: false, includeBuvid: false), part, cancellationToken);
                    if (new FileInfo(part).Length > 0)
                    {
                        File.Move(part, dest, overwrite: true);
                        db.RegisterCoverArtifact(candidate.VideoId, rel); // 先文件后行
                        filled++;
                    }
                }
                finally
                {
                    TryDelete(part);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "封面补员：{Bvid} 这趟没下载成，下轮再来", candidate.Bvid);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        logger.LogInformation("封面补员：本轮补齐 {Filled}/{Taken} 条", filled, queue.Count);
    }

    private static string? NormalizeUrl(string coverUrl)
    {
        var trimmed = coverUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = "https:" + trimmed; // 协议相对链补全，HttpClient 不吃裸 //
        }

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : null;
    }

    /// <summary>从同视频任一产物行 rel（正斜杠体）推封面 rel：目录不动，基名去扩展与 .Cover 中缀再挂 .Cover.jpg
    /// （现存产物如 xxx- BV1xx.mp4 → xxx- BV1xx.Cover.jpg，与 downkyi 同族）。</summary>
    private static string? DeriveCoverRel(string baseRel)
    {
        var slash = baseRel.LastIndexOf('/');
        var dir = slash >= 0 ? baseRel[..(slash + 1)] : string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(baseRel);
        if (fileName.EndsWith(".Cover", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^".Cover".Length];
        }

        return string.IsNullOrWhiteSpace(fileName) ? null : dir + fileName + ".Cover.jpg";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            /* 临时件删不掉就随它去：没有行，谁也当它不存在 */
        }
    }
}
