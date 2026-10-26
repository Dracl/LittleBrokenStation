using System;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

// LBS fork: moved from src/DownKyi.Desktop/Platform/DesktopNotificationService.cs (pure event
// aggregator, no Avalonia dependency) so the headless runtime can satisfy IUserNotificationService.
internal sealed class DesktopNotificationService : IUserNotificationService
{
    public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

    public void Show(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
    }
}
