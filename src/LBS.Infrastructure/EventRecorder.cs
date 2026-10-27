using System.Threading.Channels;
using LBS.Domain;
using Microsoft.Extensions.Logging;

namespace LBS.Infrastructure;

/// <summary>
/// 分站海马体的笔：业务线程只管 Record 一下（永不阻塞、永不抛），
/// 后台消费者攒批落 events 表。队列满 2 万条丢最新并吼一嗓子（宁可丢日志不可拖下载）。
/// 停机时把队列里剩的冲刷干净，不吞临别遗言。
/// </summary>
public sealed class EventRecorder
{
    private const int QueueCapacity = 20_000;
    private const int BatchSize = 200;
    private static readonly TimeSpan BatchLinger = TimeSpan.FromSeconds(1);

    private readonly StationDb _db;
    private readonly ILogger<EventRecorder> _logger;
    private readonly Channel<EventDraft> _channel = Channel.CreateBounded<EventDraft>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public EventRecorder(StationDb db, ILogger<EventRecorder> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>记一笔（发生瞬间就盖戳，落库交给后台）。任何调用方都不该为它等待。actorMemberId：成员事件的站内身份（M5.4），访客/系统留 null。</summary>
    public void Record(string actorType, long? actorMid, string eventType,
        string? subjectType = null, string? subjectId = null,
        string? payloadJson = null, string? ip = null, long? actorMemberId = null)
    {
        var draft = new EventDraft(actorType, actorMid, eventType, subjectType, subjectId,
            payloadJson, ip, DateTimeOffset.UtcNow.ToString("O"), actorMemberId);
        if (!_channel.Writer.TryWrite(draft))
        {
            _logger.LogWarning("事件队列满了，丢掉一笔 {Event}（{Actor}）", eventType, actorType);
        }
    }

    /// <summary>后台消费循环：攒够一批或等满 1 秒落一次库；停机时把手上与队列里的余量都冲刷干净。</summary>
    public async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var batch = new List<EventDraft>(BatchSize);
        var flushed = new List<EventDraft>();
        try
        {
            while (true)
            {
                batch.Clear();
                try
                {
                    // 第一笔死等（不耗电），后续笔只等 linger，攒满或等满即落库。
                    batch.Add(await _channel.Reader.ReadAsync(cancellationToken));
                    using var linger = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    linger.CancelAfter(BatchLinger);
                    try
                    {
                        while (batch.Count < BatchSize
                               && await _channel.Reader.WaitToReadAsync(linger.Token))
                        {
                            while (batch.Count < BatchSize && _channel.Reader.TryRead(out var next))
                            {
                                batch.Add(next);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // linger 到点：手上这几笔先落库，不等凑满。
                    }
                }
                catch (OperationCanceledException)
                {
                    // 停机：手上这批不能丢，带去和队列余量一起冲刷。
                    flushed.AddRange(batch);
                    break;
                }

                _db.InsertEventsBatch(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // 收工
        }

        while (_channel.Reader.TryRead(out var leftover))
        {
            flushed.Add(leftover);
        }

        if (flushed.Count > 0)
        {
            _db.InsertEventsBatch(flushed);
            _logger.LogInformation("收站前冲刷 {Count} 笔事件入台账", flushed.Count);
        }
    }

    /// <summary>把 payload 对象就地序列化成 JSON 的便捷口（序列化炸了也不拖累业务）。</summary>
    public void RecordWithPayload(string actorType, long? actorMid, string eventType,
        string? subjectType, string? subjectId, object? payload, string? ip = null, long? actorMemberId = null)
    {
        string? json = null;
        if (payload != null)
        {
            try
            {
                json = System.Text.Json.JsonSerializer.Serialize(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "事件 {Event} 的 payload 序列化失败，细节丢了但骨架照记", eventType);
            }
        }

        Record(actorType, actorMid, eventType, subjectType, subjectId, json, ip, actorMemberId);
    }
}
