using System.Security.Cryptography;
using System.Text;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 成员门闸：橱窗与放映厅的分界线。
/// 访客（无 lbs_member cookie）只能逛首页卡阵、搜索、看封面标题；
/// 放映厅（watch 数据）、弹幕读写、相关推荐、直链下载、投稿一律要会员会话。
/// 会话 = HttpOnly cookie 里的随机令牌，库里只存它的 SHA256，30 天有效（落盘，重启不踢朋友）。
/// 门在数据上不在页面壳上——与 AdminGate 同一套哲学。
/// </summary>
public sealed class MemberGate(StationDb db)
{
    public const string Cookie = "lbs_member";
    public const string MemberItem = "lbs.member";
    public static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);

    /// <summary>这条请求是不是会员事务（要过门）。</summary>
    public static bool RequiresMember(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return false;
        }

        // 放映厅数据（整页装配：流地址、评论区、UP 名片）
        if (path.StartsWith("/api/watch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 投稿（单链、收藏夹、UP 主整搬）：会员才有归属，也才烧得起自己的账号
        if (HttpMethods.IsPost(request.Method))
        {
            if (string.Equals(path, "/api/videos", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/favorites/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/up/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 弹幕读写、相关推荐、直链：放映厅的配套设施
        if (path.StartsWith("/api/videos/", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("/danmaku", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/related", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/download", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>发一张新会话：返回 (cookie 明文令牌, 落库哈希)。明文只在 Set-Cookie 里走一趟。</summary>
    public (string Token, string TokenHash) IssueSession(long memberId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Hash(token);
        db.IssueMemberSession(memberId, tokenHash, DateTimeOffset.UtcNow.Add(SessionTtl));
        return (token, tokenHash);
    }

    /// <summary>验 cookie 换会员；无效/封禁一律 null（调用方按访客或拒绝处置）。</summary>
    public StationMember? Resolve(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(Cookie, out var token) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return ResolveMemberByToken(token);
    }

    /// <summary>按令牌明文验会员（会话存的是哈希；测试与端点共用这一条正门）。</summary>
    public StationMember? ResolveMemberByToken(string token)
    {
        var memberId = db.ResolveMemberSession(Hash(token));
        if (memberId is null)
        {
            return null;
        }

        var member = db.GetMemberById(memberId.Value);
        return member is { Status: MemberStatus.Active } ? member : null;
    }

    /// <summary>把会员挂进 HttpContext.Items，端点随手取用（投稿归属、弹幕署名）。</summary>
    public void Stamp(HttpContext context, StationMember member) => context.Items[MemberItem] = member;

    public static StationMember? Current(HttpContext context)
        => context.Items.TryGetValue(MemberItem, out var m) ? m as StationMember : null;

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
