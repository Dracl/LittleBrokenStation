namespace LBS.Domain;

/// <summary>提交时带回的视频档案信息（全是展示文本，来自 B 站 view 接口的一次采集）。</summary>
public sealed record VideoMetaDraft(
    string? UpName,
    long? UpMid,
    string? Zone,
    string? PublishText,
    string? Description,
    string? ViewCountText);

/// <summary>提交时带回的分 P 草稿，落 pages 表等文件到齐。</summary>
public sealed record PageDraft(
    string TaskId,
    int Index,
    string Title,
    long? Cid,
    string? DurationText,
    string OutputBasePath);

/// <summary>分 P 的一个额外编码版本：status 取 Downloading/Arrived/Failed，relPath 仅 Arrived 后给。</summary>
public sealed record CodecVersion(
    string Codec,
    string Status,
    string? RelPath,
    long? Bytes,
    string? LastError = null);

/// <summary>观看页的一个分 P：元数据 + 本地文件（相对 videos 根，正斜杠）。</summary>
public sealed record PageWithFiles(
    int Index,
    string Title,
    long? Cid,
    string? DurationText,
    string? VideoUrl,
    string? DanmakuUrl,
    IReadOnlyList<string> SubtitleUrls,
    IReadOnlyList<CodecVersion> CodecVersions = null!,
    string? Codec = null,
    long? MainBytes = null);

/// <summary>GET /api/watch/{bvid} 的完整载荷。</summary>
public sealed record WatchData(
    long Id,
    string Bvid,
    string Url,
    string? Title,
    VideoStatus Status,
    string? CoverUrl,
    string? UpName,
    string? Zone,
    string? PublishText,
    string? Description,
    string? ViewCountText,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<PageWithFiles> Pages,
    long? Aid = null,
    string? CommentsUrl = null,
    long? UpMid = null,
    string? UpFace = null,
    string? UpSign = null,
    long? UpFans = null,
    int? DanmakuCount = null,
    long? StatLike = null,
    long? StatCoin = null,
    long? StatFavorite = null,
    long? StatShare = null,
    long? StatReply = null);
