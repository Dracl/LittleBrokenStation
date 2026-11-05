using System.IO;
using System.Text.Json;
using DownKyi.Application.Bilibili;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>采集到的 UP 主名片：faceRel 优先本地化（/avatars/...），下载失败回退 B 站热链；拿不到的字段为 null。
/// official 是 B 站官方认证标题（card.official.title 或 official_verify.desc，无认证为 null）。</summary>
public sealed record UpProfile(string? Name, string? FaceRel, string? Sign, long? Fans, string? Official);

/// <summary>B 站互动数据快照（view 接口 stat 字段）：全部真采，拿不到给 null。
/// Staff 是同一次 view 响应顺带带回的创作团队（联合创作稿才有，普通稿是空列表）——
/// 同一次出号拿两件事，不为团队单开一趟请求（采集算法就按这条定，不要拆成 FetchStaffAsync）。</summary>
public sealed record VideoStat(
    long? Like,
    long? Coin,
    long? Favorite,
    long? Share,
    long? Reply,
    IReadOnlyList<VideoStaffItem>? Staff = null);

/// <summary>创作团队一员（view 接口 staff 数组）：Role 是 B 站给的角色文本（UP主/作词/视频制作…），主 UP 本人也在数组里。
/// FaceRel 本地优先（/avatars/...，与 up_face 同一套 sha1 命名与幂等），拉不到回退热链。</summary>
public sealed record VideoStaffItem(long? Mid, string? Name, string? Role, string? FaceRel);

