using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 事件台账的抄录员：把 EventRecorder 队列里的草稿攒批落库，
/// 收站时冲刷余量。它只是笔的马达，业务侧从不等它。
/// </summary>
public sealed class EventRecorderWorker(EventRecorder recorder) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => recorder.ConsumeAsync(stoppingToken);
}
