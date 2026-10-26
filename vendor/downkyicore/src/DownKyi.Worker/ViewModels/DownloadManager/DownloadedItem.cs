using DownKyi.Application.Downloads;
using DownKyi.Models;

namespace DownKyi.ViewModels.DownloadManager;

// LBS fork: projection type of the headless runtime, so it is public now that it lives in DownKyi.Worker.
public class DownloadedItem : DownloadBaseItem
{
    // model数据
    public Downloaded Downloaded { get; set; } = null!;

    public DownloadHistoryRecord HistoryRecord { get; set; } = null!;

    //  下载速度
    public string? MaxSpeedDisplay => Downloaded.MaxSpeedDisplay;

    // 完成时间
    public string FinishedTime => Downloaded.FinishedTime;

    // LBS fork: OpenFolder / OpenVideo / RemoveVideo were DownKyi.Images VectorImage buttons
    // (ButtonIcon.Folder / Start / Trash); they are Avalonia-only presentation members and are removed here.
}
