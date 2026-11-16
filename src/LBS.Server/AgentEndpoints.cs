using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 分站 AI 管道（M1.5，成员自带干粮版）：status/chat（SSE 流）+ 席位自管接线（/api/member/ai 一家四口）。
/// 11-16 站长拍板：AI 配置从站长室搬进个人空间——每人接自己的 key、花自己的 token，
/// 不配置就没有 AI（status.ready 如实报虚）；站级设定（settings KV 的 agent-*）与站长室设定卡同批退役。
/// 门只有一色：全部端点吃 MemberGate.Current（姿势照 MemberMessageEndpoints 的 notify-prefs），
/// status 对匿名也如实答（visible=false），不该让匿名问一句「有没有 AI」就挨 401。
/// 每轮对话事后往台账落一行 agent.chat（一个字没吐回来的失败落 agent.chat.fail 不占额），
/// 日额度就是从这些行数出来的：账即是额度，不开第二本计数器。
/// </summary>
public static class AgentEndpoints
{
    /// <summary>历史裁剪三把尺：最近 20 条、单条 4000 字、总量 20000 字（超了从最旧丢，最后一条 user 必留）。</summary>
    private const int HistoryMaxMessages = 20;
    private const int HistoryMaxCharsPerMessage = 4000;
    private const int HistoryMaxTotalChars = 20_000;

    /// <summary>每席位每日对话封顶：key 是自己的、钱也是自己的，这一档只防失控脚本把自己刷爆，不是站级配给。</summary>
    private const int MemberDailyChatCap = 100;

    /// <summary>工具环封顶轮数：首轮带工具单，模型连连点单也不许无限玩——末轮不带工具，强制作答。</summary>
    private const int MaxToolRounds = 3;

    /// <summary>看板娘性格卡（11-16 站长令：默认 prompt 暴露可改）：性格与说话风格归站长写，存 settings KV；
    /// 日期、成员名、家底数字与数据查账规矩由站里每轮硬注入——性格卡怎么写都拆不掉防编造的底线。</summary>
    public const string PersonaSettingKey = "agent-persona";
    private const int PersonaMaxChars = 4000;
    private const string DefaultPersona = """
        你是「小破分站」的看板娘，也是这座分站的小管家。你熟悉站里的日常，乐于帮成员找片、了解近况、查询自己的活动。

        说话自然、亲切、有分寸，像一位熟悉的站内伙伴。可以有一点俏皮，但不刻意卖萌，不堆表情，不奉承，也不反复道歉。默认用中文，先回答重点，再补必要说明；简单问题简短回答，需要比较或解释时再展开。

        认真回应每个问题。涉及站内事实，先依据可用工具查证；查不到时，说明缺少什么信息，或者当前能查到哪一步。分清「没有查询结果」「查询失败」与「没有这项查询能力」，不要一概说成不存在。

        面对追问和质疑，先核对依据。发现自己说错就明确纠正，有依据的回答也不用为了迎合而改口。不要把推测说成事实，不要把建议说成已经执行。

        回答清楚就可以结束；确有帮助时，再给一个相关的下一步建议，不必每次都追问。

        不要发emoji作为表情包，可以使用颜文字，比如٩(•̤̀ᵕ•̤́๑) 之类的。
        """;

    /// <summary>七把只读工具（M2 + 11-17 拉取制扩容 + 同日观影足迹）：全是现成取数口的包装，递出去的只有成员本就看得到的人话字段（可见性尺同搜索页），
    /// 写类操作（投稿/推荐）不在这张桌上——prompt 里钉死「动手的活还没上岗」。</summary>
    private static readonly LlmToolSpec[] AgentTools =
    {
        new("search_videos", "搜索站内已到站的视频，回标题、BV号、UP主、B 站播放文本、弹幕数与站内链接。问某支视频或找某类视频时用。",
            """{"type":"object","properties":{"query":{"type":"string","description":"关键词，空格分隔"},"limit":{"type":"integer","description":"最多几条，默认8，封顶10"}},"required":["query"]}"""),
        new("list_arrivals", "列出近 N 日（默认 7 日，UTC+8 日历日）到站的视频，新先。回标题、BV号、UP主、播放与到站时刻。",
            """{"type":"object","properties":{"days":{"type":"integer","description":"往前几天，默认7，封顶14"},"limit":{"type":"integer","description":"最多几条，默认10，封顶20"}}}"""),
        new("site_stats", "取全站总览：到货/投稿总数、今日到货、分区计数、全站互动五维（播放点赞投币收藏弹幕，B 站快照真数）、热榜前五、站内观映近 7 日（站内自己的被看次数/成员与游客人头/站内热榜）；站长席位额外带队列健康。问大盘数字时用。",
            """{"type":"object","properties":{}}"""),
        new("member_activity", "查当前这位成员自己的站内活动：累计投稿与到货、站内弹幕条数、最新投稿。问自己的账时用。",
            """{"type":"object","properties":{}}"""),
        new("watch_history", "查当前这位成员自己的观看足迹（最近看过的片，新先）：标题、BV号、看到过哪一 P、最近观看时刻。问「我最近看过什么」「上次看的那支」「我都看过哪些片」时用。只回足迹名录，没有观看进度与播放时长。",
            """{"type":"object","properties":{"limit":{"type":"integer","description":"最多几条，默认10，封顶20"}}}"""),
        new("weekly_report", "取一站分站周报（week：0=最近一个已过完的周，1..4 往前数）：新货名录与件数、站内热榜（去重观众）、热门 UP 与热门分区（人头/次数/片数）、逐日被看曲线、全站动向（被看次数/观众/搜索/投稿/弹幕/新成员/评论归档）、游客动向，以及你本人的本周战绩（打开/播放/看片数/搜索/投稿/弹幕/评论）与占全站份额和排名。问「这周/上周怎么样」「我本周战绩」时用。",
            """{"type":"object","properties":{"week":{"type":"integer","description":"往前数第几周，0..4，默认 0"}}}"""),
        new("zone_videos", "列分站某个顶级分区的货架（到站新先），回标题/BV/UP主/发布日期。问「音乐/动画…区有什么」时用；分区名先喊 site_stats 拿分区清单。",
            """{"type":"object","properties":{"zone":{"type":"string","description":"顶级分区名，如 音乐、动画、生活"},"limit":{"type":"integer","description":"最多几条，默认10，封顶20"}},"required":["zone"]}"""),
    };

