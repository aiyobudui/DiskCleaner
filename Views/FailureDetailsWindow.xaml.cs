using DiskCleaner.Models;
using DiskCleaner.Services;
using DiskCleaner.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace DiskCleaner.Views
{
    public partial class FailureDetailsWindow : Window
    {
        private CleanItem _item;
        // true = 由「查看失败详情」打开，只展示失败相关内容；false = 由「查看清理项」打开，只展示将清理的位置/文件
        private bool _isFailureMode;

        public FailureDetailsWindow(CleanItem item)
        {
            InitializeComponent();
            _item = item;

            // 主题：与本程序其它窗口一致，窗口独立持有主题画刷并随深/浅色切换实时更新
            ThemeService.ApplyTo(this.Resources);
            if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
            {
                vm.ThemeToggled += Vm_ThemeToggled;
                Closed += (s, e) => { vm.ThemeToggled -= Vm_ThemeToggled; };
            }

            Title = item?.Name ?? "清理详情";
            DescriptionText.Text = item?.Description ?? string.Empty;
            TargetPathsText.Text = item?.TargetPathsText ?? string.Empty;
            FailureText.Text = item?.FailureHint ?? string.Empty;

            _isFailureMode = item != null && !string.IsNullOrWhiteSpace(item.FailureHint);

            DescriptionPanel.Visibility = !string.IsNullOrWhiteSpace(item?.Description)
                ? Visibility.Visible : Visibility.Collapsed;

            if (_isFailureMode)
            {
                // 查看失败详情：只显示失败明细，不显示「将清理的位置/文件」（避免与清理项内容混淆）
                Title = $"清理失败详情（{item?.Name}）";
                TargetPathsPanel.Visibility = Visibility.Collapsed;
                TargetFilesPanel.Visibility = Visibility.Collapsed;
                FailurePanel.Visibility = !string.IsNullOrWhiteSpace(item?.FailureHint)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                // 查看清理项：显示将清理的位置与文件预览，不显示失败明细
                TargetPathsPanel.Visibility = item != null && item.HasTargetPaths
                    ? Visibility.Visible : Visibility.Collapsed;
                FailurePanel.Visibility = Visibility.Collapsed;
                // 枚举每个目标目录下的实际文件，作为「将清理项」预览（最多 500 项）
                PopulateTargetFiles(item);
            }
        }

        private void Vm_ThemeToggled(object sender, EventArgs e)
        {
            ThemeService.ApplyTo(this.Resources);
        }

        private void PopulateTargetFiles(CleanItem item)
        {
            const int maxFiles = 500;
            var filesSb = new StringBuilder();
            int fileCount = 0;

            if (item?.TargetPaths != null)
            {
                foreach (var dir in item.TargetPaths)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                        continue;
                    try
                    {
                        foreach (var f in SafeEnumerateFiles(dir))
                        {
                            if (fileCount >= maxFiles) break;
                            filesSb.AppendLine(f);
                            fileCount++;
                        }
                    }
                    catch { }
                    if (fileCount >= maxFiles) break;
                }
            }

            TargetFilesText.Text = filesSb.ToString();
            TargetFilesPanel.Visibility = fileCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 递归枚举目录内文件，逐目录捕获访问异常，避免单个不可访问目录中断整个预览
        private static IEnumerable<string> SafeEnumerateFiles(string root)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root); }
            catch { yield break; }

            foreach (var f in files)
                yield return f;

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); }
            catch { yield break; }

            foreach (var d in dirs)
            {
                foreach (var f in SafeEnumerateFiles(d))
                    yield return f;
            }
        }
    }
}
