using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskCleaner.ViewModels;
using DiskCleaner.Services;

namespace DiskCleaner.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;
            
            ThemeService.ApplyTo(this.Resources);
            
            _vm.ThemeToggled += (s, e) => ThemeService.ApplyTo(this.Resources);
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        
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
