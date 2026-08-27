using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskCleaner.ViewModels;
using DiskCleaner.Services;

namespace DiskCleaner.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private DispatcherTimer _titleBarDoubleClickTimer;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;
            // 应用已保存的主题（深色 / 浅色）
            ThemeService.ApplyTo(this.Resources);
            // 主题切换时刷新窗口资源
            _vm.ThemeToggled += (s, e) => ThemeService.ApplyTo(this.Resources);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            // 点在按钮（主题 / 最小化 / 关闭）上时不触发拖动
            var src = e.OriginalSource as DependencyObject;
            while (src != null)
            {
                if (src is Button) return;
                src = VisualTreeHelper.GetParent(src);
            }

            if (e.ClickCount == 2)
            {
                _titleBarDoubleClickTimer?.Stop();
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return;
            }

            // 单击先延迟，等待可能的第二次点击，避免第一次点击就把 DragMove 吃掉导致双击失效
            if (_titleBarDoubleClickTimer == null)
            {
                _titleBarDoubleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _titleBarDoubleClickTimer.Tick += (_, _) =>
                {
                    _titleBarDoubleClickTimer.Stop();
                    // 250ms 延迟后如果用户已松开左键，则不应调用 DragMove，否则抛
                    // "只能在按下主鼠标按钮时调用 DragMove" 异常。
                    if (Mouse.LeftButton != MouseButtonState.Pressed) return;
                    if (WindowState == WindowState.Maximized)
                    {
                        WindowState = WindowState.Normal;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (Mouse.LeftButton == MouseButtonState.Pressed) DragMove();
                        }));
                    }
                    else
                    {
                        DragMove();
                    }
                };
            }
            _titleBarDoubleClickTimer.Stop();
            _titleBarDoubleClickTimer.Start();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>关于页中的超链接：点击后用默认浏览器打开目标地址。</summary>
        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打开默认浏览器失败时不阻断 UI。
            }
            e.Handled = true;
        }

        private void GroupHeader_ToggleExpanded(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is DiskCleaner.Models.CleanGroup group)
            {
                group.IsExpanded = !group.IsExpanded;
                e.Handled = true;
            }
            else if (fe.DataContext is DiskCleaner.ViewModels.MainViewModel vm)
            {
                if (fe.Tag is string tag)
                {
                    switch (tag)
                    {
                        case "GarbageCleanup":
                            vm.IsGarbageCleanupExpanded = !vm.IsGarbageCleanupExpanded;
                            break;
                        case "SystemTools":
                            vm.IsSystemToolsExpanded = !vm.IsSystemToolsExpanded;
                            break;
                        case "EmptyFolder":
                            vm.IsEmptyFolderExpanded = !vm.IsEmptyFolderExpanded;
                            break;
                        case "FolderMigration":
                            vm.IsFolderMigrationExpanded = !vm.IsFolderMigrationExpanded;
                            break;
                        case "Hosts":
                            vm.IsHostsExpanded = !vm.IsHostsExpanded;
                            break;
                        case "Uac":
                            vm.IsUacExpanded = !vm.IsUacExpanded;
                            break;
                        case "Taskbar":
                            vm.IsTaskbarExpanded = !vm.IsTaskbarExpanded;
                            break;
                        case "DesktopIcons":
                            vm.IsDesktopIconsExpanded = !vm.IsDesktopIconsExpanded;
                            break;
                        case "Explorer":
                            vm.IsExplorerExpanded = !vm.IsExplorerExpanded;
                            break;
                        case "Notepad":
                            vm.IsNotepadExpanded = !vm.IsNotepadExpanded;
                            break;
                        default:
                            vm.IsSystemToolsExpanded = !vm.IsSystemToolsExpanded;
                            break;
                    }
                }
                else
                {
                    vm.IsSystemToolsExpanded = !vm.IsSystemToolsExpanded;
                }
                e.Handled = true;
            }
        }

    }
}
