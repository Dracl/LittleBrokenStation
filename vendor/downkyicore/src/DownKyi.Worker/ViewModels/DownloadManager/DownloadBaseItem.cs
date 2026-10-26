using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Models;

namespace DownKyi.ViewModels.DownloadManager
{
    // LBS fork: projection type of the headless runtime, so it is public now that it lives in DownKyi.Worker.
    public class DownloadBaseItem : ObservableObject
    {
        // model数据
        private DownloadBase _downloadBase = new();

        public DownloadBase DownloadBase
        {
            get => _downloadBase;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _downloadBase = value;

                // LBS fork: the Avalonia DrawingImage ZoneImage (loaded from DictionaryResource by zone id)
                // is dropped with the Avalonia reference; the desktop host decorates the item itself.
                OnPropertyChanged();
                OnPropertyChanged(nameof(Order));
                OnPropertyChanged(nameof(MainTitle));
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(VideoCodecName));
                OnPropertyChanged(nameof(Resolution));
                OnPropertyChanged(nameof(AudioCodec));
                OnPropertyChanged(nameof(FileSize));
            }
        }

        // 视频序号
        public int Order => DownloadBase.Order;

        // 视频主标题
        public string MainTitle => DownloadBase.MainTitle;

        // 视频标题
        public string Name => DownloadBase.Name;

        // 时长
        public string Duration => DownloadBase.Duration;

        // 视频编码名称，AVC、HEVC
        public string VideoCodecName => DownloadBase.VideoCodecName;

        // 视频画质
        public Quality Resolution => DownloadBase.Resolution;

        // 音频编码
        public Quality AudioCodec => DownloadBase.AudioCodec;

        // 文件大小
        public string? FileSize => DownloadBase.FileSize;
    }
}
