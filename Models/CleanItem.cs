using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
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
        private string _details;

        public string Id { get; set; }
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        public string Description { get; set; }
        public RiskLevel Risk { get; set; }
        public Action CleanAction { get; set; }
        
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
                    
                    OnPropertyChanged(nameof(IsChecked));
                }
            }
        }

        public bool IsExpanded { get; set; }

        // 关联的应用进程名（如浏览器进程）。当这些进程正在运行时，卡片显示“关闭并清理”按钮。
        public string[] RelatedProcessNames { get; set; }

        private bool _isProcessRunning;
        public bool IsProcessRunning
        {
            get => _isProcessRunning;
            private set => SetProperty(ref _isProcessRunning, value);
        }

        // 点击后“杀掉关联进程再清理本项”的命令（由 ViewModel 注入）。
        public ICommand KillAndCleanCommand { get; set; }

        // 点击后弹出「详情」窗口（展示说明 / 将清理位置 / 失败明细），替代 ToolTip 内无法滚动的列表。
        public ICommand ShowFailureDetailsCommand { get; set; }

        // 仅当关联进程正在运行、且配置了进程名时，才显示关闭按钮。
        public bool CanKillAndClean => IsProcessRunning && (RelatedProcessNames?.Length > 0);

        // 刷新关联进程的运行状态（用于决定是否显示关闭按钮）。
        public void RefreshProcessState()
        {
            bool running = false;
            if (RelatedProcessNames?.Length > 0)
            {
                try
                {
                    foreach (var name in RelatedProcessNames)
                    {
                        if (Process.GetProcessesByName(name).Length > 0) { running = true; break; }
                    }
                }
                catch { }
            }
            IsProcessRunning = running;
        }

        
        public bool AlwaysShow
        {
            get => _alwaysShow;
            set => SetProperty(ref _alwaysShow, value);
        }

        
        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    OnPropertyChanged(nameof(FailureHint));
                    OnPropertyChanged(nameof(HasDetails));
                    OnPropertyChanged(nameof(HasFailureDetails));
                    OnPropertyChanged(nameof(HasTargetPathsOnly));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        
        /// <summary>扫描/清理的明细信息（如失败的完整路径、无效的注册表路径等），仅用于状态文本的 ToolTip（信息图标 ⓘ 只显示 Description 中文说明）。</summary>
        public string Details
        {
            get => _details;
            set
            {
                if (SetProperty(ref _details, value))
                {
                    OnPropertyChanged(nameof(FailureHint));
                    OnPropertyChanged(nameof(HasDetails));
                    OnPropertyChanged(nameof(HasFailureDetails));
                    OnPropertyChanged(nameof(HasTargetPathsOnly));
                }
            }
        }

        public string FailureHint
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_errorMessage) && string.IsNullOrWhiteSpace(_details))
                    return null;
                if (string.IsNullOrWhiteSpace(_errorMessage))
                    return _details;
                if (string.IsNullOrWhiteSpace(_details))
                    return _errorMessage;
                return $"{_errorMessage}\n\n{_details}";
            }
        }

        // 扫描后记录的「将清理的顶层目录/注册表项」清单，用于信息图标 ⓘ 的 ToolTip 展示。
        private List<string> _targetPaths;
        public List<string> TargetPaths
        {
            get => _targetPaths;
            set
            {
                if (SetProperty(ref _targetPaths, value))
                {
                    OnPropertyChanged(nameof(HasTargetPaths));
                    OnPropertyChanged(nameof(HasDetails));
                    OnPropertyChanged(nameof(HasTargetPathsOnly));
                    OnPropertyChanged(nameof(TargetPathsText));
                }
            }
        }

        public bool HasTargetPaths => _targetPaths != null && _targetPaths.Count > 0;

        // 「查看清理项」按钮：有将清理位置 且 无失败信息时才显示（失败项由「查看失败详情」覆盖，避免两个按钮同现）。
        public bool HasTargetPathsOnly => HasTargetPaths && string.IsNullOrWhiteSpace(FailureHint);

        // 是否值得弹出「详情」窗口：有将清理位置 或 有失败/明细信息。
        public bool HasDetails => HasTargetPaths || !string.IsNullOrWhiteSpace(FailureHint);

        // 是否出现「查看失败详情」按钮：仅当存在失败/明细信息（FailureHint 非空）时，即清理失败或局部失败后才显示。
        public bool HasFailureDetails => !string.IsNullOrWhiteSpace(FailureHint);

        public string TargetPathsText => _targetPaths == null ? null : string.Join("\n", _targetPaths);

        
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

        // 状态文字颜色从 ThemeService 取当前深/浅色对应值，避免写死暗色导致浅色模式下看不清。
        public string StatusColor => Status switch
        {
            ScanStatus.Cleaning => ThemeService.GetColor("accent-yellow"),
            ScanStatus.Cleaned => ThemeService.GetColor("accent-green"),
            ScanStatus.PartiallyCleaned => ThemeService.GetColor("accent-peach"),
            ScanStatus.Error => ThemeService.GetColor("accent-red"),
            _ => ThemeService.GetColor("text-muted")
        };

        // 主题切换时由外部调用，触发 StatusColor 重新计算以更新状态文字颜色。
        public void RefreshStatusColor() => OnPropertyChanged(nameof(StatusColor));

    }
}
