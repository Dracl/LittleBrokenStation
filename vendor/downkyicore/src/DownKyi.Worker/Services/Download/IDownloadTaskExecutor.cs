using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

// LBS fork: public runtime contract (implemented by the internal DownloadPipeline).
public interface IDownloadTaskExecutor
{
    Task ExecuteAsync(DownloadTaskId taskId, CancellationToken cancellationToken);

    Task MarkFailedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default);

    Task PersistShutdownStateAsync();
}
