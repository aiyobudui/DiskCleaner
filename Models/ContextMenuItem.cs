using DiskCleaner.ViewModels;
using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 右键菜单项类别。
    /// </summary>
    public enum ContextMenuCategory
    {
        File,
        Desktop,
        New
    }

    /// <summary>
    /// 右键菜单项在注册表中的类型。
    /// </summary>
    public enum ContextMenuItemType
    {
        /// <summary>传统静态动词（shell\Name 子键）。</summary>
        ShellVerb,
        /// <summary>Shell 扩展处理器（shellex\ContextMenuHandlers\{GUID}）。</summary>
        ShellExtension,
        /// <summary>新建菜单项（.ext\ShellNew）。</summary>
        ShellNew
    }

    /// <summary>
    /// 右键菜单项的一个注册表路径条目（用于重复注册同一 CLSID 到多个分支的场景）。
    /// </summary>
    public struct RegistryPathEntry
    {
        public string RegistryPath;
        public string KeyName;
        public string OriginalKeyName;
    }

    /// <summary>
    /// 右键菜单管理：描述一个可被启用/禁用的右键菜单项。
    /// </summary>
    public class ContextMenuItem : ViewModelBase
    {
        private bool _isEnabled;

        /// <summary>显示名称。</summary>
        public string Name { get; set; }

        /// <summary>
        /// 是否显示位数后缀（仅当同名扩展同时存在 x64 和 x86 版本时为 true）。
        /// </summary>
        public bool ShowBitnessSuffix { get; set; }

        /// <summary>带后缀的显示名称，如 "WinRAR x86" / "WinRAR x64"，供 UI 直接绑定。</summary>
        public string DisplayName => ShowBitnessSuffix
            ? (IsWow64 ? $"{Name} x86" : $"{Name} x64")
            : Name;

        /// <summary>对应的命令路径或 CLSID。</summary>
        public string Command { get; set; }

        /// <summary>注册表父路径（不含 KeyName）。</summary>
        public string RegistryPath { get; set; }

        /// <summary>当前注册表键名（可能含禁用前缀）。</summary>
        public string KeyName { get; set; }

        /// <summary>原始键名（不含禁用前缀）。</summary>
        public string OriginalKeyName { get; set; }

        /// <summary>类别。</summary>
        public ContextMenuCategory Category { get; set; }

        /// <summary>注册表类型。</summary>
        public ContextMenuItemType ItemType { get; set; }

        /// <summary>作用域描述（如“所有文件”、“桌面背景”）。</summary>
        public string Scope { get; set; }

        /// <summary>是否为 Windows 11 现代右键菜单的一级项。</summary>
        public bool IsWin11TopLevel { get; set; }

        /// <summary>
        /// 是否注册在 WOW6432Node 下的 32 位兼容扩展。
        /// 为 true 时 UI 标题旁显示 "X86" 徽章，否则显示 "X64" 徽章。
        /// </summary>
        public bool IsWow64 { get; set; }

        /// <summary>是否为受保护项（不允许禁用）。</summary>
        public bool IsProtected { get; set; }

        /// <summary>
        /// 同一扩展/动词在多个注册表分支注册时（如 CLSID 同时在 *、Folder、Directory 下），
        /// 合并后保留所有注册表路径，用于统一切换/删除操作。默认包含主路径。
        /// </summary>
        public List<RegistryPathEntry> AllRegistryEntries { get; set; } = new List<RegistryPathEntry>();

        private ImageSource _icon;

        /// <summary>该项对应程序/文件类型的图标；无可用图标时回退为系统默认文件图标。</summary>
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

        /// <summary>
        /// 切换处理器：由 ViewModel 注入，负责把期望状态同步到注册表。
        /// 参数 (item, desiredEnabled)，返回是否成功。
        /// </summary>
        public Func<ContextMenuItem, bool, bool> ToggleHandler { get; set; }

        /// <summary>当前是否启用。</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;

                // 有处理器时走真实注册表切换；无处理器时直接接受（仅初始化阶段）。
                bool ok = ToggleHandler?.Invoke(this, value) ?? true;
                if (ok)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                }
                else
                {
                    // 操作失败，强制 UI 回滚到当前真实状态
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>仅在扫描加载时设置初始状态，不触发注册表操作。</summary>
        public void SetInitialState(bool enabled)
        {
            _isEnabled = enabled;
            OnPropertyChanged(nameof(IsEnabled));
        }

        public bool CanToggle => !IsProtected;

        /// <summary>是否允许永久删除（受保护项不允许）。</summary>
        public bool CanDelete => !IsProtected;

        private bool _isDeleted;

        /// <summary>
        /// 是否已被永久删除（注册表项已删、仅保留撤销备份）。
        /// 为 true 时，UI 上的「删除」按钮就地变为「撤销」，开关（启用/禁用）隐藏。
        /// </summary>
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
