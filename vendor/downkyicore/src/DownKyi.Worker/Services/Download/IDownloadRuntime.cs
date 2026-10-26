using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

// LBS fork: the runtime contract of the download module is public so a headless host can drive the queue,
// the scheduler and the startup gate.
// CA1711 would force a rename of the queue abstraction that the desktop app, its tests and the store layer
// all reference by name, so the existing name is kept.
#pragma warning disable CA1711 // 标识符应以正确后缀命名
public interface IDownloadTaskQueue
#pragma warning restore CA1711 // 标识符应以正确后缀命名
{
    Task EnqueueAsync(DownloadTaskId taskId, CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(DownloadTaskId taskId);
}

public interface IDownloadSchedulerControl
{
    Task PauseAllAsync(CancellationToken cancellationToken = default);

    Task ResumeAllAsync(CancellationToken cancellationToken = default);
}

public interface IDownloadRuntimeAvailability
{
    void EnsureAcceptingTasks();

    Task<DownloadRuntimeStartupOutcome> WaitForStartupOutcomeAsync(
        CancellationToken cancellationToken = default);
}

public enum DownloadRuntimeStartupState
{
    Ready,
    Faulted
}

public sealed record DownloadRuntimeStartupOutcome
{
    private DownloadRuntimeStartupOutcome(
        DownloadRuntimeStartupState state,
        Exception? failure)
    {
        State = state;
        Failure = failure;
    }

    public DownloadRuntimeStartupState State { get; }

    public Exception? Failure { get; }

    public static DownloadRuntimeStartupOutcome Ready() =>
        new(DownloadRuntimeStartupState.Ready, null);

    public static DownloadRuntimeStartupOutcome Faulted(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new DownloadRuntimeStartupOutcome(
            DownloadRuntimeStartupState.Faulted,
            failure);
    }
}

public sealed class DownloadRuntimeUnavailableException : InvalidOperationException
{
    public DownloadRuntimeUnavailableException()
    {
    }

    public DownloadRuntimeUnavailableException(string message)
        : base(message)
    {
    }

    public DownloadRuntimeUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IDownloadRuntime : IDownloadTaskQueue, IDownloadSchedulerControl, IDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
