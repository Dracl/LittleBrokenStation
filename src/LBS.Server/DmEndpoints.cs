using System.Text.Json;
using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 用户间私信（M5.3，M5.4 起全面挂站内席位 id）：成员对成员往来说，素净席位也能聊——
/// 私信是站内事，不再借 B 站 mid 认人。站长也是 member，走同一条路。
/// 校验全收进 ValidateSend 一个函数——端点 lambda 与测试驱动的是同一张嘴。
/// 管理员视野 GET /api/admin/dm 挂在 /api/admin/ 前缀下，门自动把关。
/// </summary>
public static class DmEndpoints
{
    public const int MaxDmLength = 2000;

    private sealed record DmSendRequest(long? ToMemberId, string? Body);

    private sealed record VideoShareRequest(long? ToMemberId, string? Note);

    /// <summary>安利语封顶（与动态分享同规）。500 字以内刚好。</summary>
    internal const int MaxShareNoteLength = 500;

    /// <summary>
    /// 放映厅安利的校验 + 定格（与动态分享同一张嘴，端点 lambda 与测试直驱的都是这里）：
    /// 错误给状态码与文案，null = 放行；DynJson 是随信落库的视频族卡面（与动态分享 dyn_json 同族），
    /// 带 videoUrl = /video/bvid，对方点卡直落站内放映厅。
    /// </summary>
    /// <param name="viewerCanView">这位能不能看这部（调用方拿 <see cref="ViewerGate.CanView"/> 算好递进来，
    /// 必传不给默认值）：藏着的稿子安利不出去，否则任何人都能借安利把别人的私稿端回全站眼前。
    /// 安全判据的默认值不许落在放行那一侧（fail-open）：哪天有人加个调用点忘了传，就静默把私有稿的安利卡放出去了。</param>
    internal static ((int Status, string Message)? Error, long ToMemberId, string Note, string? DynJson) ValidateVideoShare(
        StationDb db, long fromMemberId, long? toMemberId, string bvid, string? rawNote, bool viewerCanView)
    {
        if (toMemberId is not long target)
        {
            return ((StatusCodes.Status400BadRequest, "得先有个收信人"), 0, string.Empty, null);
        }

        if (target == fromMemberId)
        {
            return ((StatusCodes.Status400BadRequest, "给自己安利片子，那叫自娱自乐"), 0, string.Empty, null);
        }

        if (db.GetMemberById(fromMemberId) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status403Forbidden, "你的席位不在站上，分享闸落着"), 0, string.Empty, null);
        }

        if (db.GetMemberById(target) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status404NotFound, "对方不在站"), 0, string.Empty, null);
        }

        var note = rawNote?.Trim() ?? string.Empty;
        if (note.Length > MaxShareNoteLength)
        {
            return ((StatusCodes.Status400BadRequest, "安利语太长了，500 字以内刚好"), 0, string.Empty, null);
        }

        if (db.GetVideoShareSnapshot(bvid) is not { } snap)
        {
            return ((StatusCodes.Status404NotFound, "分站馆藏里没有这个片子，安利不了空气"), 0, string.Empty, null);
        }

        // 藏着的稿子安利不出去。这道门排在「在不在馆」之后（查无那句话得原样留着），拒词照本函数的形制给一句人话，
        // 也不提「私有」二字：回执里说漏了「它藏着」，等于替人验了那稿存在。
        if (!viewerCanView)
        {
            return ((StatusCodes.Status404NotFound, "这部片子站里没摆着，安利不了"), 0, string.Empty, null);
        }

        var dynJson = JsonSerializer.Serialize(new
        {
            type = "video",
            bvid = snap.Bvid,
            title = snap.Title,
            cover = snap.Cover,
            upName = snap.UpName,
            // UP 真脸随快照一并定格（11-17 站长令：私信卡与看板娘荐片卡同步带脸，不再顶着灰人）。
            upFace = snap.UpFace,
            // 时长与动态分享同口径：定格时就收成 B 站冒号制（3:45），库里那三种方言（4m45s / 03:45 / 243）不进 JSON。
            durationText = snap.DurationText is { } dur ? DurationText.Normalize(dur) : null,
            videoUrl = $"/video/{snap.Bvid}",
        });
        return (null, target, note, dynJson);
    }

    /// <summary>发送前校验：错误给状态码与文案，null = 放行（body 已 trim）。全程席位 id。</summary>
    internal static ((int Status, string Message)? Error, long ToMemberId, string Body) ValidateSend(
        StationDb db, long fromMemberId, long? toMemberId, string? rawBody)
    {
        var body = rawBody?.Trim() ?? string.Empty;
        if (toMemberId is not long target)
        {
            return ((StatusCodes.Status400BadRequest, "得先有个收信人"), 0, body);
        }

        if (body.Length == 0)
        {
            return ((StatusCodes.Status400BadRequest, "私信总不能写空气"), target, body);
        }

        if (body.Length > MaxDmLength)
        {
            return ((StatusCodes.Status400BadRequest, "私信太长了，分站不是论文站"), target, body);
        }

        if (target == fromMemberId)
        {
            return ((StatusCodes.Status400BadRequest, "不能给自己写信，信箱就在顶栏"), target, body);
        }

        if (db.GetMemberById(fromMemberId) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status403Forbidden, "你的席位不在站上，私信闸落着"), target, body);
        }

        if (db.GetMemberById(target) is not { Status: MemberStatus.Active })
        {
            return ((StatusCodes.Status404NotFound, "对方不在站"), target, body);
        }

        return (null, target, body);
    }

    public static void MapDmEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dm/threads", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var threads = db.ListDmThreads(me.Id).Select(t => new
            {
                peerMemberId = t.PeerMemberId,
                peerName = t.PeerName,
                peerFace = t.PeerFace,
                lastBody = t.LastBody,
                lastAtUtc = t.LastAtUtc,
                unread = t.Unread,
                lastMsgType = t.LastMsgType,
            });
            return Results.Ok(new { threads });
        });

        app.MapGet("/api/dm/unread", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(new { unread = db.CountDmUnread(me.Id) });
        });

        // 可私信选人：默认只递「聊过天的人」小账本，全站花名册不再整本露面；
        // 搜新人要带 q（门槛见 DirectorySearchAllowed，后端再拦一道），自己永远不列。
        app.MapGet("/api/dm/directory", (StationDb db, HttpContext http, string? q) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            static object Person(StationMember m) => new
            {
                memberId = m.Id,
                mid = m.Mid,
                username = m.Username,
                nickname = m.Nickname,
                displayName = StationDb.DisplayOf(m),
                faceUrl = m.FaceUrl,
            };

            var people = StationDb.MatchMembersForDirectory(db.ListMembers(), q)
                .Where(m => m.Id != me.Id)
                .Select(Person);
            // 搜索态只看结果；默认态才递常聊账本（会话列表同源，最近发言新→旧，取前 12 人）。
            var searching = StationDb.DirectorySearchAllowed(q);
            var recent = searching
                ? new List<object>()
                : db.ListDmThreads(me.Id)
                    .Take(12)
                    .Select(t => (object)new
                    {
                        memberId = t.PeerMemberId,
                        mid = (long?)null,
                        username = (string?)null,
                        nickname = t.PeerName,
                        displayName = t.PeerName ?? "站友",
                        faceUrl = t.PeerFace,
                    })
                    .ToList();
            return Results.Ok(new { people, recent });
        });

        // 拉会话即标读：进会话那一刻，对方发来的就算读过了。
        app.MapGet("/api/dm/thread/{peerMemberId:long}", (long peerMemberId, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (db.GetMemberById(peerMemberId) is not { Status: MemberStatus.Active } peer)
            {
                return Results.Json(new { message = "对方不在站" }, statusCode: StatusCodes.Status404NotFound);
            }

            db.MarkDmRead(me.Id, peerMemberId);
            // 按查看者自己的清空章过滤：盖章前的老话对这侧隐身（对方视角不受影响）
            var rows = db.ListDmConversation(me.Id, peerMemberId, viewerMemberId: me.Id).ToList();
            // 老卡补脸（11-17 站长令）：当年定格的视频族快照没存 UP 脸，读口按 bvid 现取真脸盖上——
            // 脸是化妆非内容真相，不破定格语义；新卡写侧已自带，有脸的旧卡也不重复化妆。
            var need = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in rows)
            {
                if (m.MsgType == "video" && FacelessCardBvid(m.DynJson) is { } b)
                {
                    need.Add(b);
                }
            }
            var faces = need.Count > 0 ? db.GetUpFacesByBvids(need) : new Dictionary<string, string>();
            var messages = rows.Select(m => new
            {
                id = m.Id,
                fromMemberId = m.FromMemberId,
                toMemberId = m.ToMemberId,
                body = m.Body,
                createdAtUtc = m.CreatedAtUtc,
                readAtUtc = m.ReadAtUtc,
                msgType = m.MsgType,
                dyn = WithBackfilledFace(ParseShareCard(m.DynJson), m.DynJson, faces),
                // 分享卡是发送那一刻定格的：片子进了回收站，卡上标题封面照样完好，得给个死活。
                videoGone = m.MsgType == "video" && m.VideoInStation != true,
            });
            return Results.Ok(new
            {
                messages,
                peer = new
                {
                    memberId = peer.Id,
                    mid = peer.Mid,
                    username = peer.Username,
                    nickname = peer.Nickname,
                    displayName = StationDb.DisplayOf(peer),
                    faceUrl = peer.FaceUrl,
                },
            });
        });

        // 清空聊天记录（双键墓碑）：单边盖章只隐自己这侧；双方都清过，往来才真删。
        app.MapDelete("/api/dm/thread/{peerMemberId:long}", (long peerMemberId, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (peerMemberId == me.Id)
            {
                return Results.Json(new { message = "跟自己没什么可销的" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (db.GetMemberById(peerMemberId) is not { Status: MemberStatus.Active })
            {
                return Results.Json(new { message = "对方不在站" }, statusCode: StatusCodes.Status404NotFound);
            }

            var burned = db.ClearDmThread(me.Id, peerMemberId);
            return Results.Ok(new { message = burned ? "两边都清了，往来真删干净" : "记录销了，再聊再记", burned });
        });

        // 撤回：发件人自己销自己那句，真删——双方账本同时没，审计视角也不留。
        // 发别人的信要不得（WHERE 把发件人锁死在登录者身上，匹不到行就是 404）。
        app.MapDelete("/api/dm/message/{id:long}", (long id, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return db.DeleteDmMessageAsSender(me.Id, id)
                ? Results.Ok(new { message = "撤回成功，烟消云散" })
                : Results.Json(new { message = "这句没找到，或不是你说的" }, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapPost("/api/dm", (DmSendRequest request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (error, target, body) = ValidateSend(db, me.Id, request.ToMemberId, request.Body);
            if (error is not null)
            {
                return Results.Json(new { message = error.Value.Message }, statusCode: error.Value.Status);
            }

            var id = db.SendDm(me.Id, target, body);
            return Results.Ok(new { id });
        });

        // 放映厅安利：动态分享的视频形态——bvid 必查在馆，定格卡面随信落库（dyn_json 视频族），
        // 对方会话里点卡直落 /video/{bvid}。校验全收进 ValidateVideoShare（与动态分享同规）。
        app.MapPost("/api/videos/{bvid}/share", (string bvid, VideoShareRequest? request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "登录后才能给朋友安利" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // 安利也得过看客钥匙（bvid 为键，先反查 id）：私有稿只有东家本人与站长塞得进别人信箱。
            var canView = db.GetVideoIdByBvid(bvid.Trim()) is { } videoId && ViewerGate.CanView(http, db, videoId);
            var (error, target, note, dynJson) = ValidateVideoShare(db, me.Id, request?.ToMemberId, bvid, request?.Note, canView);
            if (error is not null)
            {
                return Results.Json(new { message = error.Value.Message }, statusCode: error.Value.Status);
            }

            var id = db.SendDm(me.Id, target, note, "video", bvid, dynJson!);
            return Results.Ok(new { id, message = "已把这个片子塞进对方信箱" });
        });
    }

    /// <summary>分享卡的定格快照还原：坏 JSON 当普通信处理（body 还在，不拦阅读）。</summary>
    private static JsonElement? ParseShareCard(string? dynJson)
    {
        if (string.IsNullOrWhiteSpace(dynJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(dynJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>这张定格快照若是「缺脸」的视频族卡（11-17 站长令前的老卡），回它的 bvid；
    /// 有脸/不是视频族/坏 JSON 都回 null——不用化妆。</summary>
    private static string? FacelessCardBvid(string? dynJson)
    {
        if (ParseShareCard(dynJson) is not { } el) return null;
        if (!el.TryGetProperty("type", out var t) || t.GetString() != "video") return null;
        if (el.TryGetProperty("upFace", out var f) && f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString())) return null;
        return el.TryGetProperty("bvid", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
    }

    /// <summary>读口现取的脸盖上：只给缺脸视频卡化，库里没真脸就不动；有脸卡原样递。
    /// JsonElement 不可变：拿原字典补一枚 upFace 再序列化，不手撮括号（11-16 那根 ']' 的教训还热乎）。</summary>
    private static JsonElement? WithBackfilledFace(JsonElement? dyn, string? dynJson, IReadOnlyDictionary<string, string> faces)
    {
        if (dyn is null) return null;
        var bvid = FacelessCardBvid(dynJson);
        if (bvid is null || !faces.TryGetValue(bvid, out var face)) return dyn;
        try
        {
            var bag = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dynJson!) ?? new Dictionary<string, JsonElement>();
            bag["upFace"] = JsonSerializer.SerializeToElement(face);
            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bag));
        }
        catch (JsonException)
        {
            return dyn; // 化妆失败不拦阅读：原卡照旧灰人
        }
    }

    public static void MapAdminDmEndpoints(this WebApplication app)
    {
        // 管理员视野：全部私信，或锁一对席位往来（peerA+peerB 同给才锁，两边都是席位 id）。
        app.MapGet("/api/admin/dm", (long? peerA, long? peerB, StationDb db) =>
        {
            var rows = db.ListDmForAdmin(peerA, peerB).Select(r => new
            {
                id = r.Id,
                fromMemberId = r.FromMemberId,
                fromName = r.FromName,
                toMemberId = r.ToMemberId,
                toName = r.ToName,
                body = r.Body,
                createdAtUtc = r.CreatedAtUtc,
                readAtUtc = r.ReadAtUtc,
            });
            return Results.Ok(new { messages = rows });
        });
    }
}
