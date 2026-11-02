using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 封面缩略切片器（站长 11-03「缩略图要本地原图裁切，每次入站的时候一次性切完，重新拉封面的时候也要裁」）。
///
/// 形状照 <see cref="CoverFileFetcher"/>：纯静态、传 <see cref="DataPaths"/>、<b>先文件后台账</b>、失败不动旧的、
/// 全程不抛（后台补员与同步链都调它，为一枚缩略掀翻一条稿件的入库不划算）。
/// 档位表在 <see cref="CoverThumbTiers"/>，前端 <c>web/src/utils/coverSrc.js</c> 与它一字对齐。
///
/// 三条来路都收口到这里：<c>CoverFileFetcher.TryReplaceAsync</c>（补员与重拉共用）与
/// <c>StatusSyncWorker</c> 的扫盘之后（引擎首下的封面由 <c>FinalizeArtifacts</c> 扫出来）。
/// 存量全量回填由 <c>CoverThumbBackfillWorker</c> 按同一入口补齐，不另写第二份实现。
/// </summary>
public static class CoverThumbs
{
    /// <summary>一次切片的结果。Arrived 是本次落成的档数（0 表示一档都没切成）。</summary>
    public sealed record BuildOutcome(bool Built, int Arrived, string? Error = null);

    /// <summary>没切成也算正常回，调用方只记日志，不当失败上抛。</summary>
    public static readonly BuildOutcome Nothing = new(false, 0);

