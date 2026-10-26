using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.FFmpeg;
using DownKyi.Infrastructure.Storage;
using DownKyi.Platform;
using DownKyi.Services.Download.Headless;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

// LBS fork: renamed from DownloadComposition (src/DownKyi.Desktop/Services/Download/DownloadComposition.cs).
// This is the composition entry point for a headless host: it registers the download runtime only and
// pulls in no Avalonia or Microsoft.Extensions.Hosting type. Dialog/presenter registrations that used to
// live here (DownloadStoreRecoveryPresenter, DownloadActionPlanner, DownloadContentConflictResolver,
// IAddToDownloadServiceFactory) are now registered by the desktop host, and the IHostedService alias of
// DownloadBootstrapHostedService is registered by the desktop host as well.
public static class HeadlessDownloadRegistration
{
    public static IServiceCollection AddDownloadModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new SqliteDownloadTaskStoreOptions(ApplicationStorage.GetDbPath()));
        services.AddSingleton<FfmpegProcessor>();
        services.AddSingleton<IFfmpegMediaMuxer>(provider =>
            provider.GetRequiredService<FfmpegProcessor>());
        services.AddSingleton<IPhysicalOutputPathResolver, FileSystemPhysicalOutputPathResolver>();
        services.AddSingleton<SqliteDownloadTaskStore>();
        services.AddSingleton<IDownloadTaskStore>(provider =>
            provider.GetRequiredService<SqliteDownloadTaskStore>());
        services.AddSingleton<IDownloadHistoryStore>(provider =>
            provider.GetRequiredService<SqliteDownloadTaskStore>());
        services.AddSingleton<IDownloadCompletionStore>(provider =>
            provider.GetRequiredService<SqliteDownloadTaskStore>());
        services.AddSingleton<IDownloadStoreRecovery>(provider =>
            provider.GetRequiredService<SqliteDownloadTaskStore>());
        services.AddSingleton<IDownloadHistoryService, DownloadHistoryService>();
        // LBS fork: the six Application capabilities below must keep resolving to one instance.
        services.AddSingleton<DownloadTaskApplicationService>();
        services.AddSingleton<IDownloadTaskApplicationService>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadTaskLookup>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadTaskCatalog>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadTaskSchedulerService>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadTaskStateService>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadOutputReservationService>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());

        services.AddSingleton<DownloadTaskProjectionStore>();
        services.AddSingleton<DownloadTaskStateWriter>();
        services.AddSingleton<DownloadTransferArtifactInvalidator>();
        // LBS fork: the three queue contracts below must keep resolving to one gateway instance.
        services.AddSingleton<DownloadTaskQueueGateway>();
        services.AddSingleton<IDownloadTaskQueue>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<IDownloadSchedulerControl>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<IDownloadRuntimeAvailability>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<DownloadCommittedTaskDispatcher>();
        services.AddSingleton<DownloadTaskAdmissionService>();
        services.AddSingleton<DownloadListState>();
        services.AddSingleton<IDownloadedListProjection>(provider =>
            provider.GetRequiredService<DownloadListState>());
        services.AddSingleton<DownloadTaskFileService>();
        services.AddSingleton<DownloadTaskStaging>();
        services.AddSingleton<IDownloadManagerCoordinator, DownloadManagerCoordinator>();
        services.AddSingleton<DownloadMovieMetadataBuilder>();

        services.AddSingleton<IDownloadEmergencyCleanup, BuiltinDownloadEmergencyCleanup>();
        services.AddSingleton<DownloadDiagnosticLogger>();
        services.AddSingleton<IDownloadRuntimeFactory, DownloadRuntimeFactory>();
        // LBS fork: desktop-only registrations removed from this module -
        //   AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>()
        //   AddSingleton<DownloadStoreRecoveryPresenter>()
        //   AddSingleton<DownloadContentConflictResolver>()
        //   AddSingleton<DownloadActionPlanner>()
        //   AddSingleton<IAddToDownloadServiceFactory, AddToDownloadServiceFactory>()
        //   AddSingleton<IHostedService>(provider => provider.GetRequiredService<DownloadBootstrapHostedService>())
        // The desktop host registers those itself; TryAdd keeps a host-provided implementation winning.
        services.TryAddSingleton<IUiDispatcher, InlineUiDispatcher>();
        services.TryAddSingleton<IUserNotificationService, DesktopNotificationService>();
        services.TryAddSingleton<IPlatformLauncher, NullPlatformLauncher>();
        services.AddSingleton<DownloadBootstrapHostedService>();
        // LBS fork: headless URL -> queued task submission. Registered through a factory because
        // HeadlessSubmissionService takes internal collaborators - DownloadTaskAdmissionService and
        // DownloadMovieMetadataBuilder, both registered above - that a public constructor cannot expose.
        // ISettingsStore / IBilibiliApiClient / IWbiKeyProvider / ILogger<T> are host-provided, exactly as
        // for the rest of this module, so no new dependency enters here.
        services.TryAddSingleton(static provider => new HeadlessSubmissionService(
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IBilibiliApiClient>(),
            provider.GetRequiredService<IWbiKeyProvider>(),
            provider.GetRequiredService<DownloadTaskAdmissionService>(),
            provider.GetRequiredService<DownloadMovieMetadataBuilder>(),
            provider.GetRequiredService<ILogger<HeadlessSubmissionService>>()));
        return services;
    }
}
