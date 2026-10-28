using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 观看历史（续播点位）：会员看片时把「看到哪了」随手报上来，下次进放映厅接着放。
/// 四条口径：① durationSec 说不清（≤0 或非有限数）→ 400 中文；
/// ② 刚开个头（&lt;5 秒）且库里本来没行 → 忽略回 ok，不占历史位；
/// ③ 位置过 95% 视为看完 → 删行，历史只装「还没看完」的那批；
/// ④ 其余 upsert，一人一片一行、新位盖旧位。
/// 校验与落库全收进 ValidateProgress（仿 DanmakuReportEndpoints：端点 lambda 与测试驱动同一张嘴）。
/// 所有拒绕显式 Results.Json(new { ... }, statusCode:)，不用 Results.Forbid()（分站没注册认证服务，它会 500）。
/// /api/videos/{id}/progress 由 MemberGate 的 /api/videos/ 前缀认下（见 MemberGate.RequiresMember）；
/// /api/member/history 那三条不在两张门的视野里，端点自己防御式查 MemberGate.Current，没席位一律 401 中文。
/// </summary>
public static class WatchHistoryEndpoints
{
    /// <summary>开个头地板：没到 5 秒又不算续播，就别往历史里塞行。</summary>
    internal const double StartFloorSec = 5;

    /// <summary>看完线：位置到时长这个比例就算看完，直接删行（历史只留没看完的）。</summary>
    internal const double FinishedRatio = 0.95;

    internal sealed record ProgressRequest(int? PageIdx, double? PositionSec, double? DurationSec);

    internal sealed record HistoryVideoRequest(long? VideoId);

    /// <summary>报一次位置的三种结局：落了行 / 看完删了行 / 没到地板什么都没干。</summary>
    internal enum ProgressOutcome
    {
        Saved,
        Removed,
        Ignored,
    }

    /// <summary>
    /// 进度上报的校验 + 落库（端点与测试同一张嘴）：错误给状态码与文案，null = 放行。
    /// 位置越界（比时长还大、或负数）就地夹进 [0, durationSec]，不拿脏数进台账。
    /// </summary>
    internal static ((int Status, string Message)? Error, ProgressOutcome Outcome) ValidateProgress(
        StationDb db, long memberId, long videoId, int? pageIdx, double? positionSec, double? durationSec)
    {
        var duration = durationSec ?? double.NaN;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return ((StatusCodes.Status400BadRequest, "时长都报不上来，这进度没法记"), default);
        }

        var position = positionSec ?? 0;
        if (!double.IsFinite(position))
        {
            return ((StatusCodes.Status400BadRequest, "看到哪了就报个正经秒数"), default);
        }

        position = Math.Clamp(position, 0, duration);
        var page = Math.Max(0, pageIdx ?? 0);

        // 看完的先判：过线就删行，不给它落回历史（哪怕这位又在末尾来回拖进度条）。
        if (position >= duration * FinishedRatio)
        {
            db.DeleteWatchHistory(memberId, videoId);
            return (null, ProgressOutcome.Removed);
        }

        // 刚开个头：库里没行就不占坑（有行则照常覆盖，别把上次看到的点顶没了还留着旧数）。
        if (position < StartFloorSec && db.GetWatchHistoryOne(memberId, videoId) is null)
        {
            return (null, ProgressOutcome.Ignored);
        }

        db.UpsertWatchHistory(memberId, videoId, page, position, duration);
        return (null, ProgressOutcome.Saved);
    }

    public static void MapWatchHistoryEndpoints(this WebApplication app)
    {
        // 报进度：路径以 /progress 收尾，MemberGate 的 /api/videos/ 那一块认它（GET/POST 都吃会员门）。
        app.MapPost("/api/videos/{id:long}/progress", (long id, ProgressRequest? request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "登录后才记你的看到哪了" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (error, outcome) = ValidateProgress(db, me.Id, id, request?.PageIdx, request?.PositionSec, request?.DurationSec);
            if (error is not null)
            {
                return Results.Json(new { message = error.Value.Message }, statusCode: error.Value.Status);
            }

            return Results.Ok(new
            {
                outcome = outcome.ToString().ToLowerInvariant(),
                message = outcome switch
                {
                    ProgressOutcome.Removed => "看到末尾了，这条历史就销了",
                    ProgressOutcome.Ignored => "刚开个头，就不占历史位了",
                    _ => "位置已记下，下次进来接着放",
                },
            });
        });

        // 取进度：没看过回 null（前端按「从头播」处置，不编一个 0 出来冒充）。
        app.MapGet("/api/videos/{id:long}/progress", (long id, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "登录后才查得到你的续播点位" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var progress = db.GetWatchHistoryOne(me.Id, id);
            return progress is null
                ? Results.Json((object?)null)
                : Results.Ok(new { pageIdx = progress.PageIdx, positionSec = progress.PositionSec, durationSec = progress.DurationSec });
        });

        // 历史清单：/api/member/ 前缀不在两张门的视野里，端点自己查席位。最近看过的排前头。
        app.MapGet("/api/member/history", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录，历史无从谈起" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var items = db.GetWatchHistory(me.Id).Select(r => new
            {
                videoId = r.VideoId,
                bvid = r.Bvid,
                title = r.Title,
                localCover = r.LocalCover,
                coverUrl = r.CoverUrl,
                pageIdx = r.PageIdx,
                positionSec = r.PositionSec,
                durationSec = r.DurationSec,
                updatedAtUtc = r.UpdatedAtUtc,
            });
            return Results.Ok(new { items });
        });

        app.MapPost("/api/member/history/delete", (HistoryVideoRequest? request, StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录，删的是谁的历史" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (request?.VideoId is not long videoId)
            {
                return Results.Json(new { message = "得说清删哪一部" }, statusCode: StatusCodes.Status400BadRequest);
            }

            return db.DeleteWatchHistory(me.Id, videoId)
                ? Results.Ok(new { message = "这部从历史里抹掉了" })
                : Results.Ok(new { message = "历史里本来就没有这部" });
        });

        app.MapPost("/api/member/history/clear", (StationDb db, HttpContext http) =>
        {
            if (MemberGate.Current(http) is not { } me)
            {
                return Results.Json(new { message = "还没登录，清谁的历史" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var cleared = db.ClearWatchHistory(me.Id);
            return Results.Ok(new
            {
                cleared,
                message = cleared > 0 ? $"历史已清空，带走 {cleared} 部" : "历史本来就是空的",
            });
        });
    }
}
