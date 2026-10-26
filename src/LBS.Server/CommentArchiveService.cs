using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>评论归档结果：Ok 为假时 Message 说明原因。</summary>
public sealed record CommentArchiveOutcome(bool Ok, string Message, int Count);

/// <summary>
/// 评论归档编排：查台账 → 调抓取器 → 把快照写在第一个分 P 的成片旁边（{base}.comments.json），
/// 并在 artifacts 表登记 kind='comments'。所有异常都吞进 Outcome，调用方放心 fire-and-forget。
/// </summary>
public sealed class CommentArchiveService(
    StationDb db,
    DataPaths paths,
    LbsCommentArchiver archiver,
    LbsOwnerPolicy policy,
    ILogger<CommentArchiveService> logger)
{
    public async Task<CommentArchiveOutcome> TryArchiveAsync(long videoId, bool force, CancellationToken cancellationToken)
    {
        try
        {
            var brief = db.GetVideoBrief(videoId);
            if (brief is null)
            {
                return new CommentArchiveOutcome(false, "馆藏里没有这个 id", 0);
            }

            if (brief.Value.Status != VideoStatus.Arrived)
            {
                return new CommentArchiveOutcome(false, "这条还没到站，等成片落盘再采评论", 0);
            }

            if (!force && db.GetArtifactRel(videoId, "comments") is not null)
            {
                return new CommentArchiveOutcome(true, "评论快照已存在，未重复采集", 0);
            }

            var basePath = db.GetFirstPageBasePath(videoId);
            if (basePath is null)
            {
                return new CommentArchiveOutcome(false, "还没有分 P 记录，评论快照无处安放", 0);
            }

            var snapshot = await policy.RunForVideoAsync(videoId, () => archiver.FetchAsync(brief.Value.Bvid, brief.Value.Aid, cancellationToken));
            if (snapshot is null)
            {
                return new CommentArchiveOutcome(false, "评论采集未成功，分站日志里有详情", 0);
            }

            if (brief.Value.Aid is null && snapshot.Aid > 0)
            {
                db.SaveAid(videoId, snapshot.Aid);
            }

            var target = basePath + ".comments.json";
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, LbsCommentArchiver.Serialize(snapshot));
            db.RegisterCommentsArtifact(videoId, ToRelative(target, paths.Videos));

            var count = snapshot.Replies.Count + (snapshot.Top is null ? 0 : 1);
            logger.LogInformation("馆藏 {VideoId} 评论快照落盘：主楼 {Count} 条，B 站报总数 {Total}", videoId, count, snapshot.Total);
            return new CommentArchiveOutcome(true, $"「{brief.Value.Bvid}」评论已归档：主楼 {count} 条", count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "馆藏 {VideoId} 评论归档炸了（不影响馆藏）", videoId);
            return new CommentArchiveOutcome(false, "评论归档出错，分站日志里有详情", 0);
        }
    }

    /// <summary>观看页点开楼中楼时的按需补采：拉 B 站这一楼的第 page 页，并回快照文件。</summary>
    public async Task<(bool Ok, string Message, IReadOnlyList<CommentItem> Subs, bool HasMore)> FetchSubsForVideoAsync(
        long videoId,
        long rpid,
        int page,
        CancellationToken cancellationToken)
    {
        const string empty = "这一楼没有回复";
        try
        {
            var brief = db.GetVideoBrief(videoId);
            if (brief is null)
            {
                return (false, "馆藏里没有这个 id", [], false);
            }

            var snapshotPath = ResolveSnapshotPath(videoId);
            var snapshot = snapshotPath is null ? null : CommentSnapshotOps.Load(snapshotPath);
            if (snapshot is null)
            {
                return (false, "这条还没有评论快照，先去首页点一下补采气泡", [], false);
            }

            var aid = brief.Value.Aid ?? snapshot.Aid;
            var subs = await policy.RunForVideoAsync(videoId, () => archiver.FetchSubsAsync(aid, rpid, Math.Max(1, page), cancellationToken));
            if (subs.Count == 0 && page <= 1)
            {
                return (false, empty, [], false);
            }

            var (updated, found) = CommentSnapshotOps.MergeSubs(snapshot, rpid, subs, page);
            if (!found)
            {
                return (false, "快照里没有这一楼", [], false);
            }

            CommentSnapshotOps.Save(snapshotPath!, updated);

            var target = FindItem(updated, rpid);
            var loaded = target?.Sub?.Count ?? 0;
            var hasMore = target?.ReplyCount is long rc && rc > loaded;
            return (true, "ok", target?.Sub ?? [], hasMore);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "馆藏 {VideoId} 楼中楼补采出错", videoId);
            return (false, "楼中楼补采出错，分站日志里有详情", [], false);
        }
    }

    private string? ResolveSnapshotPath(long videoId)
    {
        var rel = db.GetArtifactRel(videoId, "comments");
        if (rel is not null)
        {
            var abs = Path.GetFullPath(Path.Combine(paths.Videos, rel.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(paths.Videos).TrimEnd(Path.DirectorySeparatorChar);
            if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(abs))
            {
                return abs;
            }
        }

        var basePath = db.GetFirstPageBasePath(videoId);
        if (basePath is null)
        {
            return null;
        }

        var fallback = basePath + ".comments.json";
        return File.Exists(fallback) ? fallback : null;
    }

    private static CommentItem? FindItem(CommentSnapshot snapshot, long rpid) =>
        snapshot.Top is { } top && top.Rpid == rpid
            ? top
            : snapshot.Replies.FirstOrDefault(r => r.Rpid == rpid);

    private static string ToRelative(string file, string videosRoot)
    {
        var full = Path.GetFullPath(file);
        var root = Path.GetFullPath(videosRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[(root.Length + 1)..]
            : Path.GetFileName(full);
        return rel.Replace('\\', '/');
    }
}
