using DownKyi.Application.Bilibili;
using DownKyi.Application.Lifetime;
using DownKyi.Application.Settings;
using DownKyi.Application.Time;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Infrastructure.Settings;
using DownKyi.Infrastructure.Time;
using DownKyi.Services.Download;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LBS.Engine;

/// <summary>
/// 分站侧唯一 composition root：把 headless 下载运行时（DownKyi.Worker）装配进 Generic Host。
/// 注册清单来自上游 DesktopComposition/DownKyiHost 的 headless 最小集；
/// 上游 invariant 保持：B 站调用一律走注入的 IBilibiliApiClient，六个 Application capability
/// 与队列三契约各自解析到同一实例（由 AddDownloadModule 自身保证，这里不复制注册）。
/// </summary>
public static class EngineRegistration
{
    public static IServiceCollection AddLbsDownloadEngine(this IServiceCollection services, EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ApplicationCancellation>();
        services.TryAddSingleton<ISettingsStore, SettingsStore>();
        services.TryAddSingleton<IBilibiliCookieProvider, BilibiliCookieProvider>();
        services.TryAddSingleton<IWbiKeyProvider, WbiKeyProvider>();

        // 上游提供的 transport/buvid/client/login-session 一站式注册；分站不开系统代理。
        // UA 以站长室设置为准（设置空时用宿主兜底）；注意这个 options 单例只在启动时解一次，
        // 改 UA 后 B 站 API 侧要重启分站才换头（CDN 下载侧每传输现读设置，即时生效）。
        services.AddDownKyiBilibiliInfrastructure(provider =>
        {
            var ua = provider.GetService<ISettingsStore>()?.Current.Network.UserAgent;
            return new BilibiliNetworkOptions(
                UserAgent: string.IsNullOrWhiteSpace(ua) ? options.UserAgent : ua,
                UseProxy: false,
                ProxyAddress: null);
        });

        services.AddDownloadModule();

        services.TryAddSingleton<LbsFavoritesImport>();
        services.TryAddSingleton<LbsLoginManager>();
        services.AddHostedService<EngineBootstrap>();
        return services;
    }
}
