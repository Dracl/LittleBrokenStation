namespace LBS.Engine;

/// <summary>引擎宿主的少量固定选项，刻意不做多：UA 兜底一个，代理暂不开放（容器红线是不主动外连除 B 站 API 与版本检查）。UA 的站长可改面在站长室设置（StationEngineSettingsMapper）。</summary>
public sealed record EngineOptions(string UserAgent)
{
    /// <summary>站长没填 UA 时的兜底值（也用作 B 站 API 的默认请求头）。</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";
}
