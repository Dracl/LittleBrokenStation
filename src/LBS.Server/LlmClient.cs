using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LBS.Server;

/// <summary>一轮对话消息：role 吃 user/assistant/system/tool（端点验完再进来）；
/// assistant 带工具单时 ToolCallsJson 存上游原样的 tool_calls 数组（回灌时逐字还回去，不自作聪明重拼）；
/// tool 角色的消息用 ToolCallId 认账（哪发工具单的回执）。</summary>
public sealed record LlmChatMessage(string Role, string Content, string? ToolCallId = null, string? ToolCallsJson = null);

/// <summary>一把工具的上游牌面：OpenAI function 形状，ParametersJson 是 JSON Schema 原串（序列化时直接嵌，不再包一层字符串）。</summary>
public sealed record LlmToolSpec(string Name, string Description, string ParametersJson);

/// <summary>上游点的一发工具单：Id 要在回灌 tool 消息时原样对上号；ArgumentsJson 可能是半截坏 JSON，调用方自己接住。</summary>
public sealed record LlmToolCall(string Id, string FunctionName, string ArgumentsJson);

/// <summary>
/// OpenAI 兼容 /chat/completions 客户端：分站 AI 对话（流式）与站长室试炮（非流式）共用一张嘴。
/// 不引 SDK——SSE 增量协议就三行门道："data:" 前缀、[DONE] 收尾、取 choices[0].delta.content，
/// 手写解析足够，为这点线装个包等于雇人送报纸。
/// HttpClient 静态单例 + ResponseHeadersRead（headers 一到就开流，字符随到随发；
/// 每请求 new HttpClient 的写法会把 NAS 的 socket 池烧穿）。
/// 流式总超时 120s（<see cref="CompleteStreamAsync"/>，含读流全程）；
/// 非流式一发 12s（<see cref="CompleteOnceAsync"/>，给 test 口与 M2 画像用）。
/// 抛出的异常都是人话：没配 key 与地址不通分开说，站长不用去翻日志猜是哪根线断了。
/// </summary>
public sealed class LlmClient(ILogger<LlmClient> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan OnceTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 流式对话：逐段吐 delta。上游一行行读 SSE，[DONE] 或流尽收尾；
    /// 坏帧（半截 JSON）跳过不掀桌，调用方断线（cancellationToken 来自 RequestAborted）时上游一并掐断。
    /// </summary>
    public async IAsyncEnumerable<string> CompleteStreamAsync(string baseUrl, string apiKey, string model,
        IReadOnlyList<LlmChatMessage> messages, string? reasoningEffort = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StreamTimeout);
        using var response = await SendAsync(baseUrl, apiKey, model, messages, stream: true, reasoningEffort, tools: null, timeoutCts.Token);
        await using var body = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var reader = new StreamReader(body, Encoding.UTF8);
        while (await reader.ReadLineAsync(timeoutCts.Token) is { } line)
        {
            var frame = line.Trim();
            // SSE 的注释行（": ping" 保活心跳）与空行都是配菜，只认 data: 开头的。
            if (!frame.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = frame[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]" || payload == "\"[DONE]\"") // 有的中转把 DONE 带引号发来
            {
                yield break;
            }

            var delta = ExtractDelta(payload);
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    /// <summary>
    /// 流式对话（工具环专用）：delta 即时交给 onDelta，同时暗中收集 delta.tool_calls 碎片
    ///（按 index 归档，id/名字/参数都是逐帧片断）与 finish_reason。返回的 ToolCalls 非空且 FinishReason 为 tool_calls 时，
    /// 调用方该去跑工具、把回执接回消息尾部再发下一轮。
    /// 坏帧同 CompleteStreamAsync 一路跳过；上游断线/超时的人话异常也同一张嘴。
    /// </summary>
    public async Task<(string? FinishReason, List<LlmToolCall> ToolCalls)> CompleteStreamWithToolsAsync(
        string baseUrl, string apiKey, string model, IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolSpec> tools, string? reasoningEffort,
        Func<string, Task> onDelta, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StreamTimeout);
        using var response = await SendAsync(baseUrl, apiKey, model, messages, stream: true, reasoningEffort, tools, timeoutCts.Token);
        await using var body = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var reader = new StreamReader(body, Encoding.UTF8);

        string? finishReason = null;
        // index → (id, 名, 参数碎屑)：OpenAI 风格的工具单是逐帧片断流，id/名只首帧有，arguments 一路拼。
        var partial = new SortedDictionary<int, (string Id, StringBuilder Name, StringBuilder Args)>();
        while (await reader.ReadLineAsync(timeoutCts.Token) is { } line)
        {
            var frame = line.Trim();
            if (!frame.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = frame[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]" || payload == "\"[DONE]\"")
            {
                continue; // DONE 不掀桌：碎片还得收齐，不能提前 yield break
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    finishReason = fr.GetString();
                }

                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } text)
                {
                    await onDelta(text);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var frag in toolCalls.EnumerateArray())
                    {
                        var index = frag.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : 0;
                        if (!partial.TryGetValue(index, out var slot))
                        {
                            slot = ("", new StringBuilder(), new StringBuilder());
                        }

                        if (frag.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { Length: > 0 } id)
                        {
                            slot = (id, slot.Name, slot.Args);
                        }

                        if (frag.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String && nameEl.GetString() is { Length: > 0 } name)
                            {
                                slot.Name.Append(name);
                            }

                            if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String && argsEl.GetString() is { Length: > 0 } args)
                            {
                                slot.Args.Append(args);
                            }
                        }

                        partial[index] = slot;
                    }
                }
            }
            catch (JsonException)
            {
                logger.LogDebug("SSE 坏帧跳过（工具环）：{Frame}", Truncate(payload, 120));
            }
        }

        var calls = partial
            .Where(kv => kv.Value.Name.Length > 0)
            .Select((kv, n) => new LlmToolCall(
                string.IsNullOrEmpty(kv.Value.Id) ? $"call_{n}" : kv.Value.Id,
                kv.Value.Name.ToString(),
                kv.Value.Args.ToString()))
            .ToList();
        return (finishReason, calls);
    }

    /// <summary>非流式一发（test 口 / M2 画像用）：等整包回齐再吐正文，12s 封顶。</summary>
    public async Task<string> CompleteOnceAsync(string baseUrl, string apiKey, string model,
        IReadOnlyList<LlmChatMessage> messages, string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(OnceTimeout);
        string json;
        try
        {
            using var response = await SendAsync(baseUrl, apiKey, model, messages, stream: false, reasoningEffort, tools: null, timeoutCts.Token);
            json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 自家 12s 表炸的（调用方没掐线）：说人话报超时，别把 OCE 的洋文甩给站长。
            throw new InvalidOperationException($"等 AI 回话超时了（超过 {OnceTimeout.TotalSeconds:0} 秒）");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("AI 服务回的不是 JSON，多半是地址指错了门", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            // 有些中转 200 也夹 error 体，先接住再谈正文。
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var em)
                    ? em.GetString()
                    : err.GetRawText();
                throw new InvalidOperationException($"AI 服务回了错误：{Truncate(msg, 200)}");
            }

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            throw new InvalidOperationException("模型一个字都没回，检查模型名是否拼对");
        }
    }

    /// <summary>发请求的唯一漏斗：流式与非流式只差 stream 旗与读法；tools 非空才进体（不带工具的老链路请求体一字节不变）。异常在这里就折成人话。</summary>
    private async Task<HttpResponseMessage> SendAsync(string baseUrl, string apiKey, string model,
        IReadOnlyList<LlmChatMessage> messages, bool stream, string? reasoningEffort, IReadOnlyList<LlmToolSpec>? tools, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("还没配 AI 地址，去站长室把 base URL 填上");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("还没配 AI key，去站长室把密钥填上");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("还没配模型名，去站长室把 model 填上");
        }

        // reasoning_effort 只在递了值时才进体：OpenAI 兼容各家对这字段的支援不齐（DeepSeek/Kimi 吃，
        // 不少中转直接 400），不选就当没这回事，上游自己定默认，老网关零感知。
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(SerializeMessage).ToArray(),
            ["stream"] = stream,
        };
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            payload["reasoning_effort"] = reasoningEffort.Trim();
        }

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = ParseOrHuman(t.ParametersJson, "工具牌面"),
                },
            }).ToArray();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(baseUrl))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 调用方掐线与自家超时共用这一个口：留给上层照 token 状态分辨，这里不抢判。
            throw;
        }
        catch (HttpRequestException ex)
        {
            // 地址不通与 key 不对分开说：一个查网络/地址，一个查密钥，混成一句「请求失败」等于没说。
            throw new InvalidOperationException($"AI 地址不通，检查一下 base URL 与分站网络（{ex.Message}）", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var snippet = "";
            try
            {
                snippet = Truncate(await response.Content.ReadAsStringAsync(cancellationToken), 200);
            }
            catch
            {
                // 错误体读不出来不影响报状态码，别让它反咬一口。
            }

            var code = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException(HumanizeStatus(code, snippet));
        }

        return response;
    }

    /// <summary>消息序列化的一张嘴：assistant 带工具单时 content 置 null、tool_calls 逐字嵌回；
    /// tool 回执带 tool_call_id 认账；其余形状与旧链路完全同形（不带工具字段，老网关零感知）。</summary>
    private static object SerializeMessage(LlmChatMessage m) => m.Role switch
    {
        "tool" when !string.IsNullOrEmpty(m.ToolCallId) => new { role = m.Role, content = m.Content, tool_call_id = m.ToolCallId },
        "assistant" when !string.IsNullOrEmpty(m.ToolCallsJson) => new { role = m.Role, content = (string?)null, tool_calls = ParseOrHuman(m.ToolCallsJson!, "上轮工具单") },
        _ => new { role = m.Role, content = (string?)m.Content },
    };

    /// <summary>内部 JSON 解析失败别把洋文 JsonException 甩给成员：包成人话再抛，堆栈留在 InnerException 里查。</summary>
    private static JsonElement ParseOrHuman(string json, string what)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{what}没拼对，这轮先不聊了（内部 JSON 解析失败）", ex);
        }
    }

    /// <summary>
    /// baseUrl 归一化：末尾斜杠剃净；已含 /chat/completions 就不重复拼（有人习惯把全链抄进设置框，
    /// 再拼一次就成 /chat/completions/chat/completions，404 得莫名其妙）。
    /// </summary>
    internal static string ResolveEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
    }

    /// <summary>从一帧 SSE data 里抠 delta.content；usage-only 尾帧、坏帧都回 null（跳过不掀桌）。</summary>
    private string? ExtractDelta(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            logger.LogDebug("SSE 坏帧跳过：{Frame}", Truncate(payload, 120));
            return null;
        }
    }

    private static string HumanizeStatus(int code, string snippet) => code switch
    {
        401 or 403 => $"AI 服务不认这把 key（{code}），检查密钥是否过期或没开通该模型",
        404 => "AI 地址下没有 /chat/completions（404），看看 base URL 是不是少了 /v1 或多截了路径",
        429 => "AI 服务在限流（429），歇一会儿再试",
        >= 500 => $"AI 服务那头在闹脾气（{code}），稍后再试",
        _ => $"AI 服务回了 {code}{(string.IsNullOrEmpty(snippet) ? "" : $"：{snippet}")}",
    };

    /// <summary>把上游文本掐短再进异常消息：错误体可能几百 K，日志与人话都受不住。</summary>
    private static string Truncate(string? raw, int max) =>
        string.IsNullOrEmpty(raw) ? "" : raw.Length <= max ? raw : raw[..max] + "…";
}
