using System.ComponentModel;

namespace DiskCleaner.Models
{
    
    
    
    
    public interface ICleanNode : INotifyPropertyChanged
    {
        string Name { get; set; }
        bool? IsChecked { get; set; }
        bool IsExpanded { get; set; }
        bool IsEnabled { get; }
        long Size { get; }
        string SizeText { get; }
    }
}
