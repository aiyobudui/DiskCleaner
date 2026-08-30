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
    
    
    
    
    
    
    public partial class MainViewModel : ViewModelBase
    {
        
        private readonly IConfigService _configService;
        private readonly ILogger _logger;
        private readonly ISpaceAnalyzer _spaceAnalyzer;
        private readonly ISystemMonitorService _monitorService;
        private readonly ISystemInfoService _systemInfoService;
        private readonly IUserFolderService _userFolderService;
        private readonly ISystemOptimizeService _optimizeService;
        private readonly IWindowsTweakService _tweakService;

        
        private Config _config;
        private CancellationTokenSource _cts;
        private bool _isBusy;

        
        
        private CancellationTokenSource NewCts()
        {
            var old = _cts;
            _cts = new CancellationTokenSource();
            old?.Dispose();
            return _cts;
        }
        private readonly Dispatcher _uiDispatcher;

        
        private int _selectedTabIndex;
        private bool _isLightTheme;
        private string _themeIcon = "🌙";
        private string _statusText = "就绪";
        private string _totalSizeText = "";

        
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (!SetProperty(ref _selectedTabIndex, value)) return;
                _ = OnTabSelectedAsync(value);
            }
        }

        
        public bool IsLightTheme
        {
            get => _isLightTheme;
            private set => SetProperty(ref _isLightTheme, value);
        }

        
        public string ThemeIcon
        {
            get => _themeIcon;
            private set => SetProperty(ref _themeIcon, value);
        }

        
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        
        public string TotalSizeText
        {
            get => _totalSizeText;
            set => SetProperty(ref _totalSizeText, value);
        }

        
        public bool IsBusy => _isBusy;

        
        public event EventHandler ThemeToggled;

        
        public ICommand ToggleThemeCommand { get; private set; }

        
        
        
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

            
            IsLightTheme = ThemeService.IsLight;
            ThemeIcon = IsLightTheme ? "☀️" : "🌙";

            
            _ = LoadConfigAsync();

            
            WireCommands();

            
            _ = OnTabSelectedAsync(0);
        }

        
        partial void WireCommands();

        
        private async Task OnTabSelectedAsync(int index)
        {
            try
            {
                switch (index)
                {
                    case 0: 
                        await LoadDashboardAsync();
                        break;
                    case 1: 
                        EnsureCleanItemsInitialized();
                        break;
                    case 2: 
                        
                        _ = LoadUserFoldersAsync();
                        await LoadSystemTweaksAsync(); 
                        break;
                    case 3: 
                        await LoadContextMenuAsync();
                        break;
                    case 4: 
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"切换标签页 {index} 加载失败", ex);
            }
        }

        
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

            // 清理项状态文字颜色是运行时用 ThemeService 动态计算的，主题切换后需触发刷新
            if (CleanGroups != null)
            {
                foreach (var item in AllCleanItems(CleanGroups))
                    item.RefreshStatusColor();
            }
        }

        
        protected void SetIsBusy(bool busy)
        {
            if (_isBusy == busy) return;
            _isBusy = busy;
            OnPropertyChanged(nameof(IsBusy));
            RaiseBusyCommands();
        }

        
        protected void RaiseBusyCommands()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        
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

        
        protected static string FormatSize(long bytes) => DiskUtils.FormatSize(bytes);

        protected static long DirSize(string path) => DiskUtils.DirSize(path);

        
        partial void WireCommands()
        {
            
            ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());

            
            RefreshMonitorCommand = new RelayCommand(_ => _ = RefreshMonitorAsync());
            FreeMemoryCommand = new RelayCommand(_ => _ = FreeMemoryAsync());
            OpenDriveCommand = new RelayCommand(p => OpenDrive(p as string));

            
            ScanCommand = new RelayCommand(_ => _ = ScanAsync(), _ => !IsBusy);
            CleanCommand = new RelayCommand(_ => _ = CleanAsync(), _ => !IsBusy && HasCleanableSelection);
            RefreshCommand = new RelayCommand(_ => RefreshItems());
            EmptyFolderScanCommand = new RelayCommand(_ => _ = ScanEmptyFoldersAsync(), _ => !IsBusy);
            EmptyFolderDeleteCommand = new RelayCommand(_ => _ = DeleteEmptyFoldersAsync(), _ => !IsBusy && EmptyFolderList.Any());

            
            LoadUserFoldersCommand = new RelayCommand(_ => _ = LoadUserFoldersAsync());
            BrowseUserFolderCommand = new RelayCommand(p => BrowseUserFolder(p as UserFolderInfo));
            MoveUserFolderCommand = new RelayCommand(p => _ = MoveUserFolderAsync(p as UserFolderInfo), _ => !IsBusy);
            RestoreUserFolderCommand = new RelayCommand(p => _ = RestoreUserFolderAsync(p as UserFolderInfo), _ => !IsBusy);
            OpenHostsFileCommand = new RelayCommand(_ => OpenHostsFile());

            
            OpenSettingsCommand = new RelayCommand(_ => OpenSettings());

            
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
