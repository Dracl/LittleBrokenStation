using DownKyi.Services.Download.Headless;
using LBS.Infrastructure;

namespace LBS.Engine;

/// <summary>
/// 分站统一的提交策略：一条链接进分站该按什么规格下载，只在这里定义一次。
/// 内容勾选/画质/命名全部取站长室设置现值（映射与校验在 StationEngineSettingsMapper）；
/// 产物落 {数据目录}/videos，判重一律跳过（分站的本分是去重归档，重下留给站长室显式操作）。
/// </summary>
public static class LbsSubmissionProfile
{
    public static HeadlessSubmissionOptions ForStation(DataPaths paths, DownKyi.Application.Settings.ApplicationSettings settings) =>
        StationEngineSettingsMapper.BuildSubmissionOptions(paths, settings);
}
