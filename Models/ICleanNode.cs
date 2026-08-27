using System.ComponentModel;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 清理树节点的公共接口。既可以是叶子清理项（CleanItem），
    /// 也可以是可展开的分组（CleanGroup），支持嵌套分组。
    /// </summary>
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
