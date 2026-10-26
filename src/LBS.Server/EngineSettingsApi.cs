using DownKyi.Application.Settings;
using LBS.Engine;

namespace LBS.Server;

/// <summary>
/// 站长室「下载设置」API：读写引擎真实的 ApplicationSettings（经 ISettingsStore，引擎即刻消费）。
/// 保存目录/代理/cookie 刻意不在其中：分站红线，参数再全也不开这三个口子。
/// </summary>
public static class EngineSettingsApi
{
    public static void MapEngineSettingsApi(this WebApplication app)
    {
        app.MapGet("/api/settings/engine", (ISettingsStore store) =>
            Results.Ok(StationEngineSettingsMapper.FromSettings(store.Current)));

        app.MapPost("/api/settings/engine", async (StationEngineSettingsDto? draft, ISettingsStore store, CancellationToken cancellationToken) =>
        {
            if (draft is null)
            {
                return Results.BadRequest(new { message = "没有收到设置草稿" });
            }

            if (!StationEngineSettingsMapper.TryApplyDraft(store.Current, draft, out var updated, out var error, out var warnings))
            {
                return Results.BadRequest(new { message = error });
            }

            var applied = store.Update(_ => updated);
            await store.FlushAsync(cancellationToken);
            return Results.Ok(new
            {
                settings = StationEngineSettingsMapper.FromSettings(applied),
                warnings,
            });
        });
    }
}
