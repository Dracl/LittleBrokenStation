using System.Collections.Concurrent;
using LBS.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace LBS.Server;

/// <summary>导出任务牌：jobId → 状态。内存板重启即丢不要紧，包文件仍在档案柜。</summary>
public sealed class ExportJobBoard
{
    public sealed class Job
    {
        public string State { get; set; } = "running"; // running | done | error
        public string? Name { get; set; }
        public long? Bytes { get; set; }
        public string? Error { get; set; }
    }

    private readonly ConcurrentDictionary<string, Job> jobs = new();

    public string Create()
    {
        var id = Guid.NewGuid().ToString("N");
        jobs[id] = new Job();
        return id;
    }

    public Job? Get(string id) => jobs.TryGetValue(id, out var job) ? job : null;
}

/// <summary>
/// 数据交换端点：全挂 /api/admin/** 之下，站长会话门在前置 middleware 把关。
/// 大文件（媒体包）导出走后台 job 流式打包；导入上传绑 IFormFile 需 DisableAntiforgery（.NET 表单门）。
/// </summary>
public static class DataExchangeEndpoints
{
    /// <summary>导出勾选：withPreviews 不送则当 true（预览是派生物，旧前端没这一勾时默认随包走，与 ExportOptions 口径一致）。</summary>
    public sealed record ExportRequest(bool WithCreds, bool WithMedia, bool WithCmtImg, bool WithCaches, bool? WithPreviews);

    public sealed record ImportApplyRequest(string? Receipt);

    public static void MapDataExchangeApi(this WebApplication app)
    {
        // ─── 导出：勾选打包（必含台账 + manifest），大包走 job ───
        app.MapPost("/api/admin/export", (ExportRequest request, StationExporter exporter, ExportJobBoard board, ILogger<StationExporter> logger) =>
        {
            var options = new ExportOptions(request.WithCreds, request.WithMedia, request.WithCmtImg, request.WithCaches, request.WithPreviews ?? true);
            var jobId = board.Create();
            _ = Task.Run(() =>
            {
                var job = board.Get(jobId)!;
                try
                {
                    var result = exporter.Run(options);
                    job.Name = result.Name;
                    job.Bytes = result.Bytes;
                    job.State = "done";
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "数据交换打包失败 job={JobId}", jobId);
                    job.State = "error";
                    job.Error = ex.Message;
                }
            });
            return Results.Ok(new { jobId });
        });

        app.MapGet("/api/admin/export/jobs/{id}", (string id, ExportJobBoard board) =>
        {
            var job = board.Get(id);
            return job is null
                ? Results.NotFound(new { state = "error", error = "查无此单（分站可能重启过，包若已成形可在档案柜里找到）" })
                : Results.Ok(new { state = job.State, name = job.Name, bytes = job.Bytes, error = job.Error });
        });

        // ─── 档案柜：清单 / 下载 / 销包 ───
        app.MapGet("/api/admin/exports", (StationExporter exporter) => Results.Ok(exporter.List()));

        app.MapGet("/api/admin/exports/{name}/download", (string name, StationExporter exporter, DataPaths paths) =>
        {
            if (!DataExchangeLayout.IsSafeExportName(name))
            {
                return Results.BadRequest(new { message = "包名不认" });
            }

            var file = Path.Combine(DataExchangeLayout.ExportsDir(paths), name);
            if (!File.Exists(file))
            {
                return Results.NotFound(new { message = "包已不在档案柜" });
            }

            return Results.File(file, "application/zip", name, enableRangeProcessing: true);
        });

        app.MapDelete("/api/admin/exports/{name}", (string name, StationExporter exporter) =>
        {
            return exporter.Delete(name)
                ? Results.Ok(new { message = "包已销" })
                : Results.NotFound(new { message = "包名不认或已不在" });
        });

        // 勾选框旁的体积牌：各块磁盘占用
        app.MapGet("/api/admin/export/stats", (StationExporter exporter) => Results.Ok(exporter.GetStats()));

        // ─── 导入：上传暂存（解包+安检+验库）→ 回执 ───
        app.MapPost("/api/admin/import/upload", HandleImportUpload).DisableAntiforgery();

        // ─── 应用：自救快照 + 现站保底 + 落 pending 标记，随后自我停机 ───
        app.MapPost("/api/admin/import/apply", (ImportApplyRequest request, DataPaths paths, LedgerBackup backup, IHostApplicationLifetime lifetime) =>
        {
            var (ok, error) = ImportApplier.Apply(request.Receipt ?? "", paths, backup);
            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            // 响应先发出去，再关站：延迟一点让 JSON 落上网络线
            _ = Task.Run(async () =>
            {
                await Task.Delay(750);
                lifetime.StopApplication();
            });
            return Results.Ok(new { restarting = true });
        });

        // 卡上的「上次导入结果」状态条
        app.MapGet("/api/admin/import/pending", (DataPaths paths) =>
        {
            var pending = File.Exists(DataExchangeLayout.PendingFile(paths));
            string? lastError = null;
            var errFile = DataExchangeLayout.LastErrorFile(paths);
            if (File.Exists(errFile))
            {
                try
                {
                    var text = File.ReadAllText(errFile);
                    lastError = text.Length > 2000 ? text[..2000] : text;
                }
                catch (IOException)
                {
                    // 读不动就不展示
                }
            }

            return Results.Ok(new { pending, lastError });
        });
    }

    /// <summary>上传入口单独成法：体积/表单限额特性只能挂方法声明（媒体包不设顶）。</summary>
    [RequestSizeLimit(long.MaxValue)]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    private static async Task<IResult> HandleImportUpload(IFormFile? file, DataPaths paths)
    {
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "没收到文件" });
        }

        if (!Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "只收 .zip 导出包" });
        }

        Directory.CreateDirectory(DataExchangeLayout.ImportsDir(paths));
        var temp = Path.Combine(DataExchangeLayout.ImportsDir(paths), "_upload-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var stream = File.Create(temp))
            {
                await file.CopyToAsync(stream);
            }

            var outcome = ImportStager.Stage(temp, paths, () => NewReceipt(paths));
            if (!outcome.Ok)
            {
                return Results.BadRequest(new { error = outcome.Error });
            }

            return Results.Ok(new { receipt = outcome.Receipt, preview = outcome.Preview, warnings = outcome.Warnings });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.BadRequest(new { error = $"收包没成：{ex.Message}" });
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // 上传残件下一单再清
            }
        }
    }

    /// <summary>暂存回执号：同秒连传加序号后缀，与快照命名一个套路。</summary>
    private static string NewReceipt(DataPaths paths)
    {
        var imports = DataExchangeLayout.ImportsDir(paths);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var name = stamp;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(imports, name)))
        {
            name = $"{stamp}-{suffix}";
            suffix++;
        }

        return name;
    }
}
