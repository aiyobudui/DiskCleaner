using DiskCleaner.ViewModels;
using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace DiskCleaner.Models
{
    
    
    
    public enum ContextMenuCategory
    {
        File,
        Desktop,
        New
    }

    
    
    
    public enum ContextMenuItemType
    {
        
        ShellVerb,
        
        ShellExtension,
        
        ShellNew
    }

    
    
    
    public struct RegistryPathEntry
    {
        public string RegistryPath;
        public string KeyName;
        public string OriginalKeyName;
    }

    
    
    
    public class ContextMenuItem : ViewModelBase
    {
        private bool _isEnabled;

        
        public string Name { get; set; }

        
        
        
        public bool ShowBitnessSuffix { get; set; }

        
        public string DisplayName => ShowBitnessSuffix
            ? (IsWow64 ? $"{Name} x86" : $"{Name} x64")
            : Name;

        
        public string Command { get; set; }

        
        public string RegistryPath { get; set; }

        
        public string KeyName { get; set; }

        
        public string OriginalKeyName { get; set; }

        
        public ContextMenuCategory Category { get; set; }

        
        public ContextMenuItemType ItemType { get; set; }

        
        public string Scope { get; set; }

        
        public bool IsWin11TopLevel { get; set; }

        
        
        
        
        public bool IsWow64 { get; set; }

        
        public bool IsProtected { get; set; }

        
        
        
        
        public List<RegistryPathEntry> AllRegistryEntries { get; set; } = new List<RegistryPathEntry>();

        private ImageSource _icon;

        
        public ImageSource Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                OnPropertyChanged();
            }
        }

        
        
        
        
        public Func<ContextMenuItem, bool, bool> ToggleHandler { get; set; }

        
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;

                
                bool ok = ToggleHandler?.Invoke(this, value) ?? true;
                if (ok)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                }
                else
                {
                    
                    OnPropertyChanged();
                }
            }
        }

        
        public void SetInitialState(bool enabled)
        {
            _isEnabled = enabled;
            OnPropertyChanged(nameof(IsEnabled));
        }

        public bool CanToggle => !IsProtected;

        
        public bool CanDelete => !IsProtected;

        private bool _isDeleted;

        
        
        
        
        public bool IsDeleted
        {
            get => _isDeleted;
            set
            {
                if (_isDeleted == value) return;
                _isDeleted = value;
                OnPropertyChanged();
            }
        }

        public string ToolTipText
        {
            get
            {
                var cmd = string.IsNullOrWhiteSpace(Command) ? "（无）" : Command;
                return $"位置：{RegistryPath}\\{KeyName}\n命令：{cmd}\n范围：{Scope}";
            }
        }
    }
}
