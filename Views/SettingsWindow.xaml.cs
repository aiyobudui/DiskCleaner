using System.Windows;
using DiskCleaner.Models;
using DiskCleaner.Services;
using DiskCleaner.ViewModels;

namespace DiskCleaner.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly Config _config;
        private readonly IConfigService _configService;

        public SettingsViewModel ViewModel { get; }

        public SettingsWindow(Config config)
        {
            InitializeComponent();
            ThemeService.ApplyTo(this.Resources);
            _config = config;
            _configService = new ConfigService();
            ViewModel = new SettingsViewModel(config);
            DataContext = ViewModel;
        }

        private bool _saving;

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_saving) return;
            _saving = true;
            try
            {
                _config.IsAdvancedMode = ViewModel.IsAdvancedMode;
                _config.FastClean = ViewModel.IsFastClean;
                await _configService.SaveAsync(_config);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                // 保存失败时不要用 async void 把异常抛到 Dispatcher 导致崩溃，弹窗提示即可
                MessageBox.Show($"保存设置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _saving = false;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class SettingsViewModel : ViewModelBase
    {
        public SettingsViewModel(Config config)
        {
            IsAdvancedMode = config.IsAdvancedMode;
            IsFastClean = config.FastClean;
        }

        private bool _isAdvancedMode;
        public bool IsAdvancedMode { get => _isAdvancedMode; set => SetProperty(ref _isAdvancedMode, value); }

        private bool _isFastClean;
        public bool IsFastClean { get => _isFastClean; set => SetProperty(ref _isFastClean, value); }
    }
}
