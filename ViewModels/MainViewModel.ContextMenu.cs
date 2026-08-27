using DiskCleaner.Interop;
using DiskCleaner.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskCleaner.ViewModels
{
    
    
    
    
    public partial class MainViewModel : ViewModelBase
    {
        
        private ObservableCollection<ContextMenuItem> _fileContextMenuItems = new();
        private ObservableCollection<ContextMenuItem> _desktopContextMenuItems = new();
        private ObservableCollection<ContextMenuItem> _newMenuItems = new();

        
        private static readonly string _contextMenuBackupDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleaner", "ContextMenuBackups");

        
        private bool _isContextMenuScanning;
        private string _contextMenuStatusText = "点击刷新扫描右键菜单";
        private int _selectedContextMenuTabIndex;

        
        public ObservableCollection<ContextMenuItem> FileContextMenuItems
        {
            get => _fileContextMenuItems;
            set => SetProperty(ref _fileContextMenuItems, value);
        }

        public ObservableCollection<ContextMenuItem> DesktopContextMenuItems
        {
            get => _desktopContextMenuItems;
            set => SetProperty(ref _desktopContextMenuItems, value);
        }

        public ObservableCollection<ContextMenuItem> NewMenuItems
        {
            get => _newMenuItems;
            set => SetProperty(ref _newMenuItems, value);
        }

        public bool IsContextMenuScanning
        {
            get => _isContextMenuScanning;
            set
            {
                if (SetProperty(ref _isContextMenuScanning, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ContextMenuStatusText
        {
            get => _contextMenuStatusText;
            set => SetProperty(ref _contextMenuStatusText, value);
        }

        public int SelectedContextMenuTabIndex
        {
            get => _selectedContextMenuTabIndex;
            set => SetProperty(ref _selectedContextMenuTabIndex, value);
        }

        private bool _showSystemItems;
        
        
        
        
        public bool ShowSystemItems
        {
            get => _showSystemItems;
            set
            {
                if (SetProperty(ref _showSystemItems, value))
                {
                    if (!IsContextMenuScanning)
                        _ = LoadContextMenuAsync();
                }
            }
        }

        
        public ICommand ScanContextMenuCommand { get; private set; }

        
        public ICommand DeleteContextMenuItemCommand { get; private set; }

        
        public ICommand RestoreContextMenuItemCommand { get; private set; }

        
        public ICommand RestartExplorerCommand { get; private set; }

        
        [Conditional("DEBUG")]
        private static void DebugLog(string message)
        {
            
        }

        
        
        
        
        
        
        
        
        private static IEnumerable<(string ClassPath, string Scope)> EnumerateFileRightClickRegistryClasses()
        {
            
            foreach (var (c, s) in ContextMenuMapping.GenericFileRightClickClasses)
                yield return (c, s);

            
            string[] extNames;
            try { extNames = Registry.ClassesRoot.GetSubKeyNames(); }
            catch { yield break; }

            foreach (var name in extNames)
            {
                if (!name.StartsWith(".", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = name;
                yield return (ext, $"扩展名 {ext}");

                
                
                var progIdPaths = new List<string>();
                try
                {
                    using var extKey = Registry.ClassesRoot.OpenSubKey(ext, false);
                    if (extKey != null)
                    {
                        foreach (var subName in extKey.GetSubKeyNames())
                        {
                            if (ContextMenuMapping.NonProgIdSubKeysOfExt.Contains(subName)) continue;
                            progIdPaths.Add($@"{ext}\{subName}");
                        }
                    }
                }
                catch { }

                foreach (var p in progIdPaths)
                    yield return (p, $"扩展名 {ext}");
            }
        }

        internal async Task LoadContextMenuAsync()
        {
            if (IsContextMenuScanning) return;
            IsContextMenuScanning = true;
            ContextMenuStatusText = "正在扫描右键菜单，请稍候...";

            try
            {
                await Task.Run(() =>
                {
                    var fileList = new List<ContextMenuItem>();
                    var desktopList = new List<ContextMenuItem>();
                    var newList = new List<ContextMenuItem>();
                    var diags = new StringBuilder();

                    
                    int verbsScanned = 0, verbsFiltered = 0, verbsAdded = 0, verbsErrored = 0;
                    int extsScanned = 0, extsFiltered = 0, extsAdded = 0, extsErrored = 0;
                    int newScanned = 0, newFiltered = 0, newAdded = 0, newErrored = 0;
                    var firstErrors = new List<string>();

                    
                    foreach (var (cls, scope) in EnumerateFileRightClickRegistryClasses())
                    {
                        ScanShellVerbs(cls, ContextMenuCategory.File, scope, fileList,
                            ref verbsScanned, ref verbsFiltered, ref verbsAdded, ref verbsErrored, firstErrors);
                        ScanShellExtensions(cls, ContextMenuCategory.File, scope, fileList,
                            ref extsScanned, ref extsFiltered, ref extsAdded, ref extsErrored, firstErrors);
                    }

                    
                    foreach (var (cls, scope) in ContextMenuMapping.GenericFileRightClickClasses)
                    {
                        
                        if (cls == @"Drive" || cls == @"AllFilesystemObjects") continue;
                        ScanShellExtensions(cls, ContextMenuCategory.File, scope, fileList,
                            ref extsScanned, ref extsFiltered, ref extsAdded, ref extsErrored, firstErrors, "DragDropHandlers");
                    }

                    
                    foreach (var (cls, scope) in ContextMenuMapping.GenericFileRightClickClasses)
                    {
                        ScanShellExtensionsWOW6432(cls, ContextMenuCategory.File, scope, fileList,
                            ref extsScanned, ref extsFiltered, ref extsAdded, ref extsErrored, firstErrors);
                    }

                    
                    foreach (var (cls, scope) in ContextMenuMapping.GenericDesktopRightClickClasses)
                    {
                        ScanShellVerbs(cls, ContextMenuCategory.Desktop, scope, desktopList,
                            ref verbsScanned, ref verbsFiltered, ref verbsAdded, ref verbsErrored, firstErrors);
                        ScanShellExtensions(cls, ContextMenuCategory.Desktop, scope, desktopList,
                            ref extsScanned, ref extsFiltered, ref extsAdded, ref extsErrored, firstErrors);
                    }

                    
                    foreach (var (cls, scope) in ContextMenuMapping.GenericDesktopRightClickClasses)
                    {
                        ScanShellExtensions(cls, ContextMenuCategory.Desktop, scope, desktopList,
                            ref extsScanned, ref extsFiltered, ref extsAdded, ref extsErrored, firstErrors, "DragDropHandlers");
                    }

                    
                    foreach (var (cls, scope) in ContextMenuMapping.GenericDesktopRightClickClasses)
                    {
                        ScanShellExtensionsWOW6432(cls, ContextMenuCategory.Desktop, scope, desktopList,
                            ref extsScanned, ref extsFiltered, ref extsAdded, ref extsErrored, firstErrors);
                    }

                    
                    foreach (var (cls, scope) in ContextMenuMapping.GenericDesktopRightClickClasses)
                    {
                        ScanShellExtensions(cls, ContextMenuCategory.Desktop, scope, desktopList,
                            ref extsScanned, ref extsFiltered, ref extsAdded, ref extsErrored, firstErrors, "PropertySheetHandlers");
                    }

                    
                    ScanNewMenu(newList, ref newScanned, ref newFiltered, ref newAdded, ref newErrored, firstErrors);

                    SortByName(fileList);
                    SortByName(desktopList);
                    SortByName(newList);

                    int fileBefore = fileList.Count;
                    int desktopBefore = desktopList.Count;
                    DeduplicateAndMerge(fileList);
                    DeduplicateAndMerge(desktopList);

                    diags.Append($"动词：扫描{verbsScanned}/过滤{verbsFiltered}/加入{verbsAdded}/异常{verbsErrored} | ");
                    diags.Append($"扩展：扫描{extsScanned}/过滤{extsFiltered}/加入{extsAdded}/异常{extsErrored} | ");
                    diags.Append($"新建：扫描{newScanned}/过滤{newFiltered}/加入{newAdded}/异常{newErrored} | ");
                    diags.Append($"去重前 file:{fileBefore} desk:{desktopBefore}");
                    if (firstErrors.Count > 0)
                    {
                        diags.Append(" | 前3异常: ");
                        for (int i = 0; i < Math.Min(3, firstErrors.Count); i++)
                        {
                            if (i > 0) diags.Append("; ");
                            diags.Append(firstErrors[i]);
                        }
                    }

                    DebugLog(diags.ToString());

                    _uiDispatcher.Invoke(() =>
                    {
                        DebugLog("UI Thread dispatching");

                        
                        
                        MarkDuplicateBitness(fileList);
                        MarkDuplicateBitness(desktopList);
                        MarkDuplicateBitness(newList);

                        FileContextMenuItems = new ObservableCollection<ContextMenuItem>(fileList);
                        DesktopContextMenuItems = new ObservableCollection<ContextMenuItem>(desktopList);
                        NewMenuItems = new ObservableCollection<ContextMenuItem>(newList);
                        RehydrateDeletedItems();

                        DebugLog($"Items created: File={FileContextMenuItems.Count} Desktop={DesktopContextMenuItems.Count} New={NewMenuItems.Count}");

                        foreach (var it in FileContextMenuItems) SetItemIcon(it);
                        foreach (var it in DesktopContextMenuItems) SetItemIcon(it);
                        foreach (var it in NewMenuItems) SetItemIcon(it);

                        DebugLog("Icons done");

                        
                        var totalItems = FileContextMenuItems.Count + DesktopContextMenuItems.Count + NewMenuItems.Count;
                        ContextMenuStatusText = $"扫描完成：共 {totalItems} 项（文件 {FileContextMenuItems.Count}、桌面 {DesktopContextMenuItems.Count}、新建 {NewMenuItems.Count}）";
                        DebugLog($"StatusText: {ContextMenuStatusText}");
                    });

                    DebugLog("Background thread done");
                });

                DebugLog("Await returned");
            }
            catch (Exception ex)
            {
                DebugLog($"OUTER EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                _logger?.Error("扫描右键菜单失败", ex);
                ContextMenuStatusText = "扫描失败：" + ex.GetType().Name + " - " + ex.Message;
            }
            finally
            {
                IsContextMenuScanning = false;
            }
        }

        
        private static void SortByName(List<ContextMenuItem> list)
            => list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        
        
        
        
        
        
        private static void DeduplicateAndMerge(List<ContextMenuItem> list)
        {
            if (list == null || list.Count <= 1) return;

            
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var indicesToRemove = new HashSet<int>();

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                string key;
                var cat = item.Category.ToString();
                
                
                var bitness = (item.RegistryPath != null && item.RegistryPath.StartsWith("WOW6432Node\\", StringComparison.OrdinalIgnoreCase))
                    ? "wow64" : "x64";
                switch (item.ItemType)
                {
                    case ContextMenuItemType.ShellExtension:
                        
                        key = $"ext|{cat}|{bitness}|" + (item.Command ?? item.OriginalKeyName ?? item.Name);
                        break;
                    case ContextMenuItemType.ShellVerb:
                        
                        var regBase = item.RegistryPath?.Replace(@"\shell", "");
                        key = $"verb|{cat}|{bitness}|{regBase}|{item.OriginalKeyName}";
                        break;
                    case ContextMenuItemType.ShellNew:
                        key = $"new|{cat}|{item.Command}";
                        break;
                    default:
                        key = $"other|{cat}|{item.Name}";
                        break;
                }

                if (seen.TryGetValue(key, out int firstIdx))
                {
                    
                    var first = list[firstIdx];
                    
                    var newScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in (first.Scope ?? "").Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
                        newScopes.Add(s.Trim());
                    foreach (var s in (item.Scope ?? "").Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
                        newScopes.Add(s.Trim());
                    first.Scope = string.Join("，", newScopes);

                    
                    foreach (var entry in item.AllRegistryEntries)
                    {
                        bool exists = false;
                        foreach (var existing in first.AllRegistryEntries)
                            if (existing.RegistryPath == entry.RegistryPath && existing.KeyName == entry.KeyName)
                            { exists = true; break; }
                        if (!exists) first.AllRegistryEntries.Add(entry);
                    }

                    
                    if (item.IsEnabled && !first.IsEnabled)
                        first.SetInitialState(true);

                    indicesToRemove.Add(i);
                }
                else
                {
                    seen[key] = i;
                }
            }

            
            if (indicesToRemove.Count > 0)
            {
                var sorted = indicesToRemove.OrderDescending().ToList();
                foreach (var idx in sorted)
                    list.RemoveAt(idx);
            }
        }

        
        
        
        
        
        private static void MarkDuplicateBitness(List<ContextMenuItem> list)
        {
            if (list == null || list.Count <= 1) return;

            
            var nameHasX64 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nameHasX86 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item.Name)) continue;
                if (item.IsWow64)
                    nameHasX86.Add(item.Name);
                else
                    nameHasX64.Add(item.Name);
            }

            
            var duplicateNames = nameHasX64.Intersect(nameHasX86, StringComparer.OrdinalIgnoreCase).ToList();
            if (duplicateNames.Count == 0) return;

            
            var duplicateSet = new HashSet<string>(duplicateNames, StringComparer.OrdinalIgnoreCase);
            foreach (var item in list)
            {
                if (duplicateSet.Contains(item.Name))
                    item.ShowBitnessSuffix = true;
            }
        }

        private void ScanShellVerbs(string classPath, ContextMenuCategory category, string scope, List<ContextMenuItem> target,
            ref int scanned, ref int filtered, ref int added, ref int errored, List<string> firstErrors)
        {
            try
            {
                using var shellKey = Registry.ClassesRoot.OpenSubKey($@"{classPath}\shell", false);
                if (shellKey == null) return;

                foreach (var name in shellKey.GetSubKeyNames())
                {
                    scanned++;
                    try
                    {
                        if (name.StartsWith("__DC_Disabled_")) { filtered++; continue; }
                        if (!ShowSystemItems && ContextMenuMapping.IsSystemContextMenuItem(name, null)) { filtered++; continue; }
                        using var sub = shellKey.OpenSubKey(name, false);
                        if (sub == null) continue;

                        var rawMuiVerb = NativeMethods.LoadIndirectString(sub.GetValue("MUIVerb") as string);
                        var rawDefault = NativeMethods.LoadIndirectString(sub.GetValue(null) as string);
                        var display = ContextMenuMapping.ResolveVerbName(name, rawMuiVerb, rawDefault);
                        var command = GetShellVerbCommand(sub);
                        bool enabled = sub.GetValue("LegacyDisable") == null;

                        var verbItem = new ContextMenuItem
                        {
                            Name = display,
                            Command = command,
                            RegistryPath = $@"{classPath}\shell",
                            KeyName = name,
                            OriginalKeyName = name,
                            Category = category,
                            ItemType = ContextMenuItemType.ShellVerb,
                            IsProtected = ContextMenuMapping.ProtectedVerbs.Contains(name),
                            Scope = scope
                        };
                        verbItem.AllRegistryEntries.Add(new RegistryPathEntry
                        {
                            RegistryPath = verbItem.RegistryPath,
                            KeyName = verbItem.KeyName,
                            OriginalKeyName = verbItem.OriginalKeyName
                        });
                        verbItem.SetInitialState(enabled);
                        verbItem.ToggleHandler = ContextMenuToggleHandler;
                        target.Add(verbItem);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        errored++;
                        if (firstErrors != null && firstErrors.Count < 3)
                            firstErrors.Add($"Verb[{name}]: {ex.GetType().Name} {ex.Message}");
                        DebugLog($"[ERR] Verb[{name}]: {ex.ToString()}");
                    }
                }
            }
            catch (Exception ex)
            {
                errored++;
                if (firstErrors != null && firstErrors.Count < 3)
                    firstErrors.Add($"ShellVerbs[{classPath}]: {ex.GetType().Name} {ex.Message}");
                DebugLog($"[ERR-Outer] ShellVerbs[{classPath}]: {ex.ToString()}");
            }
        }

        private static string GetShellVerbCommand(RegistryKey verbKey)
        {
            try
            {
                
                using var cmdKey = verbKey.OpenSubKey("command", false);
                if (cmdKey != null)
                    return cmdKey.GetValue(null) as string;

                
                using var explorerCmd = verbKey.OpenSubKey("ExplorerCommand", false);
                if (explorerCmd != null)
                {
                    var cmdId = explorerCmd.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(cmdId)) return cmdId;
                }

                
                var def = verbKey.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(def) && def.Contains(".exe"))
                    return def;
            }
            catch { }
            return null;
        }

        
        
        
        
        private static bool IsWow64PathOrKey(string regPath, string keyName)
        {
            if (!string.IsNullOrWhiteSpace(regPath) &&
                regPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrWhiteSpace(keyName)) return false;
            var lower = keyName.ToLowerInvariant();
            return lower.Contains("32") || lower.Contains("x86") || lower.Contains("wow64") || lower.Contains("32bit");
        }

        private void ScanShellExtensions(string classPath, ContextMenuCategory category, string scope, List<ContextMenuItem> target,
            ref int scanned, ref int filtered, ref int added, ref int errored, List<string> firstErrors, string handlerName = "ContextMenuHandlers")
        {
            try
            {
                using var handlersKey = Registry.ClassesRoot.OpenSubKey($@"{classPath}\shellex\{handlerName}", false);
                if (handlersKey == null) return;

                foreach (var name in handlersKey.GetSubKeyNames())
                {
                    scanned++;
                    try
                    {
                        bool enabled = !name.StartsWith("__DC_Disabled_");
                        string originalName = enabled ? name : name.Substring("__DC_Disabled_".Length);

                        string guid = null;
                        using (var sub = handlersKey.OpenSubKey(name, false))
                            guid = sub?.GetValue(null) as string;

                        if (!ShowSystemItems && ContextMenuMapping.IsSystemContextMenuItem(originalName, guid)) { filtered++; continue; }

                        if (string.IsNullOrWhiteSpace(guid)) guid = originalName;
                        string display = ContextMenuMapping.ResolveClsidName(guid, originalName) ?? originalName;
                        bool isWin11 = ContextMenuMapping.IsWin11TopLevelExtension(guid, originalName);

                        var regPath = $@"{classPath}\shellex\{handlerName}";
                        var extItem = new ContextMenuItem
                        {
                            Name = display,
                            Command = guid,
                            RegistryPath = regPath,
                            KeyName = name,
                            OriginalKeyName = originalName,
                            Category = category,
                            ItemType = ContextMenuItemType.ShellExtension,
                            Scope = scope,
                            IsWin11TopLevel = isWin11,
                            
                            IsWow64 = IsWow64PathOrKey(regPath, originalName)
                        };
                        extItem.AllRegistryEntries.Add(new RegistryPathEntry
                        {
                            RegistryPath = extItem.RegistryPath,
                            KeyName = extItem.KeyName,
                            OriginalKeyName = extItem.OriginalKeyName
                        });
                        extItem.SetInitialState(enabled);
                        extItem.ToggleHandler = ContextMenuToggleHandler;
                        target.Add(extItem);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        errored++;
                        if (firstErrors != null && firstErrors.Count < 3)
                            firstErrors.Add($"Ext[{classPath}/{name}]: {ex.GetType().Name} {ex.Message}");
                        DebugLog($"[ERR] Ext[{classPath}/{name}]: {ex.ToString()}");
                    }
                }
            }
            catch (Exception ex)
            {
                errored++;
                if (firstErrors != null && firstErrors.Count < 3)
                    firstErrors.Add($"ShellExt[{classPath}/{handlerName}]: {ex.GetType().Name} {ex.Message}");
                DebugLog($"[ERR-Outer] ShellExt[{classPath}/{handlerName}]: {ex.ToString()}");
            }
        }

        
        
        
        private void ScanShellExtensionsWOW6432(string classPath, ContextMenuCategory category, string scope, List<ContextMenuItem> target,
            ref int scanned, ref int filtered, ref int added, ref int errored, List<string> firstErrors)
        {
            try
            {
                using var handlersKey = Registry.ClassesRoot.OpenSubKey($@"WOW6432Node\{classPath}\shellex\ContextMenuHandlers", false);
                if (handlersKey == null) return;

                foreach (var name in handlersKey.GetSubKeyNames())
                {
                    scanned++;
                    try
                    {
                        bool enabled = !name.StartsWith("__DC_Disabled_");
                        string originalName = enabled ? name : name.Substring("__DC_Disabled_".Length);

                        string guid = null;
                        using (var sub = handlersKey.OpenSubKey(name, false))
                            guid = sub?.GetValue(null) as string;

                        if (!ShowSystemItems && ContextMenuMapping.IsSystemContextMenuItem(originalName, guid)) { filtered++; continue; }

                        if (string.IsNullOrWhiteSpace(guid)) guid = originalName;
                        string display = ContextMenuMapping.ResolveClsidName(guid, originalName) ?? originalName;
                        bool isWin11 = ContextMenuMapping.IsWin11TopLevelExtension(guid, originalName);

                        var regPath = $@"WOW6432Node\{classPath}\shellex\ContextMenuHandlers";
                        var extItem = new ContextMenuItem
                        {
                            Name = display,
                            Command = guid,
                            RegistryPath = regPath,
                            KeyName = name,
                            OriginalKeyName = originalName,
                            Category = category,
                            ItemType = ContextMenuItemType.ShellExtension,
                            Scope = scope,
                            IsWin11TopLevel = isWin11,
                            
                            IsWow64 = true
                        };
                        extItem.AllRegistryEntries.Add(new RegistryPathEntry
                        {
                            RegistryPath = extItem.RegistryPath,
                            KeyName = extItem.KeyName,
                            OriginalKeyName = extItem.OriginalKeyName
                        });
                        extItem.SetInitialState(enabled);
                        extItem.ToggleHandler = ContextMenuToggleHandler;
                        target.Add(extItem);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        errored++;
                        if (firstErrors != null && firstErrors.Count < 3)
                            firstErrors.Add($"Wow64[{name}]: {ex.GetType().Name} {ex.Message}");
                        DebugLog($"[ERR] Wow64[{name}]: {ex.ToString()}");
                    }
                }
            }
            catch (Exception ex)
            {
                errored++;
                if (firstErrors != null && firstErrors.Count < 3)
                    firstErrors.Add($"Wow64Root[{classPath}]: {ex.GetType().Name} {ex.Message}");
                DebugLog($"[ERR-Outer] Wow64Root[{classPath}]: {ex.ToString()}");
            }
        }





        
        
        
        
        
        
        private static System.Windows.Media.ImageSource ResolveExtensionIcon(string guid, string name = null, string keyName = null)
        {
            if (string.IsNullOrWhiteSpace(guid)) return ShellIconHelper.GetDefaultFileIcon();
            try
            {
                var g = ContextMenuMapping.NormalizeClsid(guid) ?? guid;
                using var clsid = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{g}}}", false)
                    ?? Registry.ClassesRoot.OpenSubKey($@"WOW6432Node\CLSID\{{{g}}}", false);
                if (clsid == null) return ShellIconHelper.GetDefaultFileIcon();

                
                using (var defIconKey = clsid.OpenSubKey("DefaultIcon", false))
                {
                    if (defIconKey != null)
                    {
                        var iconVal = defIconKey.GetValue(null) as string;
                        if (!string.IsNullOrWhiteSpace(iconVal))
                        {
                            var icon = ResolveIconValue(iconVal);
                            if (icon != null) return icon;
                        }
                    }
                }

                
                string dllPath = null;
                using (var server = clsid.OpenSubKey("InprocServer32", false) ?? clsid.OpenSubKey("LocalServer32", false))
                {
                    dllPath = server?.GetValue(null) as string;
                }

                if (!string.IsNullOrWhiteSpace(dllPath))
                {
                    dllPath = dllPath.Trim().Trim('"');
                    dllPath = Environment.ExpandEnvironmentVariables(dllPath);

                    var icon = ShellIconHelper.GetFileIcon(dllPath);
                    if (icon != null && icon != ShellIconHelper.GetDefaultFileIcon())
                        return icon;

                    
                    var siblingExe = FindSiblingExecutableIcon(dllPath, name, keyName);
                    if (siblingExe != null) return siblingExe;
                }

                return ShellIconHelper.GetDefaultFileIcon();
            }
            catch
            {
                return ShellIconHelper.GetDefaultFileIcon();
            }
        }

        
        
        
        
        
        
        private static System.Windows.Media.ImageSource FindSiblingExecutableIcon(string dllPath, string name, string keyName)
        {
            if (string.IsNullOrWhiteSpace(dllPath)) return null;
            try
            {
                dllPath = Environment.ExpandEnvironmentVariables(dllPath).Trim().Trim('"');
                if (!File.Exists(dllPath)) return null;

                
                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrWhiteSpace(winDir))
                {
                    var full = Path.GetFullPath(dllPath);
                    if (full.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                        return null;
                }

                var dir = Path.GetDirectoryName(dllPath);
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    return null;

                var exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                if (exes.Length == 0) return null;

                
                var hints = new[] { name, keyName }
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h.Replace(" ", "").Replace(".", "").Replace("ContextMenu", "").Replace("Ext", ""))
                    .Where(h => h.Length > 0)
                    .Select(h => h.ToLowerInvariant())
                    .Distinct()
                    .ToList();

                foreach (var hint in hints)
                {
                    foreach (var exe in exes)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                        if (fileName.Contains(hint) || hint.Contains(fileName))
                            return ShellIconHelper.GetFileIcon(exe);
                    }
                }

                
                var mainExe = exes
                    .Select(e => new { Path = e, Size = new FileInfo(e).Length })
                    .OrderByDescending(x => x.Size)
                    .FirstOrDefault();
                return mainExe != null ? ShellIconHelper.GetFileIcon(mainExe.Path) : null;
            }
            catch
            {
                return null;
            }
        }

        
        
        
        
        private static System.Windows.Media.ImageSource ResolveIconValue(string iconVal)
        {
            if (string.IsNullOrWhiteSpace(iconVal)) return null;
            try
            {
                iconVal = iconVal.Trim();

                
                if (iconVal.StartsWith("@", StringComparison.Ordinal))
                {
                    var path = iconVal.TrimStart('@');
                    var comma = path.IndexOf(',');
                    if (comma > 0)
                    {
                        var iconPath = path.Substring(0, comma).Trim().Trim('"');
                        var indexStr = path.Substring(comma + 1).Trim();
                        if (int.TryParse(indexStr, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var iconIndex))
                        {
                            iconPath = Environment.ExpandEnvironmentVariables(iconPath);
                            if (File.Exists(iconPath))
                                return ShellIconHelper.GetFileIcon(iconPath, iconIndex);
                        }
                    }
                    return null;
                }

                
                var parts = iconVal.Split(',');
                var filePath = parts[0].Trim().Trim('"');
                filePath = Environment.ExpandEnvironmentVariables(filePath);
                if (!File.Exists(filePath)) return null;

                if (parts.Length > 1 && int.TryParse(parts[1].Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var idx))
                {
                    return ShellIconHelper.GetFileIcon(filePath, idx);
                }

                return ShellIconHelper.GetFileIcon(filePath);
            }
            catch
            {
                return null;
            }
        }

        
        
        
        
        
        
        
        
        private static void SetItemIcon(ContextMenuItem item)
        {
            try
            {
                ImageSource icon = null;
                string iconSource = "default";

                switch (item.ItemType)
                {
                    case ContextMenuItemType.ShellVerb:
                        {
                            
                            
                            icon = ResolveVerbIcon(item.RegistryPath, item.KeyName);
                            iconSource = $"verb-icon:{item.RegistryPath}\\{item.KeyName}";

                            
                            if (icon == null)
                            {
                                var exePath = ShellIconHelper.ExtractExecutablePath(item.Command);
                                if (exePath != null)
                                {
                                    icon = ShellIconHelper.GetFileIcon(exePath);
                                    iconSource = File.Exists(exePath) ? exePath : $"missing:{exePath}";
                                }
                                else
                                {
                                    iconSource = "no-exe-path";
                                }
                            }
                            if (icon == null)
                            {
                                icon = ShellIconHelper.GetDefaultFileIcon();
                                iconSource += " → fallback";
                            }
                        }
                        break;
                    case ContextMenuItemType.ShellExtension:
                        icon = ResolveExtensionIcon(item.Command, item.Name, item.KeyName);
                        iconSource = $"clsid:{item.Command}";
                        if (icon == ShellIconHelper.GetDefaultFileIcon())
                            iconSource += " → fallback";
                        break;
                    case ContextMenuItemType.ShellNew:
                        icon = ShellIconHelper.GetFileTypeIcon(item.Command)
                                    ?? ShellIconHelper.GetDefaultFileIcon();
                        iconSource = $"ext:{item.Command}";
                        break;
                    default:
                        icon = ShellIconHelper.GetDefaultFileIcon();
                        break;
                }

                item.Icon = icon;
                DebugLog($"Icon[{item.Name}] type={item.ItemType} source={iconSource}");
            }
            catch (Exception ex)
            {
                DebugLog($"Icon[{item?.Name}] EXCEPTION: {ex.Message}");
                item.Icon = ShellIconHelper.GetDefaultFileIcon();
            }
        }

        
        
        
        
        
        private static ImageSource ResolveVerbIcon(string registryPath, string keyName)
        {
            if (string.IsNullOrWhiteSpace(registryPath) || string.IsNullOrWhiteSpace(keyName))
                return null;
            try
            {
                using var verbKey = Registry.ClassesRoot.OpenSubKey($@"{registryPath}\{keyName}", false);
                if (verbKey == null) return null;

                var iconVal = verbKey.GetValue("Icon") as string;
                return ResolveIconValue(iconVal);
            }
            catch
            {
                return null;
            }
        }


        
        
        
        
        
        
        private void RestartExplorerCore()
        {
            try
            {
                NativeMethods.SHChangeNotify(
                    NativeMethods.SHCNE_ASSOCCHANGED,
                    NativeMethods.SHCNF_IDLIST,
                    IntPtr.Zero,
                    IntPtr.Zero);
                _logger?.Info("已通过 SHChangeNotify 刷新 Shell（无需重启资源管理器）");
            }
            catch (Exception ex)
            {
                _logger?.Error("刷新 Shell 失败", ex);
            }
        }


        private void ScanNewMenu(List<ContextMenuItem> target,
            ref int scanned, ref int filtered, ref int added, ref int errored, List<string> firstErrors)
        {
            try
            {
                using var root = Registry.ClassesRoot;

                
                
                foreach (var keyName in ContextMenuMapping.EnumerateNewMenuCandidateKeys(root))
                {
                    scanned++;
                    try
                    {
                        if (!ShowSystemItems && ContextMenuMapping.IsSystemNewMenuItem(keyName)) { filtered++; continue; }
                        using var extKey = root.OpenSubKey(keyName, false);
                        if (extKey == null) continue;

                        
                        bool foundDirectEnabled = AddShellNewItem(root, keyName, keyName, false, target,
                            ref added, ref filtered, ShowSystemItems);
                        if (!foundDirectEnabled)
                            AddShellNewItem(root, keyName, keyName, false, target,
                                ref added, ref filtered, ShowSystemItems, checkDisabled: true);

                        
                        
                        
                        try
                        {
                            foreach (var subKeyName in extKey.GetSubKeyNames())
                            {
                                scanned++;
                                try
                                {
                                    using var subKey = extKey.OpenSubKey(subKeyName, false);
                                    if (subKey == null) continue;

                                    
                                    if (ContextMenuMapping.NonProgIdSubKeysOfExt.Contains(subKeyName))
                                        continue;

                                    
                                    bool foundDirect = AddShellNewItem(root, keyName, $"{keyName}\\{subKeyName}", true, target,
                                        ref added, ref filtered, ShowSystemItems);

                                    
                                    if (!foundDirect)
                                        AddShellNewItem(root, keyName, $"{keyName}\\{subKeyName}", true, target,
                                            ref added, ref filtered, ShowSystemItems, checkDisabled: true);
                                }
                                catch (Exception ex)
                                {
                                    errored++;
                                    if (firstErrors != null && firstErrors.Count < 3)
                                        firstErrors.Add($"NewNested[{keyName}/{subKeyName}]: {ex.GetType().Name} {ex.Message}");
                                }
                            }
                        }
                        catch {   }
                    }
                    catch (Exception ex)
                    {
                        errored++;
                        if (firstErrors != null && firstErrors.Count < 3)
                            firstErrors.Add($"New[{keyName}]: {ex.GetType().Name} {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                errored++;
                if (firstErrors != null && firstErrors.Count < 3)
                    firstErrors.Add($"NewMenuRoot: {ex.GetType().Name} {ex.Message}");
            }
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        private bool AddShellNewItem(RegistryKey root, string commandKey, string registryPath,
            bool isNested, List<ContextMenuItem> target,
            ref int added, ref int filtered, bool showSystemItems,
            bool checkDisabled = false)
        {
            try
            {
                using var parentKey = root.OpenSubKey(registryPath, false);
                if (parentKey == null) return false;

                string sourceKeyName = checkDisabled ? "__DC_Disabled_ShellNew" : "ShellNew";
                using var shellNew = parentKey.OpenSubKey(sourceKeyName, false);
                if (shellNew == null) return false;

                
                if (!showSystemItems && ContextMenuMapping.IsSystemNewMenuItem(commandKey))
                {
                    filtered++;
                    return false;
                }

                string display = ContextMenuMapping.ResolveNewMenuItemDisplayName(
                    commandKey, isNested ? registryPath : null);

                var item = new ContextMenuItem
                {
                    Name = display,
                    Command = commandKey,
                    RegistryPath = registryPath,
                    KeyName = sourceKeyName,
                    OriginalKeyName = "ShellNew",
                    Category = ContextMenuCategory.New,
                    ItemType = ContextMenuItemType.ShellNew,
                    Scope = "新建菜单"
                };
                item.AllRegistryEntries.Add(new RegistryPathEntry
                {
                    RegistryPath = registryPath,
                    KeyName = sourceKeyName,
                    OriginalKeyName = "ShellNew"
                });
                item.SetInitialState(!checkDisabled);
                item.ToggleHandler = ContextMenuToggleHandler;
                target.Add(item);
                added++;
                return true;
            }
            catch
            {
                return false;
            }
        }

        
        
        
        
        private bool ContextMenuToggleHandler(ContextMenuItem item, bool desired)
        {
            if (item == null || item.IsProtected) return false;
            try
            {
                ToggleContextMenuItemCore(item, desired);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error($"切换右键菜单项失败: {item.Name}", ex);
                return false;
            }
        }

        private static void ToggleContextMenuItemCore(ContextMenuItem item, bool desired)
        {
            switch (item.ItemType)
            {
                case ContextMenuItemType.ShellVerb:
                    ToggleShellVerb(item, desired);
                    break;
                case ContextMenuItemType.ShellExtension:
                    ToggleShellExtension(item, desired);
                    break;
                case ContextMenuItemType.ShellNew:
                    ToggleShellNew(item, desired);
                    break;
            }
        }

        private static void ToggleShellVerb(ContextMenuItem item, bool desired)
        {
            if (item.AllRegistryEntries == null || item.AllRegistryEntries.Count == 0) return;

            Exception firstEx = null;
            int succeeded = 0;
            foreach (var entry in item.AllRegistryEntries)
            {
                try
                {
                    using var key = Registry.ClassesRoot.OpenSubKey($@"{entry.RegistryPath}\{entry.KeyName}", true);
                    if (key == null) continue;
                    if (desired) key.DeleteValue("LegacyDisable", false);
                    else key.SetValue("LegacyDisable", "");
                    succeeded++;
                }
                catch (Exception ex) { firstEx ??= ex; }
            }
            if (succeeded == 0) throw firstEx ?? new InvalidOperationException("未找到要修改的注册表项。");

            
            if (item.AllRegistryEntries.Count > 0)
            {
                item.RegistryPath = item.AllRegistryEntries[0].RegistryPath;
                item.KeyName = item.AllRegistryEntries[0].KeyName;
            }
        }

        private static void ToggleShellExtension(ContextMenuItem item, bool desired)
        {
            if (item.AllRegistryEntries == null || item.AllRegistryEntries.Count == 0) return;

            Exception firstEx = null;
            int succeeded = 0;
            for (int idx = 0; idx < item.AllRegistryEntries.Count; idx++)
            {
                var entry = item.AllRegistryEntries[idx];
                try
                {
                    using var parent = Registry.ClassesRoot.OpenSubKey(entry.RegistryPath, true);
                    if (parent == null) continue;

                    string sourceName = entry.KeyName;
                    string targetName = desired ? entry.OriginalKeyName : $"__DC_Disabled_{entry.OriginalKeyName}";
                    if (sourceName == targetName) { succeeded++; continue; }

                    parent.CreateSubKey(targetName);
                    using (var oldKey = parent.OpenSubKey(sourceName, false))
                    using (var newKey = parent.OpenSubKey(targetName, true))
                    {
                        if (oldKey != null && newKey != null)
                        {
                            foreach (var valueName in oldKey.GetValueNames())
                            {
                                try { newKey.SetValue(valueName, oldKey.GetValue(valueName)); }
                                catch { }
                            }
                        }
                    }
                    parent.DeleteSubKeyTree(sourceName, false);
                    succeeded++;

                    
                    entry.KeyName = targetName;
                    item.AllRegistryEntries[idx] = entry;
                }
                catch (Exception ex) { firstEx ??= ex; }
            }
            if (succeeded == 0) throw firstEx ?? new InvalidOperationException("未找到要修改的注册表项。");

            
            if (item.AllRegistryEntries.Count > 0)
            {
                item.RegistryPath = item.AllRegistryEntries[0].RegistryPath;
                item.KeyName = item.AllRegistryEntries[0].KeyName;
            }
        }

        private static void ToggleShellNew(ContextMenuItem item, bool desired)
        {
            if (item.AllRegistryEntries == null || item.AllRegistryEntries.Count == 0) return;

            Exception firstEx = null;
            int succeeded = 0;
            for (int idx = 0; idx < item.AllRegistryEntries.Count; idx++)
            {
                var entry = item.AllRegistryEntries[idx];
                try
                {
                    using var parent = Registry.ClassesRoot.OpenSubKey(entry.RegistryPath, true);
                    if (parent == null) continue;

                    string sourceName = entry.KeyName;
                    string targetName = desired ? "ShellNew" : "__DC_Disabled_ShellNew";
                    if (sourceName == targetName) { succeeded++; continue; }

                    parent.CreateSubKey(targetName);
                    using (var oldKey = parent.OpenSubKey(sourceName, false))
                    using (var newKey = parent.OpenSubKey(targetName, true))
                    {
                        if (oldKey != null && newKey != null)
                        {
                            foreach (var valueName in oldKey.GetValueNames())
                            {
                                try { newKey.SetValue(valueName, oldKey.GetValue(valueName)); }
                                catch { }
                            }
                        }
                    }
                    parent.DeleteSubKeyTree(sourceName, false);
                    succeeded++;

                    entry.KeyName = targetName;
                    item.AllRegistryEntries[idx] = entry;
                }
                catch (Exception ex) { firstEx ??= ex; }
            }
            if (succeeded == 0) throw firstEx ?? new InvalidOperationException("未找到要修改的注册表项。");

            if (item.AllRegistryEntries.Count > 0)
            {
                item.RegistryPath = item.AllRegistryEntries[0].RegistryPath;
                item.KeyName = item.AllRegistryEntries[0].KeyName;
            }
        }

        
        
        
        
        
        
        private void DeleteContextMenuItemCore(ContextMenuItem item)
        {
            if (item == null || !item.CanDelete || item.IsDeleted) return;
            try
            {
                Directory.CreateDirectory(_contextMenuBackupDir);
                var entries = item.AllRegistryEntries != null && item.AllRegistryEntries.Count > 0
                    ? item.AllRegistryEntries
                    : new List<RegistryPathEntry> { new RegistryPathEntry { RegistryPath = item.RegistryPath, KeyName = item.KeyName, OriginalKeyName = item.OriginalKeyName } };

                var backupFiles = new List<string>();
                int deletedCount = 0;

                foreach (var entry in entries)
                {
                    string fullKey = $@"HKCR\{entry.RegistryPath}\{entry.KeyName}";
                    string backupFile = null;

                    
                    try
                    {
                        var safe = string.Concat((item.Name + "_" + entry.KeyName + "_" + entry.RegistryPath).Split(Path.GetInvalidFileNameChars()));
                        backupFile = Path.Combine(_contextMenuBackupDir, $"{safe}_{DateTime.Now:yyyyMMddHHmmssfff}.reg");
                        RegExport(fullKey, backupFile);
                        backupFiles.Add(backupFile);
                    }
                    catch (Exception ex) { _logger?.Error($"备份右键项失败: {item.Name}", ex); }

                    
                    bool deletedThisEntry = false;
                    try
                    {
                        using var parent = Registry.ClassesRoot.OpenSubKey(entry.RegistryPath, true);
                        if (parent != null)
                        {
                            parent.DeleteSubKeyTree(entry.KeyName, false);
                            deletedCount++;
                            deletedThisEntry = true;
                        }
                    }
                    catch (Exception ex) { _logger?.Error($"删除注册表项失败: {fullKey}", ex); }

                    
                    if (deletedThisEntry && backupFile != null)
                    {
                        PersistDeletedEntry(new DeletedManifestEntry
                        {
                            Name = item.Name,
                            Scope = item.Scope,
                            RegistryPath = entry.RegistryPath,
                            KeyName = entry.KeyName,
                            Category = (int)item.Category,
                            BackupFile = backupFile
                        });
                    }
                }

                
                if (deletedCount == 0)
                {
                    _logger?.Error($"永久删除右键项失败（未删除任何条目）: {item.Name}");
                    return;
                }
                _uiDispatcher.Invoke(() => item.IsDeleted = true);
                _logger?.Info($"已永久删除右键项: {item.Name} ({deletedCount}/{entries.Count} 个注册表路径，备份: {string.Join(", ", backupFiles)})");
            }
            catch (Exception ex)
            {
                _logger?.Error($"永久删除右键项失败: {item?.Name}", ex);
            }
        }

        
        
        
        
        private void RestoreContextMenuItemCore(ContextMenuItem item)
        {
            if (item == null || !item.IsDeleted) return;
            try
            {
                var entries = item.AllRegistryEntries != null && item.AllRegistryEntries.Count > 0
                    ? item.AllRegistryEntries
                    : new List<RegistryPathEntry> { new RegistryPathEntry { RegistryPath = item.RegistryPath, KeyName = item.KeyName, OriginalKeyName = item.OriginalKeyName } };

                int restoredCount = 0;
                foreach (var entry in entries)
                {
                    var manifestEntry = FindDeletedEntry(entry.RegistryPath, entry.KeyName, (int)item.Category);
                    if (manifestEntry != null && !string.IsNullOrWhiteSpace(manifestEntry.BackupFile) && File.Exists(manifestEntry.BackupFile))
                    {
                        try
                        {
                            RegImport(manifestEntry.BackupFile);
                            try { File.Delete(manifestEntry.BackupFile); } catch { }
                            restoredCount++;
                        }
                        catch (Exception ex) { _logger?.Error($"恢复注册表项失败: {entry.RegistryPath}\\{entry.KeyName}", ex); }
                    }
                    RemoveDeletedEntry(entry.RegistryPath, entry.KeyName, (int)item.Category);
                }

                _uiDispatcher.Invoke(() => item.IsDeleted = false);
                
                
                _logger?.Info($"已撤销删除右键项: {item.Name} ({restoredCount}/{entries.Count} 个注册表路径已恢复)");
            }
            catch (Exception ex)
            {
                _logger?.Error($"撤销删除右键项失败: {item?.Name}", ex);
            }
        }

        
        
        
        
        
        private void RehydrateDeletedItems()
        {
            var entries = LoadDeletedManifest();
            if (entries.Count == 0) return;
            var stale = new List<DeletedManifestEntry>();
            foreach (var e in entries)
            {
                
                if (RegistryKeyExists(e.RegistryPath, e.KeyName))
                {
                    stale.Add(e);
                    continue;
                }
                
                if (FindLiveItem(e.RegistryPath, e.KeyName, (ContextMenuCategory)e.Category) != null)
                    continue;

                
                ContextMenuItemType inferredType;
                if (e.KeyName == "ShellNew" || e.KeyName == "__DC_Disabled_ShellNew")
                    inferredType = ContextMenuItemType.ShellNew;
                else if (e.RegistryPath.IndexOf("shellex\\ContextMenuHandlers", StringComparison.OrdinalIgnoreCase) >= 0)
                    inferredType = ContextMenuItemType.ShellExtension;
                else
                    inferredType = ContextMenuItemType.ShellVerb;

                
                string originalName = inferredType == ContextMenuItemType.ShellNew
                    ? "ShellNew"
                    : (e.KeyName.StartsWith("__DC_Disabled_") ? e.KeyName.Substring("__DC_Disabled_".Length) : e.KeyName);

                var item = new ContextMenuItem
                {
                    Name = e.Name,
                    Scope = e.Scope,
                    RegistryPath = e.RegistryPath,
                    KeyName = e.KeyName,
                    OriginalKeyName = originalName,
                    Category = (ContextMenuCategory)e.Category,
                    ItemType = inferredType,
                    IsProtected = false,
                    IsDeleted = true
                };
                item.AllRegistryEntries.Add(new RegistryPathEntry
                {
                    RegistryPath = e.RegistryPath,
                    KeyName = e.KeyName,
                    OriginalKeyName = originalName
                });
                item.Icon = ShellIconHelper.GetDefaultFileIcon();
                item.SetInitialState(true);
                item.ToggleHandler = ContextMenuToggleHandler;
                switch (item.Category)
                {
                    case ContextMenuCategory.File: FileContextMenuItems.Add(item); break;
                    case ContextMenuCategory.Desktop: DesktopContextMenuItems.Add(item); break;
                    case ContextMenuCategory.New: NewMenuItems.Add(item); break;
                }
            }
            if (stale.Count > 0)
            {
                foreach (var s in stale) entries.Remove(s);
                SaveDeletedManifest(entries);
            }
        }

        
        
        
        
        public static void PurgeDeletedBackupsOnExit()
        {
            try
            {
                if (Directory.Exists(_contextMenuBackupDir))
                    Directory.Delete(_contextMenuBackupDir, recursive: true);
            }
            catch { }
        }

        
        private static readonly object _manifestLock = new();
        private static readonly string _deletedManifestPath =
            Path.Combine(_contextMenuBackupDir, "deleted-manifest.json");

        private class DeletedManifestEntry
        {
            public string Name { get; set; }
            public string Scope { get; set; }
            public string RegistryPath { get; set; }
            public string KeyName { get; set; }
            public int Category { get; set; }
            public string BackupFile { get; set; }
        }

        private static List<DeletedManifestEntry> LoadDeletedManifest()
        {
            lock (_manifestLock)
            {
                try
                {
                    if (File.Exists(_deletedManifestPath))
                    {
                        var json = File.ReadAllText(_deletedManifestPath);
                        var list = JsonSerializer.Deserialize<List<DeletedManifestEntry>>(json);
                        if (list != null) return list;
                    }
                }
                catch { }
                return new List<DeletedManifestEntry>();
            }
        }

        private static void SaveDeletedManifest(List<DeletedManifestEntry> entries)
        {
            lock (_manifestLock)
            {
                try
                {
                    Directory.CreateDirectory(_contextMenuBackupDir);
                    var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_deletedManifestPath, json);
                }
                catch { }
            }
        }

        private static void PersistDeletedEntry(DeletedManifestEntry entry)
        {
            lock (_manifestLock)
            {
                var list = LoadDeletedManifest();
                var existing = list.FirstOrDefault(e =>
                    e.RegistryPath == entry.RegistryPath && e.KeyName == entry.KeyName && e.Category == entry.Category);
                if (existing != null)
                {
                    existing.Name = entry.Name;
                    existing.Scope = entry.Scope;
                    existing.BackupFile = entry.BackupFile;
                }
                else
                {
                    list.Add(entry);
                }
                SaveDeletedManifest(list);
            }
        }

        private static DeletedManifestEntry FindDeletedEntry(string registryPath, string keyName, int category)
            => LoadDeletedManifest().FirstOrDefault(e =>
                e.RegistryPath == registryPath && e.KeyName == keyName && e.Category == category);

        private static void RemoveDeletedEntry(string registryPath, string keyName, int category)
        {
            lock (_manifestLock)
            {
                var list = LoadDeletedManifest();
                var target = list.FirstOrDefault(e =>
                    e.RegistryPath == registryPath && e.KeyName == keyName && e.Category == category);
                if (target != null)
                {
                    list.Remove(target);
                    SaveDeletedManifest(list);
                }
            }
        }

        private ContextMenuItem FindLiveItem(string registryPath, string keyName, ContextMenuCategory category)
        {
            IEnumerable<ContextMenuItem> list = category switch
            {
                ContextMenuCategory.File => FileContextMenuItems,
                ContextMenuCategory.Desktop => DesktopContextMenuItems,
                _ => NewMenuItems
            };
            return list.FirstOrDefault(i => i.RegistryPath == registryPath && i.KeyName == keyName);
        }

        private static bool RegistryKeyExists(string registryPath, string keyName)
        {
            try
            {
                using var parent = Registry.ClassesRoot.OpenSubKey(registryPath, false);
                return parent?.OpenSubKey(keyName, false) != null;
            }
            catch { return false; }
        }

        
        private static bool RegExport(string keyPath, string file)
        {
            try
            {
                if (!TryParseRegRoot(keyPath, out RegistryKey root, out string subPath))
                    return false;
                using var key = root.OpenSubKey(subPath);
                if (key == null) return false;
                var sb = new StringBuilder();
                sb.AppendLine("Windows Registry Editor Version 5.00");
                sb.AppendLine();
                ExportRegKey(key, GetRegHiveName(root) + (subPath.Length > 0 ? "\\" + subPath : ""), sb);
                File.WriteAllText(file, sb.ToString(), Encoding.Unicode); 
                return true;
            }
            catch { return false; }
        }

        
        private static bool RegImport(string file)
        {
            try
            {
                if (!File.Exists(file)) return false;
                byte[] bytes = File.ReadAllBytes(file);
                string text;
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                    text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                else
                    text = Encoding.Default.GetString(bytes);

                
                var rawLines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var lines = new List<string>();
                string pending = null;
                foreach (var rl in rawLines)
                {
                    string l = rl.TrimEnd();
                    if (pending == null)
                    {
                        if (l.EndsWith("\\")) pending = l.Substring(0, l.Length - 1);
                        else lines.Add(l);
                    }
                    else
                    {
                        if (l.EndsWith("\\")) pending += l.Substring(0, l.Length - 1);
                        else { pending += l; lines.Add(pending); pending = null; }
                    }
                }
                if (pending != null) lines.Add(pending);

                RegistryKey currentKey = null;
                void CloseKey() { if (currentKey != null) { currentKey.Close(); currentKey = null; } }
                foreach (var line in lines)
                {
                    if (line.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        CloseKey();
                        string kp = line.Substring(1, line.Length - 2).Trim();
                        bool delete = false;
                        if (kp.StartsWith("-")) { delete = true; kp = kp.Substring(1).Trim(); }
                        if (delete) { TryDeleteRegKey(kp); continue; }
                        TryOpenOrCreateRegKey(kp, out currentKey);
                    }
                    else if (currentKey != null)
                    {
                        ParseRegValueLine(currentKey, line);
                    }
                }
                CloseKey();
                return true;
            }
            catch { return false; }
        }

        #region .reg 序列化辅助（替代 reg.exe 的托管实现）

        private static string GetRegHiveName(RegistryKey root) => root.Name;

        private static bool TryParseRegRoot(string keyPath, out RegistryKey root, out string subPath)
        {
            root = null; subPath = "";
            var parts = keyPath.Split(new[] { '\\' }, 2);
            switch (parts[0].ToUpperInvariant())
            {
                case "HKCR": case "HKEY_CLASSES_ROOT": root = Registry.ClassesRoot; break;
                case "HKCU": case "HKEY_CURRENT_USER": root = Registry.CurrentUser; break;
                case "HKLM": case "HKEY_LOCAL_MACHINE": root = Registry.LocalMachine; break;
                case "HKU": case "HKEY_USERS": root = Registry.Users; break;
                case "HKCC": case "HKEY_CURRENT_CONFIG": root = Registry.CurrentConfig; break;
                default: return false;
            }
            subPath = parts.Length > 1 ? parts[1] : "";
            return true;
        }

        private static void ExportRegKey(RegistryKey key, string fullPath, StringBuilder sb)
        {
            sb.AppendLine("[" + fullPath + "]");
            foreach (var name in key.GetValueNames())
            {
                var kind = key.GetValueKind(name);
                var val = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                WriteRegValue(sb, name, kind, val);
            }
            foreach (var sub in key.GetSubKeyNames())
            {
                using var sk = key.OpenSubKey(sub);
                if (sk != null) ExportRegKey(sk, fullPath + "\\" + sub, sb);
            }
        }

        private static void WriteRegValue(StringBuilder sb, string name, RegistryValueKind kind, object val)
        {
            string namePart = name.Length == 0 ? "@" : "\"" + EscapeRegString(name) + "\"";
            switch (kind)
            {
                case RegistryValueKind.String:
                    sb.AppendLine($"{namePart}=\"{EscapeRegString((string)val)}\"");
                    break;
                case RegistryValueKind.ExpandString:
                    sb.AppendLine($"{namePart}=hex(2):{RegBytesToHex(WithNullTerm(Encoding.Unicode.GetBytes((string)val)))}");
                    break;
                case RegistryValueKind.Binary:
                    sb.AppendLine($"{namePart}=hex:{RegBytesToHex((byte[])val)}");
                    break;
                case RegistryValueKind.DWord:
                    sb.AppendLine($"{namePart}=dword:{((int)val):x8}");
                    break;
                case RegistryValueKind.MultiString:
                    sb.AppendLine($"{namePart}=hex(7):{RegBytesToHex(MultiStringToBytes((string[])val))}");
                    break;
                case RegistryValueKind.QWord:
                    sb.AppendLine($"{namePart}=hex(b):{RegBytesToHex(QWordToBytes((long)val))}");
                    break;
                default:
                    sb.AppendLine($"{namePart}=hex:{RegBytesToHex((val as byte[]) ?? Array.Empty<byte>())}");
                    break;
            }
        }

        private static string EscapeRegString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string UnescapeRegString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == '\\' || n == '"') { sb.Append(n); i++; }
                    else sb.Append(s[i]);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static byte[] WithNullTerm(byte[] b)
        {
            var r = new byte[b.Length + 2];
            Buffer.BlockCopy(b, 0, r, 0, b.Length);
            return r;
        }

        private static byte[] MultiStringToBytes(string[] items)
        {
            var list = new List<byte>();
            foreach (var it in items ?? Array.Empty<string>())
            {
                list.AddRange(Encoding.Unicode.GetBytes(it ?? ""));
                list.Add(0); list.Add(0);
            }
            list.Add(0); list.Add(0);
            return list.ToArray();
        }

        private static byte[] QWordToBytes(long v)
        {
            var b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * i));
            return b;
        }

        private static string RegBytesToHex(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            var parts = new string[b.Length];
            for (int i = 0; i < b.Length; i++) parts[i] = b[i].ToString("x2");
            return string.Join(",", parts);
        }

        private static byte[] RegHexToBytes(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
            var segs = hex.Split(',');
            var list = new List<byte>(segs.Length);
            foreach (var s in segs)
            {
                if (byte.TryParse(s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                    list.Add(b);
            }
            return list.ToArray();
        }

        private static byte[] StripTrailingNulls(byte[] b)
        {
            int len = b.Length;
            while (len >= 2 && b[len - 1] == 0 && b[len - 2] == 0) len -= 2;
            var r = new byte[len];
            Buffer.BlockCopy(b, 0, r, 0, len);
            return r;
        }

        private static string[] MultiStringFromBytes(byte[] b)
        {
            var list = new List<string>();
            var chars = new List<char>();
            int i = 0;
            while (i + 1 < b.Length)
            {
                char c = (char)(b[i] | (b[i + 1] << 8));
                i += 2;
                if (c == '\0')
                {
                    if (chars.Count > 0) { list.Add(new string(chars.ToArray())); chars.Clear(); }
                }
                else chars.Add(c);
            }
            return list.ToArray();
        }

        private static void ParseRegValueLine(RegistryKey key, string line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0) return;
            string namePart = line.Substring(0, eq).Trim();
            string data = line.Substring(eq + 1).Trim();
            string name = namePart == "@" ? "" : UnescapeRegString(namePart.Trim('"'));

            if (data == "-") 
            {
                try { key.DeleteValue(name, false); } catch { }
                return;
            }

            if (data.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(data.Substring(6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int dw))
                    key.SetValue(name, dw, RegistryValueKind.DWord);
            }
            else if (data.StartsWith("hex(b):", StringComparison.OrdinalIgnoreCase))
            {
                byte[] b = RegHexToBytes(data.Substring(7));
                if (b.Length == 8) key.SetValue(name, BitConverter.ToInt64(b, 0), RegistryValueKind.QWord);
            }
            else if (data.StartsWith("hex(", StringComparison.OrdinalIgnoreCase))
            {
                int close = data.IndexOf(')');
                if (close > 4)
                {
                    string kind = data.Substring(4, close - 4).ToLowerInvariant();
                    byte[] b = RegHexToBytes(data.Substring(close + 2));
                    switch (kind)
                    {
                        case "2":
                            key.SetValue(name, Encoding.Unicode.GetString(StripTrailingNulls(b)), RegistryValueKind.ExpandString);
                            break;
                        case "7":
                            key.SetValue(name, MultiStringFromBytes(b), RegistryValueKind.MultiString);
                            break;
                        default:
                            key.SetValue(name, b, RegistryValueKind.Binary);
                            break;
                    }
                }
            }
            else if (data.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            {
                byte[] b = RegHexToBytes(data.Substring(4));
                key.SetValue(name, b, RegistryValueKind.Binary);
            }
            else if (data.StartsWith("\""))
            {
                string s = UnescapeRegString(data.Trim('"'));
                key.SetValue(name, s, RegistryValueKind.String);
            }
            else
            {
                key.SetValue(name, "", RegistryValueKind.String); 
            }
        }

        private static bool TryOpenOrCreateRegKey(string keyPath, out RegistryKey key)
        {
            key = null;
            if (!TryParseRegRoot(keyPath, out var root, out var sub) || sub.Length == 0)
                return false;
            try
            {
                key = root.CreateSubKey(sub);
                return key != null;
            }
            catch { return false; }
        }

        private static void TryDeleteRegKey(string keyPath)
        {
            if (!TryParseRegRoot(keyPath, out var root, out var sub) || sub.Length == 0) return;
            int idx = sub.LastIndexOf('\\');
            string parent = idx < 0 ? "" : sub.Substring(0, idx);
            string child = idx < 0 ? sub : sub.Substring(idx + 1);
            using var p = parent.Length == 0 ? root : root.OpenSubKey(parent, true);
            if (p == null) return;
            try { p.DeleteSubKeyTree(child, false); } catch { }
        }

        #endregion
    }
}