    /// <summary>
    /// 给一条稿件把五档缩略切齐。原图不在、读不出尺寸、ffmpeg 没装，都只回一句原因，绝不抛。
    /// 已存在的同名文件直接记账跳过（重扫安全，补员员每小时来一遍也不会重切）。
    /// </summary>
    public static async Task<BuildOutcome> TryBuildAsync(StationDb db, DataPaths paths, long videoId, CancellationToken cancellationToken)
    {
        var coverRel = db.GetCoverRel(videoId);
        if (string.IsNullOrEmpty(coverRel))
        {
            return Nothing with { Error = "还没有封面产物行" };
        }

        var bvid = db.GetBvid(videoId);
        if (!IsSafeBvid(bvid))
        {
            return Nothing with { Error = "bvid 缺失或含非法字符，造不出可反查归属的文件名" };
        }

        var source = Path.Combine(paths.Videos, coverRel!.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(source))
        {
            return Nothing with { Error = $"封面原图不在盘上（{coverRel}）" };
        }

        var dims = await Ffmpeg.ProbeSizeAsync(source, cancellationToken);
        if (dims is null)
        {
            return Nothing with { Error = "读不到封面尺寸（ffprobe 没答应）" };
        }

        FileInfo info;
        try
        {
            info = new FileInfo(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Nothing with { Error = $"读不到封面文件信息：{ex.Message}" };
        }

        var stamp = CoverThumbTiers.StampOf(info.LastWriteTimeUtc.Ticks, info.Length);
        var format = await Ffmpeg.HasWebpEncoderAsync(cancellationToken) ? CoverThumbTiers.FormatWebp : CoverThumbTiers.FormatJpg;
        // 原图换过一次内容（重拉封面）→ 版本戳变了 → 旧名字的件全是垃圾，先扫干净再切，
        // 不然 data/thumbs 只涨不消，而旧件还能被老 URL 长期命中。
        PruneStaleFiles(paths, bvid!, stamp);

        var arrived = 0;
        string? lastError = null;
        foreach (var tier in CoverThumbTiers.All)
        {
            // 不放大（站长铁律 11-03：「缩略图宁愿大一点缩小，也不要小一点放大」）：档位由小到大排，
            // 只要有一维比原图窄/矮就整排收手。只看宽会漏掉扁图——16:9 的源比 16:10 的档更扁，
            // 800×450 切 768×480 那一档时 ffmpeg 的 force_original_aspect_ratio=increase 会把高从 450 抬到 480，
            // 那就是一次放大（1.07 倍，眼睛难辨而契约已破）。宽高一起判，让它退到 512 档或原图。
            if (tier.Width > dims.Value.Width || tier.Height > dims.Value.Height)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var name = CoverThumbTiers.FileName(bvid!, tier, stamp, format);
            var dest = Path.Combine(paths.Thumbs, name);
            if (File.Exists(dest))
            {
                db.RegisterCoverThumbArrived(videoId, name, tier.Token, stamp, format);
                arrived += 1;
                continue;
            }

            try
            {
                Directory.CreateDirectory(paths.Thumbs);
                Directory.CreateDirectory(paths.ThumbsStaging);
                // 临时件留在 .staging 里且保留真后缀：后缀是 ffmpeg 认输出格式的依据（照预览那套要嘴上补 -f 的教训反着办），
                // 点开头的目录名又让 /thumbs 的半成品闸把它一律 404，切到一半的图永不可能被浏览器捡到。
                var part = Path.Combine(paths.ThumbsStaging, $"{Guid.NewGuid():N}-{name}");
                var error = await Ffmpeg.EncodeCoverTileAsync(source, part, tier.Width, tier.Height, format, cancellationToken);
                if (error is null && (!File.Exists(part) || new FileInfo(part).Length <= 0))
                {
                    error = "切出来是个空文件";
                }

                if (error is not null)
                {
                    TryDelete(part);
                    lastError = error;
                    db.MarkCoverThumbFailed(videoId, tier.Token, error);
                    continue;
                }

                File.Move(part, dest, overwrite: true);
                db.RegisterCoverThumbArrived(videoId, name, tier.Token, stamp, format);
                arrived += 1;
            }
            catch (OperationCanceledException)
            {
                throw; // 站点关停：该往上抛就得抛，别伪装成「切砸了」
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex.Message;
                db.MarkCoverThumbFailed(videoId, tier.Token, ex.Message);
            }
        }

        return new BuildOutcome(arrived > 0, arrived, arrived > 0 ? null : lastError);
    }

    /// <summary>
    /// 删掉某条稿件的全部缩略件（删稿、进回收站、站长手动重切都用它）。台账行由调用方清。
    /// 目录不存在或文件被短暂占用都算正常，回个删掉几枚做日志。
    /// </summary>
    public static int DeleteFiles(DataPaths paths, string? bvid)
    {
        if (!IsSafeBvid(bvid) || !Directory.Exists(paths.Thumbs))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var extension in new[] { CoverThumbTiers.FormatWebp, CoverThumbTiers.FormatJpg })
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(paths.Thumbs, $"{bvid}*.{extension}"))
                {
                    TryDelete(file);
                    deleted += 1;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 扫不动就算了：缩略是派生物，留下的最多是几 KB 的孤儿，不值得掀翻删除流程
            }
        }

        return deleted;
    }

    /// <summary>把带旧版本戳的同名缩略扫掉（原图重拉后旧 URL 再也不会被拼出来，留着只是占盘）。</summary>
    private static void PruneStaleFiles(DataPaths paths, string bvid, string stamp)
    {
        if (!Directory.Exists(paths.Thumbs))
        {
            return;
        }

        foreach (var extension in new[] { CoverThumbTiers.FormatWebp, CoverThumbTiers.FormatJpg })
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(paths.Thumbs, $"{bvid}*.{extension}"))
                {
                    if (!Path.GetFileName(file).Contains($"-{stamp}.", StringComparison.Ordinal))
                    {
                        TryDelete(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清不动不拦切片
            }
        }
    }

    /// <summary>文件名要直接进静态区和台账的反查列，所以只认字母数字：宁可少切，也不让脏 bvid 拼出个越狱路径。</summary>
    private static bool IsSafeBvid(string? bvid)
        => !string.IsNullOrWhiteSpace(bvid)
           && bvid.Length is >= 10 and <= 24
           && bvid.All(char.IsAsciiLetterOrDigit);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时件留个尸首，下次同名再切会另起一个 guid，不影响正确性
        }
    }
}