    public static void MapAgentEndpoints(this WebApplication app)
    {
        // ── 成员侧：探针与对话 ─────────────────────────────────

        // 探针端点：前端据此决定 AI 坞摆不摆、要不要催接线。永不 401，也永不泄 key（连 hasKey 都不给——
        // 那是席位的私房情报）；席位可见时顺手把两枚统计 chip 的数带上（单发 SQL），前端不拿全库 dump 数数。
        app.MapGet("/api/agent/status", (StationDb db, HttpContext http) =>
        {
            var member = MemberGate.Current(http);
            if (member is null)
            {
                return Results.Ok(new { visible = false, ready = false, model = "", todayCount = (long?)null, totalCount = (long?)null, reasoningOptions = "", contextK = (int?)null, contextTokens = 0 });
            }

            var ai = db.GetMemberAi(member.Id);
            var (sinceUtc, untilUtc) = TodayWindowUtc(DateTimeOffset.UtcNow);
            // 上下文两笔（11-16 美化令）：上限是席位自己配的窗口（≤0 当未配），已用量的底是 system prompt 的粗估 token
            //（EstimateTokens 与前端同一把尺，工具条的「已用 x%」= 这枚底 + 本机历史粗估）。
            var ctxK = ai?.ContextK is > 0 ? ai!.ContextK : null;
            var ctxTokens = IsUsable(ai) ? EstimateTokens(BuildSystemPrompt(db, member, DateTimeOffset.UtcNow)) : 0;
            return Results.Ok(new
            {
                visible = true,
                ready = IsUsable(ai),
                model = ai?.Model ?? "",
                todayCount = db.CountArrivedToday(sinceUtc, untilUtc),
                totalCount = db.ListZones().Sum(z => z.Count),
                // 思考档位选型跟着牌面走：AI 坞工具条的「思考」下拉就吃这一串（非敏感，不是 key）。
                reasoningOptions = ai?.ReasoningOptions ?? "",
                contextK = ctxK,
                contextTokens = ctxTokens,
            });
        });

        // 对话正门：验人 → 验接线 → 验形状 → 裁历史 → 数额度 → 开流（烧的是这位席位自己的 key）。
        // 全程直写响应（SSE 帧一帧一 Flush），所以各条拒路也自己盖状态码，不借 Results。
        app.MapPost("/api/agent/chat", async (AgentChatRequest? request, LlmClient llm,
            StationDb db, EventRecorder events, HttpContext http) =>
        {
            var response = http.Response;

            var member = MemberGate.Current(http);
            if (member is null)
            {
                response.StatusCode = StatusCodes.Status401Unauthorized;
                await response.WriteAsJsonAsync(new { message = "看板娘只招待席位里的朋友，先登录再来聊" });
                return;
            }

            var ai = db.GetMemberAi(member.Id);
            if (!IsUsable(ai))
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                await response.WriteAsJsonAsync(new { message = "你还没接线：去个人空间（右上头像 → 设置）把 AI 地址、密钥和模型配齐，AI 才归你使" });
                return;
            }

            if (request?.Messages is not { Count: > 0 } incoming)
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                await response.WriteAsJsonAsync(new { message = "总得先说点什么，我才答得上话" });
                return;
            }

            if (incoming.Any(m => m.Role is not ("user" or "assistant")))
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                await response.WriteAsJsonAsync(new { message = "消息角色只认 user 和 assistant" });
                return;
            }

