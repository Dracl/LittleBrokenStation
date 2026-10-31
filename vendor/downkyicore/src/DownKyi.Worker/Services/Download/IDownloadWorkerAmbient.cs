using System;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

// LBS fork: worker 执行链的环境上下文钩子。下载 worker 是 runtime 启动时创建的常驻循环，
// 提交调用链里的 AsyncLocal（比如 LBS 按调用链现取的 B 站 cookie）不会跨队列流进来——
// 没有这个钩子，worker 里第一个带凭据的 B 站请求就会取不到账号（LbsNoAccountException）。
// 宿主注册一份实现，orchestrator 把每个任务的执行包进它给的环境里；不注册则原样直通。
public interface IDownloadWorkerAmbient
{
    Task<T> RunAsync<T>(Func<Task<T>> action);

    Task RunAsync(Func<Task> action);
}

public sealed class NullDownloadWorkerAmbient : IDownloadWorkerAmbient
{
    public static readonly NullDownloadWorkerAmbient Instance = new();

    public Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }

    public Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }
}
