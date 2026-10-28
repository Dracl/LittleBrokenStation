using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 悬停预览的站长室端点：总览 + 设定 + 催一轮 + 重试 + 清空。
/// 门径照 /api/admin/purge：整段在站长门里（AdminGate 前缀 /api/preview/ 与点名 /api/stats/preview），
/// 普通成员与访客一律 401/403——它会点着 ffmpeg 干活、也能一键抹掉整片预览区，不能让站外的手来按。
/// </summary>
public static class PreviewEndpoints
{
    /// <summary>设定入参：没给的字段一律不动（滑块只拖了一格，不必把其余五项也报一遍）。</summary>
    public sealed record PreviewSettingsRequest(bool? Enabled, int? Res, int? Seconds, int? Batch, int? Concurrency, bool? YieldOnDownload);

    public sealed record PreviewRetryRequest(IReadOnlyList<string>? Bvids);

    /// <summary>催一轮的可选入参：带 bvids 就是点名补件（「补今天新货」），不带就是按常规欠账名单催一轮。</summary>
    public sealed record PreviewBackfillRequest(IReadOnlyList<string>? Bvids);

    public static void MapPreviewApi(this WebApplication app)
    {
        // 总览：台账账目 + 磁盘实扫体积 + 补员员自报的运行状态（inflight / 本轮进度 / 下轮倒计时只有它自己知道）。
        app.MapGet("/api/stats/preview", (StationDb db, PreviewSettings settings, PreviewBackfillWorker worker) =>
        {
            var (enabled, res, seconds, batch, concurrency, yieldOnDownload) = settings.Snapshot();
            var stats = db.PreviewStats(worker.InFlight);
            return Results.Ok(new
            {
                enabled,
                workerState = worker.State,
                roundDone = worker.RoundDone,
                roundBatch = worker.RoundBatch,
                covered = stats.Covered,
                pending = stats.Pending,
                inflight = stats.Inflight,
                failed = stats.Failed,
                nextRoundSeconds = worker.NextRoundSeconds,
                lastRoundSeconds = worker.LastRoundSeconds,
                previewBytes = stats.Bytes,
                avgBytes = stats.AvgBytes,
                // 单件实测均值（最近 20 条带耗时的成件）：没一条带 cost_seconds 的老行时回 null，前端退回推算轨。
                unit = db.PreviewUnit() is { } unit
                    ? new { seconds = unit.Seconds, costSeconds = unit.CostSeconds, bytes = unit.Bytes, count = unit.Count }
                    : null,
                res,
                seconds,
                batch,
                concurrency,
                yieldOnDownload,
                recent = db.ListRecentPreviews(20).Select(row => new
                {
                    bvid = row.Bvid,
                    title = row.Title,
                    ok = row.Ok,
                    bytes = row.Bytes,
                    seconds = row.Seconds,
                    cost = row.CostSeconds,
                    error = row.Error,
                    at = row.AtUtc,
                }),
                failures = db.ListPreviewFailures().Select(row => new { bvid = row.Bvid, error = row.Error }),
            });
        });

        // 设定：清晰度认白名单（不在名单里整单打回），秒数/每轮条数/并发数是范围量，越界夹回边界。
        app.MapPost("/api/preview/settings", (PreviewSettingsRequest request, PreviewSettings settings) =>
            settings.Update(request.Enabled, request.Res, request.Seconds, request.Batch, request.Concurrency, request.YieldOnDownload)
                ? Results.Ok(SettingsSnapshot(settings))
                : Results.BadRequest(new { message = $"清晰度只能选 {string.Join('/', Ffmpeg.HeightChoices)}" }));

        // 催一轮：起跑就走，请求线程不陪着转码（一轮几十条，HTTP 里等不起）。
        // 带了 bvids 就是把这批排进本轮（无视 attempts 上限、已有成片的也重转）；总闸关着回 triggered:false，别让前端误以为已开跑。
        app.MapPost("/api/preview/backfill", (PreviewSettings settings, PreviewBackfillWorker worker, PreviewBackfillRequest? request) =>
        {
            var bvids = request?.Bvids?.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            var triggered = worker.TryStartOnce(bvids);
            return Results.Ok(new
            {
                triggered,
                message = !settings.Enabled
                    ? "预览总闸没开，先打开再催"
                    : triggered && bvids.Length > 0
                        ? $"已把点名的 {bvids.Length} 条排进本轮"
                        : null,
            });
        });

        // 重试：连吃三次闭门羹的行被搁下了，这里清成可重试；带 bvids 就只点这些名。
        app.MapPost("/api/preview/retry", (PreviewRetryRequest? request, StationDb db) =>
        {
            var reset = db.ResetPreviewFailures(request?.Bvids?.ToArray());
            return Results.Ok(new { reset, message = reset > 0 ? $"已放回 {reset} 条，等下一轮补员" : "没有可重试的失败行" });
        });

        // 清空：预览是派生物，说清就清——行与文件一起走，videos 与视频库一根指头都不碰。
        app.MapPost("/api/preview/clear", (StationDb db, DataPaths paths) =>
        {
            var rows = db.DeleteAllPreviews();
            var (files, bytes, busy) = PurgePreviewDir(paths.Previews);
            return Results.Ok(new
            {
                message = $"预览已清空：销 {rows} 行、清掉 {files} 个文件（{(double)bytes / 1048576:F1} MB）"
                    + (busy > 0 ? $"；{busy} 个文件被占用没舍得删，回头再按一次" : ""),
                rows,
                files,
                bytes,
            });
        });
    }

    private static object SettingsSnapshot(PreviewSettings settings)
    {
        var (enabled, res, seconds, batch, concurrency, yieldOnDownload) = settings.Snapshot();
        return new { enabled, res, seconds, batch, concurrency, yieldOnDownload };
    }

    /// <summary>清空预览目录（只扫这一层：预览区不放子目录，也不必跟着递归逛）。占用中的文件记个数，不拦路。</summary>
    private static (int Files, long Bytes, int Busy) PurgePreviewDir(string root)
    {
        var (files, bytes, busy) = (0, 0L, 0);
        if (!Directory.Exists(root))
        {
            return (0, 0L, 0);
        }

        foreach (var file in Directory.EnumerateFiles(root))
        {
            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                files++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                busy++;
            }
        }

        return (files, bytes, busy);
    }
}
