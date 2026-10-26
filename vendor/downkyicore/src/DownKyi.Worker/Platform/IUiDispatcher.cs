using System;
using System.Threading.Tasks;

namespace DownKyi.Platform;

// LBS fork: moved from src/DownKyi.Desktop/Platform/IUiDispatcher.cs so the download runtime can be
// hosted without Avalonia. Made public so an external headless host can supply its own dispatcher.
public interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}
