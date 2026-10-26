using System;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownloadStatus = DownKyi.Models.DownloadStatus;

namespace DownKyi.ViewModels.DownloadManager
{
    // LBS fork: projection type of the headless runtime, so it is public now that it lives in DownKyi.Worker.
    public class DownloadingItem : DownloadBaseItem
    {
        // LBS fork: the constructor only seeded the Avalonia ButtonIcon.Pause glyph for StartOrPause;
        // the Avalonia button members below are gone, so it is removed.

        // model数据
        private Downloading _downloading = null!;


        public MovieMetadata? Metadata { get; set; }

        public Downloading Downloading
        {
            get => _downloading;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _downloading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DownloadContent));
                OnPropertyChanged(nameof(DownloadStatusTitle));
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(DownloadingFileSize));
                OnPropertyChanged(nameof(SpeedDisplay));
                OnPropertyChanged(nameof(HasFailureDetails));
                if (value.DownloadStatus != DownloadStatus.DownloadFailed)
                {
                    IsFailureDetailsExpanded = false;
                }
            }
        }

        private DownloadFailure? _failure;

        public DownloadFailure? Failure
        {
            get => _failure;
            set
            {
                if (!SetProperty(ref _failure, value))
                {
                    return;
                }

                IsFailureDetailsExpanded = false;
                OnPropertyChanged(nameof(HasFailureDetails));
                OnPropertyChanged(nameof(FailureDetailsText));
            }
        }

        public bool HasFailureDetails =>
            _failure != null && _downloading?.DownloadStatus == DownloadStatus.DownloadFailed;

        private bool _isFailureDetailsExpanded;

        public bool IsFailureDetailsExpanded
        {
            get => _isFailureDetailsExpanded;
            set => SetProperty(ref _isFailureDetailsExpanded, value && HasFailureDetails);
        }

        // LBS fork: FailureDetailsToggleText came from DictionaryResource (Avalonia resource dictionary)
        // and is removed; the desktop host supplies the toggle label itself.

        public string FailureDetailsText => _failure is null
            ? string.Empty
            : DownloadActivityPresenter.FormatFailureDetails(_failure);

        // 正在下载内容（音频、视频、弹幕、字幕、封面）
        public string? DownloadContent
        {
            get => Downloading.DownloadContent;
            set
            {
                Downloading.DownloadContent = value;
                OnPropertyChanged();
            }
        }

        // 下载状态显示
        public string? DownloadStatusTitle
        {
            get => Downloading.DownloadStatusTitle;
            set
            {
                Downloading.DownloadStatusTitle = value;
                OnPropertyChanged();
            }
        }

        // 下载进度
        public float Progress
        {
            get => Downloading.Progress;
            set
            {
                Downloading.Progress = value;
                OnPropertyChanged();
            }
        }

        //  已下载大小/文件大小
        public string? DownloadingFileSize
        {
            get => Downloading.DownloadingFileSize;
            set
            {
                Downloading.DownloadingFileSize = value;
                OnPropertyChanged();
            }
        }

        //  下载速度
        public string? SpeedDisplay
        {
            get => Downloading.SpeedDisplay;
            set
            {
                Downloading.SpeedDisplay = value;
                OnPropertyChanged();
            }
        }

        // 操作提示
        private string _operationTip = string.Empty;

        public string OperationTip
        {
            get => _operationTip;
            set => SetProperty(ref _operationTip, value);
        }

        #region 控制按钮

        // LBS fork: StartOrPause / Delete (DownKyi.Images VectorImage + ButtonIcon) and
        // RefreshControlPresentation are Avalonia-only presentation members and are removed here.
        // OperationTip and IsToggleVisible stay: they carry no Avalonia dependency.

        private bool _isToggleVisible = true;

        public bool IsToggleVisible
        {
            get => _isToggleVisible;
            set => SetProperty(ref _isToggleVisible, value);
        }

        #endregion
    }
}
