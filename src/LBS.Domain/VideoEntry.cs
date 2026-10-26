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
}

/// <summary>入库一条新视频的最小描述。</summary>
public sealed record NewVideo(string Url, string Bvid, string? Title);

/// <summary>待提交给下载引擎的站内记录（只取提交所需字段）。</summary>
public sealed record SubmissionCandidate(long Id, string Url, string? Title);

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
    int? DanmakuCount = null);
