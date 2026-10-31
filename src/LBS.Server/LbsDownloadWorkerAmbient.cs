using DownKyi.Services.Download;

namespace LBS.Server;

/// <summary>
/// 给下载 worker 的执行链补上采集号：worker 是引擎 runtime 启动时创建的常驻循环，AsyncLocal 不跨
/// 队列传播——提交链里的投稿号/采集号流不进它，worker 里第一个带凭据的 B 站请求（解析 playurl 等）
/// 在账号分流改造后一律判无号，异常炸掉 worker 还一行日志都没有（10-26 账号分流上线后引擎零完成的原凶）。
/// 钩子把每个任务的执行包进采集号上下文：worker 干的正是「站里的活」，缺站里的号就给它补站里的号。
/// 号池空了原样直通——请求时抛的 LbsNoAccountException 由 worker 的兜底 catch 记 Failed + 留日志，
/// 不再是静默僵尸；站长补上号后加档重投即可恢复。
/// </summary>
public sealed class LbsDownloadWorkerAmbient(LbsCollectPolicy collect) : IDownloadWorkerAmbient
{
    public Task<T> RunAsync<T>(Func<Task<T>> action) =>
        collect.HasAccount() ? collect.RunForActivityAsync(action) : action();

    public Task RunAsync(Func<Task> action) =>
        collect.HasAccount() ? collect.RunForActivityAsync(action) : action();
}
