using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 系统工具列表项：图标 + 名称 + 详细说明 + 执行命令 + 实时状态。
    /// 用于“极限清理”标签页以列表形式呈现，每项自带执行按钮与下方状态文本。
    /// </summary>
    public class OptimizeTool : INotifyPropertyChanged
    {
        public string Icon { get; set; }
        public string Name { get; set; }
        /// <summary>详细说明（多行），解释该工具做什么、影响与注意事项。</summary>
        public string Detail { get; set; }
        /// <summary>执行标识，对应 SystemOptimizeService 的 action 分支。</summary>
        public string Action { get; set; }
        /// <summary>是否需要管理员权限（如 DISM/SFC/删除 Windows.old/清理 Delivery Optimization）。未提权时 UI 显示“需管理员”并禁用。</summary>
        public bool RequiresAdmin { get; set; }
        public ICommand Command { get; set; }
        /// <summary>是否提供“恢复”动作（如关闭休眠后可恢复）。</summary>
        public bool HasRestore { get; set; }
        /// <summary>恢复动作的说明（用作按钮 ToolTip）。</summary>
        public string RestoreDescription { get; set; }
        public ICommand RestoreCommand { get; set; }

        private string _statusText;
        /// <summary>当前项执行过程中的实时状态，显示在卡片下方。</summary>
        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        private bool _isRunning;
        /// <summary>当前项是否正在执行（用于在卡片内显示不确定进度条）。</summary>
        public bool IsRunning
        {
            get => _isRunning;
            set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(InlineProgressVisible)); } }
        }

        private double _progressValue;
        /// <summary>
        /// 执行进度百分比（0–100），由 DISM 等可解析控制台百分比的命令实时填充；
        /// 其余命令保持 0（进度条呈不确定态跑马灯）。
        /// </summary>
        public double ProgressValue
        {
            get => _progressValue;
            set { if (_progressValue != value) { _progressValue = value; OnPropertyChanged(); } }
        }

        private bool _isIndeterminate = true;
        /// <summary>进度条是否为不确定态（跑马灯）。收到首个真实百分比后置 false 显示确定进度。</summary>
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set { if (_isIndeterminate != value) { _isIndeterminate = value; OnPropertyChanged(); } }
        }

        /// <summary>卡片内进度条/状态文字是否可见：正在执行时显示。</summary>
        public bool InlineProgressVisible => IsRunning;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
