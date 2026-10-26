using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace DownKyi.Services.Download;

// LBS fork: the migrated runtime used to read these strings from the Avalonia resource dictionary
// (DownKyi.Utils.DictionaryResource, which needs Avalonia.Application + Dispatcher). The wording is copied
// verbatim from src/DownKyi.Desktop/Languages/Default.axaml so a headless host produces the same text
// without an Avalonia application. An unknown key is returned unchanged to make gaps visible in logs.
internal static class DownloadText
{
    private static readonly FrozenDictionary<string, string> Values =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Parsing"] = "正在解析……",
            ["DownloadingAudio"] = "音频",
            ["DownloadingVideo"] = "视频",
            ["DownloadingDanmaku"] = "弹幕",
            ["DownloadingSubtitle"] = "字幕",
            ["DownloadingCover"] = "封面",
            ["MixedFlow"] = "混流中……",
            ["ConcatVideos"] = "视频合并中……",
            ["WhileDownloading"] = "下载中……",
            ["Waiting"] = "等待中……",
            ["Pausing"] = "暂停中……",
            ["Paused"] = "已暂停",
            ["DownloadFailed"] = "下载失败",
            ["SubtitleDownloadFailed"] = "字幕下载失败",
            ["DownloadFailureUnknownCode"] = "未知",
            ["DownloadFailureCode"] = "错误代码",
            ["DirectoryError"] = "路径错误！",
            ["DownloadOutputCollision"] = "目标文件名已存在或正在被其他下载任务使用。请选择其他下载文件夹，或到「设置 → 基本」开启「重复文件自动添加数字序号后缀」后重试。",
            ["DownloadTlsUntrusted"] = "下载失败：服务器证书不受信任",
            ["DownloadTlsExpired"] = "下载失败：服务器证书已过期",
            ["DownloadTlsNotYetValid"] = "下载失败：服务器证书尚未生效",
            ["DownloadTlsHostnameMismatch"] = "下载失败：服务器证书与主机名不匹配",
            ["DownloadTlsInvalidChain"] = "下载失败：服务器证书链无效",
            ["DownloadTlsHandshakeFailed"] = "下载失败：TLS 安全连接验证失败",
            ["DownloadFailureLegacyDetails"] = "此任务使用已移除的旧下载器，无法直接重试。请重新解析并建立任务。",
            ["DownloadFailureRuntimeDetails"] = "下载服务无法继续运行。请重新启动应用；若仍失败，请在“设置 → 关于与反馈”导出诊断包。",
            ["DownloadFailureTlsDetails"] = "安全连接验证失败。请检查系统日期、代理和证书设置，然后再重试。",
            ["DownloadFailureRateLimitDetails"] = "服务器暂时限制请求。请稍后再点“重试”，不要连续重复提交。",
            ["DownloadFailureAccessDetails"] = "服务器拒绝了下载请求。请检查登录状态和内容访问权限；持续失败时可重新解析并选择其他画质。",
            ["DownloadFailureCoverAccessDetails"] = "服务器拒绝了封面请求。请稍后重试；持续失败时请重新建立任务，或取消封面选项。",
            ["DownloadFailureArtifactAccessDetails"] = "服务器拒绝了附加文件请求。请检查登录状态和内容访问权限后重试；持续失败时请重新建立任务，或取消对应的附加文件选项。",
            ["DownloadFailureExpiredAddressDetails"] = "下载地址已失效或被服务器拒绝。程序已经尝试切换并刷新地址；请重新解析并建立任务。持续失败时请检查登录状态和内容访问权限。",
            ["DownloadFailureUnavailableDetails"] = "请求的画质、音轨或其他内容目前不可用。请重新解析并建立任务，选择当前可用的内容；原任务的选择不会因重试而改变。",
            ["DownloadFailureStorageDetails"] = "本地下载文件无法写入、清理或与现有文件冲突。请检查下载目录权限、剩余空间及同名文件，再点“重试”。",
            ["DownloadFailureInvalidOutputDetails"] = "下载结果未通过完整性检查。可点“重试”；若同一内容持续失败，请重新解析并尝试其他画质。",
            ["DownloadFailureSubtitleDetails"] = "所选字幕未能完成下载。可点“重试”；若字幕已不可用，请重新建立任务并调整字幕选择。",
            ["DownloadFailureArtifactDetails"] = "附加文件未能完成下载。请检查网络和下载目录后重试；持续失败时可在“设置 → 关于与反馈”导出诊断包。",
            ["DownloadFailureProcessingDetails"] = "下载后的文件处理未完成。可点“重试”；持续失败时可在“设置 → 关于与反馈”导出诊断包。",
            ["DownloadFailurePublicationRecoveryDetails"] = "上次文件发布的状态无法安全确认，当前任务不能直接重试。请确认同名文件后删除此失败任务，再重新解析并建立任务。",
            ["DownloadFailurePublishedArtifactDetails"] = "已发布的下载文件已缺失或损坏，原任务不能直接重试。若损坏的同名文件仍存在，请先将它移走或删除；再删除此失败任务，重新解析并建立任务。",
            ["DownloadFailureNetworkDetails"] = "网络连接或服务器响应中断。请检查网络和代理设置，然后点“重试”。",
            ["DownloadFailureGenericDetails"] = "暂时无法判断具体原因。可点“重试”；若再次失败，请在“设置 → 关于与反馈”导出诊断包。",
            ["TipAlreadyToAddDownloading"] = "此内容已加入下载队列，无需重复添加。",
            ["TipAddDownloadingUnsupportedInput"] = "当前输入无法建立下载任务。",
            ["TipAddDownloadingCanceled"] = "已取消新增下载任务。",
            ["TipAddDownloadingSelectionUnavailable"] = "下载目录不可用，未新增任务。",
            ["TipAddDownloadingUnavailable"] = "所选内容目前没有可下载的资源。",
            ["TipAddDownloadingSkippedByChoice"] = "已按你的选择跳过下载。",
            ["TipAddDownloadingFailed"] = "新增下载任务失败。",
            ["TipAddDownloadingZero"] = "没有选中项符合下载要求！",
            ["TipAddDownloadingCanceledAfterProgress"] = "{0} 操作随后取消。",
            ["TipAddDownloadingSummary"] = "新增 {0} 项；重复 {1} 项，跳过 {2} 项，失败 {3} 项。",
            ["TipAddDownloadingNoAddedSummary"] = "未新增任务；重复 {0} 项，跳过 {1} 项，失败 {2} 项。",
            ["TipAddDownloadingFinished1"] = "成功添加了",
            ["TipAddDownloadingFinished2"] = "项~",
            ["TipAddDownloadingSkipped"] = "跳过了 {1} 个不可用项目，没有添加下载项。",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string Get(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        return Values.TryGetValue(resourceKey, out var value) ? value : resourceKey;
    }
}
