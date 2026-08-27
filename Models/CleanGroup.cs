using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using DiskCleaner.Services;
using DiskCleaner.ViewModels;

namespace DiskCleaner.Models
{
    
    
    
    
    
    public class CleanGroup : ViewModelBase, ICleanNode
    {
        // 分组默认统一展开（在模型一处定义，所有 new CleanGroup 自动继承，无需逐个设置）
        private bool _isExpanded = true;
        private bool? _isChecked = true;
        private string _name;
        private long _totalSize;
        private bool _isUpdating;
        private bool _isVisible = true;

        public CleanGroup(string name)
        {
            _name = name;
            Children = new ObservableCollection<ICleanNode>();
            Children.CollectionChanged += (_, __) =>
            {
                if (_isUpdating) return;
                RefreshFromChildren();
            };
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public ObservableCollection<ICleanNode> Children { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                if (SetProperty(ref _isChecked, value))
                {
                    SyncChildren(value == true);
                    
                    RefreshFromChildren();
                    OnPropertyChanged(nameof(SizeText));
                }
            }
        }

        public bool IsEnabled => true;

        public bool IsVisible
        {
            get => _isVisible;
            private set => SetProperty(ref _isVisible, value);
        }

        public long TotalSize
        {
            get => _totalSize;
            private set
            {
                if (SetProperty(ref _totalSize, value))
                {
                    OnPropertyChanged(nameof(SizeText));
                    OnPropertyChanged(nameof(Size));
                }
            }
        }

        public long Size => TotalSize;

        public string SizeText => DiskUtils.FormatSize(_totalSize);

        
        public void AddRange(IEnumerable<ICleanNode> items)
        {
            _isUpdating = true;
            try
            {
                foreach (var item in items)
                {
                    item.PropertyChanged += Child_PropertyChanged;
                    Children.Add(item);
                }
            }
            finally
            {
                _isUpdating = false;
                RefreshFromChildren();
            }
        }

        public void RefreshFromChildren()
        {
            long size = 0;
            int checkedCount = 0;
            int enabledCount = 0;
            bool anyVisible = false;
            foreach (var child in Children)
            {
                if (child is CleanItem item)
                {
                    if (item.IsVisible)
                        anyVisible = true;
                }
                else if (child is CleanGroup group)
                {
                    if (group.IsVisible)
                        anyVisible = true;
                }

                
                if (child.IsEnabled)
                    size += child.Size;
                if (child.IsEnabled)
                {
                    enabledCount++;
                    if (child.IsChecked == true) checkedCount++;
                }
            }

            TotalSize = size;
            IsVisible = anyVisible;

            if (enabledCount == 0)
                _isChecked = false;
            else if (checkedCount == 0)
                _isChecked = false;
            else if (checkedCount == enabledCount)
                _isChecked = true;
            else
                _isChecked = null; 

            OnPropertyChanged(nameof(IsChecked));
        }

        private void SyncChildren(bool check)
        {
            _isUpdating = true;
            try
            {
                foreach (var child in Children)
                    if (child.IsEnabled)
                        child.IsChecked = check;
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void Child_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isUpdating) return;
            if (e.PropertyName == nameof(ICleanNode.IsChecked)
                || e.PropertyName == nameof(ICleanNode.Size)
                || e.PropertyName == nameof(CleanItem.IsVisible)
                || e.PropertyName == nameof(CleanGroup.IsVisible))
                RefreshFromChildren();
        }

    }
}
