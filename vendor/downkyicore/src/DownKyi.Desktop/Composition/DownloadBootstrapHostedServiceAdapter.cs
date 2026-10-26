using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Services.Download;
using Microsoft.Extensions.Hosting;

namespace DownKyi.Composition;

// LBS fork: DownKyi.Worker must not reference Microsoft.Extensions.Hosting, so DownloadBootstrapHostedService
// no longer implements IHostedService. The desktop host adapts its StartAsync/StopAsync back onto the
// generic-host contract here; a headless host calls those methods directly instead.
internal sealed class DownloadBootstrapHostedServiceAdapter : IHostedService
{
    private readonly DownloadBootstrapHostedService _bootstrap;

    public DownloadBootstrapHostedServiceAdapter(DownloadBootstrapHostedService bootstrap)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _bootstrap.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _bootstrap.StopAsync(cancellationToken);
}
