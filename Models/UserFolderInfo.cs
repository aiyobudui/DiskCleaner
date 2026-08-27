using DiskCleaner.ViewModels;

namespace DiskCleaner.Models
{
    
    
    
    public class UserFolderInfo : ViewModelBase
    {
        public string Name { get; set; }
        public string Icon { get; set; }
        public Guid FolderId { get; set; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }

        private string _currentPath;
        public string CurrentPath
        {
            get => _currentPath;
            set => SetProperty(ref _currentPath, value);
        }

        private string _defaultPath;
        public string DefaultPath
        {
            get => _defaultPath;
            set => SetProperty(ref _defaultPath, value);
        }

        private string _newPath;
        public string NewPath
        {
            get => _newPath;
            set => SetProperty(ref _newPath, value);
        }

        private string _sizeText;
        public string SizeText
        {
            get => _sizeText;
            set => SetProperty(ref _sizeText, value);
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        
        public bool IsRedirected => !string.Equals(CurrentPath, DefaultPath, System.StringComparison.OrdinalIgnoreCase);
    }
}
