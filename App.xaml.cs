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
            // 部分启动方式不会触发 manifest 的 requireAdministrator，
            // 导致任务栏/搜索/小组件等受保护注册表键写入失败（UnauthorizedAccessException）。
            // 这里检测真实提权状态，未提权则用 runas 重新拉起自身。
            // 提权判定与 SystemOptimizeService.IsRunningAsAdministrator 共用同一实现（单一事实来源）。
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

            // 全局未处理异常：以弹窗暴露，避免静默崩溃
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

            // ── OS 版本检查: 仅支持 Windows 10 (build 10240+) 和 Windows 11 ──
            // manifest 声明了 Win10 supportedOS，Environment.OSVersion 在 Win10/11 上返回正确版本
            // Win11 版本号仍为 10.0.x (build >= 22000)，与 Win10 (build < 22000) 共用 Major=10
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
            // 退出时清理右键管理的删除备份：运行期间可撤销，退出后彻底不可恢复
            MainViewModel.PurgeDeletedBackupsOnExit();
            base.OnExit(e);
        }
    }
}
