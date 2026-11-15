using System.Security.Cryptography;
using System.Text;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 成员门闸：橱窗与放映厅的分界线。
/// 访客（无 lbs_member cookie）只能逛首页卡阵、搜索、看封面标题；
/// 放映厅（video 装配数据，旧名 watch）、弹幕读写、相关推荐、直链下载、投稿一律要会员会话。
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
        // 查牌前先剪掉尾部多余斜杠：路由层把 /x 与 /x/ 当同一扇门，门闸照原串点名时，
        // 精确匹配与 EndsWith 型的口（/related、/download、投稿）会被一枚斜杠穿过去。
        // 点名一律 IgnoreCase：Minimal API 的路由匹配本身不吃大小写，门闸若按 Ordinal 认字面，
        // /API/videos/9/danmaku 这种换体写法就在路由上照样命中端点、在门闸上却「不是 /api/」——整道门形同虚设（活体实测漏过）。
        var path = (request.Path.Value ?? "").TrimEnd('/');
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 放映厅数据（整页装配：流地址、评论区、UP 名片）
        // 放映厅数据（video 装配，旧名 watch，11-15 改名）：点名带尾斜杠——不写斜杠会把公开的
        // /api/videos 清单与 /api/videos/{id} 详情一并误封成会员事务（门闸宁挡不漏也不能乱挡）。
        if (path.StartsWith("/api/video/", StringComparison.OrdinalIgnoreCase))
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

        // 弹幕读写、相关推荐、直链、续播进度：放映厅的配套设施
        if (path.StartsWith("/api/videos/", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("/danmaku", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/related", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/download", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase)
                // 加档前的预估规格：看着像只读情报，其实每一发都替分站出去打 playurl（风控敏感口、吃的是采集号），
                // 不挡就等于让访客拿站里的号去探 B 站。
                || path.EndsWith("/estimates", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 投稿人自管（刷评论快照 / 重拉弹幕 / 删自己的稿 / 续下 / 重新排队）与楼中楼按需补采：
        // 从站长室摘出来后必须在这里补上门闩，否则就成了匿名可调用。归属不是这里的事，端点自己拿 MemberGate.Current 再核一遗。
        // 投稿人自管（刷评论快照 / 重拉弹幕 / 删自己的稿 / 续下 / 重新排队 / 取消自己的加档 / 删自己那版加档的件）
        // 与楼中楼按需补采：从站长室摘出来后必须在这里补上门闩，否则就成了匿名可调用（claim 就踩过：
        // 门没点它，匿名直接落到端点里吃了 403，与同族两口的 401 摆成两套脸）。归属不是这里的事，
        // 端点自己拿 MemberGate.Current 再核一遗。
        if (HttpMethods.IsDelete(request.Method) && (IsVideoIdOnlyPath(path) || IsCodecVersionPath(path)))
        {
            return true;
        }

        // 稿件设置（PATCH /api/videos/{id}：可见性 / 标题 / 简介 / 分区）：与上面删稿那一口同形——
        // 会员门挡访客（匿名拿到与删稿同一句 401），归属归端点里 CanManageVideo 再核一遗（不是自己的稿 403）。
        // 只点纯数字 id 那一个形状，不顺手拓到其它 PATCH（合集抽屉那几口仍留在两张门外面自防）。
        if (HttpMethods.IsPatch(request.Method) && IsVideoIdOnlyPath(path))
        {
            return true;
        }

        // 重拉弹幕 / 重拉封面与媒体 / 重拉视频数据（POST …/danmaku-refresh、…/media-refresh 与 …/meta-refresh）、续下/重新排队（…/revive、…/requeue）
        // 与站长认领（…/claim）也在这份点名清单里：少了这一条，匿名就能拿别人的 id 驱分站出去跑 B 站、把健康馆藏踢回队列重下，
        // 或把被取消的稿悄悄恢复排队（归属归端点里的 CanManageVideo）。revive/requeue 刚从 AdminGate 摘给投稿人，
        // 不在这里补闩就会退化成匿名 403/404 两套脸（claim 刚写完就踩中：门没点它，匿名直接落到端点里吃了 403）。
        if (HttpMethods.IsPost(request.Method)
            && path.StartsWith("/api/videos/", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith("/comments", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/comments/subs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/danmaku-refresh", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/subtitles-refresh", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/media-refresh", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/meta-refresh", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/revive", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/requeue", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/claim", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/codecs/cancel", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // UP 主动态整页（列表 / 评论 / 订阅对象名单）：站长定的规矩是访客只看主页。
        // 口在这里挡死才算挡，前端那扇路由守卫只是不让人白跑一趟。
        if (string.Equals(path, "/api/dynamics", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/dynamics/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 合集详情：名录 /api/collections 仍公开（首页大屏幕模式那排入口卡靠它摆封面），
        // 但「点进某一架」是进站待遇 —— 详情里是稿件清单与简介，不是张封面。
        // 名录本身末尾不带斜杠，所以这一条只拿得住 /api/collections/{id} 那个形状（与 IsVideoIdOnlyPath 同一思路）。
        if (path.StartsWith("/api/collections/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // UP 空间（GET /api/up/{mid}/videos）：放映厅名片那颗蓝钮的去向，里头是一份稿件清单 ——
        // 与合集详情同权（进站待遇，不是张封面）。只点 GET：POST /api/up/ 那口（UP 主整搬）在上面投稿那一段已点名。
        if (HttpMethods.IsGet(request.Method) && path.StartsWith("/api/up/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>路径是不是加档版本的写口（/api/videos/{id}/codecs/… 那两口删版本）：与 <see cref="IsVideoIdOnlyPath"/>
    /// 同一思路，只是形状不同——AdminGate 那边把 /codecs 整片算站长事务时，这里必须同步接住，
    /// 否则摘了站长门又没补会员门，匿名就能删别人的件。</summary>
    internal static bool IsCodecVersionPath(string path) =>
        path.StartsWith("/api/videos/", StringComparison.OrdinalIgnoreCase)
        && path.Contains("/codecs/", StringComparison.OrdinalIgnoreCase);

    /// <summary>路径是不是 /api/videos/{纯数字 id}（投稿人自管删稿的形状；AdminGate 的豁免判据与它一字不差）。</summary>
    internal static bool IsVideoIdOnlyPath(string path)
    {
        const string prefix = "/api/videos/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tail = path[prefix.Length..];
        return tail.Length > 0 && tail.All(char.IsAsciiDigit);
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

    /// <summary>这位是站长吗（头名即站长，钥匙就是他的会话）。</summary>
    public static bool IsOwner(StationMember? member) => member?.Role == MemberRoles.Owner;

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
