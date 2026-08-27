using DiskCleaner.Services;
using DiskCleaner.Views;
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace DiskCleaner.ViewModels
{
    /// <summary>
    /// 关于标签页：展示软件介绍与作者信息。
    /// 删除文件始终移入回收站（见 CleanService.UseRecycleBin 默认值）。
    /// 版本号统一调用：从程序集版本（csproj 的 &lt;Version&gt; 唯一真相源）动态读取，不硬编码。
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        // ---------- 属性 ----------
        /// <summary>完整版本显示文本，如 "DiskCleaner v0.8.0"。</summary>
        public string VersionText => $"九歌・系统清洁小工具 {VersionSuffix}";

        /// <summary>版本后缀，如 "v0.8.0"，供 XAML 标题栏/关于页 Binding 调用。</summary>
        public string VersionSuffix
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    // 取主版本.次版本.修订号（3 段），避免显示 build 号
                    return $"v{v.Major}.{v.Minor}.{v.Build}";
                }
                catch
                {
                    return "v0.9.0";
                }
            }
        }

        // ---------- 命令 ----------
        public ICommand OpenSettingsCommand { get; private set; }

        // ---------- 打开设置窗口（保留扩展入口） ----------
        private void OpenSettings()
        {
            try
            {
                // 配置是异步加载的，若尚未加载完成则给出提示，避免 NullReferenceException 静默失败
                if (_config == null)
                {
                    System.Windows.MessageBox.Show("设置尚未加载完成，请稍后再试。", "提示",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return;
                }
                var settingsWindow = new SettingsWindow(_config);
                settingsWindow.Owner = Application.Current.MainWindow;
                settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.Error("打开设置窗口失败", ex);
            }
        }
    }
}
