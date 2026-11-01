using System.Text.Json;

namespace LBS.Domain;

/// <summary>放映厅「创作团队」一员（B 站 view 接口 staff 数组的归档形）：Role 是 B 站给的角色文本（UP主/作词/视频制作…）。
/// 联合创作稿才有；Face 本地优先（/avatars/...），拉不到留热链，与 up_face 同一口径。</summary>
public sealed record WatchStaff(long? Mid, string Name, string? Role, string? Face);

/// <summary>创作团队的落库形态：video_meta.staff_json 一列存 camelCase JSON 数组。
/// 写侧（Server 采到就写）与读侧（GetWatchData）共用这一张嘴，免得两头各写一份字段名、哪天错开就静默变成「没团队」。
/// 空列表当 null 存：采过、但这稿不是联合创作，与「压根没采过」在库里同形 —— 分不分得清不重要，要不要再出去采由 stat_probed 定。</summary>
public static class WatchStaffCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? ToJson(IReadOnlyList<WatchStaff>? staff) =>
        staff is null || staff.Count == 0 ? null : JsonSerializer.Serialize(staff, Options);

    /// <summary>脏数据一律当「没团队」读回 null：放映厅不该因为一列写坏而整页 500。</summary>
    public static IReadOnlyList<WatchStaff>? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<WatchStaff>>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>提交时带回的视频档案信息（全是展示文本，来自 B 站 view 接口的一次采集）。</summary>
public sealed record VideoMetaDraft(
    string? UpName,
    long? UpMid,
    string? Zone,
    string? PublishText,
    string? Description,
    string? ViewCountText);

/// <summary>
/// 「重拉封面与媒体」往台账回写的那几栏（全站唯一形状，写口只有 <c>StationDb.UpdateArchiveFromView</c>）。
/// 与 <see cref="VideoMetaDraft"/> 的区别：那条是首次提交的七栏整套（走 DELETE + INSERT），这条只挑「重取能从 B 站拿到、
/// 又归档案口径管」的六栏 + 封面链：UP 名片（up_face/up_sign/up_fans）、互动数据（stat_*）、创作团队（staff_json）
/// 与分 P 台账都不在里面——它们是另几拨采集员的家底，重取不许碰。
/// <b>没有 Title 这一栏</b>：标题是站长能在稿件设置里覆写的那一栏，跟着 B 站跑会把他改过的字冲掉（他勾的是「重拉封面与媒体」）。
/// 递 null / 空白 = 这一栏 B 站这次没给，写侧 COALESCE 保留旧值（不拿空串涂旧账）。
/// </summary>
public sealed record ArchiveRefreshPatch(
    string? Zone = null,
    string? Description = null,
    string? ViewCountText = null,
    string? UpName = null,
    long? UpMid = null,
    string? PublishText = null,
    string? CoverUrl = null);

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
/// <remarks>Title / Zone / Description 三栏下发的是**有效值**（站内覆写优先，没覆写才吃 B 站归档那份，
/// 表达式在 StationDb.VideoSettings.cs / StationDb.Zones.cs），与卡阵、分区页、合集、观看历史同一张嘴；
/// 面板要区分「这栏是站内改过的」看尾追那三枚 *Overridden 标。</remarks>
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
    // 简介只此一个名字 Description（JSON 里就是 description）：读写两头共用，不开 desc 那种第二张脸（一个出处两个名字早晚错开）。
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
    bool CanManage = false,
    // 创作团队（联合创作稿的 B 站 staff 档案）：null = 这稿还没采过 staff（老稿一律如此，按约不回头补）；
    // 采到过、但不是联合创作也给 null（空列表落库即 null）。前端只在 ≥2 人时摆那块，单人不必占位。
    IReadOnlyList<WatchStaff>? Staff = null,
    // 这稿的可见性字面量（'public'/'private'，与 VisibilityCodec 同一张嘴）：放映厅拿它画那枚「锁」，
    // 也是端点拿「自己才能改可见性」回显的料。老调用点不传就是 null（不是「公开」，只是没说）。
    string? Visibility = null,
    // ─── 以下是「稿件设置」面板那几栏（全尾追带默认值，不动既有位置）───
    // 这位是站长吗（role=owner）：面板拿它决定显不显站长那一组（重新排队 / 强制重下 / 加档）。
    // 与 CanManage 同一分工：数据层不认身份不查角色，默认假，正身由 /api/watch 端点盖（免得哪里漏赋值就白送）。
    bool IsStationOwner = false,
    // 这三栏是否处于「本地覆写中」（site_* 槽非空）：前端据此把输入框标「已改」并摆「还原」钮；
    // 上面那三栏（title/desc/zone）念的已经是有效值，覆写中就是站内那份。
    bool TitleOverridden = false,
    bool DescOverridden = false,
    bool ZoneOverridden = false);
