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

/// <summary>
/// 分 P 的一个额外编码版本：status 取 Downloading/Arrived/Failed，relPath 仅 Arrived 后给。
/// 分轨码率（kbps，ffprobe 实测）给版本行显示用，探不到就 null——前端显示破折号，不给 0 冒充。
/// 尾追五件是清晰度/徽记判据：audioCodec 是 ffprobe 实测音频编码（'flac'/…，白名单外 null），
/// fps/height 是同趟实测形态，qualityName/audioQualityId 是引擎实发规格真值（116=1080P 60帧、
/// 30251/31251=Hi-Res 系）——有真值前端直接照报，没真值才退回实测链。
/// </summary>
public sealed record CodecVersion(
    string Codec,
    string Status,
    string? RelPath,
    long? Bytes,
    string? LastError = null,
    int? VideoKbps = null,
    int? AudioKbps = null,
    string? AudioCodec = null,
    double? Fps = null,
    int? Height = null,
    string? QualityName = null,
    int? AudioQualityId = null);

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
    long? MainBytes = null,
    // 主版本的分轨码率（kbps）：与 CodecVersions 里的同名两列同一口径，尾追可选参数，老调用点不受影响。
    int? VideoKbps = null,
    int? AudioKbps = null,
    // 清晰度菜单/Hi-Res 徽记判据：与 CodecVersion 尾追五件同一口径（主版本在 pages 上的那份）。
    string? AudioCodec = null,
    double? Fps = null,
    int? Height = null,
    string? QualityName = null,
    int? AudioQualityId = null);

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
    long? StatReply = null,
    // 能不能重拉快照/删档：服务端按「投稿人本人 ∥ 站长」算好下发，前端只认这个布尔。
    bool CanManage = false);
