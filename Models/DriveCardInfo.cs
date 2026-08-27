using DiskCleaner.ViewModels;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 仪表盘“磁盘空间”中单个磁盘的展示模型。
    /// 枚举本机所有就绪的本地/可移动磁盘后填充。
    /// </summary>
    public class DriveCardInfo : ViewModelBase
    {
        private string _name;
        private string _volumeLabel;
        private string _typeText;
        private string _freeSpace;
        private string _totalSize;
        private string _usedSpace;
        private double _usedPercent;
        private string _path;

        /// <summary>盘符，例如 "C:"</summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>卷标（可能为空）</summary>
        public string VolumeLabel
        {
            get => _volumeLabel;
            set
            {
                if (SetProperty(ref _volumeLabel, value))
                    OnPropertyChanged(nameof(DisplayLabel));
            }
        }

        /// <summary>盘符旁显示的卷标，如 "(Windows)"；无卷标时返回空字符串</summary>
        public string DisplayLabel => string.IsNullOrWhiteSpace(VolumeLabel) ? "" : $"({VolumeLabel})";

        /// <summary>类型描述：本地磁盘 / 可移动磁盘</summary>
        public string TypeText
        {
            get => _typeText;
            set => SetProperty(ref _typeText, value);
        }

        public string FreeSpace
        {
            get => _freeSpace;
            set => SetProperty(ref _freeSpace, value);
        }

        public string TotalSize
        {
            get => _totalSize;
            set => SetProperty(ref _totalSize, value);
        }

        public string UsedSpace
        {
            get => _usedSpace;
            set => SetProperty(ref _usedSpace, value);
        }

        /// <summary>已用百分比，0–100，用于环形进度条与配色</summary>
        public double UsedPercent
        {
            get => _usedPercent;
            set => SetProperty(ref _usedPercent, value);
        }

        /// <summary>根目录路径，例如 "C:\"，用于点击打开资源管理器</summary>
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }
    }
}
