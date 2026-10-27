using System.Text.Json;
using DownKyi.Application.Bilibili;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>采集到的 UP 主名片：faceRel 优先本地化（/avatars/...），下载失败回退 B 站热链；拿不到的字段为 null。</summary>
public sealed record UpProfile(string? Name, string? FaceRel, string? Sign, long? Fans);

/// <summary>B 站互动数据快照（view 接口 stat 字段）：全部真采，拿不到给 null。</summary>
public sealed record VideoStat(long? Like, long? Coin, long? Favorite, long? Share, long? Reply);

/// <summary>
/// UP 主名片采集器：走官方 web-interface/card 拿头像、简介、粉丝数（复用引擎登录态客户端），
/// 头像落盘交给 LbsCommentArchiver（与视频/动态评论区共用 data/avatars 的 sha1 命名 + 原图存档那一套库，不再自存 up-{mid}.jpg）。失败不逞强：返回 null，前端按“—”降级。
/// </summary>
public sealed class LbsUpInfoFetcher(
    LbsQueryClient client,
    LbsCommentArchiver assetCache,
    ILogger<LbsUpInfoFetcher> logger)
{
    private const string Referer = "https://www.bilibili.com";

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
            return new UpProfile(name, faceRel ?? face, string.IsNullOrWhiteSpace(sign) ? null : sign, fans);
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

    /// <summary>互动数据：走公开 view 接口，点赞/投币/收藏/分享/回复五维真数。</summary>
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

            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("stat", out var stat))
            {
                return null;
            }

            return new VideoStat(Num(stat, "like"), Num(stat, "coin"), Num(stat, "favorite"), Num(stat, "share"), Num(stat, "reply"));
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

    private static long? Num(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n) ? n : null;

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
