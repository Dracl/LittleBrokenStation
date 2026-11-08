using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 站内弹幕举报（最小闭环）：分站朋友对自己看着不对的站内弹幕点一下举报，站长在站长室决定删还是忽略。
/// 只此一口：B 站档案弹幕（ass 通道）不提供举报入口——那是 B 站的东西，分站不代管。
/// 举报不预隐藏：danmaku_local 没有状态列也不加，pending 的弹幕照常飘，只有站长处置才消失。
/// 校验与落库全收进 ValidateReport / HandleReport（仿 DmEndpoints.ValidateSend：端点 lambda 与测试驱动同一张嘴）。
/// 所有拒绕显式 Results.Json(new { ... }, statusCode:)——分站没注册认证服务，Results.Forbid() 会 500（Program.cs 的删弹幕处有先例）。
/// </summary>
public static class DanmakuReportEndpoints
{
    /// <summary>举报理由封顶：50 字足够把事说清。超了拒，不偷偷截——截剩的话不是举报人想说的话。</summary>
    internal const int MaxReasonLength = 50;

    private const string ActionDelete = "delete";
    private const string ActionDismiss = "dismiss";

    internal sealed record ReportRequest(string? Reason);

    internal sealed record HandleRequest(string? Action);

    /// <summary>落单的两种结局：新单 / 这个席位早就举报过（幂等，不算错误）。</summary>
    internal enum ReportOutcome
    {
        Created,
        Duplicate,
    }

    /// <summary>
    /// 举报的校验 + 定格落库（端点与测试同一张嘴）：错误给状态码与文案，null = 放行。
    /// 只认站内弹幕（danmaku_local）：弹幕不存在或不属这个视频 → 404；举报自己（同席位或同一张浏览器凭证）→ 400；
    /// 理由超 50 字 → 400。落单时把弹幕正文、被举报人、举报人、bvid 全抄成快照，之后弹幕被删、席位改名都不回头改这张单。
    /// </summary>
    internal static ((int Status, string Message)? Error, ReportOutcome Outcome, long ReportId) ValidateReport(
        StationDb db, long videoId, long dmId, long reporterMemberId, string? rawReason, string? reporterDmid)
    {
        var dm = db.GetLocalDanmakuSnapshot(dmId);
        if (dm is null || dm.VideoId != videoId)
        {
            return ((StatusCodes.Status404NotFound, "馆藏里没有这条站内弹幕，举报不了空气"), default, 0);
        }

        // 举报自己 = 无事生非：席位 id 相同算自己；当年没挂席位的匿名行，拿浏览器凭证认（两边都非空才比，免得空串互认成一人）。
        if (dm.SenderMemberId == reporterMemberId
            || (!string.IsNullOrWhiteSpace(dm.SenderDmid)
                && !string.IsNullOrWhiteSpace(reporterDmid)
                && string.Equals(dm.SenderDmid, reporterDmid, StringComparison.Ordinal)))
        {
            return ((StatusCodes.Status400BadRequest, "自己发的弹幕举报自己，图什么"), default, 0);
        }

        var reason = rawReason?.Trim() ?? string.Empty;
        if (reason.Length > MaxReasonLength)
        {
            return ((StatusCodes.Status400BadRequest, $"举报理由得写在 {MaxReasonLength} 字以内，火气也得节制"), default, 0);
        }

        var (created, reportId) = db.AddDanmakuReport(
            dm,
            db.GetVideoFileNameInfo(videoId)?.Bvid,
            SeatName(db, dm.SenderMemberId),
            reporterMemberId,
            SeatName(db, reporterMemberId),
            reason.Length == 0 ? null : reason);
        return (null, created ? ReportOutcome.Created : ReportOutcome.Duplicate, reportId);
    }

    /// <summary>
    /// 站长处置的校验 + 落库（端点与测试同一张嘴）：delete 真删弹幕并盖 deleted，dismiss 只盖 dismissed，两者都落 handled_at_utc。
    /// 抢单靠 status='pending' 条件：抢不到的人（重复处置）吃 409，单不存在 404，动作名不认 400。
    /// 弹幕已被发送者自删时 DeleteLocalDanmaku 返回 false——跳过不算错，这张单照样结。
    /// </summary>
    internal static (int Status, string Message) HandleReport(StationDb db, long reportId, string? rawAction)
    {
        var action = rawAction?.Trim().ToLowerInvariant();
        if (action is not (ActionDelete or ActionDismiss))
        {
            return (StatusCodes.Status400BadRequest, "处置只有两个动作：delete 删弹幕、dismiss 忽略");
        }

        var (exists, claimed, dmId) = db.HandleDanmakuReport(reportId, action == ActionDelete ? "deleted" : "dismissed");
        if (!exists)
        {
            return (StatusCodes.Status404NotFound, "没有这张举报单，可能被谁收走了");
        }

        if (!claimed)
        {
            return (StatusCodes.Status409Conflict, "这张举报单早就处置过了，不劳重复动手");
        }

        if (action == ActionDelete)
        {
            // 走回收站而不是一挥就没（11-08）：举报删错的能捐回，且这张单重开时能自动把弹幕带回来。
            // 返回 0 = 原行早就不在（发送者自删过），跳出不算错，这张单照样结。
            db.MoveDanmakuToRecycle(dmId, "站长（举报处置）", null, "report");
            return (StatusCodes.Status200OK, "弹幕已删（进了回收站），这张单也结了");
        }

        return (StatusCodes.Status200OK, "已忽略，这条弹幕照旧留着");
    }

