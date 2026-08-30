using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DiskCleaner.Interop;

namespace DiskCleaner
{
    
    
    
    
    
    public static class ContextMenuMapping
    {
        
        
        
        
        private static readonly HashSet<string> BuiltInSystemVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            
            "open", "opennew", "opennewprocess", "opennewtab", "opennewwindow",
            "edit", "print", "printto", "explore", "runas", "find", "findtargets",
            "properties", "cut", "copy", "paste", "delete", "rename",
            "cmd", "powershell",
            
            "pintohome", "pintohomefile", "pintotaskbar", "pintostartscreen", "removeproperties",
            "includeinlibrary", "addtolibraryfromsearch",
            
            "UpdateEncryptionSettings", "UpdateEncryptionSettingsWork",
            "encrypt-bde", "encrypt-bde-elev", "manage-bde",
            "resume-bde", "resume-bde-elev", "unlock-bde",
            "change-passphrase", "change-pin",
            
            ".SpotlightLearnMore", ".SpotlightNextImage",
            "Display", "EditStickers", "Personalize",
            
            "PreviousVersions", "RestorePreviousVersions",           
            "Burn",                                                  
            "SyncCenter", "AlwaysAvailableOffline",                
            "DeviceCenter", "PortableDevicesMenu",                 
            "Share",                                                 
            "CopyAsPath",                                            
            "SendTo", "CreateShortcut",                            
            "SetAsDesktopBackground", "SetAsLockScreen",           
            "RotateLeft", "RotateRight",                           
            "EditWithPhotos", "EditWithPaint3D", "EditWithClipchamp",
            "MapNetworkDrive", "DisconnectNetworkDrive", "Format", "Eject", "AutoPlay",
            "Scan", "Acquire", "PrintToPDF", "PrintToFax",
            "Troubleshoot", "TroubleshootCompatibility", "RunAsDate",
            "LaunchPreviewHandler", "OpenWith", "OpenWithList",
        };

        
        internal static readonly HashSet<string> NonProgIdSubKeysOfExt = new(StringComparer.OrdinalIgnoreCase)
        {
            "OpenWithProgids", "OpenWithList", "ShellEx", "ShellNew",
            "PersistentHandler", "Content Type", "PerceivedType"
        };

        
        internal static readonly (string ClassPath, string Scope)[] GenericFileRightClickClasses = new (string, string)[]
        {
            (@"*", "所有文件"),
            (@"Directory", "文件夹"),
            (@"Folder", "文件夹"),
            (@"Drive", "驱动器"),
            (@"AllFilesystemObjects", "所有对象"),
        };

        
        internal static readonly (string ClassPath, string Scope)[] GenericDesktopRightClickClasses = new (string, string)[]
        {
            (@"DesktopBackground", "桌面背景"),
            (@"Directory\Background", "文件夹背景"),
        };

        
        internal static readonly string[] InvalidContextMenuGenericClasses = new[]
        {
            "*", "Directory", "Folder", "Drive", "DesktopBackground", @"Directory\Background"
        };

        
        internal static readonly string[] ClassRootsForInvalidScan = new[]
        {
            @"SOFTWARE\Classes",
            @"SOFTWARE\WOW6432Node\Classes"
        };

        
        internal static readonly HashSet<string> ProtectedVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "open", "opennew", "edit", "print", "explore", "runas", "find",
            "properties", "cut", "copy", "paste", "delete", "rename"
        };

        
        
        
        
        private static bool IsResourceReference(string s) => !string.IsNullOrWhiteSpace(s) && s.StartsWith("@", StringComparison.Ordinal);

        
        private static bool IsUnderWindowsDirectory(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            try
            {
                file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(file)) return false;
                string full = Path.GetFullPath(file);
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return !string.IsNullOrWhiteSpace(win) && full.StartsWith(win, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        
        
        
        
        
        private static string ExtractFirstExecutablePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var s = command.Trim();
            try
            {
                if (s.StartsWith("\"", StringComparison.Ordinal))
                {
                    var end = s.IndexOf('"', 1);
                    if (end > 1) return s.Substring(1, end - 1);
                }
                var space = s.IndexOf(' ');
                var candidate = space > 0 ? s.Substring(0, space) : s;
                candidate = Environment.ExpandEnvironmentVariables(candidate).Trim('"');
                return candidate;
            }
            catch { }
            return null;
        }

        
        private static RegistryKey OpenClsidKey(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            var g = guid.Trim('{', '}');
            var clsidPath = $@"CLSID\{{{g}}}";

            
            var hklm = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\{clsidPath}", false);
            if (hklm != null) return hklm;

            
            var hkcu = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Classes\{clsidPath}", false);
            if (hkcu != null) return hkcu;

            
            var wow = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\WOW6432Node\{clsidPath}", false);
            if (wow != null) return wow;

            return null;
        }

        
        private static string GetClsidServerFile(string guid)
        {
            try
            {
                using var clsid = OpenClsidKey(guid);
                if (clsid == null)
                {
                    Debug.WriteLine($"[GetClsidServerFile] OpenClsidKey returned null for guid={guid}");
                    return null;
                }
                using (var s = clsid.OpenSubKey("InprocServer32", false))
                {
                    var v = s?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                using (var s = clsid.OpenSubKey("InprocServer64", false))
                {
                    var v = s?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                using (var s = clsid.OpenSubKey("LocalServer32", false))
                {
                    var v = s?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                using (var s = clsid.OpenSubKey("LocalServer64", false))
                {
                    var v = s?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetClsidServerFile] EXCEPTION guid={guid}: {ex.Message}");
            }
            Debug.WriteLine($"[GetClsidServerFile] returning null for guid={guid}");
            return null;
        }

        
        
        
        
        private static bool ProgIdServerUnderWindows(string extOrSpecialKey)
        {
            if (string.IsNullOrWhiteSpace(extOrSpecialKey)) return false;
            try
            {
                string progId = null;
                
                using (var extKey = Registry.ClassesRoot.OpenSubKey(extOrSpecialKey, false))
                {
                    if (extKey == null) return false;
                    progId = extKey.GetValue(null) as string;
                }
                if (string.IsNullOrWhiteSpace(progId)) progId = extOrSpecialKey;

                
                using var progKey = Registry.ClassesRoot.OpenSubKey(progId, false);
                if (progKey == null) return false;

                using (var iconKey = progKey.OpenSubKey("DefaultIcon", false))
                {
                    var v = iconKey?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        
                        var comma = v.IndexOf(',');
                        var file = comma > 0 ? v.Substring(0, comma) : v;
                        if (IsUnderWindowsDirectory(file)) return true;
                    }
                }
                using (var cmdKey = progKey.OpenSubKey(@"shell\open\command", false))
                {
                    var v = cmdKey?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(v) && IsUnderWindowsDirectory(v)) return true;
                }
            }
            catch { }
            return false;
        }

        
        
        
        

        private static readonly Regex _clsidRegex = new Regex(
            @"\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?",
            RegexOptions.Compiled);

        public static string NormalizeClsid(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = _clsidRegex.Match(s);
            return m.Success ? m.Value.Trim('{', '}') : null;
        }

        private static readonly Lazy<HashSet<string>> Win11CachedGuids =
            new(() =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    try
                    {
                        using var cached = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached", false);
                        if (cached == null) continue;
                        foreach (var name in cached.GetValueNames())
                        {
                            var n = NormalizeClsid(name);
                            if (n != null) set.Add(n);
                        }
                    }
                    catch { }
                }
                return set;
            });

        
        
        
        
        
        // 已知合法的第三方 Shell 扩展 CLSID（压缩 / 云盘 / PDF / 工具类等）。
        // 这些扩展即便其 InprocServer32 路径因 MSIX 打包、ACL 保护或路径解析差异而无法被
        // IsDeadFileReference 直接校验，也绝不应被“失效清理”误删，否则会破坏其右键菜单
        // （典型如 Bandizip 通过 MSIX 打包的 Shell 扩展提供 Win11 一级右键菜单）。
        // 数据来源：自动从下方 _displayNameFallback 中提取「CLSID 形态、且不属于 _clsidResText 系统项」的键值，
        // 与“已知第三方扩展”清单保持一致，新增同类工具无需手动维护。
        private static readonly HashSet<string> ProtectedShellExtensionClsids = new(StringComparer.OrdinalIgnoreCase);

        static ContextMenuMapping()
        {
            foreach (var kv in _displayNameFallback)
            {
                var k = NormalizeClsid(kv.Key);
                if (k == null) continue;                       // 仅保留 CLSID 形态键值（过滤 ProgID / 扩展名等）
                if (_clsidResText.ContainsKey(kv.Key)) continue; // 跳过纯系统项（shell32.dll 等资源引用）
                ProtectedShellExtensionClsids.Add(k);
            }
        }

        // 受保护的名称片段（大小写不敏感）。用于兜底：当处理器/菜单项的名称包含这些片段时，
        // 即便其 CLSID 尚未登记到 ProtectedShellExtensionClsids，也视为合法扩展而不删除。
        // 覆盖用户明确反馈会被误删的火绒安全、系统记事本、Windows Terminal，以及常见压缩/云盘/PDF 工具。
        private static readonly HashSet<string> _protectedNameFragments = new(StringComparer.OrdinalIgnoreCase)
        {
            "bandizip", "7-zip", "winrar", "2345", "快压", "好压", "百度网盘", "腾讯微云", "dropbox", "googledrive",
            "火绒", "huorong", "hips", "notepad", "windowsnotepad", "windows terminal", "wt.exe", "adobe", "foxit"
        };

        /// <summary>
        /// 判断某个上下文菜单处理器 / COM 组件是否为已知合法的第三方 Shell 扩展（受保护，不应被失效清理删除）。
        /// </summary>
        public static bool IsProtectedShellExtension(string guid, string keyName)
        {
            var g = NormalizeClsid(guid);
            if (!string.IsNullOrWhiteSpace(g) && ProtectedShellExtensionClsids.Contains(g)) return true;
            var k = NormalizeClsid(keyName);
            if (!string.IsNullOrWhiteSpace(k) && ProtectedShellExtensionClsids.Contains(k)) return true;

            if (!string.IsNullOrWhiteSpace(keyName))
            {
                var n = keyName.ToLowerInvariant();
                foreach (var frag in _protectedNameFragments)
                    if (n.Contains(frag)) return true;
            }
            return false;
        }

        private static readonly Regex _accelRegex = new Regex(@"\s*\(&[A-Za-z0-9]\)\s*", RegexOptions.Compiled);

        
        private static readonly Dictionary<string, string> _assocCache = new(StringComparer.OrdinalIgnoreCase);

        
        
        
        
        
        private static readonly Dictionary<string, string> _verbNameOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            { "ThunderShell", "迅雷" },
            { "YunShellExplorerCommand", "百度网盘" },
            { "git_shell", "在此处打开 Git Bash" },
            { "git_gui", "在此处打开 Git GUI" },
        };

        
        
        
        
        
        
        
        
        
        
        private static readonly Dictionary<string, string> _displayNameFallback = new(StringComparer.OrdinalIgnoreCase)
        {
            
            { "90AA3A4E-1CE8-4D3F-95A7-1C40B2A666D5", "压缩(zipped)文件夹右键拖拽处理程序" },
            { "2559a1f0-21d7-11d4-bdaf-00c04f60b9f0", "搜索" },
            { "2559a1f1-21d7-11d4-bdaf-00c04f60b9f0", "帮助和支持" },
            { "2559a1f3-21d7-11d4-bdaf-00c04f60b9f0", "运行" },
            { "2559a1ff-21d7-11d4-bdaf-00c04f60b9f0", "共享文档" },
            { "7BA4C740-9E81-11CF-99D3-00AA004AE837", "电脑搜索" },
            { "A470F8CF-A1E8-4f65-8335-2274757EDB54", "文件夹选项" },
            { "88895560-9AA2-1069-930E-00AA004AE837", "HyperTerminal 图标" },
            { "E29829DA-9CDB-4C2F-83EE-9F4B0E5360D5", "文件夹" },
            { "D14BEDD3-4FEF-450E-A06D-DED95F5B4E3B", "便携设备" },
            { "679E85CB-0228-434A-A5E0-81B4B8808A9E", "便携设备菜单" },
            { "4657278A-4000-4D37-9360-3FE2DBC5B8A9", "启动同步中心" },
            { "67CA7650-96E6-4FDD-B33D-D25E0D1C8F5E", "工作文件夹" },

            
            { "AIMP Shell Unit", "AIMP 音乐播放器" },
            { "WinRAR", "WinRAR 压缩文件" },
            { "7-Zip", "7-Zip 压缩文件" },
            { "AAbdzCtx Class", "ACDSee 相册扩展" },
            { "AccExt Class", "ACDSee 扩展" },
            { "Briefcase", "公文包" },
            { "ShellFolder", "Shell 文件夹" },

            
            { "open", "打开" },
            { "opennew", "在新窗口打开" },
            { "opennewprocess", "在新进程打开" },
            { "opennewtab", "在新标签页打开" },
            { "opennewwindow", "在新窗口打开" },
            { "edit", "编辑" },
            { "print", "打印" },
            { "printto", "打印到" },
            { "explore", "资源管理器" },
            { "runas", "以管理员身份运行" },
            { "find", "查找" },
            { "findtargets", "查找目标" },
            { "properties", "属性" },
            { "cut", "剪切" },
            { "copy", "复制" },
            { "paste", "粘贴" },
            { "delete", "删除" },
            { "rename", "重命名" },
            { "cmd", "在此处打开命令提示符" },
            { "powershell", "在此处打开 PowerShell" },
            { "pintohome", "固定到主页" },
            { "pintohomefile", "固定到主页" },
            { "removeproperties", "删除属性" },
            { "UpdateEncryptionSettings", "更新加密设置" },
            { "encrypt-bde", "启用 BitLocker 加密" },
            { "manage-bde", "管理 BitLocker" },
            { "resume-bde", "恢复 BitLocker" },
            { "unlock-bde", "解锁 BitLocker" },
            { "change-passphrase", "更改密码" },
            { "change-pin", "更改 PIN" },
            { "Display", "显示设置" },
            { "EditStickers", "编辑贴纸" },
            { "Personalize", "个性化" },

            
            { "PreviousVersions", "还原以前的版本" },
            { "RestorePreviousVersions", "还原以前的版本" },
            { "Burn", "刻录到光盘" },
            { "SyncCenter", "同步中心" },
            { "AlwaysAvailableOffline", "始终脱机可用" },
            { "DeviceCenter", "便携设备菜单" },
            { "PortableDevicesMenu", "便携设备菜单" },
            { "PinToTaskbar", "固定到任务栏" },
            { "PinToStartScreen", "固定到「开始」屏幕" },
            { "Share", "共享" },
            { "IncludeInLibrary", "包含到库中" },
            { "AddToLibraryFromSearch", "包含到库中" },
            { "CopyAsPath", "复制路径" },
            { "SendTo", "发送到" },
            { "CreateShortcut", "创建快捷方式" },
            { "SetAsDesktopBackground", "设为桌面背景" },
            { "SetAsLockScreen", "设置为锁屏界面" },
            { "RotateLeft", "向左旋转" },
            { "RotateRight", "向右旋转" },
            { "EditWithPhotos", "使用照片编辑" },
            { "EditWithPaint3D", "使用 Paint 3D 编辑" },
            { "EditWithClipchamp", "使用 Clipchamp 编辑" },
            { "MapNetworkDrive", "映射网络驱动器" },
            { "DisconnectNetworkDrive", "断开网络驱动器" },
            { "Format", "格式化" },
            { "Eject", "弹出" },
            { "AutoPlay", "自动播放" },
            { "Scan", "扫描" },
            { "Acquire", "获取图片" },
            { "PrintToPDF", "打印到 PDF" },
            { "PrintToFax", "打印到传真" },
            { "Troubleshoot", "疑难解答" },
            { "TroubleshootCompatibility", "兼容性疑难解答" },

            
            { ".txt", "文本文档" },
            { ".bmp", "BMP 图像" },
            { ".dib", "DIB 图像" },
            { ".rtf", "RTF 格式" },
            { ".zip", "压缩(zipped)文件夹" },
            { ".lnk", "快捷方式" },
            { ".doc", "DOC 文档" },
            { ".docx", "DOCX 文档" },
            { ".ppt", "PPT 演示文稿" },
            { ".pptx", "PPTX 演示文稿" },
            { ".xls", "XLS 工作表" },
            { ".xlsx", "XLSX 工作表" },
            { ".rar", "WinRAR 压缩文件" },
            { ".7z", "7-Zip 压缩文件" },
            { ".contact", "联系人" },
            { ".library-ms", "Windows 库" },
            { ".searchConnector-ms", "搜索连接器" },

            
            
            
            { "B41DB860-64E4-11D2-9906-E49FADC173CA", "WinRAR 64bit" },
            { "B41DB860-8EE4-11D2-9906-E49FADC173CA", "WinRAR 32bit" },
            { "23170F69-40C1-278A-1000-000100020000", "7-Zip" },
            { "B1832224-9F22-4965-A6E8-E6A6E3C4FDF7", "52好压" },
            { "5FED836A-C96C-4D88-A91E-F63F07726585", "2345好压" },
            { "6ADF19E3-77A3-4395-ADB4-9FD7D351EB3E", "快压" },
            { "903D855A-D671-4A8E-A592-9168755917DB", "快压" },
            { "5B69A6B4-393B-459C-8EBB-214237A9E7AC", "Bandizip" },
            { "9179176E-B763-3200-8500-BB1B90B3D5DE", "360zip" },
            { "E0D79304-84BE-11CE-9641-444553540000", "WinZip" },
            { "ACF03765-1658-485F-9615-FE03C372FB8C", "微压" },
            { "DD847E93-86DF-4D75-A421-46D29228F6DA", "极光压缩" },
            { "5E3B6EA4-DE96-464F-BB86-5587969F4C62", "万能压缩" },
            { "9175E343-1C41-4490-B178-14F36504F07E", "160压缩" },
            { "5E3CA55C-D9F3-4D1C-8B94-AD192B0F7C16", "速压" },
            { "02E77C8D-2798-441E-8E6D-0C2ED9FE206E", "简压缩" },
            { "5C551008-A347-4DB3-AF48-014076FD2B46", "极速压缩" },
            { "0A4861A5-448A-48B4-812A-CF71A07A674F", "6789压缩" },
            { "7522B611-F22B-4F24-9CE4-3ECBBA85C126", "CoffeeZip" },
            { "AA7F8063-F898-44D0-B147-8A6E9891E32E", "布丁压缩" },
            { "9B8DFF9D-0CB2-4100-8F21-C6DD6CE01141", "布丁压缩" },
            { "D81A0ED9-6C47-417B-B298-4EBD813CABD0", "极客压缩" },
            { "E677C7AD-2B66-4539-AA29-3771A1CFEDA9", "jZip" },
            { "A6630968-27DC-8DB8-9BCE-E12B3198A9B1", "WinArchiver" },
            { "1532B32D-3A75-4A4D-9B38-5A6000EA7045", "FreeArc" },
            { "AD392E40-428C-459F-961E-9B147782D099", "UltraISO" },
            { "BE86F80B-EB1A-45B4-B4B6-4B12D302B6BC", "AntZip" },
            { "9D731A0B-89E4-44E2-A6A4-3B05C80D562C", "i-Zip" },
            
            { "09A47860-11B0-4DA5-AFA5-26D86198A780", "Microsoft Defender" },
            { "18A3E590-F219-4105-9079-93DAFA51B9D6", "使用火绒安全进行杀毒" },
            { "9F012318-CAA5-402C-9BF6-28EB36F61496", "使用火绒安全粉碎文件" },
            { "63332668-8CE1-445D-A5EE-25929176714E", "扫描病毒(电脑管家)" },
            { "754DF2CE-51E8-4895-B53C-6381418B84AE", "文件粉碎(电脑管家)" },
            { "CBDECEF7-7A29-4CBF-A009-2673D82C7BF9", "强力卸载(电脑管家)" },
            { "C5617F6A-39BB-436D-91CF-61C1B45DD688", "深度加速(管家小火箭)" },
            { "B7667919-3765-4815-A66D-98A09BE662D6", "清理垃圾(电脑管家)" },
            { "086F171D-5ED1-4ED2-B736-CFF3AD6A128E", "使用 360杀毒 扫描" },
            { "7C0F6D57-E799-4C8A-A319-8E2B4D724CF0", "360安全卫士" },
            { "5E19C0CE-C02C-46C2-98C3-A2E12EDE0E17", "360强力卸载 && 桌面助手" },
            { "C4F75DB1-B9F4-425A-9F5B-778911BCF176", "2345扫描病毒 && 强力删除" },
            { "6B3BA4A8-EC7E-4714-9ECF-EB33995B3384", "使用2345软件管家卸载软件" },
            { "DDEA5705-1BB0-4C03-AC1E-8FF9716A0D51", "金山毒霸(64位)" },
            { "D21D88E8-4123-48BA-B0B1-3FDBE4AE5FA4", "金山毒霸(32位)" },
            { "367F6AE2-6809-4BED-B09B-228893FB33DD", "金山毒霸" },
            { "758C684B-4D10-4BC1-90DA-6BEBF0B4E0B4", "使用联想电脑管家进行扫描" },
            { "C97C3E31-2AF4-4651-9E93-EDE827605A23", "强力卸载此软件(联想电脑管家)" },
            { "C49499AC-DC25-478B-B903-E005012B3DD1", "使用智量扫描" },
            { "CCA9EFD3-29ED-430A-BA6D-E6BBFF0A60C2", "McAfee" },
            { "472083B0-C522-11CF-8763-00608CC02F24", "Avast" },
            { "45AC2688-0253-4ED8-97DE-B5370FA7D48A", "使用 Avira 扫描所选文件" },
            { "9B9F6E01-A5CF-4269-B245-CFF66A7DAEBD", "卡巴斯基" },
            { "57CE581A-0CB6-4266-9CA0-19364C90A0B3", "Malwarebytes" },
            { "F7CAA2A1-67A2-44BB-B20F-202FD8EB1DAB", "Norton 360" },
            { "FAD61B3D-699D-49B2-BE16-7F82CB4C59CA", "Norton文件智能分析" },
            { "E8215BEA-3290-4C73-964B-75502B9B41B2", "Norton File Shredder" },
            { "1C7593CB-C1CC-4BA7-BE52-8EEA47F9CB1D", "使用瑞星杀毒" },
            { "0BB81440-5F42-4480-A5F7-770A6F439FC8", "IObit Malware Fighter" },
            { "B089FE88-FB52-11D3-BDF1-0050DA34150D", "ESET Endpoint Antivirus" },
            
            { "53D2405C-48AB-4C8A-8F59-CE0610F13BBC", "通过QQ发送到" },
            { "CB3D0F55-BC2C-4C1A-85ED-23ED75B5106B", "OneDrive" },
            { "6D85624F-305A-491D-8848-C1927AA0D790", "上传到百度网盘" },
            { "2008CAF4-A5C1-4037-99A8-699E4D01456D", "天翼云盘" },
            { "1D39A523-4DF5-4562-8FFF-08C740632F4F", "360云盘" },
            { "EEE949EB-C9ED-4967-98B0-ED4E543BEFA5", "115网盘" },
            { "171B6B53-17B1-40B7-AFB2-A415B2B40401", "上传到腾讯微云" },
            { "5D652B62-B702-496A-92BC-92C308251FEA", "坚果云" },
            { "67F4D210-BFC2-4ADD-9A2A-C9B9E1F42C4F", "上传到 \"WPS云文档\"" },
            { "AA147FFB-0B1F-4BB1-9B1E-8D062B35C145", "自动同步文件夹到 \"WPS云文档\"" },
            { "970A26B5-2B84-4B60-8067-1440C229672D", "钉盘" },
            { "0229E5E7-09E9-45CF-9228-0228EC7D5F17", "MEGA" },
            { "ECD97DE5-3C8F-4ACB-AEEE-CCAB78F7711C", "Dropbox" },
            { "2A535B11-6CFC-4E85-A75F-0E397B1584CF", "通过网易邮箱大师发送" },
            { "9557F42F-BD61-4E26-9752-33A8A20FC9F9", "分享文件(华为电脑管家)" },
            { "C3DB4192-4C22-428F-8C12-CF9CFABBDD17", "通过AirDroid发送到我的设备" },
            { "0B488C12-E68E-44D7-9259-FB8E5DF8BB27", "内网通" },
            { "256EF94C-697D-4986-B99B-9D3B15D79B49", "Infinit" },
            { "A97A01B0-3B66-4336-BC9B-168E64BFB296", "广讯通发送文件" },
            { "41B3B91F-D6B3-3430-BB86-A143F85353CA", "Huawei Share" },
            { "1BCA9901-05C3-4D01-8AD4-78DA2EAC9B3F", "使用小米互传发送" },
            
            { "9C5397BB-07BE-4E38-98BA-395F94045091", "福昕PDF编辑器" },
            { "27BE7B9D-935F-325C-9A05-63557D69F4F9", "万兴PDF专家" },
            { "A6595CD1-BF77-430A-A452-18696685F7C7", "Adobe Acrobat" },
            { "D25B2CAB-8A9A-4517-A9B2-CB5F68A5A802", "转换为Adobe PDF" },
            { "1ECDA7BF-4DFA-41D8-9380-1A27B26CFC41", "使用WPS PDF编辑" },
            { "E3ED4700-22D4-41B6-8144-E3F5F1AC5153", "使用WPS PDF编辑" },
            { "AF1D7D2F-6AE8-4BAA-ABFA-738201F4871C", "WPS Office PDF" },
            { "B4E15CD0-F916-4C8E-830A-15E3E9D01A1B", "迅读PDF大师 合并、拆分、转换" },
            { "19A73C67-F0B8-4A28-8C33-9D4EDDD6FCBC", "云上PDF" },
            { "29424E92-60A1-40C0-BF79-1B8472DDE706", "使用PDF猫编辑器打开" },
            
            { "098A124A-AA1C-38C8-A65E-D1199A14516A", "Convert to...(万兴PDF专家)" },
            { "FB074836-8286-4089-84DC-F504E9EF621C", "ABBYY FineReader" },
            { "D16ADFE3-66C5-4DF5-9978-87DE0C687E85", "嗨格式PDF转换器" },
            { "2ACD35AB-F74A-4C20-AA9B-2DE80081626D", "PDF-XChange Editor" },
            { "6C405CEC-8624-4FEF-B3BA-9D4E5A8F58B5", "迅捷PDF转换器" },
            
            { "9B6D38F3-8EF4-48A5-AD30-FFFFFFFFFFFF", "Honeyview" },
            { "C2396F1E-4BA2-4B7D-857A-F764761C012B", "ACD看图" },
            { "83A97A48-F5D7-4D12-8BA3-5263A016D936", "使用光影4.0编辑" },
            { "8F556DA3-987D-47B0-AA88-EB8D52FE1B9A", "迅雷播放组件" },
            { "5CD76C57-6893-478A-B776-47E7C82504BE", "爱奇艺万能联播" },
            { "A3888921-CFD3-4A6B-89BF-08E6B95716E8", "图片工厂" },
            { "51B4D7E5-7568-4234-B4BB-47FB3C016A69", "调整图片大小(PowerToys)" },
            { "5C6A637C-9780-4D0F-A379-4732EDCCE7C3", "网易云音乐" },
            { "1F77B17B-F531-44DB-ACA4-76ABB5010A28", "AIMP" },
            { "8E7861BB-3A13-40A1-AF25-633458757201", "QQ影音" },
            { "7A1884A3-F647-49BE-B93C-8FFAF4A1F1BF", "使用PP视频播放" },
            { "8F556DA3-987D-47B0-AA88-EB8D52FE1B99", "迅雷影音" },
            { "4D2FBA8D-621B-4447-AF6D-5794F479C4A5", "爱奇艺看图" },
            { "4A34B3E3-F50E-4FF6-8979-7E4176466FF2", "SageThumbs" },
            { "D149B06B-BC31-4425-B5BB-0E04308982AE", "用百图秀打开" },
            { "5ADD1A3C-29E2-4D18-B79A-C56E1DF5486C", "百图秀" },
            { "1BB8F8E1-6492-4EC9-AC40-9AD0F5C01FA2", "鲨鱼看图" },
            { "A2CF4243-6525-4764-B3F5-2FCDE2F47989", "MPC-BE" },
            { "FFE2A43C-56B9-4BF5-9A79-CC6D4285608A", "向右、向左旋转" },
            { "E598560B-28D5-46AA-A14A-8A3BEA34B576", "幻灯片放映" },
            { "8A734961-C4AA-4741-AC1E-791ACEBF5B39", "联机购买音乐" },
            { "F1B9284F-E9DC-4E68-9D7E-42362A59F0FD", "添加到Windows Media Player列表" },
            { "CE3FB1D1-02AE-4A5F-A6E9-D9F1B4073E6C", "使用Windows Media Player播放" },
            { "7D4734E6-047E-41E2-AEAA-E763B4739DC4", "使用 Media Player 播放" },
            
            { "CF444751-60FC-48B8-AC0F-363063EB2A9E", "开启桌面整理(电脑管家)" },
            { "B3A2F1A4-10A2-410C-9C19-622B621C61D0", "换壁纸(小鸟壁纸)" },
            { "3B7162FB-4389-40C8-83A5-DA10D491EC66", "映射此文件夹到桌面(小智桌面)" },
            { "96BABF4F-7C38-4885-B407-3800F461669B", "打开火萤视频桌面" },
            { "C3C76829-1D8B-49A2-AFB4-196C443F1B0B", "人工桌面" },
            { "2D5AD9EB-31BC-48F7-A438-28F363632C73", "开启布丁桌面" },
            { "B5E436BC-642A-4BF6-B725-26038AF26E89", "开启桌面整理(元气、猎豹轻桌面)" },
            { "5F8D079B-8CE6-4F58-BF10-55C1B68D88F3", "选择颜色设置" },
            { "6C125022-639D-43CC-9F3D-647E6CC69EF1", "右键菜单背景插件(ContextBG)" },
            
            { "A2A9545D-A0C2-42B4-9708-A0B2BADD77C9", "附到「开始」菜单(StartIsBack)" },
            { "6A451C0A-9597-4915-BCCE-6E859BC996B2", "Pin to Start (Start10)" },
            { "AF8FA9C9-9907-463E-BDC3-4CC1200D6310", "Start Menu 8" },
            { "07451604-FBE4-4475-9DD6-261B7B619417", "电脑管家经典开始菜单" },
            
            { "B298D29A-A6ED-11DE-BA8C-A68E55D89593", "Notepad++" },
            { "36D94110-787C-4828-9C1B-0DAFEBC36069", "EditPlus" },
            { "B5EEDEE0-C06E-11CF-8C56-444553540000", "UltraEdit" },
            { "51EEE242-AD87-11D3-9C1E-0090278BBD99", "Vim" },
            { "ED90173A-3B4C-4E7E-B9CF-79714425D4B5", "PSPad Editor" },
            { "A3777921-CFD3-4A6B-89BF-08E6B95716E8", "格式工厂" },
            { "AF9B72B5-F4E4-44B0-A3D9-B55B748EFE90", "File Converter" },
            { "DDE4BEEB-DDE6-48FD-8EB5-035C09923F83", "Unlocker" },
            { "0BB27CDA-7029-4C0E-9C56-D922B229F0EB", "解除文件锁定(LockHunter)" },
            { "410BF280-86EF-4E0F-8279-EC5848546AD3", "IObit Unlocker" },
            { "836AB26C-2DE4-41D3-AC24-4C6C2699B960", "强力卸载(IObit Uninstaller)" },
            { "2803063F-4B8D-4DC6-8874-D1802487FE2D", "Advanced SystemCare" },
            { "FE8E6AD6-DABE-45E1-88C2-48DC4578924C", "Uninstall Tool" },
            { "2C5515DC-2A7E-4BFD-B813-CACC2B685EB7", "Revo Uninstaller Pro" },
            { "B3026062-4D7E-3601-9A6B-622CEAC3FC5C", "File Governor" },
            { "1650DC30-2343-498A-B49A-37B90918F611", "CleanMyPC" },
            { "6CB83A5A-AA68-4895-9F54-175E789AE149", "Balabolka" },
            { "0440049F-D1DC-4E46-B27B-98393D79486B", "PowerRename" },
            { "2E7A2C6C-B938-40A4-BA1C-C7EC982DC202", "发布DWF" },
            { "57FA2D12-D22D-490A-805A-5CB48E84F12A", "Beyond Compare" },
            { "C3B42C03-C1B7-4C1A-B384-BBAE19646333", "UltraCompare" },
            { "1ED0F018-76B9-4DB9-9C06-CA0F3088F04F", "FileLocator Pro" },
            { "C1B2C38F-3DCA-4E3D-BC34-D5B87B636543", "FileMenu Tools" },
            { "189F1E63-33A7-404B-B2F6-8C76A452CC54", "Smart Defrag" },
            { "4380C993-0C43-4E02-9A7A-0D40B6EA7590", "Defraggler" },
            
            { "30351349-7B7D-4FCC-81B4-1E394CA267EB", "TortoiseSVN" },
            { "10A0FDD2-B0C0-4CD4-A7AE-E594CE3B91C8", "TortoiseGit" },
            { "271DC252-6FE1-4D59-9053-E4CF50AB99DE", "VMware" },
            { "E4D28EDC-8C0B-43EE-9E7D-C8A8682334DC", "VMware 64bit" },
            { "C52B9871-E5E9-41FD-B84D-C5ACADBEC7AE", "Locale Emulator" },
            { "0A479751-02BC-11D3-A855-0004AC2568AA", "NTFS文件连接扩展配置工具" },
            { "FDF253AC-1724-4853-BE34-C2DBC18FB5CA", "Copywhiz" },
            { "9F156763-7844-4DC4-B2B1-901F640F5155", "Open in Windows Terminal" },
            
            { "00000000-B39D-4CB2-85F6-6E055D3B9A96", "在此窗口中打开" },
            { "9EE0C3EF-C379-4FED-B2A8-651BB61F5C0F", "打开为文件夹" },
            { "9EE0C3EF-C379-4FED-B2A8-651BB61F5C0E", "压缩并邮寄" },
            { "59624DCD-DE34-4E3B-ABE4-F40DAFA1F2E0", "TC4Shell" },
            { "6B9228DA-9C15-419E-856C-19E768A13BDC", "Windows 小工具" },
            { "A929C4CE-FD36-4270-B4F5-34ECAC5BD63C", "用图形处理器运行" },
            { "E97DEC16-A50D-49BB-AE24-CF682282E08D", "更改 OpenGL 渲染 GPU" },
            { "3D1975AF-48C6-4F8E-A182-BE0E08FA86A9", "NVIDIA 控制面板" },
            { "9B5F5829-A529-4B12-814A-E81BCB8D93FC", "英特尔显卡设置" },
            { "3AB1675A-CCFF-11D2-8B20-00A0C93CB1F4", "Intel Graphics" },
            { "5E2121EE-0300-11D4-8D3B-444553540000", "AMD 显卡" },
            { "0DF44E3E-3B5D-4226-9CC1-94C4D48626D1", "以前的版本" },
            { "6C467336-8281-4E60-8204-430CED96822D", "共享文件夹同步" },
            { "6FA85DAD-AC32-4D74-9CBA-6A1C038F9A56", "文档加密/解密" },
            { "EF479680-EA35-4EA9-B093-7114F3E3E0DA", "Directory Lister" },
            { "8E57C449-7891-49BB-80E5-DDAC375F8AC8", "使用微表格打开" },
            { "0D8B46EA-7D35-4921-B88C-7E2B1E2D80F0", "WPS Office" },
            { "53506455-E799-443F-ADDB-891CA6EFC928", "扫描游戏到快吧游戏" },
            { "2A118EB5-5797-4F5E-8B3D-F4ECBA3C98E4", "Adobe Core Sync Extension" },
            { "776DBC8D-7347-478C-8D71-791E12EF49D8", "使用 Skype 共享" },
            { "3DAD6C5D-2167-4CAE-9914-F99E41C12CFA", "包含到库中" },
            { "59099400-57FF-11CE-BD94-0020AF85B590", "复制磁盘" },
            { "00000000-0000-0000-0000-000000000012", "显示隐藏文件+扩展名" },

            
            { "90AA3A4E-1CBA-4233-B8BB-535773D48449", "固定到任务栏" },
            { "ADEA1FEC-5832-4B19-9B1D-C6EF361BD85D", "腾讯应用宝" },
            { "99D4E39A-EA7F-453E-9488-58C56BBF7B98", "夸克 AI" },
            { "D7D43EA6-BCE2-489C-9A70-9027A42E1EFD", "夸克网盘 AI" },
        };

        
        
        
        
        
        private static readonly Dictionary<string, string> _clsidResText = new(StringComparer.OrdinalIgnoreCase)
        {
            { "20D04FE0-3AEA-1069-A2D8-08002B30309D", "@shell32.dll,-8770" },       
            { "00021401-0000-0000-C000-000000000046", "@windows.storage.dll,-8496" }, 
            { "37EA3A21-7493-4208-A011-7F9EA79CE9F5", "@shell32.dll,-1033" },        
            { "FF609CC7-D34D-4049-A1AA-2293517FFCC6", "@stobject.dll,-417" },         
            { "0BF754AA-C967-445C-AB3D-D8FDA9BAE7EF", "@stobject.dll,-416" },         
            { "3080F90E-D7AD-11D9-BD98-0000947B0257", "@shell32.dll,-12715" },        
            { "470C0EBD-5D73-4D58-9CED-E91E22E23282", "@shell32.dll,-51201" },        
            { "90AA3A4E-1CBA-4233-B8BB-535773D48449", "@shell32.dll,-5386" },         
            { "F3D06E7C-1E45-4A26-847E-F9FCDEE59BE0", "@shell32.dll,-30328" },        
            { "C2FBB630-2971-11D1-A18C-00C04FD75D13", "@shell32.dll,-30304" },        
            { "C2FBB631-2971-11D1-A18C-00C04FD75D13", "@shell32.dll,-30305" },        
            { "B8CDCB65-B1BF-4B42-9428-1DFDB7EE92AF", "@shell32.dll,-37514" },        
            { "09799AFB-AD67-11D1-ABCD-00C04FC30936", "@shell32.dll,-5376" },         
            { "D969A300-E7FF-11D0-A93B-00A0C90F2719", "@shell32.dll,-95851" },        
            { "A470F8CF-A1E8-4F65-8335-227475AA5C46", "@acppage.dll,-2022" },         
            { "7AD84985-87B4-4A16-BE58-8B72A5B390F7", "@playtomenu.dll,-101" },       
            { "E2BF9676-5F8F-435C-97EB-11607A5BEDF7", "@ntshrui.dll,-107" },          
            { "E61BF828-5E63-4287-BEF1-60B1A4FDE0E3", "@WorkfoldersControl.dll,-1" }, 
            { "F81E9010-6EA4-11CE-A7FF-00AA003CA9F6", "@ntshrui.dll,-103" },          
            { "474C98EE-CF3D-41F5-80E3-4AAB0AB04301", "@cscui.dll,-38" },             
            { "2854F705-3548-414C-A113-93E27C808C85", "@EhStorShell.dll,-101" },       
            { "FBEB8A05-BEEE-4442-804E-409D6C4515E9", "@shell32.dll,-12560" },        
            { "D6791A63-E7E2-4FEE-BF52-5DED8E86E9B8", "@wpdshext.dll,-511" },          
            { "0AF96EDE-AEBF-41ED-A1C8-CF7A685505B6", "@shell32.dll,-34618" },         
            { "596AB062-B4D2-4215-9F74-E9109B0A8153", "@twext.dll,-1037" },            
            { "85BBD920-42A0-1069-A2E4-08002B30309D", "@shell32.dll,-22978" },         
            { "1A184871-359E-4F67-AAD9-5B9905D62232", "@fontext.dll,-340" },           
            { "645FF040-5081-101B-9F08-00AA002F954E", "@mmres.dll,-5831" },            
            { "BD472F60-27FA-11CF-B8B4-444553540000", "@zipfldr.dll,-10095" },         
            { "5635493F-7D77-4372-A839-8AD89F5B3726", "@msimsg.dll,-38" },             
        };

        private static string StripAccelerator(string name)
            => string.IsNullOrWhiteSpace(name) ? name : _accelRegex.Replace(name, "").Trim();

        
        
        
        
        
        private static string LookupFallbackZhName(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            
            if (_displayNameFallback.TryGetValue(key, out var v1)) return v1;
            
            var g = NormalizeClsid(key);
            if (g != null)
            {
                if (_displayNameFallback.TryGetValue(g, out var v2)) return v2;
                
                if (_displayNameFallback.TryGetValue($"{{{g}}}", out var v3)) return v3;
                
                if (_clsidResText.TryGetValue(g, out var resText) ||
                    _clsidResText.TryGetValue($"{{{g}}}", out resText))
                {
                    var resolved = NativeMethods.LoadIndirectString(resText);
                    if (!string.IsNullOrWhiteSpace(resolved)) return StripAccelerator(resolved);
                }
            }
            return null;
        }

        
        
        
        
        
        
        
        
        
        
        public static string ResolveClsidName(string guid, string keyName)
        {
            
            
            var fbGuid = LookupFallbackZhName(guid);
            if (!string.IsNullOrWhiteSpace(fbGuid)) return fbGuid;
            var fbKey = LookupFallbackZhName(keyName);
            if (!string.IsNullOrWhiteSpace(fbKey)) return fbKey;

            
            if (!string.IsNullOrWhiteSpace(guid))
            {
                try
                {
                    using var clsid = OpenClsidKey(guid);
                    if (clsid != null)
                    {
                        
                        var localized = clsid.GetValue("LocalizedString") as string;
                        var r1 = TryResolveIndirectString(localized);
                        if (!string.IsNullOrWhiteSpace(r1)) return StripAccelerator(r1);

                        
                        using (var defIcon = clsid.OpenSubKey("DefaultIcon", false))
                        {
                            if (defIcon != null)
                            {
                                var friendly = defIcon.GetValue("FriendlyTypeName") as string;
                                var r2 = TryResolveIndirectString(friendly);
                                if (!string.IsNullOrWhiteSpace(r2)) return StripAccelerator(r2);

                                
                                var di = defIcon.GetValue(null) as string;
                                var r2b = TryResolveIndirectString(di);
                                if (!string.IsNullOrWhiteSpace(r2b)) return StripAccelerator(r2b);
                            }
                        }

                        
                        using (var shellOpen = clsid.OpenSubKey(@"shell\open\command", false))
                        {
                            if (shellOpen != null)
                            {
                                var ft = shellOpen.GetValue("FriendlyTypeName") as string;
                                var r3 = TryResolveIndirectString(ft);
                                if (!string.IsNullOrWhiteSpace(r3)) return StripAccelerator(r3);
                            }
                        }

                        
                        var def = clsid.GetValue(null) as string;
                        var r4 = TryResolveIndirectString(def);
                        if (!string.IsNullOrWhiteSpace(r4)) return StripAccelerator(r4);

                        
                        using var server = clsid.OpenSubKey("InprocServer32", false)
                                    ?? clsid.OpenSubKey("LocalServer32", false)
                                    ?? clsid.OpenSubKey("InprocServer64", false)
                                    ?? clsid.OpenSubKey("LocalServer64", false);
                        var dll = server?.GetValue(null) as string;
                        if (!string.IsNullOrWhiteSpace(dll))
                        {
                            dll = Environment.ExpandEnvironmentVariables(dll.Trim().Trim('"'));
                            if (File.Exists(dll))
                            {
                                var info = FileVersionInfo.GetVersionInfo(dll);
                                var product = info.ProductName ?? info.FileDescription;
                                if (!string.IsNullOrWhiteSpace(product)) return StripAccelerator(product);
                            }
                        }

                        
                        var r6 = TryClsidAssocFallback(guid);
                        if (!string.IsNullOrWhiteSpace(r6)) return StripAccelerator(r6);
                    }
                }
                catch { }
            }

            
            
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                
                var g = NormalizeClsid(keyName);
                if (g != null)
                {
                    
                    try
                    {
                        using var wowClsid = Registry.ClassesRoot.OpenSubKey($@"WOW6432Node\CLSID\{{{g}}}", false);
                        if (wowClsid != null)
                        {
                            var wloc = TryResolveIndirectString(wowClsid.GetValue("LocalizedString") as string);
                            if (!string.IsNullOrWhiteSpace(wloc)) return StripAccelerator(wloc);
                            var wdef = TryResolveIndirectString(wowClsid.GetValue(null) as string);
                            if (!string.IsNullOrWhiteSpace(wdef)) return StripAccelerator(wdef);
                        }
                    }
                    catch { }
                }
                else
                {
                    
                    
                    var rProg = TryAssocQueryCached(keyName);
                    if (!string.IsNullOrWhiteSpace(rProg)) return StripAccelerator(rProg);
                }

                return StripAccelerator(keyName);
            }

            return null;
        }

        private static string TryResolveIndirectString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (!s.StartsWith("@", StringComparison.Ordinal)) return s;
            return NativeMethods.LoadIndirectString(s);
        }

        
        
        
        
        
        private static string TryClsidAssocFallback(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            try
            {
                using var clsid = OpenClsidKey(guid);
                if (clsid == null) return null;

                
                using (var shellEx = clsid.OpenSubKey("ShellEx", false))
                {
                    if (shellEx != null)
                    {
                        foreach (var shellExSubName in shellEx.GetSubKeyNames())
                        {
                            using var shellExSub = shellEx.OpenSubKey(shellExSubName, false);
                            if (shellExSub == null) continue;
                            
                            var defVal = shellExSub.GetValue(null) as string;
                            var r1 = TryAssocQueryCached(defVal);
                            if (!string.IsNullOrWhiteSpace(r1)) return r1;
                            
                            var r2 = TryAssocQueryCached(shellExSubName);
                            if (!string.IsNullOrWhiteSpace(r2)) return r2;
                        }
                    }
                }

                
                using (var defIcon = clsid.OpenSubKey("DefaultIcon", false))
                {
                    if (defIcon != null)
                    {
                        var subName = defIcon.GetSubKeyNames().FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(subName))
                        {
                            var r = TryAssocQueryCached(subName);
                            if (!string.IsNullOrWhiteSpace(r)) return r;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        
        private static string TryAssocQueryCached(string assoc)
        {
            if (string.IsNullOrWhiteSpace(assoc)) return null;
            lock (_assocCache)
            {
                if (_assocCache.TryGetValue(assoc, out var cached))
                    return cached;
            }
            var result = NativeMethods.GetAssocFriendlyDocName(assoc);
            lock (_assocCache)
            {
                _assocCache[assoc] = result;
            }
            return result;
        }

        
        
        
        
        
        public static string ResolveVerbName(string keyName, string muiVerb, string defaultValue)
        {
            
            if (!string.IsNullOrWhiteSpace(keyName) && _verbNameOverrides.TryGetValue(keyName, out var overrideName))
                return overrideName;

            if (!string.IsNullOrWhiteSpace(muiVerb))
            {
                if (IsResourceReference(muiVerb))
                {
                    var r = NativeMethods.LoadIndirectString(muiVerb);
                    if (!string.IsNullOrWhiteSpace(r)) return StripAccelerator(r);
                }
                return StripAccelerator(muiVerb);
            }
            if (!string.IsNullOrWhiteSpace(defaultValue)) return StripAccelerator(defaultValue);

            
            
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                var assocFriendly = TryAssocQueryCached(keyName);
                if (!string.IsNullOrWhiteSpace(assocFriendly)) return StripAccelerator(assocFriendly);
            }

            
            var fb = LookupFallbackZhName(keyName);
            if (!string.IsNullOrWhiteSpace(fb)) return fb;

            return keyName;
        }

        
        
        
        
        
        
        // 记事本（含 Windows 11 商店版 Notepad）按“普通应用”处理，不作为系统项隐藏。
        // 依据：其实现文件位于非 Windows 目录（如 C:\Program Files\WindowsApps\...\Notepad\），
        // 与系统内置组件不同，应始终在右键管理中显示。
        private static bool IsNotepadApp(string keyName, string guid)
        {
            if (ContainsToken(keyName, "notepad", "记事本", "windowsnotepad")) return true;
            if (ContainsToken(guid, "notepad", "记事本", "windowsnotepad")) return true;

            var g = NormalizeClsid(guid);
            if (g != null)
            {
                var server = GetClsidServerFile(g);
                if (ContainsToken(server, "notepad", "windowsnotepad")) return true;
                try
                {
                    using var clsidKey = OpenClsidKey(g);
                    var loc = clsidKey?.GetValue("LocalizedString") as string;
                    if (ContainsToken(loc, "notepad", "记事本", "windowsnotepad")) return true;
                }
                catch { }
            }
            return false;
        }

        private static bool ContainsToken(string s, params string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var lower = s.ToLowerInvariant();
            foreach (var t in tokens)
                if (!string.IsNullOrWhiteSpace(t) && lower.Contains(t.ToLowerInvariant()))
                    return true;
            return false;
        }

        public static bool IsSystemContextMenuItem(string keyName, string guid)
        {
            if (IsNotepadApp(keyName, guid)) return false; // 记事本：按普通应用显示，不隐藏

            if (!string.IsNullOrWhiteSpace(keyName) && BuiltInSystemVerbs.Contains(keyName)) return true;

            
            
            
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                try
                {
                    foreach (var classPath in new[]
                    {
                        @"*", @"Directory", @"Folder", @"AllFilesystemObjects", @"Drive",
                        @"DesktopBackground", @"Directory\Background",
                    })
                    {
                        using (var vk = Registry.ClassesRoot.OpenSubKey($@"{classPath}\shell\{keyName}", false))
                        {
                            if (vk != null)
                            {
                                var mui = vk.GetValue("MUIVerb") as string;
                                var def = vk.GetValue(null) as string;
                                var icon = vk.GetValue("Icon") as string;
                                if (IsSystemResourceReference(mui) || IsSystemResourceReference(def) || IsSystemResourceReference(icon))
                                    return true;

                                
                                using (var cmdKey = vk.OpenSubKey("command", false))
                                {
                                    var cmd = cmdKey?.GetValue(null) as string;
                                    if (!string.IsNullOrWhiteSpace(cmd))
                                    {
                                        var exe = ExtractFirstExecutablePath(cmd);
                                        if (!string.IsNullOrWhiteSpace(exe) && IsUnderWindowsDirectory(exe))
                                        {
                                            Debug.WriteLine($"[IsSystem] verb={keyName} command={exe} → system");
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            
            if (!string.IsNullOrWhiteSpace(guid))
            {
                var g = NormalizeClsid(guid);
                if (g != null)
                {
                    
                    var server = GetClsidServerFile(g);
                    if (IsUnderWindowsDirectory(server))
                    {
                        Debug.WriteLine($"[IsSystem] CLSID {guid} → server={server} → system");
                        return true;
                    }

                    
                    try
                    {
                        using var clsidKey = OpenClsidKey(g);
                        if (clsidKey != null)
                        {
                            var loc = clsidKey.GetValue("LocalizedString") as string;
                            if (IsSystemResourceReference(loc))
                            {
                                Debug.WriteLine($"[IsSystem] CLSID {guid} → LocalizedString={loc} → system");
                                return true;
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"[IsSystem] CLSID {guid} → clsidKey=null");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[IsSystem] CLSID {guid} → ex: {ex.Message}");
                    }

                    Debug.WriteLine($"[IsSystem] CLSID {guid} → server={server} not under windows, loc check failed");
                }
                else
                {
                    Debug.WriteLine($"[IsSystem] guid={guid} → NormalizeClsid returned null");
                }
            }

            return false;
        }

        
        
        
        
        private static bool IsSystemResourceReference(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (!s.StartsWith("@", StringComparison.Ordinal)) return false;
            var path = s.TrimStart('@');
            var comma = path.IndexOf(',');
            if (comma > 0) path = path.Substring(0, comma);
            path = Environment.ExpandEnvironmentVariables(path).Trim('"');
            try
            {
                var full = Path.GetFullPath(path);
                if (IsUnderWindowsDirectory(full)) return true;
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(parent) && IsUnderWindowsDirectory(parent)) return true;
            }
            catch { }
            return false;
        }

        public static bool IsWin11TopLevelExtension(string guid, string keyName)
        {
            var g = NormalizeClsid(guid);
            if (!string.IsNullOrWhiteSpace(g) && Win11CachedGuids.Value.Contains(g))
                return true;
            var k = NormalizeClsid(keyName);
            if (!string.IsNullOrWhiteSpace(k) && Win11CachedGuids.Value.Contains(k))
                return true;
            return false;
        }

        
        
        
        
        
        
        
        public static bool IsSystemNewMenuItem(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            try
            {
                
                using (var classKey = Registry.ClassesRoot.OpenSubKey(ext, false))
                {
                    if (classKey != null)
                    {
                        var loc = classKey.GetValue("LocalizedString") as string;
                        if (IsSystemResourceReference(loc)) return true;

                        var infoTip = classKey.GetValue("InfoTip") as string;
                        if (IsSystemResourceReference(infoTip)) return true;

                        var friendly = classKey.GetValue("FriendlyTypeName") as string;
                        if (IsSystemResourceReference(friendly)) return true;

                        using (var defIcon = classKey.OpenSubKey("DefaultIcon", false))
                        {
                            var dv = defIcon?.GetValue(null) as string;
                            if (IsSystemResourceReference("@" + dv)) return true; 
                            if (dv != null)
                            {
                                var comma = dv.IndexOf(',');
                                var p = (comma > 0 ? dv.Substring(0, comma) : dv).Trim('"');
                                p = Environment.ExpandEnvironmentVariables(p);
                                if (IsUnderWindowsDirectory(p)) return true;
                            }
                        }

                        using (var shellNew = classKey.OpenSubKey("ShellNew", false))
                        {
                            var iconPath = shellNew?.GetValue("IconPath") as string;
                            if (iconPath != null && IsUnderWindowsDirectory(Environment.ExpandEnvironmentVariables(iconPath)))
                                return true;
                        }
                    }
                }

                
                if (ProgIdServerUnderWindows(ext)) return true;
            }
            catch { }
            return false;
        }

        
        
        

        
        
        
        
        
        
        
        
        public static IEnumerable<string> EnumerateNewMenuCandidateKeys(RegistryKey classesRoot)
        {
            if (classesRoot == null) yield break;
            string[] names;
            try { names = classesRoot.GetSubKeyNames(); }
            catch { yield break; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    if (seen.Add(name)) yield return name;
                    continue;
                }
                
                
                if (IsLikelyShellNewCarrier(name))
                {
                    if (seen.Add(name)) yield return name;
                    continue;
                }
                
                
                var extraCandidates = new List<string>();
                try
                {
                    using var k = classesRoot.OpenSubKey(name, false);
                    if (k != null)
                    {
                        var subs = k.GetSubKeyNames();
                        bool hasShellNew = Array.Exists(subs, s => s.Equals("ShellNew", StringComparison.OrdinalIgnoreCase));
                        bool hasNestedProgId = false;
                        foreach (var s in subs)
                        {
                            if (NonProgIdSubKeysOfExt.Contains(s)) continue;
                            using var subK = k.OpenSubKey(s, false);
                            if (subK == null) continue;
                            var subSubs = subK.GetSubKeyNames();
                            if (Array.Exists(subSubs, ss => ss.Equals("ShellNew", StringComparison.OrdinalIgnoreCase)))
                            { hasNestedProgId = true; break; }
                        }
                        if (hasShellNew || hasNestedProgId)
                            if (seen.Add(name)) extraCandidates.Add(name);
                    }
                }
                catch { }
                foreach (var c in extraCandidates)
                    yield return c;
            }
        }

        
        
        
        
        
        private static bool IsLikelyShellNewCarrier(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return false;
            var k = keyName;
            
            if (k.EndsWith("file", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.EndsWith("folder", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.EndsWith("briefcase", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.Equals("directory", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.Equals("allfilesystemobjects", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.Equals("drive", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return true;
            
            if (k.StartsWith("AppX", StringComparison.Ordinal)) return true;
            return false;
        }

        
        
        
        
        
        
        
        public static string ResolveNewMenuItemDisplayName(string keyName, string nestedRegistryPath = null)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return null;

            
            string nestedProgIdDesc = null;
            if (!string.IsNullOrWhiteSpace(nestedRegistryPath))
            {
                var parts = nestedRegistryPath.Split('\\');
                if (parts.Length >= 2)
                {
                    var progIdName = parts[parts.Length - 1];
                    nestedProgIdDesc = ResolveClassKeyDefaultName(progIdName);
                }
            }

            
            string keyDesc = ResolveClassKeyDefaultName(keyName);

            
            if (string.IsNullOrWhiteSpace(keyDesc))
                keyDesc = NativeMethods.GetAssocFriendlyDocName(keyName);

            
            if (!keyName.StartsWith(".", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(nestedProgIdDesc))
                    return $"{(keyDesc ?? keyName)} ({nestedProgIdDesc})";
                if (!string.IsNullOrWhiteSpace(keyDesc)) return keyDesc;
                
                var fb = LookupFallbackZhName(keyName);
                if (!string.IsNullOrWhiteSpace(fb)) return fb;
                return keyName;
            }

            
            var effectiveDesc = nestedProgIdDesc ?? keyDesc;
            if (!string.IsNullOrWhiteSpace(effectiveDesc))
                return $"{keyName} ({effectiveDesc})";

            
            var fbExt = LookupFallbackZhName(keyName);
            if (!string.IsNullOrWhiteSpace(fbExt)) return $"{keyName} ({fbExt})";

            return keyName;
        }

        
        
        
        
        
        
        
        private static string ResolveClassKeyDefaultName(string classKeyName)
            => ResolveClassKeyDefaultNameInternal(classKeyName, null);

        private static string ResolveClassKeyDefaultNameInternal(string classKeyName, HashSet<string> visited)
        {
            if (string.IsNullOrWhiteSpace(classKeyName)) return null;

            
            visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!visited.Add(classKeyName)) return null;
            if (visited.Count > 3) return null;  

            try
            {
                using var k = Registry.ClassesRoot.OpenSubKey(classKeyName, false);
                if (k == null) return null;

                
                var r1 = TryResolveIndirectString(k.GetValue("FriendlyTypeName") as string);
                if (!string.IsNullOrWhiteSpace(r1)) return StripAccelerator(r1);

                
                var r2 = TryResolveIndirectString(k.GetValue("LocalizedString") as string);
                if (!string.IsNullOrWhiteSpace(r2)) return StripAccelerator(r2);

                
                using (var defIcon = k.OpenSubKey("DefaultIcon", false))
                {
                    if (defIcon != null)
                    {
                        var r3a = TryResolveIndirectString(defIcon.GetValue("FriendlyTypeName") as string);
                        if (!string.IsNullOrWhiteSpace(r3a)) return StripAccelerator(r3a);
                    }
                }

                
                using (var shellOpen = k.OpenSubKey(@"shell\open\command", false))
                {
                    if (shellOpen != null)
                    {
                        var r4a = TryResolveIndirectString(shellOpen.GetValue("FriendlyTypeName") as string);
                        if (!string.IsNullOrWhiteSpace(r4a)) return StripAccelerator(r4a);
                    }
                }

                
                var r5 = TryResolveIndirectString(k.GetValue("InfoTip") as string);
                if (!string.IsNullOrWhiteSpace(r5)) return StripAccelerator(r5);

                
                var def = k.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(def))
                {
                    if (LooksLikeProgIdPointer(def))
                    {
                        var progDesc = ResolveClassKeyDefaultNameInternal(def, visited);
                        if (!string.IsNullOrWhiteSpace(progDesc)) return StripAccelerator(progDesc);
                    }
                    var r6 = TryResolveIndirectString(def);
                    if (!string.IsNullOrWhiteSpace(r6)) return StripAccelerator(r6);
                }
            }
            catch { }

            
            var r7 = TryAssocQueryCached(classKeyName);
            if (!string.IsNullOrWhiteSpace(r7)) return StripAccelerator(r7);

            return null;
        }

        
        
        
        
        private static bool LooksLikeProgIdPointer(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            
            if (s.Contains(' ')) return false;
            
            if (s.StartsWith("@", StringComparison.Ordinal)) return false;
            
            if (s.Contains('\\') || s.Contains('/')) return false;
            
            foreach (var c in s)
                if (c > 0x4E00 && c < 0x9FFF) return false;
            
            
            if (s.Length > 64) return false;  
            
            if (!char.IsLetter(s[0])) return false;
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') continue;
                return false;
            }
            return true;
        }
    }
}
