using System.Text.Json;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 动态巡捕（M6）：上岗先补一轮（错峰 LedgerBackup 的 2 分钟，站刚开别急着出号），
/// 从订阅台账灌一次 UP 种子，之后每小时前后随机抖 10 分钟逐 enabled target 拉动态。
/// 一切异常只 log 不炸站；风控信号交给 LbsApiThrottle 的统一熔断，这里只管弃轮。
/// 站长室「立即巡一轮」走 TryStartOnce：内存闸防重入，busy 时直接回绝。
/// </summary>
public sealed class DynamicFetchWorker(
    StationDb db,
    LbsDynamicFetcher fetcher,
    LbsCommentArchiver assetCache,
    LbsUpInfoFetcher upInfo,
    LbsCollectPolicy policy,
    ILogger<DynamicFetchWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim roundGate = new(1, 1);
    private int manualBusy;

    /// <summary>图片资产类 JSON（pics_local_json / body_emotes_json / pictures_*_json）统一 camelCase 存。</summary>
    private static readonly JsonSerializerOptions AssetJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("动态巡捕上岗，90 秒后灌种子补首轮，之后每小时抖一抖");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
            var seeded = db.SeedTargetsFromSubscriptions();
            if (seeded > 0)
            {
                logger.LogInformation("动态关注名单从订阅台账灌进 {Seeded} 位 UP", seeded);
            }

            await RunRoundAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "动态首轮没跑成，等例行巡视");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // 1h ± 10min 抖动（仿订阅爬工）：固定周期的爬工在 B 站日志里是条直线。
            var nextRound = TimeSpan.FromMinutes(Random.Shared.Next(50, 71));
            try
            {
                await Task.Delay(nextRound, stoppingToken);
                await RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "动态巡视出错，下一轮再试");
            }
        }
    }

    /// <summary>站长手动催一轮：进不来闸就回 false（防重入），成功起跑回 true。</summary>
    public bool TryStartOnce(long? onlyMid)
    {
        if (Interlocked.CompareExchange(ref manualBusy, 1, 0) != 0)
        {
            return false;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunRoundAsync(CancellationToken.None, onlyMid);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "站长催的这轮动态没跑成");
                }
                finally
                {
                    Interlocked.Exchange(ref manualBusy, 0);
                }
            });
        return true;
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken, long? onlyMid = null)
    {
        await roundGate.WaitAsync(cancellationToken);
        try
        {
            // 没号不开工：动态、评论这些采集全挂在采集号上，空着出去只能挨 -101，还白耗一轮节流窗口。
            if (!policy.HasAccount())
            {
                logger.LogInformation("动态巡捕本轮歇脚：站里没有可用的 B 站号（成员号池空了或全被判失效）");
                return;
            }

            var freshIds = new List<(string DynamicId, long? CommentType, string? CommentOid)>();
            var freshDynamics = new List<ParsedDynamic>();
            foreach (var target in db.ListDynamicTargets())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!target.Enabled || (onlyMid is not null && target.Mid != onlyMid.Value))
                {
                    continue;
                }

                try
                {
                    var outcome = await policy.RunForActivityAsync(() => fetcher.FetchTargetAsync(
                        target.Mid, target.LastOffset,
                        parsed =>
                        {
                            var fresh = db.UpsertDynamic(
                                parsed.DynamicId, target.Mid, parsed.AuthorMid, parsed.AuthorName,
                                parsed.Type, parsed.Text,
                                // pics_json 走 AssetJson（camelCase）+ ParsedPic 的 [JsonPropertyName] 双保险：首轮实跑曾拿默认 options 裸奔，入库成了 PascalCase。
                                parsed.Pics.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(parsed.Pics, AssetJson) : null,
                                parsed.Bvid, parsed.PublishedAtUtc,
                                parsed.AuthorFaceUrl, parsed.ArchiveTitle, parsed.ArchiveCover, parsed.ArchiveDuration,
                                parsed.StatComment, parsed.StatForward, parsed.StatLike,
                                parsed.OrigAuthorMid, parsed.OrigAuthorName, parsed.OrigAuthorFace,
                                parsed.CommentType, parsed.CommentOid);
                            if (fresh)
                            {
                                freshIds.Add((parsed.DynamicId, parsed.CommentType, parsed.CommentOid));
                                freshDynamics.Add(parsed);
                            }

                            return Task.FromResult(fresh);
                        },
                        cancellationToken));
                    if (outcome.Ok)
                    {
                        db.SetTargetOffset(target.Mid, outcome.Offset, DateTimeOffset.UtcNow.ToString("O"));
                        if (outcome.Fresh > 0)
                        {
                            logger.LogInformation("动态巡捕：UP {Mid} 本轮新收 {Fresh} 条（过目 {Seen} 条）",
                                target.Mid, outcome.Fresh, outcome.Seen);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "动态巡捕：UP {Mid} 这轮翻车，下个 target 继续", target.Mid);
                }
            }

            // 新货的九宫格/封面/正文表情先落盘（与评论巡捕同款位置风格：policy 之外直接 await），再进评论段。
            await CacheDynamicAssetsAsync(freshDynamics, cancellationToken);
            await RunCommentRoundAsync(freshIds, cancellationToken);
        }
        finally
        {
            roundGate.Release();
        }
    }

    /// <summary>
    /// 新收动态的图片资产落盘：九宫格与卡面封面走评论图同库（data/cmt-img，sha1 命名），正文 [表情] 走 data/emotes——
    /// 批量一次下载，风控弃单不受影响：存不下的行留 null 洞 / 不落库，前端热链兑底，绝不拦巡捕。
    /// </summary>
    private async Task CacheDynamicAssetsAsync(IReadOnlyList<ParsedDynamic> fresh, CancellationToken cancellationToken)
    {
        if (fresh.Count == 0)
        {
            return;
        }

        try
        {
            var urls = fresh
                .SelectMany(d => d.Pics.Select(p => p.Url))
                .Concat(fresh.Select(d => d.ArchiveCover))
                .ToList();
            var pictureMap = await assetCache.CachePictureUrlsAsync(urls, cancellationToken);

            var emotePairs = fresh.SelectMany(d => d.Emotes ?? []).Distinct().ToList();
            var emoteMap = emotePairs.Count > 0
                ? await assetCache.CacheEmoteUrlsAsync(emotePairs, cancellationToken)
                : null;

            foreach (var d in fresh)
            {
                // 本地图数组与 pics_json 同序对齐：单图失败存 null 元素，前端逐格回退热链。
                var picsLocal = d.Pics.Count > 0
                    ? d.Pics.Select(p => pictureMap.TryGetValue(p.Url, out var local) ? local : null).ToList()
                    : null;
                var coverLocal = d.ArchiveCover is not null && pictureMap.TryGetValue(d.ArchiveCover, out var cover) ? cover : null;
                string? bodyEmotesJson = d.Emotes is { Count: > 0 } seqs
                    ? JsonSerializer.Serialize(
                        seqs.Where(e => emoteMap is not null && emoteMap.ContainsKey(e.Seq))
                            .ToDictionary(e => e.Seq, e => emoteMap![e.Seq]),
                        AssetJson)
                    : null;
                if (picsLocal is not null || coverLocal is not null || bodyEmotesJson is not null)
                {
                    db.SetDynamicAssetLocals(
                        d.DynamicId,
                        picsLocal is not null ? JsonSerializer.Serialize(picsLocal, AssetJson) : null,
                        coverLocal,
                        bodyEmotesJson);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "动态图片资产落盘翻车（前端热链兑底，不拦巡捕）");
        }
    }

    /// <summary>
    /// 评论段：先给本轮新收的动态收评论，再摄 5 条存量欠账（新→旧，逐轮摊平不打爆 B 站）。
    /// 条间 350ms 节流；一旦风控信号现形，剩下的一律留下轮——评论没盖碑的欠账自会重逢。
    /// </summary>
    private async Task RunCommentRoundAsync(List<(string DynamicId, long? CommentType, string? CommentOid)> freshIds, CancellationToken cancellationToken)
    {
        // 存量头像补员：早于头像存档上线的老行（或当年存图失败的）face_local 空着，每轮摄 200 个 URL 补档盖章；图床挂了自然留待下轮。
        await BackfillAvatarsAsync(cancellationToken);
        await BackfillCommentLevelsAsync(cancellationToken);

        // 新货带完整坐标直接办；存量欠账从库里拿坐标（没坐标的行等重巡补完再办，不拿兑底误盖章）。
        var pending = db.ListPendingCommentDynamics(60)
            .Where(row => !freshIds.Any(f => f.DynamicId == row.DynamicId))
            .Select(row => (row.DynamicId, row.CommentType, row.CommentOid))
            .ToList();
        var queue = freshIds.Concat(pending).ToList();
        if (queue.Count == 0)
        {
            return;
        }

        var totalComments = 0;
        var finished = 0;
        foreach (var (dynamicId, commentType, commentOid) in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await policy.RunForActivityAsync(() => fetcher.FetchCommentsAsync(dynamicId, commentType, commentOid, cancellationToken));
                if (!outcome.Ok && !outcome.RiskControlled)
                {
                    logger.LogWarning("动态评论：{DynamicId} 没取成（{Error}），欠账留下轮", dynamicId, outcome.Error);
                    continue;
                }

                // 头像、表情与评论配图复用视频评论区的落盘库（data/avatars、data/emotes、data/cmt-img）：存不下的前端热链兑底。
                var faceMap = await assetCache.CacheAvatarUrlsAsync(
                    outcome.Comments.Select(c => c.Face), cancellationToken);
                var emoteMap = outcome.Emotes is { Count: > 0 } emotePairs
                    ? await assetCache.CacheEmoteUrlsAsync(emotePairs, cancellationToken)
                    : null;
                var pictureMap = await assetCache.CachePictureUrlsAsync(
                    outcome.Comments.SelectMany(c => c.Pictures ?? (IReadOnlyList<string>)[]), cancellationToken);

                totalComments += db.UpsertComments(dynamicId, outcome.Comments
                    .Select(c => new StationDb.DynamicCommentRow(c.Rpid, c.RootRpid, c.ParentRpid, c.Mid, c.Name, c.Face, c.Message, c.Like, c.CtimeUtc,
                        c.Face is not null && faceMap.TryGetValue(c.Face, out var local) ? local : null,
                        // 配图两数组同序对齐：单图下载失败存 null 元素，端点层逐张本地优先合并。
                        c.Pictures is { Count: > 0 } remotePics ? JsonSerializer.Serialize(remotePics, AssetJson) : null,
                        c.Pictures is { Count: > 0 } localPics
                            ? JsonSerializer.Serialize(
                                localPics.Select(u => pictureMap.TryGetValue(u, out var picLocal) ? picLocal : null).ToList(),
                                AssetJson)
                            : null,
                        c.Level))
                    .ToList());
                if (outcome.Ok)
                {
                    // 盖章时顺手把 B 站报的一级评论总数回住底栏（动态流不带 stat，这是唯一可靠来源），表情映射一并定格。
                    db.MarkCommentsFetched(dynamicId, outcome.TotalCount > 0 ? outcome.TotalCount : null,
                        emoteMap is { Count: > 0 } ? JsonSerializer.Serialize(emoteMap) : null);
                    finished++;
                }

                if (outcome.RiskControlled)
                {
                    logger.LogWarning("动态评论：撞上风控，{DynamicId} 之后的单留下轮", dynamicId);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "动态评论：{DynamicId} 收评翻车，继续下一单", dynamicId);
            }

            await Task.Delay(350, cancellationToken);
        }

        if (totalComments > 0 || finished > 0)
        {
            logger.LogInformation("动态评论巡捕：本轮回货 {Comments} 条，盖章 {Finished}/{Queued} 条动态",
                totalComments, finished, queue.Count);
        }
    }

    /// <summary>存量头像补员：拉 face_local 空的老行头像 URL，交给视频评论区同一套落盘库（data/avatars，sha1 命名存原图），
    /// 存上一张盖一张戳；张间 120ms 节流，存不上的（图床拒链/网络抖）自然留下轮。</summary>
    private async Task BackfillAvatarsAsync(CancellationToken cancellationToken)
    {
        var urls = db.ListPendingAvatarUrls(200);
        if (urls.Count == 0)
        {
            return;
        }

        var map = await assetCache.CacheAvatarUrlsAsync(urls, cancellationToken);
        var stamped = 0;
        foreach (var (url, local) in map)
        {
            stamped += db.BackfillCommentFaceLocal(url, local);
            await Task.Delay(120, cancellationToken);
        }

        if (stamped > 0)
        {
            logger.LogInformation("头像补员：本轮给 {Count} 条存量评论的头像补档盖戳（候选 {Candidates} 张）", stamped, urls.Count);
        }
    }

    /// <summary>存量等级补员：拉 level 空的老评论行 mid（去重、每轮封顶 50），逐个走 card 接口查级后 UPDATE 盖章
    /// （存量行是 INSERT OR IGNORE 进的、新解析不会回头补）。个间 200ms 节流，单点失败跳过留待下轮；
    /// 除取消外一切异常只记 LogDebug，绝不扰巡捕主流程。</summary>
    private async Task BackfillCommentLevelsAsync(CancellationToken cancellationToken)
    {
        var mids = db.ListPendingLevelMids(50);
        if (mids.Count == 0)
        {
            return;
        }

        var stamped = 0;
        foreach (var mid in mids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await upInfo.FetchLevelAsync(mid, cancellationToken) is int level)
                {
                    stamped += db.BackfillCommentLevel(mid, level);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "等级补员：mid {Mid} 这趟没查到，下轮再来", mid);
            }

            await Task.Delay(200, cancellationToken);
        }

        if (stamped > 0)
        {
            logger.LogInformation("等级补员：本轮给 {Count} 条存量评论盖上等级戳（候选 {Candidates} 位）", stamped, mids.Count);
        }
    }
}