    /// <summary>
    /// 重开一张举报单（11-08 站长拍板）：拍回 pending 让它能被重新处置一次，处置当初删掉的弹幕从回收站捐回来。
    /// 捐不动（片子已下架、条目已被销毁）不算重开失败：单照重开，话里说清弹幕为什么没能回来，
    /// 不拿一个假成功骗站长，也不拿一个假失败阻止他重新处置。
    /// </summary>
    internal static (int Status, string Message) ReopenReport(StationDb db, long reportId)
    {
        var (exists, reopened, previous, dmId, _) = db.ReopenDanmakuReport(reportId);
        if (!exists)
        {
            return (StatusCodes.Status404NotFound, "没有这张举报单，可能被谁收走了");
        }

        if (!reopened)
        {
            return (StatusCodes.Status409Conflict, "这张单还挂在待处理里，不用重开");
        }

        if (previous != "deleted")
        {
            return (StatusCodes.Status200OK, "已重开，这张单回到案头等你重新处置");
        }

        var (ok, message) = db.RestoreDanmakuByDmId(dmId);
        return (StatusCodes.Status200OK, ok
            ? "已重开，当初删掉的弹幕也捐回去了（它照旧飘在原处）"
            : $"已重开，但当初删掉的弹幕没能捐回：{message}");
    }

    /// <summary>席位的展示名（昵称优先，素净席位落 @username）；席位走了或本就没席位 → null（台账吃快照，不回头查）。</summary>
    private static string? SeatName(StationDb db, long? memberId) =>
        memberId is long id && db.GetMemberById(id) is { } member ? StationDb.DisplayOf(member) : null;

    public static void MapDanmakuReportEndpoints(this WebApplication app)
    {
        // 举报站内弹幕：路径含 /danmaku，MemberGate 自动吃会员门（无需另外注册）；
        // AdminGate 的 /api/videos/ POST 关键词清单里没有 report，这一发不会被误当成站长事务。
        app.MapPost("/api/videos/{id:long}/danmaku/{dmId:long}/report",
            (long id, long dmId, ReportRequest? request, StationDb db, HttpContext http) =>
            {
                if (MemberGate.Current(http) is not { } me)
                {
                    return Results.Json(new { message = "登录后才能举报弹幕" }, statusCode: StatusCodes.Status401Unauthorized);
                }

                // 只读现成的 lbs_dmid（没 cookie 就是没有，不替举报人现发一张）。
                var reporterDmid = http.Request.Cookies.TryGetValue("lbs_dmid", out var dmid) && dmid is { Length: >= 8 and <= 64 }
                    ? dmid
                    : null;
                var (error, outcome, reportId) = ValidateReport(db, id, dmId, me.Id, request?.Reason, reporterDmid);
                if (error is not null)
                {
                    return Results.Json(new { message = error.Value.Message }, statusCode: error.Value.Status);
                }

                return Results.Ok(new
                {
                    id = reportId,
                    message = outcome == ReportOutcome.Duplicate
                        ? "这条你早就举报过了，站长正在看"
                        : "举报已递到站长案头，站长处置前这条弹幕照常飘",
                });
            });
    }

    public static void MapAdminDanmakuReportEndpoints(this WebApplication app)
    {
        // 整段挂在 /api/admin/ 前缀下，站长门（身份即钥匙，只认站长席位会话）自动把关；普通成员与访客一律 401/403。
        app.MapGet("/api/admin/danmaku-reports", (string? status, StationDb db) =>
        {
            // 缺省与认生的值都当「待处理」看：站长室首屏要的就是这个，别让人空手而归。
            var pendingOnly = !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase);
            var items = db.ListDanmakuReports(pendingOnly).Select(r => new
            {
                id = r.Id,
                dmId = r.DmId,
                videoId = r.VideoId,
                bvid = r.Bvid,
                pageIdx = r.PageIdx,
                tSec = r.TSec,
                text = r.Text,
                senderMemberId = r.SenderMemberId,
                senderDmid = r.SenderDmid,
                senderName = r.SenderName,
                dmCreatedAtUtc = r.DmCreatedAtUtc,
                reporterMemberId = r.ReporterMemberId,
                reporterName = r.ReporterName,
                reason = r.Reason,
                status = r.Status,
                createdAtUtc = r.CreatedAtUtc,
                handledAtUtc = r.HandledAtUtc,
                inStation = r.VideoInStation,
            });
            return Results.Ok(new { pending = db.CountDanmakuReports(StationDb.DanmakuReportPending), items });
        });

        app.MapPost("/api/admin/danmaku-reports/{id:long}/handle", (long id, HandleRequest? request, StationDb db) =>
        {
            var (status, message) = HandleReport(db, id, request?.Action);
            return status == StatusCodes.Status200OK
                ? Results.Ok(new { message })
                : Results.Json(new { message }, statusCode: status);
        });

        // 重开这张单（11-08）：处置只此一次是铁规，但铁规不等于不能反悔——反悔得走明路。
        // 判据与落库全在 ReopenReport（与 handle 同一张嘴，测试可直驱）。
        app.MapPost("/api/admin/danmaku-reports/{id:long}/reopen", (long id, StationDb db) =>
        {
            var (status, message) = ReopenReport(db, id);
            return status == StatusCodes.Status200OK
                ? Results.Ok(new { message })
                : Results.Json(new { message }, statusCode: status);
        });
    }
}
