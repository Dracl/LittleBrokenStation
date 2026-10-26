using DownKyi.Application.Bilibili;
using DownKyi.Infrastructure.Bilibili;

namespace LBS.Engine;

/// <summary>
/// 全站分流 cookie 供给：上游客户端每个请求都来这里现取 cookie，本类按调用链的环境号给出主/副 cookie。
/// 注册时机卡在 AddLbsDownloadEngine 之前，让上游的 TryAddSingleton 自动让位——
/// 从此 playurl、view info 等所有经 IBilibiliApiClient 的 B 站请求都跟着视频的归属号走。
/// 不在任何分号上下文（或副号未配置）时原样返回主号单槽，行为与改造前完全一致。
/// </summary>
public sealed class LbsRoutedCookieProvider(LbsAccountRouter router, LbsAltLoginManager alt) : IBilibiliCookieProvider
{
    public string GetCookieHeader()
    {
        if (router.Effective(router.Current) == LbsOwner.Alt && alt.TryGetCookieHeader() is { } altCookie)
        {
            return altCookie;
        }

        return LoginHelper.GetLoginInfoCookiesString();
    }
}