/// <summary>
/// UP 主名片采集器：走官方 web-interface/card 拿头像、简介、粉丝数与官方认证（card.official.title），
/// 复用引擎登录态客户端，
/// 头像落盘交给 LbsCommentArchiver（与视频/动态评论区共用 data/avatars 的 sha1 命名 + 原图存档那一套库，不再自存 up-{mid}.jpg）。失败不逞强：返回 null，前端按“—”降级。
/// 唯一匿名例外是认证：登录态下 B 站藏 card.official，认证另走一发只读 official 的匿名 card（见 <see cref="ResolveOfficialAsync"/>）。
/// </summary>
public sealed class LbsUpInfoFetcher(
    LbsQueryClient client,
    LbsCommentArchiver assetCache,
    ILogger<LbsUpInfoFetcher> logger)
{
    private const string Referer = "https://www.bilibili.com";

    /// <summary>匿名认证补读专用的 UA：要像浏览器，不能像脚本（card 是客人口， UA 太素容易被风控页糊脸）。</summary>
    private const string GuestUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>只给匿名认证补读用的一把裸客户端：不带 cookie、不进号池分流，专读 card.official 一个字段（见 <see cref="ResolveOfficialAsync"/>）。</summary>
    private static readonly HttpClient GuestHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const string AvatarsPrefix = "/avatars/";

    /// <summary>
    /// 该不该出去重采 UP 名片。三态分清：null 是没采过，要采；空串是上一趟失败销账，不重采（要重采把该行置回 NULL）；
    /// 有值还要验货 —— 热链直接算能用，/avatars/xxx 这种本地得盘上真有文件。
    /// 早年那一代把 up_face 写成 /avatars/up-{mid}.jpg，头像命名换 sha1 后这些文件已经不在了，
    /// 只判非空会让 UpCard 的 img 永远 404 到「粉底首字」占位、且永不重采。
    /// </summary>
    public static bool NeedsRefetch(string? face, string avatarsDir)
    {
        if (face is null)
        {
            return true;
        }

        var value = face.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!value.StartsWith(AvatarsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leaf = value[AvatarsPrefix.Length..]
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (leaf.Length == 0 || leaf.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(leaf))
        {
            return true;
        }

        return !File.Exists(Path.Combine(avatarsDir, leaf));
    }

    public async Task<UpProfile?> FetchAsync(long mid, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.bilibili.com/x/web-interface/card?mid={mid}&photo=false";
            var raw = await client.GetStringAsync(new BilibiliHttpRequest(url, Referer), cancellationToken);
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                logger.LogWarning("UP 主 card 接口非零返回 code={Code}（mid={Mid}）", code.GetInt32(), mid);
                return null;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("card", out var card))
            {
                return null;
            }

            var name = Str(card, "name");
            var face = Str(card, "face");
            var sign = Str(card, "sign");
            long? fans = card.TryGetProperty("fans", out var f) && f.TryGetInt64(out var fv) ? fv : null;

            var faceRel = face is null ? null : await CacheFaceAsync(face, mid, cancellationToken);
            // 认证是锦上添花：拿不到只置 null，不许把已经采到的名片一并顶掉（helper 自带 try/catch）。
            return new UpProfile(
                name,
                faceRel ?? face,
                string.IsNullOrWhiteSpace(sign) ? null : sign,
                fans,
                await ResolveOfficialAsync(card, mid, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UP 主 {Mid} 名片采集失败（不致命）", mid);
            return null;
        }
    }

    /// <summary>头像落盘交给评论资产缓存（sha1 命名 + 原图存档，与视频/动态评论区同库同命名）；已存在即跳过（幂等）。失败返回 null，调用方回退热链。</summary>
    private async Task<string?> CacheFaceAsync(string faceUrl, long mid, CancellationToken cancellationToken)
    {
        try
        {
            var map = await assetCache.CacheAvatarUrlsAsync([faceUrl], cancellationToken);
            return map.TryGetValue(faceUrl, out var local) ? local : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UP 主 {Mid} 头像本地化失败，热链回退", mid);
            return null;
        }
    }

    /// <summary>
    /// 认证的取货路线（11-05 真机对版破案）：card 接口对认证有 <b>两副面孔</b>，同一无名请求会按 B 站心情轮换——
    /// 一副把认证装在 official.title（浏览器形态），另一副只给 official:null 把认证改装进 official_verify.desc（站里实测见到的就是这副）；
    /// 登录态（采集号 cookie）实测只回 official:null。所以认证靠匿名补读一发 card + 两副面孔都认（见 <see cref="OfficialTitle"/>）。
    /// 这是「禁匿名出网」铁律的一处定点例外：card 本就是客人接口（匿名实测通、不吃 -352），这一发只读认证字段，
    /// 不碰任何互动口，拿不到只回 null 不惊动名片主链路。登录态响应里的认证仍作首选读（万一哪天 B 站直给，零改动回落）。
    /// </summary>
    private async Task<string?> ResolveOfficialAsync(JsonElement card, long mid, CancellationToken cancellationToken)
    {
        var fromLogin = OfficialTitle(card);
        if (fromLogin is not null)
        {
            return fromLogin;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/web-interface/card?mid={mid}&photo=false");
            req.Headers.TryAddWithoutValidation("User-Agent", GuestUserAgent);
            req.Headers.TryAddWithoutValidation("Referer", Referer);
            using var res = await GuestHttp.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            var raw = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                return null;
            }

            return doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("card", out var guestCard)
                ? OfficialTitle(guestCard)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 无认证与风控页都长这样，不值得 Warning 刷屏；认证是锦上添花，主名片不许受牵连。
            logger.LogDebug(ex, "UP 主 {Mid} 匿名认证补读空手（无认证/风控页属常态）", mid);
            return null;
        }
    }

    /// <summary>B 站官方认证标题：card 响应有两副面孔（11-05 实拍：浏览器形态把认证装在 <c>official.title</c>，
    /// 站里见到的匿名形态只给 official:null 却把认证装在 <c>official_verify.desc</c>）。两副都认，
    /// 任一形态给出非空标题就算有认证（「bilibili直播高能主播」「bilibili知名UP主」实走 official_verify；
    /// 影视飓风「2025百大…」实走 official.title；早先按 type==0 一票否决会把真认证全杀成 null，双案实证）。
    /// type=-1 或标题空白统一回 null；两副都没有也回 null —— 页头据此不画认证那一行，而不是画个空章。</summary>
    public static string? OfficialTitle(JsonElement card)
    {
        return OfficialFrom(card, "official", "title") ?? OfficialFrom(card, "official_verify", "desc");
    }

    private static string? OfficialFrom(JsonElement card, string container, string titleField)
    {
        if (!card.TryGetProperty(container, out var official) || official.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (official.TryGetProperty("type", out var type) && type.TryGetInt32(out var typeValue) && typeValue == -1)
        {
            return null;
        }

        var title = Str(official, titleField);
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }

    /// <summary>轻量查等级：同走 card 接口、同一张嘴（client + Referer + 无 cookie），只取 level_info.current_level、退 card.level。
    /// 给存量评论等级补员用；认不到给 null，调用方留待下轮。</summary>
    public async Task<int?> FetchLevelAsync(long mid, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.bilibili.com/x/web-interface/card?mid={mid}&photo=false";
            var raw = await client.GetStringAsync(new BilibiliHttpRequest(url, Referer), cancellationToken);
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("card", out var card))
            {
                return null;
            }

            if (card.TryGetProperty("level_info", out var li) && li.TryGetProperty("current_level", out var cl) && cl.TryGetInt32(out var lv) && lv is >= 0 and <= 99)
            {
                return lv;
            }

            return Num(card, "level") is long flat and >= 0 and <= 99 ? (int)flat : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "UP 主 {Mid} 等级没查到（不致命）", mid);
            return null;
        }
    }

    /// <summary>互动数据与创作团队：走公开 view 接口一发拿齐——点赞/投币/收藏/分享/回复五维真数，
    /// 加上 data.staff 那串联合创作名单（B 站的「创作团队 N 人」就来自它）。</summary>
    public async Task<VideoStat?> FetchStatAsync(string bvid, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";
            var raw = await client.GetStringAsync(new BilibiliHttpRequest(url, Referer), cancellationToken);
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                logger.LogWarning("view 接口非零返回 code={Code}（{Bvid}）", code.GetInt32(), bvid);
                return null;
            }

            // stat 缺了不再整段作废：staff 与 stat 同一次拿，只缺一个就交另一半，另一些字段由调用方销账。
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            long? like = null, coin = null, favorite = null, share = null, reply = null;
            if (data.TryGetProperty("stat", out var stat))
            {
                like = Num(stat, "like");
                coin = Num(stat, "coin");
                favorite = Num(stat, "favorite");
                share = Num(stat, "share");
                reply = Num(stat, "reply");
            }

            var staff = await FetchStaffAsync(data, bvid, cancellationToken);
            return new VideoStat(like, coin, favorite, share, reply, staff);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Bvid} 互动数据采集失败（不致命）", bvid);
            return null;
        }
    }

    /// <summary>创作团队（联合创作稿的 data.staff 数组）：普通稿没这个字段，给空列表（不是 null，免得调用方分不清「没采」与「没团队」）。
    /// 头像一次批量交给评论资产缓存本地化（与 UP 名片/评论区同一个 sha1 库，同图天然不重下）；单张拉不到只退回热链，不整队作废。</summary>
    private async Task<IReadOnlyList<VideoStaffItem>> FetchStaffAsync(JsonElement data, string bvid, CancellationToken cancellationToken)
    {
        if (!data.TryGetProperty("staff", out var staff) || staff.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<VideoStaffItem>();
        }

        var rows = new List<VideoStaffItem>();
        var faces = new List<string>();
        foreach (var item in staff.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Str(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue; // 连名字都没有的条目显不出来，不如不收
            }

            long? mid = item.TryGetProperty("mid", out var m) && m.TryGetInt64(out var mv) ? mv : null;
            var face = Str(item, "face");
            if (face is { Length: > 0 })
            {
                faces.Add(face);
            }

            rows.Add(new VideoStaffItem(mid, name.Trim(), Str(item, "title"), face));
        }

        if (rows.Count == 0 || faces.Count == 0)
        {
            return rows;
        }

        try
        {
            var map = await assetCache.CacheAvatarUrlsAsync(faces, cancellationToken);
            if (map.Count > 0)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].FaceRel is { Length: > 0 } hot && map.TryGetValue(hot, out var local))
                    {
                        rows[i] = rows[i] with { FaceRel = local };
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{Bvid} 创作团队头像本地化没走通，整队留热链", bvid);
        }

        logger.LogInformation("馆藏 {Bvid} 创作团队 {Count} 人：{Team}", bvid, rows.Count,
            string.Join(" / ", rows.Select(r => $"{r.Name}·{r.Role ?? "?"}")));
        return rows;
    }

    private static long? Num(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n) ? n : null;

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
