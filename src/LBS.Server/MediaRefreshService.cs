using System.Globalization;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.BiliApi.Video.Models;
using DownKyi.Core.BiliApi.Zone;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>重拉的结果状态：NotFound → 404；NotArrived → 400（这条还没到站，别去动它的状态）；Ok → 200 带三件事的结果。</summary>
public enum MediaRefreshStatus
{
    Ok,
    NotFound,
    NotArrived,
}

/// <summary>
/// 重拉封面与媒体的结果。三件事各报各的字面量（与前端契约同名同值，不许自己改名）：
/// <c>cover</c> = replaced / kept / failed，<c>meta</c> = updated / kept / failed，<c>media</c> = queued / intact。
/// </summary>
public sealed record MediaRefreshOutcome(
    MediaRefreshStatus Status,
    string Message,
    string? Cover = null,
    string? Meta = null,
    string? Media = null);

/// <summary>
/// 稿件设置窗那枚「重拉封面与媒体」的后端半边（站长批的是全量：含档案重取）。一趟办三件事：
/// ① <b>问 B 站要一份档案</b>：走上游 <c>VideoViewInfoAsync</c>（Pic/Title/Tname/Desc/Owner/Stat 齐全），
///    落库只走 <see cref="StationDb.UpdateArchiveFromView"/> 那口窄 UPDATE——<b>绝不许调 SaveSubmissionMetadata</b>
///    （它是 DELETE FROM video_meta 再插七栏，会把 UP 名片、互动数据、创作团队一起抹成 NULL）；
/// ② <b>重抓封面件原地覆盖</b>：与封面补员共用 <see cref="CoverFileFetcher"/>（一份实现两处调），抓不着就旧件旧行原样留着；
/// ③ <b>单条缺件才回炉</b>：判据与开机全库扫同源（<see cref="MissingMediaReconciler.IsMediaMissing"/>），
///    一件不缺就一个下载都不触发；回炉走的链与开机回炉一字不差（ResetToPending + EnqueueAsync，不自加 MarkForceRedownload）。
/// ①② 整步挂在 <see cref="LbsCollectPolicy"/> 的出号上下文里：LbsQueryClient 第一行就是 RequireCookie，
/// 没号时当场判无号，绝不拿匿名请求的失败冒充「B 站没给」。
/// 标题这一栏<b>故意不重取</b>（它是站长可以做站内覆写的那一栏，跟着 B 站跑会把他改的字冲掉），
/// 站内覆写那三栏（site_title / site_desc / site_zone）也一个字节不碰。
/// </summary>
public sealed class MediaRefreshService(
    StationDb db,
    DataPaths paths,
    LbsQueryClient client,
    LbsCollectPolicy policy,
    IWbiKeyProvider wbiKeys,
    TimeProvider timeProvider,
    DownloadQueue queue,
    ILogger<MediaRefreshService> logger)
{
    /// <summary>封面件：新件已落盘并登记。</summary>
    public const string CoverReplaced = "replaced";

    /// <summary>封面件：没出去抓（链认不出 / 没产物行可派生名），旧件旧行原样留着。</summary>
    public const string CoverKept = "kept";

    /// <summary>封面件：出去抓了没成（含抓回空文件），旧件旧行照样原样留着。</summary>
    public const string CoverFailed = "failed";

    /// <summary>档案：六栏里至少一栏真写到了行上。</summary>
    public const string MetaUpdated = "updated";

    /// <summary>档案：B 站这次没给新东西（或这稿压根没有档案行）。</summary>
    public const string MetaKept = "kept";

    /// <summary>档案：没问着（没采集号、风控、这条在 B 站已查无）。</summary>
    public const string MetaFailed = "failed";

    /// <summary>媒体：盘上真缺件，已 ResetToPending + 排队重下。</summary>
    public const string MediaQueued = "queued";

    /// <summary>媒体：产物行件件在盘上，一个下载都没触发。</summary>
    public const string MediaIntact = "intact";

    /// <summary>
    /// 跑一趟。钥匙（投稿人本人 ∥ 站长）归端点里的 <c>CanManageVideo</c>，这里只管事不管人——
    /// 与 <see cref="DanmakuArchiveService.TryRefreshAsync"/> 同一分工。查无此片与「还没到站」分两种脸回，
    /// 后者不该被拉去动状态（正在下载中的稿子被 ResetToPending 打断重下才是真出事）。
    /// </summary>
    public async Task<MediaRefreshOutcome> TryRefreshAsync(long videoId, CancellationToken cancellationToken)
    {
        var brief = db.GetVideoBrief(videoId);
        if (brief is null)
        {
            return new MediaRefreshOutcome(MediaRefreshStatus.NotFound, "馆藏里没有这个编号");
        }

        if (brief.Value.Status != VideoStatus.Arrived)
        {
            return new MediaRefreshOutcome(
                MediaRefreshStatus.NotArrived, $"这条还在下载中（{brief.Value.Status}），等这班到了再拉");
        }

        var bvid = brief.Value.Bvid;
        var cover = CoverKept;
        var meta = MetaKept;
        try
        {
            (meta, cover) = await policy.RunForVideoAsync(videoId, () => PullAsync(videoId, bvid, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 没采集号 / 风控 / B 站查无都到这儿：两件事各自回 failed，不抛（媒体那一步照旧要判）。
            logger.LogWarning(ex, "馆藏 {VideoId} 重拉封面与媒体：出号或问 B 站这一趟没走通", videoId);
            meta = MetaFailed;
            cover = CoverFailed;
        }

        var media = await RequeueIfMissingAsync(db, paths, queue, videoId, bvid, logger, cancellationToken);
        return new MediaRefreshOutcome(MediaRefreshStatus.Ok, ComposeMessage(cover, meta, media), cover, meta, media);
    }

    /// <summary>
    /// 重拉视频数据（稿件设置窗的独立钮与卡阵冒泡钮的顺带那件）：只办 <see cref="TryRefreshAsync"/> 的①——
    /// 问 B 站要一份档案、窄口 UPDATE 六栏；封面件与媒体两件都不碰。标题与站内覆写照旧不动（同 UpdateArchiveFromView 的口径）。
    /// </summary>
    public async Task<MediaRefreshOutcome> TryRefreshMetaAsync(long videoId, CancellationToken cancellationToken)
    {
        var brief = db.GetVideoBrief(videoId);
        if (brief is null)
        {
            return new MediaRefreshOutcome(MediaRefreshStatus.NotFound, "馆藏里没有这个编号");
        }

        if (brief.Value.Status != VideoStatus.Arrived)
        {
            return new MediaRefreshOutcome(
                MediaRefreshStatus.NotArrived, $"这条还在下载中（{brief.Value.Status}），等这班到了再拉");
        }

        var bvid = brief.Value.Bvid;
        string meta;
        try
        {
            meta = await policy.RunForVideoAsync(videoId, async () =>
            {
                var view = await WbiRequestExecutor.ExecuteAsync(
                    wbiKeys,
                    (keys, unixTimeSeconds) => client.VideoViewInfoAsync(keys, unixTimeSeconds, bvid, -1, cancellationToken),
                    timeProvider,
                    cancellationToken);
                return view is null ? MetaFailed : MetaFromView(videoId, view);
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "馆藏 {VideoId} 重拉视频数据：出号或问 B 站这一趟没走通", videoId);
            meta = MetaFailed;
        }

        return new MediaRefreshOutcome(MediaRefreshStatus.Ok, MetaPhrase(meta), Meta: meta);
    }

    /// <summary>单条缺件判定 + 只在真缺时回炉（判据与开机全库扫同一张嘴，预览行天生不算缺件）。
    /// 静态口，测试可以绕开网络直接钉这条判据与它的副作用——与 ManageGate 那族「本仓没有 HTTP harness，
    /// 钉判据本身」同一做法。
    /// </summary>
    public static async Task<string> RequeueIfMissingAsync(
        StationDb db,
        DataPaths paths,
        DownloadQueue queue,
        long videoId,
        string bvid,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (!MissingMediaReconciler.IsMediaMissing(db, paths, videoId, bvid))
        {
            return MediaIntact;
        }

        // 与开机缺件回炉同一条链（DataExchange 那条），不自加 MarkForceRedownload：口径别自创。
        // 只从 Arrived 拉回：读到与落笔之间行若被重投/复活改走状态，不硬掰——有人接着管就当已回炉。
        if (!db.ResetToPending(videoId, [LBS.Domain.VideoStatus.Arrived]))
        {
            logger?.LogInformation("馆藏 {VideoId}（{Bvid}）重拉媒体：回炉前状态已被改走，不插一脚", videoId, bvid);
            return MediaQueued;
        }

        await queue.EnqueueAsync(videoId, cancellationToken);
        logger?.LogInformation("馆藏 {VideoId}（{Bvid}）重拉封面与媒体：盘上缺件，已回炉重下", videoId, bvid);
        return MediaQueued;
    }

    /// <summary>问 B 站一趟：档案落窄口 UPDATE，封面件原地覆盖。两件事各自回字面量，谁也没连带谁。</summary>
    private async Task<(string Meta, string Cover)> PullAsync(long videoId, string bvid, CancellationToken cancellationToken)
    {
        var view = await WbiRequestExecutor.ExecuteAsync(
            wbiKeys,
            (keys, unixTimeSeconds) => client.VideoViewInfoAsync(keys, unixTimeSeconds, bvid, -1, cancellationToken),
            timeProvider,
            cancellationToken);

        if (view is null)
        {
            return (MetaFailed, CoverFailed);
        }

        // 窄口回写：只有这六栏 + 封面链能从 B 站回灌，别的一律不动（理由写在 UpdateArchiveFromView 的注释里）。
        var meta = MetaFromView(videoId, view);
        var cover = await RefreshCoverAsync(videoId, view, cancellationToken);
        return (meta, cover);
    }

    /// <summary>档案窄口回写（重拉媒体与重拉数据两条动线共用这一张嘴）：六栏里至少一栏真写到行上才算 updated。</summary>
    private string MetaFromView(long videoId, VideoView view)
    {
        var (metaWritten, _) = db.UpdateArchiveFromView(videoId, PatchOf(view));
        return metaWritten ? MetaUpdated : MetaKept;
    }

    /// <summary>
    /// 抓封面件：B 站给了新链就用新链，没给就拿台账里那枚旧链原地重抓（站长勾的是「重拉」，不是「换源」）。
    /// 落点名从任一产物行推（正片行优先，与补员那条子查询同源），没产物行时 helper 回 Kept。
    /// </summary>
    private async Task<string> RefreshCoverAsync(long videoId, VideoView? view, CancellationToken cancellationToken)
    {
        var url = NonBlank(view?.Pic) ?? db.GetCoverUrl(videoId);
        var result = await CoverFileFetcher.TryReplaceAsync(
            client, db, paths, videoId, url, db.GetCoverBaseRel(videoId), cancellationToken);
        if (result.Outcome == CoverFetch.Failed)
        {
            logger.LogWarning(result.Error, "馆藏 {VideoId} 重拉封面：这趟没抓到，旧的还留着", videoId);
            return CoverFailed;
        }

        return result.Outcome == CoverFetch.Replaced ? CoverReplaced : CoverKept;
    }

    /// <summary>把 B 站 view 载荷里「归档案口径管」的那几栏挑成窄口回写料（认不出的零值一律 null = 这栏没给，COALESCE 留着旧账）。</summary>
    private static ArchiveRefreshPatch PatchOf(VideoView view)
    {
        var upMid = view.Owner?.Mid ?? 0;
        return new ArchiveRefreshPatch(
            Zone: ResolveZone(view),
            Description: view.Desc,
            // stat 缺档时整个 Stat 是零值壳，拿 0 冒充「B 站说这稿零播放」不如留着旧的那句。
            ViewCountText: view.Stat.View > 0 ? DownKyi.Core.Utils.Format.FormatNumber(view.Stat.View) : null,
            UpName: view.Owner?.Name,
            UpMid: upMid > 0 ? upMid : null,
            PublishText: view.Pubdate > 0 ? PublishTextOf(view.Pubdate) : null,
            CoverUrl: view.Pic);
    }

    /// <summary>分区取法与下载引擎一字不差（HeadlessVideoPreparer 那一族）：按 tid 找子区，有父区就拼「父&gt;子」，
    /// 认不出 tid 才退到 B 站给的 tname。只写 tname 会把「音乐&gt;翻唱」写成「翻唱」——分区页按顶级段归堆，
    /// 等于一次重取把这条货换了户口。</summary>
    private static string? ResolveZone(VideoView view)
    {
        var zones = VideoZone.Current.Zones;
        var zone = zones.FirstOrDefault(it => it.Id == view.Tid);
        if (zone is null)
        {
            return view.Tname;
        }

        var parent = zones.FirstOrDefault(it => it.Id == zone.ParentId);
        return parent is not null ? parent.Name + ">" + zone.Name : zone.Name;
    }

    /// <summary>发布时间文本与引擎同形（本地时区「yyyy-MM-dd HH:mm:ss」）：档案那一栏全站只此一种念法。</summary>
    private static string PublishTextOf(long pubdate)
    {
        var start = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local);
        return start.AddSeconds(pubdate).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    }

    /// <summary>三件事拼一句人话（前端只显这一句，字段自己另有机器值可读）。两件顺的 comma 相连，回炉那种要用分号断一下。</summary>
    private static string ComposeMessage(string cover, string meta, string media)
    {
        var head = $"{CoverPhrase(cover)}，{MetaPhrase(meta)}";
        return media == MediaQueued ? $"{head}；这条已回炉，下一班进站" : $"{head}，媒体一件不缺";
    }

    private static string CoverPhrase(string cover) => cover switch
    {
        CoverReplaced => "封面已换新",
        CoverFailed => "封面没抓到，旧的还留着",
        _ => "封面没动（B 站没给新链或没处落盘）",
    };

    private static string MetaPhrase(string meta) => meta switch
    {
        MetaUpdated => "档案已刷新",
        MetaFailed => "档案没查到，台账照旧",
        _ => "档案没新东西可换",
    };

    private static string? NonBlank(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
