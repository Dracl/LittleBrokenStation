using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

// LBS fork: a headless host has no shell to hand a file or folder to, so the desktop open capability is
// not offered here. The registration uses TryAdd, so a desktop host keeps its own AvaloniaPlatformLauncher
// and only headless runs get this implementation - which keeps DownloadManagerCoordinator resolvable while
// making OpenVideoAsync/OpenFolderAsync fail with NotSupportedException instead of silently lying.
internal sealed class NullPlatformLauncher : IPlatformLauncher
{
    private const string UnsupportedMessage =
        "The DownKyi.Worker headless runtime does not provide desktop file or folder opening.";

    public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        throw new NotSupportedException(UnsupportedMessage);
    }

    public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        throw new NotSupportedException(UnsupportedMessage);
    }

    public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        throw new NotSupportedException(UnsupportedMessage);
    }
}
