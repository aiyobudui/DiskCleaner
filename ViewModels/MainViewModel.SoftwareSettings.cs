using DiskCleaner.Services;
using DiskCleaner.Views;
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace DiskCleaner.ViewModels
{
    
    
    
    
    
    public partial class MainViewModel : ViewModelBase
    {
        
        
        public string VersionText => $"九歌・系统清洁小工具 {VersionSuffix}";

        
        public string VersionSuffix
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    
                    return $"v{v.Major}.{v.Minor}.{v.Build}";
                }
                catch
                {
                    return "v0.9.1";
                }
            }
        }

        
        public ICommand OpenSettingsCommand { get; private set; }

        
        private void OpenSettings()
        {
            try
            {
                
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
