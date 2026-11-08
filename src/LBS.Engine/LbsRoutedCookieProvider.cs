using DownKyi.Application.Bilibili;
using DownKyi.Infrastructure.Bilibili;

namespace LBS.Engine;

/// <summary>
/// 全站 cookie 供给：上游客户端每个请求都来这里现取 cookie，本类按调用链的环境号给出。
/// 注册时机卡在 AddLbsDownloadEngine 之前，让上游的 TryAddSingleton 自动让位——
/// 从此 playurl、view info 等所有经 IBilibiliApiClient 的 B 站请求都跟着调用链的号走。
///
/// 站级主号/副号退役之后这里只有一条规矩：链上有号就用，没号就判无号。
/// 不再回落到 LoginHelper 那份全局单槽（那是原站长主号的落盘点），因为「没号还发匿名请求」
/// 换来的只是 -101/-352 与被风控盯上，不如当场失败、把原因写给站长看。
/// </summary>
public sealed class LbsRoutedCookieProvider(LbsAccountRouter router) : IBilibiliCookieProvider
{
    public string GetCookieHeader() => router.CurrentCookie ?? throw new LbsNoAccountException(
        "这条 B 站请求没有可用账号：投稿要投稿人自己的 B 站号，采集与加档要站里的采集号（站长席位的活号优先）。"
        + "分站已不设站级兜底登录态，请到「个人空间」绑定或重新扫码。");
}