            if (incoming[^1].Role != "user")
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                await response.WriteAsJsonAsync(new { message = "最后一条得是你问的话" });
                return;
            }

            var history = TrimHistory(incoming);

            // 日额度：数当日（UTC+8 那一个日历日，与周报 LocalWeekOffset 同一把算日法）台账里
            // 这位席位的 agent.chat 行。账即是额度——被拒的调用与没吐字的失败都没落正账，自然不占额度。
            var (sinceUtc, untilUtc) = TodayWindowUtc(DateTimeOffset.UtcNow);
            var used = db.CountAgentChatsToday(member.Id, sinceUtc, untilUtc);
            if (used >= MemberDailyChatCap)
            {
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                await response.WriteAsJsonAsync(new { message = "今天找看板娘聊得够多了，明天再来（自家 key 也省着点烧）" });
                return;
            }

            // 思考档位白名单：前端选的值必须命中这位席位自己配的选型（大小写不敏感）才递给上游；
            // 没命中就当没选而非拒聊——选型可能刚在配置页改过，浏览器里一台旧 localStorage 不该把对话卡死。
            string? reasoning = null;
            if (!string.IsNullOrWhiteSpace(request.Reasoning) && !string.IsNullOrWhiteSpace(ai!.ReasoningOptions))
            {
                var wanted = request.Reasoning.Trim();
                if (ai.ReasoningOptions.Split('|', StringSplitOptions.TrimEntries)
                        .Any(o => o.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    reasoning = wanted;
                }
            }

            var prompt = BuildSystemPrompt(db, member, DateTimeOffset.UtcNow);
            var messages = new List<LlmChatMessage>(history.Count + 1) { new("system", prompt) };
            messages.AddRange(history);

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no"; // nginx 身后不关这扇，缓冲会把 SSE 攒成一坨再吐

            var chars = 0;
            var ok = false;
            var degraded = false; // 上游不吃工具单（老网关/小模型常见）：降级成只聊概况的旧链路，本轮不再递工具
            var toolNamesUsed = new List<string>();
            string? error = null;
            var token = http.RequestAborted;

            async Task EmitDelta(string d)
            {
                chars += d.Length;
                await response.Body.WriteAsync(Frame($"{{\"delta\":{JsonSerializer.Serialize(d)}}}"), token);
                await response.Body.FlushAsync(token);
            }

            async Task EmitFrame(string json)
            {
                await response.Body.WriteAsync(Frame(json), token);
                await response.Body.FlushAsync(token);
            }

            try
            {
                // 裁剪透明化（11-16 上下文包第一步）：历史被那三重尺子掉了几条，摆淡灰一行告诉成员，失忆失得明白。
                if (incoming.Count > history.Count)
                {
                    await EmitFrame($"{{\"note\":{JsonSerializer.Serialize($"为省 token，本轮没带最旧的 {incoming.Count - history.Count} 条历史")}}}");
                }

                var wantTools = ai!.ToolsEnabled is not false; // null = 默认开；个人空间可拆
                for (var round = 0; ; round++)
                {
                    var lastRound = round >= MaxToolRounds - 1;
                    string? finish;
                    List<LlmToolCall> calls;
                    try
                    {
                        if (wantTools && !degraded && !lastRound)
                        {
                            (finish, calls) = await llm.CompleteStreamWithToolsAsync(ai.BaseUrl!, ai.ApiKey!, ai.Model!, messages, AgentTools, reasoning, EmitDelta, token);
                        }
                        else
                        {
                            calls = [];
                            finish = null;
                            await foreach (var delta in llm.CompleteStreamAsync(ai.BaseUrl!, ai.ApiKey!, ai.Model!, messages, reasoning, token))
                            {
                                await EmitDelta(delta);
                            }
                        }
                    }
                    catch (InvalidOperationException) when (wantTools && !lastRound && chars == 0 && !degraded && round == 0)
                    {
                        // 连首包都没吐就抱错、而身上带着工具单：八成是老网关/小模型不吃 tools 字段，白送一发不带工具的救援，
                        // 递淡灰一行告诉成员「只聊概况」——不能换个模型站 AI 就当哑巴。
                        degraded = true;
                        await EmitFrame("{\"note\":\"上游没吃下工具单，本轮只带概况作答\"}");
                        continue;
                    }

                    if (calls.Count == 0 || lastRound)
                    {
                        break; // 没点单，或末轮强制作答（就算还想点单也不陪了），本轮收尾
                    }

                    foreach (var call in calls)
                    {
                        toolNamesUsed.Add(call.FunctionName);
                        var (summary, resultJson) = RunTool(db, member, call.FunctionName, call.ArgumentsJson);
                        await EmitFrame($"{{\"tool\":{JsonSerializer.Serialize(call.FunctionName)},\"status\":{(summary.StartsWith("出错", StringComparison.Ordinal) ? "\"err\"" : "\"done\"")},\"summary\":{JsonSerializer.Serialize(summary)}}}");
                        // 工具单与回执回灌：tool_calls 用序列化器拼，绝不手撮括号——11-16 血案：手撮多打一根 ']'，
                        // 回灌时 Parse 当场爆，整轮聊死还甩了成员一嘴洋文。
                        var toolCallsJson = JsonSerializer.Serialize(new[]
                        {
                            new
                            {
                                id = call.Id,
                                type = "function",
                                function = new { name = call.FunctionName, arguments = call.ArgumentsJson },
                            },
                        });
                        messages.Add(new LlmChatMessage("assistant", "", null, toolCallsJson));
                        messages.Add(new LlmChatMessage("tool", resultJson, call.Id));
                    }
                }

                ok = true;
                var quotaLeft = Math.Max(0, MemberDailyChatCap - (int)used - 1); // 本轮这发也占一格
                await response.Body.WriteAsync(Frame($"{{\"done\":true,\"quotaLeft\":{quotaLeft}}}"), CancellationToken.None);
                await response.Body.FlushAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // 两种断法分家：页面关了（RequestAborted 炸的）静悄悄收场；上游 120s 没回话才是事故。
                if (token.IsCancellationRequested)
                {
                    error = "客户端中途离场";
                }
                else
                {
                    error = "等 AI 回话超时了（超过 120 秒）";
                    await TryWriteFrame(response, $"{{\"error\":{JsonSerializer.Serialize(error)}}}");
                }
            }
            catch (Exception ex)
            {
                // LlmClient 抛的都是人话（没配 key / 地址不通 / 限流……），原样递进 error 帧。
                error = ex.Message;
                await TryWriteFrame(response, $"{{\"error\":{JsonSerializer.Serialize(ex.Message)}}}");
            }

            // 事后账：成败都落行，但名字分家——上游一个字都没吐回来的失败（地址不通、key 作废）记成
            // agent.chat.fail：留痕可查，却不占日额度——AI 宕机不该烧成员的额度；
            // 吐过字的（含中途断线）都算真烧了 token，照数进 agent.chat。
            var quotaCounts = ok || chars > 0;
            events.RecordWithPayload(EventActors.Member, member.Mid, quotaCounts ? EventNames.AgentChat : EventNames.AgentChatFail, "agent", null,
                new { model = ai!.Model, chars, ok, error, reasoning, tools = toolNamesUsed, degraded },
                http.Connection.RemoteIpAddress?.ToString(), member.Id);
        });

        // ── 席位自管接线（个人空间抽屉里的「分站 AI」那一块）────────
        // 全 key 只进不出：GET 回 hasKey/keyHint（尾四位，短 key 全打星），test 只回通没通。
        // 一人一行存 member_ai 表；拆线（DELETE）连行拔除，key 跟着没。

        // ── 荐片视频卡取数面（11-17 站长令：看板娘荐片发私信同款分享卡）──
        // 模型只吐 BV 号（```video 围栏），前端拿这批号来这里换卡面：与搜索页同尺只认已到站，
        // 别人的私稿、下架、未入库都一律 found=false——死活报给前端，缘由不外泄。
        app.MapPost("/api/agent/videocards", (VideoCardBatchRequest? request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } member)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var wanted = (request?.Bvids ?? Array.Empty<string>())
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray();
            var scope = MemberGate.IsOwner(member) ? VisibilityScope.Everything : VisibilityScope.Of(member.Id);
            var cards = wanted.Select(bvid =>
            {
                // 搜索口本身就命 BV 号，拿它当查卡器：可见性尺、已到站滤都是现成的，不开第二道取数口。
                var hit = db.SearchVideos(bvid, null, 3, scope)
                    .FirstOrDefault(v => string.Equals(v.Bvid, bvid, StringComparison.OrdinalIgnoreCase));
                if (hit is null)
                {
                    return new { bvid, found = false, title = (string?)null, cover = (string?)null, upName = (string?)null, durationText = (string?)null, upFace = (string?)null };
                }
                // 来源行那枚小脸：UP 名片现采的 up_face（空串是采失败销账，当没有）；
                // 没采过的不硬凑，前端那枚默认灰人兑底——私信分享卡没这栏，同一条降级路。
                var face = db.GetUpCardState(hit.Id).Face;
                return new { bvid, found = true, title = hit.Title, cover = hit.CoverUrl, upName = hit.UpName, durationText = DurationText.Normalize(hit.DurationText), upFace = string.IsNullOrWhiteSpace(face) ? null : face };
            }).ToArray();
            return Results.Ok(new { cards });
        });

        app.MapGet("/api/member/ai", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(SnapshotJson(db.GetMemberAi(me.Id)));
        });

        app.MapPut("/api/member/ai", (MemberAiSaveRequest? request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // 字段子集语义：null 栏不改；baseUrl/model 给空白 = 拆那一栏；apiKey 空白 = 不改
            //（key 的清除走 DELETE 整行拔——表单回显空栏不该顺手抹掉存好的 key）。
            string? baseUrl = null;
            if (request?.BaseUrl is not null)
            {
                if (string.IsNullOrWhiteSpace(request.BaseUrl))
                {
                    baseUrl = "";
                }
                else if (!TryNormalizeBaseUrl(request.BaseUrl, out baseUrl, out var urlError))
                {
                    return Results.BadRequest(new { message = urlError ?? "AI 地址只收 http(s):// 绝对地址" });
                }
            }

            var apiKey = string.IsNullOrWhiteSpace(request?.ApiKey) ? null : request!.ApiKey!.Trim();
            string? model = null;
            if (request?.Model is not null)
            {
                model = string.IsNullOrWhiteSpace(request.Model) ? "" : request.Model.Trim();
            }

            // 推理选型同字段子集语义：null 不改、空白清栏；形不正直接 400，不默默吞选型。
            string? reasoning = null;
            if (request?.ReasoningOptions is not null && !TryNormalizeReasoningOptions(request.ReasoningOptions, out reasoning, out var reasoningError))
            {
                return Results.BadRequest(new { message = reasoningError ?? "推理强度档位格式不对" });
            }

            // 上下文窗口（单位 k）：null 不改；0 = 清栏；负数/超百万直接 400（这数只拿来算百分比，收得宽容但不收野值）。
            int? contextK = null;
            if (request?.ContextK is int ck)
            {
                if (ck < 0 || ck > 1_000_000)
                {
                    return Results.BadRequest(new { message = "上下文大小收 0~1000000 的整数（单位 k，0 = 清掉）" });
                }

                contextK = ck;
            }

            // 工具开关（M2）：null 不改；true/false 直写（库里存 0/1，读回 null = 开）。拆它的是席位自己，站里不拦头。
            db.SaveMemberAi(me.Id, baseUrl, apiKey, model, reasoning, contextK, request?.ToolsEnabled);
            return Results.Ok(SnapshotJson(db.GetMemberAi(me.Id)));
        });

        app.MapGet("/api/admin/agent/persona", (StationDb db) =>
        {
            // 递三段：存着的自定义（空=没用过）、生效中是不是自定义、内置默认原文（前端直接拿它当 placeholder 展给站长看）。
            var saved = db.GetSetting(PersonaSettingKey);
            return Results.Ok(new
            {
                persona = saved ?? "",
                isCustom = !string.IsNullOrWhiteSpace(saved),
                defaultPersona = DefaultPersona,
                maxChars = PersonaMaxChars,
            });
        });

        app.MapPut("/api/admin/agent/persona", (AgentPersonaRequest? request, StationDb db, EventRecorder events, HttpContext http) =>
        {
            var text = request?.Persona?.Trim() ?? "";
            if (text.Length > PersonaMaxChars)
            {
                return Results.BadRequest(new { message = $"性格卡最长 {PersonaMaxChars} 字，现在写了 {text.Length} 字" });
            }

            // 空串 = 回到内置默认（行留着写空，组装时判白当默认用）；存卡留痕，改没改都落行。
            db.SetSetting(PersonaSettingKey, text);
            events.RecordWithPayload(EventActors.Admin, null, EventNames.AdminAgentPersona, "agent", null,
                new { chars = text.Length, isCustom = text.Length > 0 },
                http.Connection.RemoteIpAddress?.ToString(), null);
            return Results.Ok(new
            {
                persona = text,
                isCustom = text.Length > 0,
                defaultPersona = DefaultPersona,
                maxChars = PersonaMaxChars,
            });
        });

        app.MapDelete("/api/member/ai", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            db.ClearMemberAi(me.Id);
            return Results.Ok(new { message = "线拆了，AI 坞回到待接线", @operator = "cleared" });
        });

        // 试炮：非流式一发 "ping"（12s 封顶）。表单里的草稿优先、缺栏回落存好的值——
        // 让「配完先试再存」成立；只回通没通与 latency，不回正文，省得被当免费补全用。
        app.MapPost("/api/member/ai/test", async (MemberAiTestRequest? request, LlmClient llm,
            StationDb db, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var saved = db.GetMemberAi(me.Id);
            var baseUrl = PickDraft(request?.BaseUrl, saved?.BaseUrl);
            var apiKey = PickDraft(request?.ApiKey, saved?.ApiKey);
            var model = PickDraft(request?.Model, saved?.Model);
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
            {
                return Results.BadRequest(new { message = "地址、密钥、模型三样都齐了才试得出炮" });
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                await llm.CompleteOnceAsync(baseUrl!, apiKey!, model!, [new LlmChatMessage("user", "ping")], null, cancellationToken);
                return Results.Ok(new { ok = true, latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, error = ex.Message });
            }
        });
    }

    /// <summary>三件套（地址+key+模型）齐才算接好线：status 的 ready 与 chat 的 403 共用这一把尺。</summary>
    private static bool IsUsable(StationDb.MemberAiConfig? ai) =>
        !string.IsNullOrWhiteSpace(ai?.BaseUrl) && !string.IsNullOrWhiteSpace(ai?.ApiKey) && !string.IsNullOrWhiteSpace(ai?.Model);

    /// <summary>test 草稿回落：表单给了非空白就用表单的，没给吃存好的。</summary>
    private static string? PickDraft(string? draft, string? saved) =>
        string.IsNullOrWhiteSpace(draft) ? saved : draft.Trim();

    /// <summary>对外形状：全 key 永不露面，只给 hasKey + keyHint（后四位；≤4 位短 key 整个就是 key 本体，一律打星）。</summary>
    private static object SnapshotJson(StationDb.MemberAiConfig? ai)
    {
        var key = ai?.ApiKey;
        var hasKey = !string.IsNullOrWhiteSpace(key);
        return new
        {
            hasKey,
            baseUrl = string.IsNullOrWhiteSpace(ai?.BaseUrl) ? null : ai!.BaseUrl,
            model = string.IsNullOrWhiteSpace(ai?.Model) ? null : ai!.Model,
            keyHint = !hasKey ? "" : key!.Length <= 4 ? new string('*', key.Length) : key[^4..],
            configured = IsUsable(ai),
            reasoningOptions = ai?.ReasoningOptions ?? "",
            contextK = ai?.ContextK is > 0 ? ai!.ContextK : null,
            toolsEnabled = ai?.ToolsEnabled is not false,
        };
    }

    /// <summary>token 粗估（11-16 美化令，与 AgentDock 的 estTokens 同一把尺）：CJK/全角每字计 1，其余可视字符 4 字折 1。
    /// 只拿来喂「已用 x%」的观感，不是计费口径，别拿它当真数。</summary>
    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        double units = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            units += ch >= '\u2E80' ? 1d : 0.25d;
        }

        return (int)Math.Ceiling(units);
    }

    /// <summary>历史裁剪：最近 20 条 → 单条掐到 4000 字 → 总量超 20000 从最旧丢（最后一条是本轮问话，钉死不留）。</summary>
    private static List<LlmChatMessage> TrimHistory(IReadOnlyList<AgentChatMessage> incoming)
    {
        var taken = new List<LlmChatMessage>();
        foreach (var m in incoming.Skip(Math.Max(0, incoming.Count - HistoryMaxMessages)))
        {
            var content = m.Content ?? "";
            taken.Add(new LlmChatMessage(m.Role!, content.Length > HistoryMaxCharsPerMessage ? content[..HistoryMaxCharsPerMessage] : content));
        }

        var total = taken.Sum(m => m.Content.Length);
        var drop = 0;
        while (total > HistoryMaxTotalChars && drop < taken.Count - 1)
        {
            total -= taken[drop].Content.Length;
            drop++;
        }

        return taken.GetRange(drop, taken.Count - drop);
    }

    /// <summary>当日窗口（UTC+8 日历日）的起止，折成 UTC "O" 串——与台账 ts_utc / created_at_utc 的存储格式同尺。</summary>
    private static (string SinceUtc, string UntilUtc) TodayWindowUtc(DateTimeOffset nowUtc)
    {
        var localNow = nowUtc.ToOffset(StationDb.LocalWeekOffset);
        var dayStart = new DateTimeOffset(
            DateOnly.FromDateTime(localNow.DateTime).ToDateTime(TimeOnly.MinValue), StationDb.LocalWeekOffset);
        return (dayStart.ToUniversalTime().ToString("O"), dayStart.AddDays(1).ToUniversalTime().ToString("O"));
    }

    /// <summary>
    /// system prompt 组装（11-17 拉取制改造令）：性格卡（站长室可改，settings KV 存 agent-persona，留空用内置默认）
    /// + 日期、成员名、一行极薄开场快照（今日到货/馆藏/累计投稿——最高频问题，豁免一跳工具）+ 数据查账规矩。
    /// 其余站内数字一律不随每轮搭车：LLM 无状态，system 每轮都重发——旧制下闲聊一句也要多烧三百来 token 的家底税，
    /// 如今数据全住工具台，system 只留人格与底线（11-16 血案的对策 3/4 原样保留：被质疑就重查，不许认错真数）。
    /// </summary>
    private static string BuildSystemPrompt(StationDb db, StationMember member, DateTimeOffset nowUtc)
    {
        var localNow = nowUtc.ToOffset(StationDb.LocalWeekOffset);
        var stats = db.AdminStats(); // 保险丝只用总览两笔（馆藏/累计投稿）+ 今日到货，其余数字全在工具台
        var (sinceUtc, untilUtc) = TodayWindowUtc(nowUtc);
        var arrivedToday = db.CountArrivedToday(sinceUtc, untilUtc);
        var who = !string.IsNullOrWhiteSpace(member.Nickname) ? member.Nickname
            : !string.IsNullOrWhiteSpace(member.Username) ? member.Username
            : "这位成员";

        var custom = db.GetSetting(PersonaSettingKey);
        var persona = string.IsNullOrWhiteSpace(custom) ? DefaultPersona : custom.Trim();

        return $$"""
            {{persona}}

            你正在跟站内成员「{{who}}」聊天，今天是 {{localNow:yyyy-MM-dd}}（UTC+8）。
            开场快照（本轮开局时点，可直接引）：今日到货 {{arrivedToday}} 件 · 馆藏 {{stats.Arrived}} 件（累计投稿 {{stats.Total}} 件）。其余一切站内数字都必须喊工具去取。
            规矩：
            1. 站内的事不在你脑子里，在工具台上——按需喊：search_videos 查片 / list_arrivals 到货名录 / site_stats 大盘与站内观映 / zone_videos 分区货架 / weekly_report 周报 / member_activity 你自己的账；一轮里可以连喊几把；
            2. 站内数字只认开场快照与本轮工具回的值，两头都没有就说不确定，绝不许编；本轮没调过工具，就不报站内的任何具体数字；
            3. 被质疑是重查的机会：重新喊工具核数、核时间范围与口径，是自己误读记错就承认并纠正；不许为迎合人改口，也不许把查实的数字说成自己编的；
            4. 工具没回的字段就是不知道——分清「没结果」「查询失败」「没这项查询能力」，别一概说成不存在；工具回了错就如实说错，别硬编数字圆场；
            5. 写操作（投稿、下载、删改、设置）还没上岗：答「这个还没上岗，正在装配」，绝不假装做过；荐片不是写操作，照常做；
            6. 推荐视频用视频卡形式：每支推荐单独输出一个 ```video 围栏块，块内每行只有一个 BV 号，且必须是本轮工具回过的号；没调过工具就不发卡，也别编片名与 BV 号。随口荐一两支直接发卡就行，多字段比较才用表格，短清单用列表；
            7. 聊分站以外的事可以正常回答。
            """;
    }

    /// <summary>大数折字（只给 prompt 看）：过亿说亿、过万说万，一位小数，省 token 也好读。</summary>
    private static string HumanNum(long n) => n switch
    {
        >= 100_000_000 => $"{n / 10_000_000d:0.#}亿",
        >= 10_000 => $"{n / 10_000d:0.#}万",
        _ => n.ToString(),
    };

    /// <summary>标题掐尖：prompt 里的名录不拿长标题霸屏。</summary>
    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>工具台（M2 + 拉取制扩容 + 观影足迹）：七把只读口全在服务器内跑，成员递的参数只当查数线索、不当权限——
    /// 可见性尺永远由站里掌（Of(member.Id) / Everything）；出错回人话 + 给模型的 error 体，不抛不爆流。</summary>
    private static (string Summary, string Json) RunTool(StationDb db, StationMember member, string name, string argsRaw)
    {
        JsonDocument? args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsRaw) ? "{}" : argsRaw);
        }
        catch (JsonException)
        {
            return ("出错：工具参数读不懂", """{"error":"malformed arguments"}""");
        }

        using (args)
        {
            var root = args.RootElement;
            try
            {
                switch (name)
                {
                    case "search_videos":
                    {
                        var query = root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return ("出错：没给搜索关键词", """{"error":"missing query"}""");
                        }

                        var limit = ClampInt(root, "limit", 8, 1, 10);
                        var hits = db.SearchVideos(query, null, limit, VisibilityScope.Of(member.Id))
                            .Select(v => new
                            {
                                title = Shorten(v.Title ?? v.Bvid, 40),
                                bvid = v.Bvid,
                                up = v.UpName,
                                view = v.ViewCountText,
                                danmaku = v.DanmakuCount,
                                url = $"/video/{v.Id}",
                            })
                            .ToArray();
                        return ($"命中 {hits.Length} 条", JsonSerializer.Serialize(new { hits }));
                    }

                    case "list_arrivals":
                    {
                        var days = ClampInt(root, "days", 7, 1, 14);
                        var limit = ClampInt(root, "limit", 10, 1, 20);
                        var localNow = DateTimeOffset.UtcNow.ToOffset(StationDb.LocalWeekOffset);
                        var dayStart = new DateTimeOffset(DateOnly.FromDateTime(localNow.DateTime).ToDateTime(TimeOnly.MinValue), StationDb.LocalWeekOffset).AddDays(-(days - 1));
                        var rows = db.ListArrivalsForAgent(limit, dayStart.ToUniversalTime().ToString("O"), dayStart.AddDays(days).ToUniversalTime().ToString("O"), VisibilityScope.Of(member.Id))
                            .Select(r => new
                            {
                                title = Shorten(r.Title ?? r.Bvid, 40),
                                bvid = r.Bvid,
                                up = r.UpName,
                                view = r.ViewCountText,
                                arrived = LocalStamp(r.ArrivedAtUtc),
                            })
                            .ToArray();
                        return ($"近 {days} 日到货 {rows.Length} 件", JsonSerializer.Serialize(new { arrivals = rows }));
                    }

                    case "site_stats":
                    {
                        var stats = db.AdminStats();
                        var (sinceUtc, untilUtc) = TodayWindowUtc(DateTimeOffset.UtcNow);
                        var hot = db.ListAgentHottest(5, VisibilityScope.PublicOnly)
                            .Select(h => new { title = Shorten(h.Title, 30), view = h.ViewText })
                            .ToArray();
                        // 站内观映（11-17 站长补数据令）：站内自己的观众账，与 B 站快照五维分开两节——口径照周报，聚合数不递明细。
                        var localNow = DateTimeOffset.UtcNow.ToOffset(StationDb.LocalWeekOffset);
                        var weekSince = new DateTimeOffset(localNow.Date.AddDays(-6), StationDb.LocalWeekOffset).ToString("O");
                        var weekUntil = new DateTimeOffset(localNow.Date.AddDays(1), StationDb.LocalWeekOffset).ToString("O");
                        var play = db.SitePlaySummary(5, weekSince, weekUntil, VisibilityScope.PublicOnly);
                        // 双态投影（11-08 盘点裁决）：队列健康是站长情报，普通成员这份直接 null，不拿底牌递人。
                        object? queue = member.Role == MemberRoles.Owner
                            ? new { downloading = stats.Downloading, pending = stats.Pending, failed = stats.Failed, liveAccounts = stats.LiveAccounts }
                            : null;
                        var json = JsonSerializer.Serialize(new
                        {
                            arrived = stats.Arrived,
                            total = stats.Total,
                            today_arrived = db.CountArrivedToday(sinceUtc, untilUtc),
                            zones = string.Join("、", db.ListZones().Take(5).Select(z => $"{z.Name}×{z.Count}")),
                            interaction = new { view = stats.ViewTotal, like = stats.LikeTotal, coin = stats.CoinTotal, fav = stats.FavTotal, share = stats.ShareTotal, danmaku = stats.ReplyTotal },
                            hot,
                            site_play = new
                            {
                                window = "近7日（UTC+8）",
                                plays = play.Plays,
                                member_heads = play.MemberHeads,
                                guest_heads = play.GuestHeads,
                                hottest = play.Hottest.Select(h => new { title = Shorten(h.Title, 30), plays = h.Plays }).ToArray(),
                            },
                            queue,
                        });
                        return ("全站总览已取", json);
                    }

                    case "weekly_report":
                    {
                        // 成员能看什么周报，她就能看什么周报（同一只 GetWeeklyReport，同一把看客尺）；
                        // heatWall/heatMatrix/poster 这类给渲染用的重行不递——LLM 不需要马赛克，递了白烧 token。
                        var back = ClampInt(root, "week", 0, 0, 4);
                        var wkScope = MemberGate.IsOwner(member) ? VisibilityScope.Everything : VisibilityScope.Of(member.Id);
                        var rep = db.GetWeeklyReport(back, DateTimeOffset.UtcNow, member.Id, StationDb.WeeklyNewsPagedTake, wkScope);
                        var wkJson = JsonSerializer.Serialize(new
                        {
                            week = rep.WeekLabel,
                            news = new { count = rep.NewsCount, titles = rep.News.Select(n => Shorten(n.Title ?? n.Bvid, 26)).ToArray() },
                            hottest = rep.Hottest.Select(h => new { title = Shorten(h.Title ?? h.Bvid, 26), viewers = h.Viewers }).ToArray(),
                            top_ups = rep.TopUps.Select(u => new { up = u.UpName, viewers = u.Viewers, plays = u.Plays, videos = u.Videos }).ToArray(),
                            top_zones = rep.TopZones.Select(z => new { zone = z.Zone, viewers = z.Viewers, plays = z.Plays, videos = z.Videos }).ToArray(),
                            day_bars = rep.DayBars.Select(d => new { date = d.LocalDate, plays = d.Plays, members = d.Heads, guests = d.GuestHeads }).ToArray(),
                            danmaku_added = rep.DanmakuAdded,
                            site = new { plays = rep.PlaybackPlays, unique_viewers = rep.UniqueViewers, searches = rep.Searches, submissions = rep.Submissions, new_members = rep.NewMembers, comments_archived = rep.CommentsArchived },
                            guest = new { heads = rep.Guest.Heads, plays = rep.Guest.Plays, searches = rep.Guest.Searches },
                            personal = rep.Personal is null ? null : new
                            {
                                opens = rep.Personal.Opens,
                                plays = rep.Personal.Plays,
                                videos_watched = rep.Personal.VideosWatched,
                                searches = rep.Personal.Searches,
                                submissions = rep.Personal.Submissions,
                                danmaku = rep.Personal.DanmakuSent,
                                comments = rep.Personal.CommentsPosted,
                                home = rep.Personal.Home,
                            },
                            your_rank = rep.PersonalVsAll is null ? null : new { share_permille = rep.PersonalVsAll.SharePermille, rank = rep.PersonalVsAll.Rank, head_count = rep.PersonalVsAll.HeadCount },
                            your_watch_time = rep.PersonalDuration is null ? null : new { seconds = rep.PersonalDuration.Seconds, videos = rep.PersonalDuration.Videos, finished = rep.PersonalDuration.Finished },
                        });
                        return ($"周报 {rep.WeekLabel} 已取", wkJson);
                    }

                    case "zone_videos":
                    {
                        var zone = root.TryGetProperty("zone", out var z) && z.ValueKind == JsonValueKind.String ? z.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(zone))
                        {
                            return ("出错：没给分区名", """{"error":"missing zone"}""");
                        }

                        var zTake = ClampInt(root, "limit", 10, 1, 20);
                        var zScope = MemberGate.IsOwner(member) ? VisibilityScope.Everything : VisibilityScope.Of(member.Id);
                        var shelves = db.ListZoneVideos(zone.Trim(), zScope).Take(zTake)
                            .Select(v => new { title = Shorten(v.Title ?? v.Bvid, 26), bvid = v.Bvid, up = v.UpName, publish = v.PublishText })
                            .ToArray();
                        if (shelves.Length == 0)
                        {
                            return ($"分区「{zone.Trim()}」没找到货", JsonSerializer.Serialize(new { zone = zone.Trim(), note = "没这个分区或它是空的：先喊 site_stats 看分区清单" }));
                        }

                        return ($"分区 {zone.Trim()} 摆出 {shelves.Length} 件", JsonSerializer.Serialize(new { zone = zone.Trim(), videos = shelves }));
                    }

                    case "watch_history":
                    {
                        var limit = ClampInt(root, "limit", 10, 1, 20);
                        // 与 /api/member/history 同口径：历史是本人足迹，本人全见（自己藏着的照旧露面）；
                        // 别人后来转私的行被尺子挡下——行不删，只是看不见。position/duration 两栏不进回包：
                        // 站长 11-17 点单「只要足迹不要时长」，续播点位是放映厅的私事，不递进模型上下文。
                        var hScope = MemberGate.IsOwner(member) ? VisibilityScope.Everything : VisibilityScope.Of(member.Id);
                        var foot = db.GetVideoHistory(member.Id, limit, hScope)
                            .Select(r => new
                            {
                                title = Shorten(r.Title ?? r.Bvid, 40),
                                bvid = r.Bvid,
                                page = r.PageIdx,
                                watched_at = LocalStamp(r.UpdatedAtUtc),
                            })
                            .ToArray();
                        return ($"观影足迹 {foot.Length} 部", JsonSerializer.Serialize(new { videos = foot }));
                    }

                    case "member_activity":
                    {
                        var act = db.MemberAgentActivity(member.Id);
                        var json = JsonSerializer.Serialize(new
                        {
                            posts = act.Posts,
                            posts_arrived = act.Arrived,
                            danmaku = act.Danmaku,
                            last_post = act.LastPostTitle is null ? null : new { title = Shorten(act.LastPostTitle, 40), arrived = LocalStamp(act.LastPostAtUtc) },
                        });
                        return ($"投稿 {act.Posts} 件 · 弹幕 {act.Danmaku} 条", json);
                    }

                    default:
                        return ("出错：不认得这把工具", """{"error":"unknown tool"}""");
                }
            }
            catch (Exception ex)
            {
                // 库里抛什么都不许掀 SSE 的桌：给模型递个 error 体，它自会向成员解释。
                return ($"出错：{TruncateMsg(ex.Message)}", """{"error":"tool failed"}""");
            }
        }
    }

    /// <summary>模型递的整数参数只当线索不当闸：缺省给 default，越界钳进 [min,max]，坏值丢回默认。</summary>
    private static int ClampInt(JsonElement root, string prop, int fallback, int min, int max)
    {
        if (root.TryGetProperty(prop, out var el) && el.TryGetInt32(out var v))
        {
            return Math.Clamp(v, min, max);
        }

        return Math.Clamp(fallback, min, max);
    }

    /// <summary>"O" 串折成给人看的「MM-dd HH:mm」（UTC+8）；认不出来就截前 16 字原样递，不为美饰抛。</summary>
    private static string? LocalStamp(string? isoUtc)
    {
        if (string.IsNullOrEmpty(isoUtc))
        {
            return null;
        }

        return DateTimeOffset.TryParse(isoUtc, out var dt)
            ? dt.ToOffset(StationDb.LocalWeekOffset).ToString("MM-dd HH:mm")
            : isoUtc[..Math.Min(16, isoUtc.Length)];
    }

    private static string TruncateMsg(string? raw) => string.IsNullOrEmpty(raw) ? "工具闹脾气" : raw.Length <= 80 ? raw : raw[..80] + "…";

    /// <summary>一帧 SSE：data: 前缀 + 双换行收尾。</summary>
    private static ReadOnlyMemory<byte> Frame(string json) => Encoding.UTF8.GetBytes($"data:{json}\n\n");

    /// <summary>错误帧尽力而为：客户端多半已经断线，写不进去不为最后一帧再炸一次。</summary>
    private static async Task TryWriteFrame(HttpResponse response, string json)
    {
        try
        {
            await response.Body.WriteAsync(Frame(json), CancellationToken.None);
            await response.Body.FlushAsync(CancellationToken.None);
        }
        catch
        {
            // 管道都断了，这帧没人听，罢了。
        }
    }

    /// <summary>
    /// 地址闸（原住 AgentSettings，站级设定退役后搬驻此处）：绝对 http(s) URL、必须有 host、
    /// 不许夹用户信息/查询/锚点，尾斜杠剃净。可以带路径（/v1 是 OpenAI 兼容惯例），
    /// /chat/completions 尾段由 <see cref="LlmClient"/> 负责拼，不重复贴。
    /// </summary>
    internal static bool TryNormalizeBaseUrl(string raw, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        var trimmed = raw.Trim().TrimEnd('/');
        if (trimmed.Length > 300)
        {
            error = "AI 地址太长了";
            return false;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri is null)
        {
            error = "AI 地址只收 http(s):// 绝对地址，认不出来";
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "AI 地址只收 http(s):// 地址";
            return false;
        }
        if (uri.UserInfo.Length > 0)
        {
            error = "AI 地址里不许夹用户信息";
            return false;
        }
        if (uri.Host.Length == 0)
        {
            error = "AI 地址域名没填，光有协议头不行";
            return false;
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "AI 地址不许带查询或锚点";
            return false;
        }

        normalized = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
        return true;
    }

    /// <summary>推理选型归一（个人空间配置页的输入）：竖线分隔 → trim、去空、保序去重（大小写不敏感）；
    /// 每档限字母数字 - _ .（防着把带空格带引号的洋文塞进上游请求体），单档 ≤32 字、整串 ≤8 档；
    /// 全空白 = 空串清栏（与 baseUrl/model 的「给空白拆那一栏」同一族语义）。</summary>
    private static bool TryNormalizeReasoningOptions(string raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        var parts = raw.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            normalized = "";
            return true; // 给空白 = 拆这一栏
        }

        if (parts.Length > 8)
        {
            error = "推理档位太多了，封顶 8 档（例如 none|low|medium|xhigh）";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length > 32)
            {
                error = "推理档位单档不能超 32 字";
                return false;
            }

            if (!part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                error = "推理档位只收字母、数字、- _ .（例如 none|low|medium|xhigh）";
                return false;
            }

            if (seen.Add(part))
            {
                kept.Add(part);
            }
        }

        normalized = string.Join('|', kept);
        return true;
    }

    public sealed record AgentChatMessage(string? Role, string? Content);

    public sealed record AgentChatRequest(IReadOnlyList<AgentChatMessage>? Messages, string? Reasoning);

    public sealed record MemberAiSaveRequest(string? BaseUrl, string? ApiKey, string? Model, string? ReasoningOptions, int? ContextK, bool? ToolsEnabled);

    /// <summary>荐片视频卡批量取数体：只收 BV 号（端点自己限去重掩 12 枚）。</summary>
    public sealed record VideoCardBatchRequest(string[]? Bvids);

    /// <summary>性格卡保存体：只一张卡。空串/空白 = 恢复内置默认。</summary>
    public sealed record AgentPersonaRequest(string? Persona);

    public sealed record MemberAiTestRequest(string? BaseUrl, string? ApiKey, string? Model);
}
