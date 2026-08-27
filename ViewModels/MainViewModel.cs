using DiskCleaner.Models;
using DiskCleaner.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DiskCleaner.ViewModels
{
    /// <summary>
    /// 主视图模型（共享核心）。
    /// 本文件只放「被多个标签页共用」的成员：服务实例、配置、与主题/忙碌/导航/路径相关的基础属性与命令。
    /// 每个标签页自己的逻辑放在对应的 MainViewModel.XXX.cs（同为 partial 分部类），
    /// 它们直接调用本文件里的共享字段与方法，无需任何额外引用。
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        // ---------- 服务（所有标签页共用） ----------
        private readonly IConfigService _configService;
        private readonly ILogger _logger;
        private readonly ISpaceAnalyzer _spaceAnalyzer;
        private readonly ISystemMonitorService _monitorService;
        private readonly ISystemInfoService _systemInfoService;
        private readonly IUserFolderService _userFolderService;
        private readonly ISystemOptimizeService _optimizeService;
        private readonly IWindowsTweakService _tweakService;

        // ---------- 共享状态 ----------
        private Config _config;
        private CancellationTokenSource _cts;
        private bool _isBusy;

        /// <summary>创建新的 CancellationTokenSource，并释放上一个（避免内核 wait 句柄泄漏）。
        /// 扫描/清理操作互斥（受 IsBusy 保护），不会出现并发替换导致旧 token 仍在被使用的情况。</summary>
        private CancellationTokenSource NewCts()
        {
            var old = _cts;
            _cts = new CancellationTokenSource();
            old?.Dispose();
            return _cts;
        }
        private readonly Dispatcher _uiDispatcher;

        // ---------- 与主题/导航/路径相关的基础属性 ----------
        private int _selectedTabIndex;
        private bool _isLightTheme;
        private string _themeIcon = "🌙";
        private string _statusText = "就绪";
        private string _totalSizeText = "";

        /// <summary>当前选中的标签页索引（与 XAML TabControl 双向绑定）。</summary>
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (!SetProperty(ref _selectedTabIndex, value)) return;
                _ = OnTabSelectedAsync(value);
            }
        }

        /// <summary>是否处于浅色主题（持久化在 Config.Theme）。</summary>
        public bool IsLightTheme
        {
            get => _isLightTheme;
            private set => SetProperty(ref _isLightTheme, value);
        }

        /// <summary>标题栏主题切换按钮显示的图标。</summary>
        public string ThemeIcon
        {
            get => _themeIcon;
            private set => SetProperty(ref _themeIcon, value);
        }

        /// <summary>全局状态文本（底部/进度区展示）。</summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>清理页顶部的「已选择 / 可清理」汇总文本。</summary>
        public string TotalSizeText
        {
            get => _totalSizeText;
            set => SetProperty(ref _totalSizeText, value);
        }

        /// <summary>是否正在执行耗时操作（用于命令可用性判断）。</summary>
        public bool IsBusy => _isBusy;

        // ---------- 主题切换事件（供 MainWindow 刷新资源画刷） ----------
        public event EventHandler ThemeToggled;

        // ---------- 命令：主题切换 ----------
        public ICommand ToggleThemeCommand { get; private set; }

        /// <summary>
        /// 构造函数：初始化所有服务实例、加载配置、装配各标签页命令，并触发首屏加载。
        /// </summary>
        public MainViewModel()
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;

            _configService = new ConfigService();
            _logger = Logger.Instance;
            _spaceAnalyzer = new SpaceAnalyzer();
            _monitorService = new SystemMonitorService();
            _systemInfoService = new SystemInfoService();
            _userFolderService = new UserFolderService();
            _optimizeService = new SystemOptimizeService(_logger);
            _tweakService = new WindowsTweakService();

            // 构造函数阶段 _config 尚未加载，先按系统主题占位；LoadConfigAsync 会用已保存主题覆盖。
            IsLightTheme = ThemeService.IsLight;
            ThemeIcon = IsLightTheme ? "☀️" : "🌙";

            // 异步加载配置（配置存在前先用默认值占位）
            _ = LoadConfigAsync();

            // 装配各标签页的命令（分布在各个 partial 文件中定义）
            WireCommands();

            // 触发首个标签页（仪表盘）的加载
            _ = OnTabSelectedAsync(0);
        }

        /// <summary>装配所有 ICommand 字段。各标签页自己的命令也在此统一接线。</summary>
        partial void WireCommands();

        // ---------- 导航：切换到某个标签页时按需加载数据 ----------
        private async Task OnTabSelectedAsync(int index)
        {
            try
            {
                switch (index)
                {
                    case 0: // 仪表盘
                        await LoadDashboardAsync();
                        break;
                    case 1: // 深度清理
                        EnsureCleanItemsInitialized();
                        break;
                    case 2: // 系统设置（文件夹搬迁 + HOSTS + 系统策略）
                        // 用户文件夹搬迁加载较慢（扫描磁盘占用），改为后台进行，不阻塞系统策略值显示
                        _ = LoadUserFoldersAsync();
                        await LoadSystemTweaksAsync(); // 读注册表，毫秒级，立即显示当前值
                        break;
                    case 3: // 右键管理
                        await LoadContextMenuAsync();
                        break;
                    case 4: // 关于
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"切换标签页 {index} 加载失败", ex);
            }
        }

        // ---------- 主题切换 ----------
        private void ToggleTheme()
        {
            ThemeService.Toggle();
            IsLightTheme = ThemeService.IsLight;
            ThemeIcon = IsLightTheme ? "☀️" : "🌙";
            if (_config != null)
            {
                _config.Theme = IsLightTheme ? "light" : "dark";
                _ = SaveConfigAsync();
            }
            ThemeToggled?.Invoke(this, EventArgs.Empty);
        }

        // ---------- 忙碌状态管理（供各标签页的异步操作复用） ----------
        protected void SetIsBusy(bool busy)
        {
            if (_isBusy == busy) return;
            _isBusy = busy;
            OnPropertyChanged(nameof(IsBusy));
            RaiseBusyCommands();
        }

        /// <summary>通知所有以「非忙碌」为可用性条件的命令重新求值（按钮禁用态刷新）。</summary>
        protected void RaiseBusyCommands()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        // ---------- 配置读写（共享） ----------
        private async Task LoadConfigAsync()
        {
            try
            {
                _config = await _configService.LoadAsync();
            }
            catch (Exception ex)
            {
                _logger.Error("加载配置失败", ex);
                _config = new Config();
            }

            IsLightTheme = string.Equals(_config.Theme, "light", StringComparison.OrdinalIgnoreCase);
            ThemeIcon = IsLightTheme ? "☀️" : "🌙";

            // 程序删除文件始终移入回收站（便于误删恢复），此处强制为 true。
            CleanService.UseRecycleBin = true;
        }

        protected async Task SaveConfigAsync()
        {
            if (_config == null) return;
            try
            {
                await _configService.SaveAsync(_config);
            }
            catch (Exception ex)
            {
                _logger.Error("保存配置失败", ex);
            }
        }

        // ---------- 磁盘信息（共享，供仪表盘卡片与清理页复用） ----------
        protected void UpdateDriveInfo()
        {
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(root)) root = "C:\\";
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    var label = root.TrimEnd('\\', ':');
                    TotalSizeText = $"{label} 盘 可用 {DiskUtils.FormatSize(drive.AvailableFreeSpace)} / 共 {DiskUtils.FormatSize(drive.TotalSize)}";
                }
            }
            catch { }
        }

        // ---------- 共享工具方法（供各标签页调用） ----------
        protected static string FormatSize(long bytes) => DiskUtils.FormatSize(bytes);

        protected static long DirSize(string path) => DiskUtils.DirSize(path);

        // ---------- 统一装配所有命令（partial 方法，在核心文件实现一次） ----------
        partial void WireCommands()
        {
            // 主题
            ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());

            // 仪表盘
            RefreshMonitorCommand = new RelayCommand(_ => _ = RefreshMonitorAsync());
            FreeMemoryCommand = new RelayCommand(_ => _ = FreeMemoryAsync());
            OpenDriveCommand = new RelayCommand(p => OpenDrive(p as string));

            // 深度清理
            ScanCommand = new RelayCommand(_ => _ = ScanAsync(), _ => !IsBusy);
            CleanCommand = new RelayCommand(_ => _ = CleanAsync(), _ => !IsBusy && HasCleanableSelection);
            RefreshCommand = new RelayCommand(_ => RefreshItems());
            EmptyFolderScanCommand = new RelayCommand(_ => _ = ScanEmptyFoldersAsync(), _ => !IsBusy);
            EmptyFolderDeleteCommand = new RelayCommand(_ => _ = DeleteEmptyFoldersAsync(), _ => !IsBusy && EmptyFolderList.Any());

            // 系统设置
            LoadUserFoldersCommand = new RelayCommand(_ => _ = LoadUserFoldersAsync());
            BrowseUserFolderCommand = new RelayCommand(p => BrowseUserFolder(p as UserFolderInfo));
            MoveUserFolderCommand = new RelayCommand(p => _ = MoveUserFolderAsync(p as UserFolderInfo), _ => !IsBusy);
            RestoreUserFolderCommand = new RelayCommand(p => _ = RestoreUserFolderAsync(p as UserFolderInfo), _ => !IsBusy);
            OpenHostsFileCommand = new RelayCommand(_ => OpenHostsFile());

            // 软件设置
            OpenSettingsCommand = new RelayCommand(_ => OpenSettings());

            // 右键管理
            ScanContextMenuCommand = new RelayCommand(_ => _ = LoadContextMenuAsync(), _ => !IsContextMenuScanning);
            DeleteContextMenuItemCommand = new RelayCommand(
                p => _ = Task.Run(() => DeleteContextMenuItemCore(p as ContextMenuItem)),
                p => p is ContextMenuItem ci && ci.CanDelete);
            RestoreContextMenuItemCommand = new RelayCommand(
                p => _ = Task.Run(() => RestoreContextMenuItemCore(p as ContextMenuItem)),
                p => p is ContextMenuItem ci && ci.IsDeleted);
            RestartExplorerCommand = new RelayCommand(_ => RestartExplorerCore());
        }
    }
}
