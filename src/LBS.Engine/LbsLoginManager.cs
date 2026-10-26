using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LBS.Engine;

/// <summary>站长室展示的登录态（只回显非敏感字段，cookie 本体绝不外出）。</summary>
public sealed record LbsLoginState(bool IsLogin, string? Name, long? Mid, int VipStatus, bool HasSavedCookies, string? Face);

/// <summary>登录/登出动作的结果。</summary>
public sealed record LbsLoginOutcome(bool Success, string? Error, string? Name, long? Mid);

/// <summary>
/// headless 站长登录管理：复刻上游 LoginCoordinator.CommitLoginCookiesAsync 的 40 行语义
/// （src/DownKyi.Desktop/Services/Account/LoginCoordinator.cs:101）——
/// 解析 → 备份旧登录态 → 原子落盘 → nav 验证 → 失败回滚。
/// 只做 cookie 粘贴流（不做扫码）；cookie 只进不出：任何返回值和日志都不携带 cookie 内容。
/// </summary>
public sealed class LbsLoginManager(IBilibiliApiClient client, ILogger<LbsLoginManager> logger)
{
    public async Task<LbsLoginOutcome> LoginWithCookieAsync(string? cookieHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return new LbsLoginOutcome(false, "粘贴框是空的。从浏览器开发者工具的请求头里整行复制 Cookie。", null, null);
        }

        var cookies = BilibiliCookieProvider.ParseBrowserCookieHeader(cookieHeader);
        if (cookies is null || cookies.Count == 0)
        {
            return new LbsLoginOutcome(false, "这段文本里解析不出任何 cookie，确认复制的是完整的 Cookie 串（不是 URL）。", null, null);
        }

        // 没有 SESSDATA 就是没登录，直接省一次 B 站请求。
        if (!cookies.Any(c => string.Equals(c.Name, "SESSDATA", StringComparison.OrdinalIgnoreCase)))
        {
            return new LbsLoginOutcome(false, "这份 cookie 里没有 SESSDATA，登录态不完整；请先在浏览器里登录 www.bilibili.com 再复制。", null, null);
        }

        var previous = LoginHelper.GetLoginInfoCookies();
        if (!LoginHelper.SaveLoginInfoCookies(cookies))
        {
            return new LbsLoginOutcome(false, "cookie 落盘失败，检查数据目录 Config/Login 的写权限。", null, null);
        }

        var navigation = await TryFetchNavigationAsync(cancellationToken);
        if (navigation is { IsLogin: true })
        {
            logger.LogInformation("站长以「{Name}」身份进站（mid={Mid}）", navigation.Name, navigation.Mid);
            return new LbsLoginOutcome(true, null, navigation.Name, navigation.Mid);
        }

        var rolledBack = previous.Count > 0
            ? LoginHelper.SaveLoginInfoCookies(previous)
            : LoginHelper.DeleteLoginInfoCookies();
        if (!rolledBack)
        {
            logger.LogError("cookie 登录失败且旧登录态回滚失败，需要手动清理 Config/Login");
        }

        return navigation is null
            ? new LbsLoginOutcome(false, "验证请求没能到达 B 站（网络或风控），登录态未变更，稍后重试。", null, null)
            : new LbsLoginOutcome(false, "B 站没认这份 cookie，多半是过期了；重新登录 B 站后再整行复制一次。", null, null);
    }

    public async Task<LbsLoginState> GetStateAsync(CancellationToken cancellationToken)
    {
        var navigation = await TryFetchNavigationAsync(cancellationToken);
        return new LbsLoginState(
            navigation?.IsLogin == true,
            navigation?.Name,
            navigation?.Mid,
            navigation?.VipStatus ?? 0,
            LoginHelper.HasSavedLoginInfo,
            navigation?.Face);
    }

    public LbsLoginOutcome Logout()
    {
        if (!LoginHelper.HasSavedLoginInfo)
        {
            return new LbsLoginOutcome(true, null, null, null);
        }

        if (!LoginHelper.DeleteLoginInfoCookies())
        {
            return new LbsLoginOutcome(false, "登出失败：登录信息文件删不掉，检查数据目录权限。", null, null);
        }

        logger.LogInformation("站长离站，登录 cookie 已清空");
        return new LbsLoginOutcome(true, null, null, null);
    }

    private async Task<UserInfoForNavigation?> TryFetchNavigationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetUserInfoForNavigationAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "nav 验证请求失败");
            return null;
        }
    }
}
