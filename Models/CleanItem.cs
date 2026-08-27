using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DiskCleaner.Services;
using DiskCleaner.ViewModels;

namespace DiskCleaner.Models
{
    public enum RiskLevel
    {
        Safe,
        Caution,
        Forbidden
    }

    public enum ScanStatus
    {
        NotScanned,
        Scanned,
        Cleaning,
        Cleaned,
        PartiallyCleaned,
        Error
    }

    public class CleanItem : ViewModelBase, ICleanNode
    {
        private bool _isChecked;
        private bool _isEnabled;
        private string _name;
        private long _size;
        private ScanStatus _status;
        private bool _isVisible = true;
        private bool _alwaysShow;
        private string _errorMessage;

        public string Id { get; set; }
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        public string Description { get; set; }
        public RiskLevel Risk { get; set; }
        public Action CleanAction { get; set; }
        /// <summary>支持部分成功报告的清理动作（优先于 CleanAction 使用）。若为 null 则回退到 CleanAction + try/catch。</summary>
        public Func<CleanService.CleanResult> CleanActionWithResult { get; set; }
        public Func<bool> CanClean { get; set; }

        public bool? IsChecked
        {
            get => _isChecked && IsEnabled;
            set
            {
                bool b = value == true;
                if (_isChecked != b)
                {
                    _isChecked = b;
                    OnPropertyChanged(nameof(IsChecked));
                }
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled && Risk != RiskLevel.Forbidden;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged(nameof(IsEnabled));
                    // IsEnabled 变化会改变 IsChecked 的取值，必须通知 UI 刷新勾选态
                    OnPropertyChanged(nameof(IsChecked));
                }
            }
        }

        public bool IsExpanded { get; set; }

        /// <summary>始终显示（不随「仅显示有内容的项」过滤隐藏），用于「清空回收站」等动作型项。</summary>
        public bool AlwaysShow
        {
            get => _alwaysShow;
            set => SetProperty(ref _alwaysShow, value);
        }

        /// <summary>清理失败时的简短错误信息，用于 Tooltip 与状态提示。</summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    OnPropertyChanged(nameof(FailureHint));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        /// <summary>清理失败时对外展示的友好提示，若异常含「被占用」相关描述则明确提示。</summary>
        public string FailureHint
        {
            get
            {
                return string.IsNullOrWhiteSpace(_errorMessage) ? null : _errorMessage;
            }
        }

        /// <summary>是否在列表中可见。扫描后仅「有内容（Size>0）」或 AlwaysShow 的项才可见，0B 的隐藏。</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public long Size
        {
            get => _size;
            set
            {
                if (SetProperty(ref _size, value))
                {
                    OnPropertyChanged(nameof(Size));
                    OnPropertyChanged(nameof(SizeText));
                }
            }
        }

        public string SizeText => DiskUtils.FormatSize(_size);
        
        public ScanStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        /// <summary>状态文字：未扫描/可清理/清理中.../已清理/部分清理/失败。</summary>
        public string StatusText => _status switch
        {
            ScanStatus.NotScanned => "未扫描",
            ScanStatus.Scanned => "可清理",
            ScanStatus.Cleaning => "清理中...",
            ScanStatus.Cleaned => "已清理",
            ScanStatus.PartiallyCleaned => string.IsNullOrWhiteSpace(_errorMessage) ? "部分清理" : $"部分清理 · {_errorMessage}",
            ScanStatus.Error => string.IsNullOrWhiteSpace(_errorMessage) ? "清理失败" : _errorMessage,
            _ => ""
        };

        public string StatusColor => Status switch
        {
            ScanStatus.Cleaning => "#F9E2AF",
            ScanStatus.Cleaned => "#A6E3A1",
            ScanStatus.PartiallyCleaned => "#FAB387",  // 琥珀色：介于成功(绿)与失败(红)之间
            ScanStatus.Error => "#F38BA8",
            _ => "#6C7086"
        };

    }
}
