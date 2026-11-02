using System.Text.Json;
using System.Collections.Concurrent;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 前端事件上报口（AI 行为台账的进水管）：播放心跳、停留、页面浏览等由浏览器批量报送。
/// 三条铁律：事件名只认白名单（客户端说了不算，身份由服务端盖章）；每 IP 限流；payload 限长。
/// 访客也收——谁在逛橱窗却不进站，正是 AI 想看的「欲看而不得」。
/// </summary>
public static class EventIngestEndpoints
{
    private static readonly string[] ClientAllowed =
    [
        "watch.open", "watch.play", "watch.pause", "watch.heartbeat", "watch.leave",
        "page.view", "home.open",
    ];

    private static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(1);
    private const int MaxRequestsPerWindow = 40;
    private const int MaxEventsPerRequest = 50;
    private const int MaxPayloadChars = 2048;
    private const int MaxSubjectChars = 128;

    private sealed record ClientEvent(string? Name, string? SubjectType, string? SubjectId, JsonElement Payload);

    private sealed record IngestRequest(IReadOnlyList<ClientEvent>? Events);

    public static void MapEventIngestEndpoints(this WebApplication app)
    {
        var throttle = new ConcurrentDictionary<string, (int Count, DateTimeOffset Start)>();

        app.MapPost("/api/events/ingest", (IngestRequest? request, EventRecorder events, HttpContext http) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var now = DateTimeOffset.UtcNow;
            var slot = throttle.AddOrUpdate(ip,
                _ => (1, now),
                (_, old) => now - old.Start > IpWindow ? (1, now) : (old.Count + 1, old.Start));
            if (slot.Count > MaxRequestsPerWindow)
            {
                return Results.Json(new { message = "上报太密了，歇一分钟" }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (request?.Events is not { Count: > 0 } list)
            {
                return Results.Ok(new { accepted = 0 });
            }

            var member = MemberGate.Current(http);
            var actor = member is null ? EventActors.Guest : EventActors.Member;
            var accepted = 0;

            foreach (var item in list.Take(MaxEventsPerRequest))
            {
                if (item.Name is null || !ClientAllowed.Contains(item.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                string? payload = null;
                if (item.Payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var raw = item.Payload.GetRawText();
                    payload = raw.Length <= MaxPayloadChars ? raw : null;
                }

                events.Record(actor, member?.Mid, item.Name,
                    TrimSubject(item.SubjectType), TrimSubject(item.SubjectId), payload, ip, member?.Id);
                accepted++;
            }

            return Results.Ok(new { accepted });
        });
    }

    private static string? TrimSubject(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : (value.Length <= MaxSubjectChars ? value : value[..MaxSubjectChars]);
}
