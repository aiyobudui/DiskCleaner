using DiskCleaner.Models;
using DiskCleaner.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DiskCleaner.ViewModels
{
    
    
    
    
    public partial class MainViewModel : ViewModelBase
    {
        
        private ObservableCollection<DriveCardInfo> _driveCards = new();

        
        private string _systemVersion = "检测中...";
        private string _systemVersionDetail = "";
        private string _cpuModel = "检测中...";
        private string _cpuDetail = "";
        private string _memoryModel = "检测中...";
        private string _memoryDetail = "";
        private string _gpuModel = "检测中...";

        
        private double _cpuPercent;
        private double _ramPercent;
        private double _gpuPercent;
        private string _cpuUsageText = "0%";
        private string _ramUsageText = "0%";
        private string _gpuUsageText = "0%";
        private string _releaseStatusMessage = "";
        private CancellationTokenSource _releaseMessageCts;

        
        public ObservableCollection<DriveCardInfo> DriveCards
        {
            get => _driveCards;
            set => SetProperty(ref _driveCards, value);
        }

        public string SystemVersion { get => _systemVersion; set => SetProperty(ref _systemVersion, value); }
        public string SystemVersionDetail { get => _systemVersionDetail; set => SetProperty(ref _systemVersionDetail, value); }
        public string CpuModel { get => _cpuModel; set => SetProperty(ref _cpuModel, value); }
        public string CpuDetail { get => _cpuDetail; set => SetProperty(ref _cpuDetail, value); }
        public string MemoryModel { get => _memoryModel; set => SetProperty(ref _memoryModel, value); }
        public string MemoryDetail { get => _memoryDetail; set => SetProperty(ref _memoryDetail, value); }
        public string GpuModel { get => _gpuModel; set => SetProperty(ref _gpuModel, value); }

        public double CpuPercent { get => _cpuPercent; set => SetProperty(ref _cpuPercent, value); }
        public double RamPercent { get => _ramPercent; set => SetProperty(ref _ramPercent, value); }
        public double GpuPercent { get => _gpuPercent; set => SetProperty(ref _gpuPercent, value); }

        public string CpuUsageText { get => _cpuUsageText; set => SetProperty(ref _cpuUsageText, value); }
        public string RamUsageText { get => _ramUsageText; set => SetProperty(ref _ramUsageText, value); }
        public string GpuUsageText { get => _gpuUsageText; set => SetProperty(ref _gpuUsageText, value); }

        public string ReleaseStatusMessage { get => _releaseStatusMessage; set => SetProperty(ref _releaseStatusMessage, value); }

        
        public ICommand RefreshMonitorCommand { get; private set; }
        public ICommand FreeMemoryCommand { get; private set; }
        public ICommand OpenDriveCommand { get; private set; }

        
        internal async Task LoadDashboardAsync()
        {
            await Task.WhenAll(LoadSystemInfoAsync(), RefreshMonitorAsync());
            await LoadDriveCardsAsync();
        }

        private async Task LoadSystemInfoAsync()
        {
            try
            {
                var info = await Task.Run(() => _systemInfoService.Get());
                SystemVersion = info.SystemVersion;
                SystemVersionDetail = info.SystemVersionDetail;
                CpuModel = info.CpuModel;
                CpuDetail = info.CpuDetail;
                MemoryModel = info.MemoryModel;
                MemoryDetail = info.MemoryDetail;
                GpuModel = info.GpuModel;
            }
            catch (Exception ex)
            {
                _logger.Error("采集系统信息失败", ex);
            }
        }

        private async Task LoadDriveCardsAsync()
        {
            try
            {
                var cards = new ObservableCollection<DriveCardInfo>();
                await Task.Run(() =>
                {
                    foreach (var drive in DriveInfo.GetDrives()
                                 .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)))
                    {
                        try
                        {
                            var free = drive.AvailableFreeSpace;
                            var total = drive.TotalSize;
                            var used = total - free;
                            var percent = total == 0 ? 0 : (double)used / total * 100;
                            cards.Add(new DriveCardInfo
                            {
                                Name = drive.Name.TrimEnd('\\'),
                                VolumeLabel = drive.VolumeLabel,
                                TypeText = drive.DriveType == DriveType.Removable ? "可移动磁盘" : "本地磁盘",
                                FreeSpace = DiskUtils.FormatSize(free),
                                TotalSize = DiskUtils.FormatSize(total),
                                UsedSpace = DiskUtils.FormatSize(used),
                                UsedPercent = Math.Round(percent, 1),
                                Path = drive.RootDirectory.FullName
                            });
                        }
                        catch { }
                    }
                });
                DriveCards = cards;
            }
            catch (Exception ex)
            {
                _logger.Error("加载磁盘卡片失败", ex);
            }
        }

        private async Task RefreshMonitorAsync()
        {
            try
            {
                
                var fast = await _monitorService.GetCpuRamDiskAsync();
                CpuPercent = fast.cpu;
                RamPercent = fast.ram;
                CpuUsageText = $"{fast.cpu:0.0}%";
                RamUsageText = $"{fast.ram:0.0}%";

                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var g = await _monitorService.GetGpuUsageAsync();
                        _uiDispatcher.Invoke(() =>
                        {
                            GpuPercent = g;
                            GpuUsageText = $"{g:0.0}%";
                        });
                    }
                    catch {   }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("刷新性能监控失败", ex);
            }
        }

        private async Task FreeMemoryAsync()
        {
            try
            {
                // 取消上一次自动清空的计时，避免新的状态被旧定时器清掉
                _releaseMessageCts?.Cancel();
                _releaseMessageCts?.Dispose();
                _releaseMessageCts = new CancellationTokenSource();
                var cts = _releaseMessageCts;

                ReleaseStatusMessage = "正在整理系统内存...";
                var (message, _, _) = await Task.Run(() =>
                    SystemOptimizeService.OptimizeMemory(msg => _uiDispatcher.Invoke(() => ReleaseStatusMessage = msg)));
                ReleaseStatusMessage = message;
                await RefreshMonitorAsync();

                // 3 秒后自动清空提示
                try
                {
                    await Task.Delay(3000, cts.Token);
                    ReleaseStatusMessage = "";
                }
                catch (OperationCanceledException) { }
            }
            catch (Exception ex)
            {
                ReleaseStatusMessage = "整理系统内存失败: " + ex.Message;
                _logger.Error("整理系统内存失败", ex);
            }
        }

        private void OpenDrive(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                using var explorer = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"打开磁盘失败: {path}", ex);
            }
        }
    }
}
