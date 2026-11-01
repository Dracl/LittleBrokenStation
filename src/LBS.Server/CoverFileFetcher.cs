using DownKyi.Application.Bilibili;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>一趟封面重抓的三种下场（与端点契约的 replaced / kept / failed 一一对应）。</summary>
public enum CoverFetch
{
    /// <summary>新件已落盘并登记：文件换过主，产物行也齐。</summary>
    Replaced,

    /// <summary>没出去抓（封面链认不出、或没产物行可派生文件名）：旧件与旧行原样留着，也不算失败。</summary>
    Kept,

    /// <summary>出去抓了没成（含抓回空文件）：旧件与旧行同样原样留着，绝不因为「刷新」把封面弄没。</summary>
    Failed,
}

/// <summary>一趟封面重抓的结果：<see cref="Error"/> 只在 Failed 时非空，交调用方按自己的分寸记日志（补员记 Debug，重取记 Warning）。</summary>
public sealed record CoverFetchResult(CoverFetch Outcome, Exception? Error = null);

/// <summary>
/// 封面件的一趟「抓 → 写 .part → 验货 → 原地换主 → 登记」：<see cref="CoverBackfillWorker"/>（每小时补员）
/// 与 <see cref="MediaRefreshService"/>（稿件设置里那枚「重拉封面与媒体」）共用这一份实现，不许长第二份。
/// 口径钉死：
/// ① 先文件后行——缺件回炉（<see cref="MissingMediaReconciler"/>）拿 artifacts 行验盘，先插行会把「有行无件」误判成缺件；
/// ② 抓失败 / 抓回空文件时旧文件与旧产物行都不动，也不抛（封面是锦上添花，前端随时有热链兑底）；
/// ③ 同 rel 二次登记不产生第二行——<see cref="StationDb.RegisterCoverArtifact"/> 按 (video_id, rel_path) 幂等；
/// ④ 临时件永远不落到成品名下（.part 由静态区那道半成品闸兜住，这里再保证它随趟清掉）。
/// </summary>
public static class CoverFileFetcher
{
    private const string Referer = "https://www.bilibili.com";

    /// <summary>
    /// 抓 <paramref name="coverUrl"/> 落到 <paramref name="baseRel"/> 推出的封面名上，原地覆盖。
    /// 只有 <see cref="CoverFetch.Replaced"/> 会动到盘与台账；其余两种下场都保持原样。
    /// </summary>
    public static async Task<CoverFetchResult> TryReplaceAsync(
        LbsQueryClient client,
        StationDb db,
        DataPaths paths,
        long videoId,
        string? coverUrl,
        string? baseRel,
        CancellationToken cancellationToken)
    {
        if (NormalizeUrl(coverUrl) is not { } url || DeriveCoverRel(baseRel) is not { } rel)
        {
            // 封面链认不出或基名退化：这单今天办不了，不算失败也不建行（与补员一贯的克制）。
            return new CoverFetchResult(CoverFetch.Kept);
        }

        var dest = Path.Combine(paths.Videos, rel.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(dest);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        // 临时件落盘、验过货再换正主：半成品永远不会被 artifacts 行（和缺件回炉）看见。
        var part = dest + ".part";
        try
        {
            await client.DownloadFileAsync(
                new BilibiliHttpRequest(url, Referer, includeCredentials: false, includeBuvid: false), part, cancellationToken);
            if (new FileInfo(part).Length <= 0)
            {
                return new CoverFetchResult(CoverFetch.Failed, new IOException("封面抓回来是个空文件"));
            }

            File.Move(part, dest, overwrite: true);
            db.RegisterCoverArtifact(videoId, rel); // 先文件后行
            return new CoverFetchResult(CoverFetch.Replaced);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CoverFetchResult(CoverFetch.Failed, ex);
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>协议相对链补全（HttpClient 不吃裸 //），非 http(s) 一律认不出。</summary>
    public static string? NormalizeUrl(string? coverUrl)
    {
        if (coverUrl is null)
        {
            return null;
        }

        var trimmed = coverUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = "https:" + trimmed;
        }

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : null;
    }

    /// <summary>从同视频任一产物行 rel（正斜杠体）推封面 rel：目录不动，基名去扩展与 .Cover 中缀再挂 .Cover.jpg
    /// （现存产物如 xxx- BV1xx.mp4 → xxx- BV1xx.Cover.jpg，与 downkyi 同族）。</summary>
    public static string? DeriveCoverRel(string? baseRel)
    {
        if (string.IsNullOrWhiteSpace(baseRel))
        {
            return null;
        }

        var normalized = baseRel.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var dir = slash >= 0 ? normalized[..(slash + 1)] : string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(normalized);
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
