using DiskCleaner.ViewModels;

namespace DiskCleaner.Models
{
    
    
    
    
    public class DriveCardInfo : ViewModelBase
    {
        private string _name;
        private string _volumeLabel;
        private string _typeText;
        private string _freeSpace;
        private string _totalSize;
        private string _usedSpace;
        private double _usedPercent;
        private string _path;

        
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        
        public string VolumeLabel
        {
            get => _volumeLabel;
            set
            {
                if (SetProperty(ref _volumeLabel, value))
                    OnPropertyChanged(nameof(DisplayLabel));
            }
        }

        
        public string DisplayLabel => string.IsNullOrWhiteSpace(VolumeLabel) ? "" : $"({VolumeLabel})";

        
        public string TypeText
        {
            get => _typeText;
            set => SetProperty(ref _typeText, value);
        }

        public string FreeSpace
        {
            get => _freeSpace;
            set => SetProperty(ref _freeSpace, value);
        }

        public string TotalSize
        {
            get => _totalSize;
            set => SetProperty(ref _totalSize, value);
        }

        public string UsedSpace
        {
            get => _usedSpace;
            set => SetProperty(ref _usedSpace, value);
        }

        
        public double UsedPercent
        {
            get => _usedPercent;
            set => SetProperty(ref _usedPercent, value);
        }

        
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }
    }
}
