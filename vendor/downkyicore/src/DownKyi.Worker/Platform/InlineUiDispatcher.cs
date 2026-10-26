using System;
using System.Threading.Tasks;

namespace DownKyi.Platform;

// LBS fork: headless replacement for AvaloniaUiDispatcher. There is no UI thread in a headless host,
// so every projection callback runs inline on the calling thread.
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }
}
