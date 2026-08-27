using DiskCleaner.Models;
using DiskCleaner.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DiskCleaner.ViewModels
{
    
    
    
    public partial class MainViewModel : ViewModelBase
    {
        
        private ObservableCollection<UserFolderInfo> _userFolders = new();

        
        private bool _isFolderMigrationExpanded;
        private bool _isHostsExpanded;
        private bool _isUacExpanded;
        private bool _isTaskbarExpanded;
        private bool _isDesktopIconsExpanded;
        private bool _isExplorerExpanded;
        private bool _isNotepadExpanded;

        
        public ObservableCollection<UserFolderInfo> UserFolders
        {
            get => _userFolders;
            set => SetProperty(ref _userFolders, value);
        }

        public bool IsFolderMigrationExpanded { get => _isFolderMigrationExpanded; set => SetProperty(ref _isFolderMigrationExpanded, value); }
        public bool IsHostsExpanded { get => _isHostsExpanded; set => SetProperty(ref _isHostsExpanded, value); }
        public bool IsUacExpanded { get => _isUacExpanded; set => SetProperty(ref _isUacExpanded, value); }
        public bool IsTaskbarExpanded { get => _isTaskbarExpanded; set => SetProperty(ref _isTaskbarExpanded, value); }
        public bool IsDesktopIconsExpanded { get => _isDesktopIconsExpanded; set => SetProperty(ref _isDesktopIconsExpanded, value); }
        public bool IsExplorerExpanded { get => _isExplorerExpanded; set => SetProperty(ref _isExplorerExpanded, value); }
        public bool IsNotepadExpanded { get => _isNotepadExpanded; set => SetProperty(ref _isNotepadExpanded, value); }

        
        public ICommand LoadUserFoldersCommand { get; private set; }
        public ICommand BrowseUserFolderCommand { get; private set; }
        public ICommand MoveUserFolderCommand { get; private set; }
        public ICommand RestoreUserFolderCommand { get; private set; }
        public ICommand OpenHostsFileCommand { get; private set; }

        
        internal async Task LoadUserFoldersAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    var folders = _userFolderService.GetAllFolders();
                    _uiDispatcher.Invoke(() => UserFolders = new ObservableCollection<UserFolderInfo>(folders));
                }
                catch (Exception ex)
                {
                    _logger.Error("加载用户文件夹失败", ex);
                }
            });
        }

        
        private void BrowseUserFolder(UserFolderInfo info)
        {
            if (info == null) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = $"选择 {info.Name} 的新位置",
                InitialDirectory = info.CurrentPath,
                Multiselect = false
            };
            if (dlg.ShowDialog() == true)
            {
                info.NewPath = dlg.FolderName;
            }
        }

        
        private async Task MoveUserFolderAsync(UserFolderInfo info)
        {
            if (info == null) return;
            var newPath = info.NewPath;
            if (string.IsNullOrWhiteSpace(newPath))
            {
                info.StatusText = "请先填写或浏览新位置。";
                return;
            }

            SetIsBusy(true);
            info.StatusText = "正在搬迁...";
            try
            {
                var progress = new Progress<string>(msg => info.StatusText = msg);
                var result = await Task.Run(() => _userFolderService.MoveFolder(info.FolderId, newPath, progress));
                info.StatusText = result.message;
                if (result.success)
                {
                    await LoadUserFoldersAsync();
                }
            }
            catch (Exception ex)
            {
                info.StatusText = "搬迁失败：" + ex.Message;
                _logger.Error("MoveUserFolderAsync", ex);
            }
            finally
            {
                SetIsBusy(false);
            }
        }

        
        private async Task RestoreUserFolderAsync(UserFolderInfo info)
        {
            if (info == null) return;
            SetIsBusy(true);
            info.StatusText = "正在恢复...";
            try
            {
                var progress = new Progress<string>(msg => info.StatusText = msg);
                var result = await Task.Run(() => _userFolderService.RestoreFolder(info.FolderId, progress));
                info.StatusText = result.message;
                if (result.success)
                {
                    await LoadUserFoldersAsync();
                }
            }
            catch (Exception ex)
            {
                info.StatusText = "恢复失败：" + ex.Message;
                _logger.Error("RestoreUserFolderAsync", ex);
            }
            finally
            {
                SetIsBusy(false);
            }
        }

        
        private void OpenHostsFile()
        {
            try
            {
                var hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");
                if (!File.Exists(hosts))
                {
                    MessageBox.Show("未找到 HOSTS 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                using var hostsEditor = Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = hosts,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                
            }
            catch (Exception ex)
            {
                _logger.Error("打开 HOSTS 失败", ex);
                MessageBox.Show($"打开 HOSTS 失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        
        private bool _isLoadingTweaks; 
        private int _tweakLoadToken;   

        private int _uacLevelIndex;
        public int UacLevelIndex
        {
            get => _uacLevelIndex;
            set
            {
                if (SetProperty(ref _uacLevelIndex, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        _tweakService.SetUacLevel((UacLevel)value);
                        StatusText = "UAC 通知级别已更新（部分更改需注销后完全生效）。";
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改 UAC 失败（需要管理员权限）";
                        _logger.Error("SetUacLevel", ex);
                    }
                }
            }
        }

        private bool _smartScreenEnabled;
        public bool SmartScreenEnabled
        {
            get => _smartScreenEnabled;
            set
            {
                if (SetProperty(ref _smartScreenEnabled, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        _tweakService.SetSmartScreenEnabled(value);
                        StatusText = value ? "SmartScreen 筛选器已启用。" : "SmartScreen 筛选器已关闭（电脑感染病毒或遭受网络攻击的风险显著增加）。";
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改 SmartScreen 失败（需要管理员权限）";
                        _logger.Error("SetSmartScreen", ex);
                    }
                }
            }
        }

        private int _taskbarGlom;
        public int TaskbarGlom
        {
            get => _taskbarGlom;
            set
            {
                if (SetProperty(ref _taskbarGlom, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        _tweakService.SetTaskbarGlom(value);
                        StatusText = "任务栏合并方式已更新。";
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改任务栏设置失败";
                        _logger.Error("SetTaskbarGlom", ex);
                    }
                }
            }
        }

        private bool _startMenuCenter;
        public bool StartMenuCenter
        {
            get => _startMenuCenter;
            set
            {
                if (SetProperty(ref _startMenuCenter, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        _tweakService.SetStartAlign(value ? 1 : 0);
                        StatusText = value ? "开始菜单已设置为居中对齐。" : "开始菜单已设置为左对齐。";
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改开始菜单设置失败";
                        _logger.Error("SetStartAlign", ex);
                    }
                }
            }
        }

        private bool _desktopIconThisPC;
        public bool DesktopIconThisPC { get => _desktopIconThisPC; set => SetDesktopIcon(ref _desktopIconThisPC, value, DesktopIconGuids.ThisPC); }
        private bool _desktopIconRecycleBin;
        public bool DesktopIconRecycleBin { get => _desktopIconRecycleBin; set => SetDesktopIcon(ref _desktopIconRecycleBin, value, DesktopIconGuids.RecycleBin); }
        private bool _desktopIconNetwork;
        public bool DesktopIconNetwork { get => _desktopIconNetwork; set => SetDesktopIcon(ref _desktopIconNetwork, value, DesktopIconGuids.Network); }
        private bool _desktopIconControlPanel;
        public bool DesktopIconControlPanel { get => _desktopIconControlPanel; set => SetDesktopIcon(ref _desktopIconControlPanel, value, DesktopIconGuids.ControlPanel); }
        private bool _desktopIconUserFolder;
        public bool DesktopIconUserFolder { get => _desktopIconUserFolder; set => SetDesktopIcon(ref _desktopIconUserFolder, value, DesktopIconGuids.UserFolder); }

        private void SetDesktopIcon(ref bool field, bool value, string guid)
        {
            if (SetProperty(ref field, value) && !_isLoadingTweaks)
            {
                try
                {
                    _tweakService.SetDesktopIcon(guid, value);
                    StatusText = (value ? "已显示" : "已隐藏") + "桌面图标。";
                }
                catch (Exception ex)
                {
                    StatusText = "修改桌面图标失败";
                    _logger.Error("SetDesktopIcon", ex);
                }
            }
        }

        private bool _showFileExt;
        public bool ShowFileExt
        {
            get => _showFileExt;
            set
            {
                if (SetProperty(ref _showFileExt, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        _tweakService.SetShowFileExt(value);
                        StatusText = value ? "已显示文件扩展名。" : "已隐藏文件扩展名。";
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改资源管理器设置失败";
                        _logger.Error("SetShowFileExt", ex);
                    }
                }
            }
        }

        private bool _showHidden;
        public bool ShowHidden
        {
            get => _showHidden;
            set
            {
                if (SetProperty(ref _showHidden, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        _tweakService.SetShowHidden(value);
                        StatusText = value ? "已显示隐藏文件。" : "已隐藏隐藏文件。";
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改资源管理器设置失败";
                        _logger.Error("SetShowHidden", ex);
                    }
                }
            }
        }

        private bool _showSuperHidden;
        private bool _notepadOpenInNewTab;
        private bool _notepadNewSession;
        public bool ShowSuperHidden
        {
            get => _showSuperHidden;
            set
            {
                if (SetProperty(ref _showSuperHidden, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        _tweakService.SetShowSuperHidden(value);
                        StatusText = value ? "已显示受保护的系统文件。" : "已隐藏受保护的系统文件。";
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改资源管理器设置失败";
                        _logger.Error("SetShowSuperHidden", ex);
                    }
                }
            }
        }

        
        public bool NotepadOpenInNewTab
        {
            get => _notepadOpenInNewTab;
            set
            {
                if (SetProperty(ref _notepadOpenInNewTab, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        var restarted = _tweakService.SetNotepadOpenInNewTab(value);
                        StatusText = restarted
                            ? (value ? "记事本已设置为在新选项卡中打开文件，已重启记事本使设置生效。" : "记事本已设置为在新窗口中打开文件，已重启记事本使设置生效。")
                            : (value ? "记事本已设置为在新选项卡中打开文件，下次打开记事本时生效。" : "记事本已设置为在新窗口中打开文件，下次打开记事本时生效。");
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改记事本设置失败";
                        _logger.Error("SetNotepadOpenInNewTab", ex);
                    }
                }
            }
        }

        public bool NotepadNewSession
        {
            get => _notepadNewSession;
            set
            {
                if (SetProperty(ref _notepadNewSession, value) && !_isLoadingTweaks)
                {
                    try
                    {
                        var restarted = _tweakService.SetNotepadNewSession(value);
                        StatusText = restarted
                            ? (value ? "记事本启动时将启动新会话并放弃未保存的更改，已重启记事本使设置生效。" : "记事本启动时将继续上一个会话，已重启记事本使设置生效。")
                            : (value ? "记事本已设置为启动新会话，下次打开记事本时生效。" : "记事本已设置为继续上一个会话，下次打开记事本时生效。");
                    }
                    catch (Exception ex)
                    {
                        StatusText = "修改记事本设置失败";
                        _logger.Error("SetNotepadNewSession", ex);
                    }
                }
            }
        }

        /// <summary>
        /// 打开文件方式：0 = 在新选项卡中打开；1 = 在新窗口中打开。
        /// 对应新记事本设置页的「打开文件」下拉项。
        /// </summary>
        public int NotepadOpenInNewTabIndex
        {
            get => _notepadOpenInNewTab ? 0 : 1;
            set
            {
                if (value < 0) return;
                NotepadOpenInNewTab = (value == 0);
                OnPropertyChanged(nameof(NotepadOpenInNewTabIndex));
            }
        }

        /// <summary>
        /// 记事本启动方式：0 = 启动新会话并放弃未保存的更改；1 = 继续上一个会话。
        /// 对应新记事本设置页的「记事本启动时」下拉项。
        /// </summary>
        public int NotepadNewSessionIndex
        {
            get => _notepadNewSession ? 0 : 1;
            set
            {
                if (value < 0) return;
                NotepadNewSession = (value == 0);
                OnPropertyChanged(nameof(NotepadNewSessionIndex));
            }
        }

        internal async Task LoadSystemTweaksAsync()
        {
            
            var token = System.Threading.Interlocked.Increment(ref _tweakLoadToken);

            int uac = 0;
            bool smart = true, center = false;
            int glom = 0;
            bool thisPc = true, recycle = true, net = true, cp = true, uf = true;
            bool ext = false, hidden = false, super = false;
            bool notepadTab = true, notepadNewSession = false;
            bool ok = false;

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        uac = (int)_tweakService.GetUacLevel();
                        smart = _tweakService.GetSmartScreenEnabled();
                        glom = _tweakService.GetTaskbarGlom();
                        center = _tweakService.GetStartAlign() == 1;
                        thisPc = _tweakService.GetDesktopIcon(DesktopIconGuids.ThisPC);
                        recycle = _tweakService.GetDesktopIcon(DesktopIconGuids.RecycleBin);
                        net = _tweakService.GetDesktopIcon(DesktopIconGuids.Network);
                        cp = _tweakService.GetDesktopIcon(DesktopIconGuids.ControlPanel);
                        uf = _tweakService.GetDesktopIcon(DesktopIconGuids.UserFolder);
                        ext = _tweakService.GetShowFileExt();
                        hidden = _tweakService.GetShowHidden();
                        super = _tweakService.GetShowSuperHidden();
                        notepadTab = _tweakService.GetNotepadOpenInNewTab();
                        notepadNewSession = _tweakService.GetNotepadNewSession();
                        ok = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("读取系统策略失败", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("加载系统策略失败", ex);
                return;
            }

            if (!ok || token != _tweakLoadToken) return; 

            _uiDispatcher.Invoke(() =>
            {
                if (token != _tweakLoadToken) return; 
                _isLoadingTweaks = true; 
                try
                {
                    
                    _uacLevelIndex = uac; OnPropertyChanged(nameof(UacLevelIndex));
                    _smartScreenEnabled = smart; OnPropertyChanged(nameof(SmartScreenEnabled));
                    _taskbarGlom = glom; OnPropertyChanged(nameof(TaskbarGlom));
                    _startMenuCenter = center; OnPropertyChanged(nameof(StartMenuCenter));
                    _desktopIconThisPC = thisPc; OnPropertyChanged(nameof(DesktopIconThisPC));
                    _desktopIconRecycleBin = recycle; OnPropertyChanged(nameof(DesktopIconRecycleBin));
                    _desktopIconNetwork = net; OnPropertyChanged(nameof(DesktopIconNetwork));
                    _desktopIconControlPanel = cp; OnPropertyChanged(nameof(DesktopIconControlPanel));
                    _desktopIconUserFolder = uf; OnPropertyChanged(nameof(DesktopIconUserFolder));
                    _showFileExt = ext; OnPropertyChanged(nameof(ShowFileExt));
                    _showHidden = hidden; OnPropertyChanged(nameof(ShowHidden));
                    _showSuperHidden = super; OnPropertyChanged(nameof(ShowSuperHidden));
                    _notepadOpenInNewTab = notepadTab; OnPropertyChanged(nameof(NotepadOpenInNewTab));
                    _notepadNewSession = notepadNewSession; OnPropertyChanged(nameof(NotepadNewSession));
                    OnPropertyChanged(nameof(NotepadOpenInNewTabIndex));
                    OnPropertyChanged(nameof(NotepadNewSessionIndex));
                }
                finally
                {
                    _isLoadingTweaks = false;
                }
            });
        }
    }
}
