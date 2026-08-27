using System;
using System.Diagnostics;
using System.Windows;
using DiskCleaner.Services;
using DiskCleaner.ViewModels;

namespace DiskCleaner
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            
            
            
            
            if (!SystemOptimizeService.IsRunningAsAdministrator())
            {
                try
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe,
                            UseShellExecute = true,
                            Verb = "runas"
                        });
                    }
                }
                catch { }
                Shutdown();
                return;
            }

            
            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show(
                    "程序发生未处理异常，已终止：\n\n" + ex.Exception,
                    "九歌・系统清洁小工具 崩溃",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ex.Handled = true;
                Shutdown();
            };

            base.OnStartup(e);

            
            
            
            var osVer = Environment.OSVersion;
            if (osVer.Version.Major < 10 || osVer.Version.Build < 10240)
            {
                MessageBox.Show(
                    $"本程序仅支持 Windows 10 和 Windows 11。\n\n" +
                    $"检测到系统版本: {osVer.Version.Major}.{osVer.Version.Minor}.{osVer.Version.Build}\n\n" +
                    $"请升级到 Windows 10 或 Windows 11 后使用。",
                    "系统版本不兼容",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            Logger.Initialize();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            
            MainViewModel.PurgeDeletedBackupsOnExit();
            base.OnExit(e);
        }
    }
}
