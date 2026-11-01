namespace LBS.Domain;

/// <summary>视频在站内的到站状态。</summary>
public enum VideoStatus
{
    /// <summary>已入库，等待下载。</summary>
    Pending = 0,

    /// <summary>下载中。</summary>
    Downloading = 1,

    /// <summary>已到站（全部产物就绪）。</summary>
    Arrived = 2,

    /// <summary>下载失败，可重试。</summary>
    Failed = 3,

    /// <summary>引擎判定无需重复下载（已在下载列表/历史里）或内容不可下，不重试。</summary>
    Skipped = 4,

    /// <summary>站长在下载面板取消了排队行（还没碰引擎，零成本取消）；卡阵里可重新排队。</summary>
    Canceled = 5,
}

/// <summary>入库一条新视频的最小描述。</summary>
/// <param name="Visibility">这稿进站的可见性：代投/导入链一律不传，天然 public；
/// 只有「我自己交东西、先藏起来」那条路会带 private 进来。尾追可选参数，老调用点不受影响。</param>
public sealed record NewVideo(
    string Url,
    string Bvid,
    string? Title,
    string? CoverUrl = null,
    string? PublishedAtUtc = null,
    VideoVisibility Visibility = VideoVisibility.Public);

/// <summary>下载队列卡片的一行（排队/下载中同形）。</summary>
public sealed record DownloadQueueItem(long Id, string Bvid, string? Title, string? CoverUrl);

/// <summary>待提交给下载引擎的站内记录（只取提交所需字段）。</summary>
/// <param name="ForceRedownload">这次提交绕开引擎的「已下过」记录：引擎准入判重键是 (cid, 需要下载的内容)，
/// 历史台账里只要同名一行就在门口被打回（Skipped）。Skipped 出口靠这枚旗走正门（RedownloadCompleted），
/// 不伸手改引擎的库；引擎自己给加档重下用的也是这条道。</param>
public sealed record SubmissionCandidate(long Id, string Url, string? Title, bool ForceRedownload = false);

/// <summary>批量入库结果：insertedIds 保持插入顺序，供逐条入队。</summary>
public sealed record AddVideosOutcome(int Added, int Duplicated, IReadOnlyList<long> InsertedIds);

/// <summary>站内一条视频记录。M1 接入 downkyicore 后扩展分 P、弹幕、字幕等维度。</summary>
public sealed record VideoEntry(
    long Id,
    string Url,
    string Bvid,
    long? Avid,
    string? Title,
    VideoStatus Status,
    DateTimeOffset CreatedAtUtc,
    string? CoverUrl = null,
    string? LastError = null,
    string? UpName = null,
    string? ViewCountText = null,
    string? DurationText = null,
    int? DanmakuCount = null,
    // 真正的 B 站投稿时间（导入源自带 pubdate/created），首页卡阵展示它而非入站时间；
    // 没抽到时的兜底是 video_meta.publish_text（到站归档的发布文本）。
    string? PublishedAtUtc = null,
    string? PublishText = null,
    // 本地封面（与观看页同形：/media/ + artifacts 相对路径）；没归档封面的行留 null，前端热链兑底。
    string? LocalCover = null,
    // 卡阵那两枚自管钮（补采/删档）可不可见：服务端按「投稿人本人 ∥ 站长」算好，访客一律假。
    bool CanManage = false,
    // 首页卡片悬停预览（与封面同形：/previews/ + previews.rel_path，rel 相对 data/previews）；
    // 没生成过预览的行留 null，前端不摆悬停。
    string? PreviewUrl = null,
    // 这稿的可见性：读侧已经拿尺子滤过一轮，能出现在列表里的都是「这位看得着的」，
    // 这里带出来只给前端画那枚「锁」徽记（尾追具名传参，不动既有位置）。
    VideoVisibility Visibility = VideoVisibility.Public);
