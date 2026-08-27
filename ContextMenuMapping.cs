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
    /// <summary>
    /// 右键菜单（上下文菜单）的系统判定与显示名解析。
    /// 解析策略：补充映射清单优先（已知 CLSID 直接返回中文），动态注册表读取为补充。
    /// 视图模型仅通过本类静态查询接口访问。
    /// </summary>
    public static class ContextMenuMapping
    {
        // ─────────────────────────────────────────────────────────────
        // 1. Windows 内置核心动词名（用于快速判定系统自带动词）
        //    系统项判定以动态注册表读取为主：资源引用字符串 / command 路径 / 服务器 DLL 路径。
        // ─────────────────────────────────────────────────────────────
        private static readonly HashSet<string> BuiltInSystemVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            // 核心文件操作
            "open", "opennew", "opennewprocess", "opennewtab", "opennewwindow",
            "edit", "print", "printto", "explore", "runas", "find", "findtargets",
            "properties", "cut", "copy", "paste", "delete", "rename",
            "cmd", "powershell",
            // 固定 / 库 / 属性
            "pintohome", "pintohomefile", "pintotaskbar", "pintostartscreen", "removeproperties",
            "includeinlibrary", "addtolibraryfromsearch",
            // BitLocker
            "UpdateEncryptionSettings", "UpdateEncryptionSettingsWork",
            "encrypt-bde", "encrypt-bde-elev", "manage-bde",
            "resume-bde", "resume-bde-elev", "unlock-bde",
            "change-passphrase", "change-pin",
            // 桌面背景 / 聚焦
            ".SpotlightLearnMore", ".SpotlightNextImage",
            "Display", "EditStickers", "Personalize",
            // 常见 Windows 系统功能动词（右键管理中应默认隐藏）
            "PreviousVersions", "RestorePreviousVersions",           // 还原以前的版本
            "Burn",                                                  // 刻录到光盘
            "SyncCenter", "AlwaysAvailableOffline",                // 同步中心 / 始终脱机可用
            "DeviceCenter", "PortableDevicesMenu",                 // 便携设备菜单
            "Share",                                                 // 共享
            "CopyAsPath",                                            // 复制路径
            "SendTo", "CreateShortcut",                            // 发送到 / 创建快捷方式
            "SetAsDesktopBackground", "SetAsLockScreen",           // 设为桌面背景 / 锁屏
            "RotateLeft", "RotateRight",                           // 向左/右旋转
            "EditWithPhotos", "EditWithPaint3D", "EditWithClipchamp",
            "MapNetworkDrive", "DisconnectNetworkDrive", "Format", "Eject", "AutoPlay",
            "Scan", "Acquire", "PrintToPDF", "PrintToFax",
            "Troubleshoot", "TroubleshootCompatibility", "RunAsDate",
            "LaunchPreviewHandler", "OpenWith", "OpenWithList",
        };

        /// <summary>扩展名键下不应作为 ProgID 的子键噪音名集合。</summary>
        internal static readonly HashSet<string> NonProgIdSubKeysOfExt = new(StringComparer.OrdinalIgnoreCase)
        {
            "OpenWithProgids", "OpenWithList", "ShellEx", "ShellNew",
            "PersistentHandler", "Content Type", "PerceivedType"
        };

        /// <summary>通用文件右键类键（*、Directory、Folder、Drive 等）。</summary>
        internal static readonly (string ClassPath, string Scope)[] GenericFileRightClickClasses = new (string, string)[]
        {
            (@"*", "所有文件"),
            (@"Directory", "文件夹"),
            (@"Folder", "文件夹"),
            (@"Drive", "驱动器"),
            (@"AllFilesystemObjects", "所有对象"),
        };

        /// <summary>通用桌面/背景右键类键（DesktopBackground、Directory\Background）。</summary>
        internal static readonly (string ClassPath, string Scope)[] GenericDesktopRightClickClasses = new (string, string)[]
        {
            (@"DesktopBackground", "桌面背景"),
            (@"Directory\Background", "文件夹背景"),
        };

        /// <summary>垃圾清理"无效右键菜单"所用的通用类键（含桌面背景）。</summary>
        internal static readonly string[] InvalidContextMenuGenericClasses = new[]
        {
            "*", "Directory", "Folder", "Drive", "DesktopBackground", @"Directory\Background"
        };

        /// <summary>Classes 根（64 位 + 32 位 WOW6432Node），用于垃圾清理扫描。</summary>
        internal static readonly string[] ClassRootsForInvalidScan = new[]
        {
            @"SOFTWARE\Classes",
            @"SOFTWARE\WOW6432Node\Classes"
        };

        /// <summary>受保护的内置动词（不允许在右键管理中删除/禁用）。</summary>
        internal static readonly HashSet<string> ProtectedVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "open", "opennew", "edit", "print", "explore", "runas", "find",
            "properties", "cut", "copy", "paste", "delete", "rename"
        };

        // ─────────────────────────────────────────────────────────────
        // 2. 动态判定辅助（无任何硬编码映射，仅基于注册表值 + Windows 目录判断）
        // ─────────────────────────────────────────────────────────────
        /// <summary>判断字符串是否为系统 DLL 资源引用形式（"@shell32.dll,-8770" 或 @{包?ms-resource}）。</summary>
        private static bool IsResourceReference(string s) => !string.IsNullOrWhiteSpace(s) && s.StartsWith("@", StringComparison.Ordinal);

        /// <summary>判断文件是否位于 Windows 系统目录（%WINDIR%\*、System32、SysWOW64）。</summary>
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

        /// <summary>
        /// 从命令行字符串中提取第一个可执行文件路径。
        /// 支持 "C:\path\a.exe" arg1、C:\path\a.exe arg1、%SystemRoot%\a.exe 等常见形式。
        /// 仅用于系统目录判定，不保证解析 100% 复杂命令行。
        /// </summary>
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

        /// <summary>打开 HKCR 下的 CLSID 键（优先 HKLM，其次 HKCU，再次 WOW6432Node）。</summary>
        private static RegistryKey OpenClsidKey(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            var g = guid.Trim('{', '}');
            var clsidPath = $@"CLSID\{{{g}}}";

            // 优先 HKLM（系统级），因为 HKCR 在非管理员下可能受注册表虚拟化影响
            var hklm = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\{clsidPath}", false);
            if (hklm != null) return hklm;

            // 其次 HKCU（用户级）
            var hkcu = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Classes\{clsidPath}", false);
            if (hkcu != null) return hkcu;

            // 最后 WOW6432Node
            var wow = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\WOW6432Node\{clsidPath}", false);
            if (wow != null) return wow;

            return null;
        }

        /// <summary>读取 CLSID 的服务器文件（Inproc/LocalServer32）路径，用于判定是否系统 DLL。</summary>
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

        /// <summary>
        /// 给定扩展名（.txt）或特殊键名（Directory），动态判定是否为系统项：
        /// 取其 ProgID → DefaultIcon 文件 或 shell\open\command → 服务器在 Windows 目录下即判为系统项。
        /// </summary>
        private static bool ProgIdServerUnderWindows(string extOrSpecialKey)
        {
            if (string.IsNullOrWhiteSpace(extOrSpecialKey)) return false;
            try
            {
                string progId = null;
                // 扩展名键：读取默认值作为 ProgID，不存在时直接用扩展名键本身
                using (var extKey = Registry.ClassesRoot.OpenSubKey(extOrSpecialKey, false))
                {
                    if (extKey == null) return false;
                    progId = extKey.GetValue(null) as string;
                }
                if (string.IsNullOrWhiteSpace(progId)) progId = extOrSpecialKey;

                // 通过 ProgID 打开，判断 DefaultIcon 文件或 open\command 是否在 Windows 目录下
                using var progKey = Registry.ClassesRoot.OpenSubKey(progId, false);
                if (progKey == null) return false;

                using (var iconKey = progKey.OpenSubKey("DefaultIcon", false))
                {
                    var v = iconKey?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        // DefaultIcon 可能是 "C:\xxx.dll,0" 形式，去掉逗号后的索引
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

        // ─────────────────────────────────────────────────────────────
        // 3. Windows 11 缓存判定（从注册表 Shell Extensions\Cached 动态读取）
        //    仅对 Shell 扩展（CLSID）有效；Win11 动词无可靠的注册表缓存列表，不做顶层判定。
        // ─────────────────────────────────────────────────────────────

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

        // ─────────────────────────────────────────────────────────────
        // 4. 统一查询接口（动态优先：注册表 + DLL 产品名 + Windows API 兜底）
        //    最后兜底：已知 Shell 扩展 CLSID/子键名 → 中文名 补充映射清单
        //    仅在 7 层动态解析全部失败时使用，保证显示中文。
        // ─────────────────────────────────────────────────────────────
        private static readonly Regex _accelRegex = new Regex(@"\s*\(&[A-Za-z0-9]\)\s*", RegexOptions.Compiled);

        // AssocQueryString 结果缓存，避免同一扩展名/ProgID 重复调用昂贵 API
        private static readonly Dictionary<string, string> _assocCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 动词键名 → 中文名的高优先级覆盖表。
        /// 这些动词在注册表里的默认值/MUIVerb 常为英文（如 ThunderShell、git_shell），
        /// 若走普通解析会直接返回英文，用户无法一眼看出是什么；这里强制给出中文。
        /// </summary>
        private static readonly Dictionary<string, string> _verbNameOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            { "ThunderShell", "迅雷" },
            { "YunShellExplorerCommand", "百度网盘" },
            { "git_shell", "在此处打开 Git Bash" },
            { "git_gui", "在此处打开 Git GUI" },
        };

        /// <summary>
        /// 已知 Shell 扩展 CLSID / ContextMenuHandlers 子键名 → 中文名 补充映射清单。
        /// 仅在动态解析链（注册表 LocalizedString / DLL 产品名 / AssocQueryStringW 等）
        /// 全部失败后作为最终兜底，保证显示中文。
        /// 键不区分大小写；键可为以下形式：
        ///   ① 纯 GUID（如 "90AA3A4E-1CE8-4D3F-95A7-1C40B2A666D5"，带不带花括号均可）
        ///   ② ContextMenuHandlers 子键名（如 "AIMP Shell Unit"、"WinRAR"）
        ///   ③ 动词键名（如 "open"/"print"，用于 ResolveVerbName 兜底）
        ///   ④ 扩展名（如 ".txt"/".docx"，用于 ResolveNewMenuItemDisplayName 兜底）
        /// </summary>
        private static readonly Dictionary<string, string> _displayNameFallback = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Windows 内置 Shell 扩展 CLSID（LocalizedString 真的没注册中文的） ──
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

            // ── 常见第三方软件英文子键名 → 中文名 ──
            { "AIMP Shell Unit", "AIMP 音乐播放器" },
            { "WinRAR", "WinRAR 压缩文件" },
            { "7-Zip", "7-Zip 压缩文件" },
            { "AAbdzCtx Class", "ACDSee 相册扩展" },
            { "AccExt Class", "ACDSee 扩展" },
            { "Briefcase", "公文包" },
            { "ShellFolder", "Shell 文件夹" },

            // ── 常见系统英文动词名 → 中文名（用于 ResolveVerbName 兜底） ──
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

            // ── 常见 Windows 系统功能动词 → 中文名（用于 ResolveVerbName 兜底） ──
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

            // ── 新建菜单常见扩展名 → 中文名（用于 ResolveNewMenuItemDisplayName 兜底） ──
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

            // ── ContextMenuManager 权威 CLSID→中文映射（188 条显式中文名） ──
            // 来源：BluePointLilac/ContextMenuManager GuidInfosDic.ini
            // 压缩/归档
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
            // 杀毒/安全
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
            // 传输/网盘
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
            // PDF/文档
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
            // 注：29424E91 已删除（转转大师，杀软误报触发源）
            { "098A124A-AA1C-38C8-A65E-D1199A14516A", "Convert to...(万兴PDF专家)" },
            { "FB074836-8286-4089-84DC-F504E9EF621C", "ABBYY FineReader" },
            { "D16ADFE3-66C5-4DF5-9978-87DE0C687E85", "嗨格式PDF转换器" },
            { "2ACD35AB-F74A-4C20-AA9B-2DE80081626D", "PDF-XChange Editor" },
            { "6C405CEC-8624-4FEF-B3BA-9D4E5A8F58B5", "迅捷PDF转换器" },
            // 影音/图片
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
            // 桌面/美化
            { "CF444751-60FC-48B8-AC0F-363063EB2A9E", "开启桌面整理(电脑管家)" },
            { "B3A2F1A4-10A2-410C-9C19-622B621C61D0", "换壁纸(小鸟壁纸)" },
            { "3B7162FB-4389-40C8-83A5-DA10D491EC66", "映射此文件夹到桌面(小智桌面)" },
            { "96BABF4F-7C38-4885-B407-3800F461669B", "打开火萤视频桌面" },
            { "C3C76829-1D8B-49A2-AFB4-196C443F1B0B", "人工桌面" },
            { "2D5AD9EB-31BC-48F7-A438-28F363632C73", "开启布丁桌面" },
            { "B5E436BC-642A-4BF6-B725-26038AF26E89", "开启桌面整理(元气、猎豹轻桌面)" },
            { "5F8D079B-8CE6-4F58-BF10-55C1B68D88F3", "选择颜色设置" },
            { "6C125022-639D-43CC-9F3D-647E6CC69EF1", "右键菜单背景插件(ContextBG)" },
            // 开始菜单替代
            { "A2A9545D-A0C2-42B4-9708-A0B2BADD77C9", "附到「开始」菜单(StartIsBack)" },
            { "6A451C0A-9597-4915-BCCE-6E859BC996B2", "Pin to Start (Start10)" },
            { "AF8FA9C9-9907-463E-BDC3-4CC1200D6310", "Start Menu 8" },
            { "07451604-FBE4-4475-9DD6-261B7B619417", "电脑管家经典开始菜单" },
            // 编辑器/工具
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
            // 开发/虚拟化
            { "30351349-7B7D-4FCC-81B4-1E394CA267EB", "TortoiseSVN" },
            { "10A0FDD2-B0C0-4CD4-A7AE-E594CE3B91C8", "TortoiseGit" },
            { "271DC252-6FE1-4D59-9053-E4CF50AB99DE", "VMware" },
            { "E4D28EDC-8C0B-43EE-9E7D-C8A8682334DC", "VMware 64bit" },
            { "C52B9871-E5E9-41FD-B84D-C5ACADBEC7AE", "Locale Emulator" },
            { "0A479751-02BC-11D3-A855-0004AC2568AA", "NTFS文件连接扩展配置工具" },
            { "FDF253AC-1724-4853-BE34-C2DBC18FB5CA", "Copywhiz" },
            { "9F156763-7844-4DC4-B2B1-901F640F5155", "Open in Windows Terminal" },
            // 其他
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

            // ── 本机实测补充（用户系统实际存在的第三方/系统项） ──
            { "90AA3A4E-1CBA-4233-B8BB-535773D48449", "固定到任务栏" },
            { "ADEA1FEC-5832-4B19-9B1D-C6EF361BD85D", "腾讯应用宝" },
            { "99D4E39A-EA7F-453E-9488-58C56BBF7B98", "夸克 AI" },
            { "D7D43EA6-BCE2-489C-9A70-9027A42E1EFD", "夸克网盘 AI" },
        };

        /// <summary>
        /// 系统 Shell 扩展 CLSID → @资源引用字符串（如 @shell32.dll,-8770）。
        /// 这些 CLSID 在注册表中没有 LocalizedString 或值为英文，但资源引用可解析出中文。
        /// 来源：BluePointLilac/ContextMenuManager GuidInfosDic.ini 的 ResText 条目。
        /// </summary>
        private static readonly Dictionary<string, string> _clsidResText = new(StringComparer.OrdinalIgnoreCase)
        {
            { "20D04FE0-3AEA-1069-A2D8-08002B30309D", "@shell32.dll,-8770" },       // 此电脑
            { "00021401-0000-0000-C000-000000000046", "@windows.storage.dll,-8496" }, // 快捷方式
            { "37EA3A21-7493-4208-A011-7F9EA79CE9F5", "@shell32.dll,-1033" },        // 打开方式
            { "FF609CC7-D34D-4049-A1AA-2293517FFCC6", "@stobject.dll,-417" },         // 可移动设备
            { "0BF754AA-C967-445C-AB3D-D8FDA9BAE7EF", "@stobject.dll,-416" },         // 可移动设备2
            { "3080F90E-D7AD-11D9-BD98-0000947B0257", "@shell32.dll,-12715" },        // 桌面背景
            { "470C0EBD-5D73-4D58-9CED-E91E22E23282", "@shell32.dll,-51201" },        // 属性
            { "90AA3A4E-1CBA-4233-B8BB-535773D48449", "@shell32.dll,-5386" },         // 压缩文件夹拖拽
            { "F3D06E7C-1E45-4A26-847E-F9FCDEE59BE0", "@shell32.dll,-30328" },        // 固定到任务栏
            { "C2FBB630-2971-11D1-A18C-00C04FD75D13", "@shell32.dll,-30304" },        // 复制到文件夹
            { "C2FBB631-2971-11D1-A18C-00C04FD75D13", "@shell32.dll,-30305" },        // 移动到文件夹
            { "B8CDCB65-B1BF-4B42-9428-1DFDB7EE92AF", "@shell32.dll,-37514" },        // 发送到
            { "09799AFB-AD67-11D1-ABCD-00C04FC30936", "@shell32.dll,-5376" },         // 共享
            { "D969A300-E7FF-11D0-A93B-00A0C90F2719", "@shell32.dll,-95851" },        // 创建快捷方式
            { "A470F8CF-A1E8-4F65-8335-227475AA5C46", "@acppage.dll,-2022" },         // 加密
            { "7AD84985-87B4-4A16-BE58-8B72A5B390F7", "@playtomenu.dll,-101" },       // 播放到
            { "E2BF9676-5F8F-435C-97EB-11607A5BEDF7", "@ntshrui.dll,-107" },          // 共享(NT)
            { "E61BF828-5E63-4287-BEF1-60B1A4FDE0E3", "@WorkfoldersControl.dll,-1" }, // 工作文件夹
            { "F81E9010-6EA4-11CE-A7FF-00AA003CA9F6", "@ntshrui.dll,-103" },          // 共享和安全
            { "474C98EE-CF3D-41F5-80E3-4AAB0AB04301", "@cscui.dll,-38" },             // 脱机文件
            { "2854F705-3548-414C-A113-93E27C808C85", "@EhStorShell.dll,-101" },       // 增强存储
            { "FBEB8A05-BEEE-4442-804E-409D6C4515E9", "@shell32.dll,-12560" },        // 打印
            { "D6791A63-E7E2-4FEE-BF52-5DED8E86E9B8", "@wpdshext.dll,-511" },          // 便携设备
            { "0AF96EDE-AEBF-41ED-A1C8-CF7A685505B6", "@shell32.dll,-34618" },         // OneDrive 占位
            { "596AB062-B4D2-4215-9F74-E9109B0A8153", "@twext.dll,-1037" },            // 以前的版本
            { "85BBD920-42A0-1069-A2E4-08002B30309D", "@shell32.dll,-22978" },         // 公文包
            { "1A184871-359E-4F67-AAD9-5B9905D62232", "@fontext.dll,-340" },           // 字体
            { "645FF040-5081-101B-9F08-00AA002F954E", "@mmres.dll,-5831" },            // 回收站
            { "BD472F60-27FA-11CF-B8B4-444553540000", "@zipfldr.dll,-10095" },         // 压缩文件夹
            { "5635493F-7D77-4372-A839-8AD89F5B3726", "@msimsg.dll,-38" },             // 安装程序
        };

        private static string StripAccelerator(string name)
            => string.IsNullOrWhiteSpace(name) ? name : _accelRegex.Replace(name, "").Trim();

        /// <summary>
        /// 在补充映射清单中查询中文名。键可为 GUID（带/不带花括号）、子键名、动词名、扩展名。
        /// 同时检查 _clsidResText（@资源引用），通过 LoadIndirectString 实时解析中文。
        /// 找不到返回 null。
        /// </summary>
        private static string LookupFallbackZhName(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            // ① 直接用原值查显式中文名
            if (_displayNameFallback.TryGetValue(key, out var v1)) return v1;
            // ② 若是 GUID 样式，剥掉花括号再查一次
            var g = NormalizeClsid(key);
            if (g != null)
            {
                if (_displayNameFallback.TryGetValue(g, out var v2)) return v2;
                // ③ 加花括号再查一次
                if (_displayNameFallback.TryGetValue($"{{{g}}}", out var v3)) return v3;
                // ④ 查 @资源引用字典，实时解析中文
                if (_clsidResText.TryGetValue(g, out var resText) ||
                    _clsidResText.TryGetValue($"{{{g}}}", out resText))
                {
                    var resolved = NativeMethods.LoadIndirectString(resText);
                    if (!string.IsNullOrWhiteSpace(resolved)) return StripAccelerator(resolved);
                }
            }
            return null;
        }

        /// <summary>
        /// 解析 Shell 扩展 CLSID 的中文显示名。
        /// ⓪ 补充映射清单优先（已知 CLSID 直接返回中文，避免被注册表英文默认值/DLL产品名截断）
        /// ① 注册表 CLSID\LocalizedString（通过 SHLoadIndirectString 解析资源）
        /// ② CLSID 的 DefaultIcon\FriendlyTypeName / shell\open\command\FriendlyTypeName
        /// ③ AssocQueryStringW ASSOCSTR_FRIENDLYDOCNAME（Windows 官方 API 兜底）
        /// ④ 注册表 CLSID 默认值
        /// ⑤ InprocServer32/LocalServer32 DLL 的文件产品名/文件描述
        /// ⑥ ContextMenuHandlers 子键名本身（如 "7-Zip"）作为兜底。
        /// </summary>
        public static string ResolveClsidName(string guid, string keyName)
        {
            // ⓪ 优先查补充映射清单：已知 CLSID/子键名直接返回中文，
            //    避免后续动态解析返回英文 CLSID 默认值或 DLL 产品名
            var fbGuid = LookupFallbackZhName(guid);
            if (!string.IsNullOrWhiteSpace(fbGuid)) return fbGuid;
            var fbKey = LookupFallbackZhName(keyName);
            if (!string.IsNullOrWhiteSpace(fbKey)) return fbKey;

            // ① 尝试通过注册表 CLSID 直接解析（多层回退）
            if (!string.IsNullOrWhiteSpace(guid))
            {
                try
                {
                    using var clsid = OpenClsidKey(guid);
                    if (clsid != null)
                    {
                        // 1) LocalizedString（最直接的本地化显示名，系统项多为 @shell32.dll,-xxxx 资源引用）
                        var localized = clsid.GetValue("LocalizedString") as string;
                        var r1 = TryResolveIndirectString(localized);
                        if (!string.IsNullOrWhiteSpace(r1)) return StripAccelerator(r1);

                        // 2) DefaultIcon\FriendlyTypeName（部分 Shell 扩展在此存显示名，AppX 注册的 Shell 扩展多走此路径）
                        using (var defIcon = clsid.OpenSubKey("DefaultIcon", false))
                        {
                            if (defIcon != null)
                            {
                                var friendly = defIcon.GetValue("FriendlyTypeName") as string;
                                var r2 = TryResolveIndirectString(friendly);
                                if (!string.IsNullOrWhiteSpace(r2)) return StripAccelerator(r2);

                                // DefaultIcon 默认值
                                var di = defIcon.GetValue(null) as string;
                                var r2b = TryResolveIndirectString(di);
                                if (!string.IsNullOrWhiteSpace(r2b)) return StripAccelerator(r2b);
                            }
                        }

                        // 3) shell\open\command\FriendlyTypeName（Shell 扩展把"打开"命令显示名放这里）
                        using (var shellOpen = clsid.OpenSubKey(@"shell\open\command", false))
                        {
                            if (shellOpen != null)
                            {
                                var ft = shellOpen.GetValue("FriendlyTypeName") as string;
                                var r3 = TryResolveIndirectString(ft);
                                if (!string.IsNullOrWhiteSpace(r3)) return StripAccelerator(r3);
                            }
                        }

                        // 4) CLSID 默认值
                        var def = clsid.GetValue(null) as string;
                        var r4 = TryResolveIndirectString(def);
                        if (!string.IsNullOrWhiteSpace(r4)) return StripAccelerator(r4);

                        // 5) InprocServer32/LocalServer32 DLL 文件版本信息
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

                        // 6) AssocQueryStringW 兜底（通过 ShellEx 子键名/默认值反查 ProgID）
                        var r6 = TryClsidAssocFallback(guid);
                        if (!string.IsNullOrWhiteSpace(r6)) return StripAccelerator(r6);
                    }
                }
                catch { }
            }

            // ② keyName（ContextMenuHandlers 子键名）本身不是 GUID 时：
            //    先尝试 AssocQueryStringW 把它当 ProgID 查友好名（baidunetdisk→百度网盘, 7-Zip→7-Zip 压缩文件等）
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                // 如果 keyName 是 GUID 样式但没在 CLSID 找到，先试 AssocQueryStringW 看是否有文件关联映射
                var g = NormalizeClsid(keyName);
                if (g != null)
                {
                    // 是 GUID 但 CLSID 解析已失败，尝试 WOW6432Node CLSID 一次
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
                    // keyName 不是 GUID：通常是 ProgID（baidunetdisk/7-Zip/WinRAR/AIMP Shell Unit 等），
                    // 直接用 AssocQueryStringW 取友好名。
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

        /// <summary>
        /// 对 CLSID 做 AssocQueryString 兜底。AssocQueryString 只接受扩展名或 ProgID，
        /// 不接受 CLSID\{guid}。因此先尝试通过 HKCR\CLSID\{guid}\ShellEx 下的
        /// ContextMenuHandlers/Icon 子键名（往往就是 ProgID），再用 AssocQueryString 查一次。
        /// </summary>
        private static string TryClsidAssocFallback(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            try
            {
                using var clsid = OpenClsidKey(guid);
                if (clsid == null) return null;

                // ① ShellEx 子键下的子键名（如 "ContextMenuHandlers" → 其默认值或子键名本身可能就是 ProgID）
                using (var shellEx = clsid.OpenSubKey("ShellEx", false))
                {
                    if (shellEx != null)
                    {
                        foreach (var shellExSubName in shellEx.GetSubKeyNames())
                        {
                            using var shellExSub = shellEx.OpenSubKey(shellExSubName, false);
                            if (shellExSub == null) continue;
                            // 取该子键的默认值
                            var defVal = shellExSub.GetValue(null) as string;
                            var r1 = TryAssocQueryCached(defVal);
                            if (!string.IsNullOrWhiteSpace(r1)) return r1;
                            // 子键名本身可能就是 ProgID
                            var r2 = TryAssocQueryCached(shellExSubName);
                            if (!string.IsNullOrWhiteSpace(r2)) return r2;
                        }
                    }
                }

                // ② DefaultIcon 子键名也可能是 ProgID
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

        /// <summary>带缓存的 AssocQueryString 调用，避免同一 assoc 字符串重复调用昂贵的 Windows API。</summary>
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

        /// <summary>
        /// 解析 Shell 动词键的显示名。无任何硬编码映射清单：
        /// 优先 MUIVerb（本地化字符串，自动解析资源），其次默认值，再用 AssocQueryStringW 兜底，
        /// 再用补充映射表最终兜底，最后返回动词键名。
        /// </summary>
        public static string ResolveVerbName(string keyName, string muiVerb, string defaultValue)
        {
            // 高优先级覆盖：注册表默认值是英文的已知动词直接给中文
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

            // AssocQueryStringW 兜底：用动词键名 + 扩展名探测友好名
            // 部分系统动词（如 "open"/"print"）虽然没注册 MUIVerb，但 AssocQueryStringW 能查到
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                var assocFriendly = TryAssocQueryCached(keyName);
                if (!string.IsNullOrWhiteSpace(assocFriendly)) return StripAccelerator(assocFriendly);
            }

            // 补充映射清单最终兜底（保证系统动词一定有中文，如 open→打开、print→打印）
            var fb = LookupFallbackZhName(keyName);
            if (!string.IsNullOrWhiteSpace(fb)) return fb;

            return keyName;
        }

        /// <summary>
        /// 动态判定是否为系统自带右键菜单项（应被屏蔽，不显示在"右键管理"）。
        /// 无任何硬编码黑名单：
        ///   动词：键名在内置动词集合 或 MUIVerb/默认值 是系统 DLL 资源引用
        ///   扩展：GUID 对应服务器 DLL 在 Windows 目录下
        /// </summary>
        public static bool IsSystemContextMenuItem(string keyName, string guid)
        {
            // ① 动词键名 → 内置动词集合
            if (!string.IsNullOrWhiteSpace(keyName) && BuiltInSystemVerbs.Contains(keyName)) return true;

            // ② 动词键名 → 若能在任意通用类键的 shell 下读到 MUIVerb 或 默认值 为系统 DLL 资源引用，
            //    或 command 指向 Windows 系统目录下的可执行文件 → 视为系统项。
            //    覆盖所有类键：*/Directory/Folder/AllFilesystemObjects/Drive/DesktopBackground/Directory\Background
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

                                // command 指向系统目录下可执行文件（如 rundll32.exe / shell32.dll）
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

            // ③ GUID → 对应服务器 DLL 在 Windows 目录下（shell32.dll 等系统组件）
            if (!string.IsNullOrWhiteSpace(guid))
            {
                var g = NormalizeClsid(guid);
                if (g != null)
                {
                    // 3a. InprocServer32/LocalServer32 路径在 Windows 目录下
                    var server = GetClsidServerFile(g);
                    if (IsUnderWindowsDirectory(server))
                    {
                        Debug.WriteLine($"[IsSystem] CLSID {guid} → server={server} → system");
                        return true;
                    }

                    // 3b. CLSID 下 LocalizedString 是系统 DLL 资源引用 → 视为系统项
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

        /// <summary>
        /// 判定注册表值字符串是否引用了系统 DLL 的资源（@shell32.dll / @C:\Windows\... / @%SystemRoot%\... 等）。
        /// 与 IsResourceReference 相同，仅额外对 @%SystemRoot% 展开后路径判定。
        /// </summary>
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

        /// <summary>
        /// 动态判定"新建"菜单项是否为系统项。无任何硬编码扩展名/键名黑名单。
        /// 判定优先级：
        ///   ① 扩展名/特殊键名的类键下，LocalizedString/InfoTip/FriendlyTypeName 是系统 DLL 资源引用；
        ///   ② 扩展名 → ProgID → DefaultIcon 或 open\command → 文件在 Windows 目录下；
        ///   ③ 扩展名本身无 ProgID 但 DefaultIcon/ShellNew\IconPath 在系统目录下。
        /// </summary>
        public static bool IsSystemNewMenuItem(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            try
            {
                // ① ClassKey 本身：资源引用名 / 系统 DLL FriendlyTypeName
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
                            if (IsSystemResourceReference("@" + dv)) return true; // 像 @%SystemRoot%\...
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

                // ②③ ProgID 解析
                if (ProgIdServerUnderWindows(ext)) return true;
            }
            catch { }
            return false;
        }

        // ─────────────────────────────────────────────────────────────
        // 5. 新建菜单：动态候选键枚举 + 显示名解析（无任何硬编码清单）
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 动态枚举所有可能包含 ShellNew 的 HKCR 下候选键名。
        /// 不使用硬编码扩展名/特殊键名单，而是：
        ///   ① 所有 "." 开头的扩展名键；
        ///   ② 所有其他不含 "\". 的非扩展名根键（如 Directory、Folder、lnkfile 等）——
        ///      这些键本身或其子键下可能注册 ShellNew（例如 lnkfile\ShellNew、Folder\ShellNew）。
        /// 调用方后续再实际探测 ShellNew 是否存在，避免漏扫新出现的特殊键。
        /// </summary>
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
                // 扩展名键：一律加入（大量 .txt/.docx 等 ShellNew 注册在此）
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    if (seen.Add(name)) yield return name;
                    continue;
                }
                // 非扩展名根键（如 Directory、Folder、lnkfile、Briefcase、ShellFolder、
                //   .rar 的 ProgID 根、AppX 注册的 progid 等）
                if (IsLikelyShellNewCarrier(name))
                {
                    if (seen.Add(name)) yield return name;
                    continue;
                }
                // 其他非扩展名键：探测是否含 ShellNew。
                // 注意：yield 不能出现在带 catch 的 try 内 → 先收集到列表再 yield
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

        /// <summary>
        /// 启发式判断键名是否为"可能携带 ShellNew"的特殊键（文件夹/快捷方式/公文包等）。
        /// 不使用硬编码清单，而是基于模式匹配，未来新增的 Windows 特殊键只要符合命名
        /// 规范（xxfile、xxFolder、xxBriefcase 等）即可自动识别。
        /// </summary>
        private static bool IsLikelyShellNewCarrier(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return false;
            var k = keyName;
            // Directory / Folder / Briefcase / ShellFolder / lnkfile 等通用命名模式
            if (k.EndsWith("file", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.EndsWith("folder", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.EndsWith("briefcase", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.Equals("directory", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.Equals("allfilesystemobjects", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.Equals("drive", StringComparison.OrdinalIgnoreCase)) return true;
            if (k.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return true;
            // AppX ProgID 命名模式 (通常形如 "AppXabcd1234...")
            if (k.StartsWith("AppX", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 动态解析新建菜单项的中文显示名。完全无硬编码字典：
        ///   ① 嵌套路径：先解析 ProgID 子键名的显示名；
        ///   ② 特殊键（非扩展名，如 Directory/Folder/lnkfile）：直接取注册表显示名；
        ///   ③ 扩展名：先取扩展名的显示名，再用 ProgID 描述补充；
        ///   ④ AssocQueryStringW 作为最终兜底（官方 API，扩展名/ProgID 都能命中）。
        /// </summary>
        public static string ResolveNewMenuItemDisplayName(string keyName, string nestedRegistryPath = null)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return null;

            // 嵌套路径：解析 ProgID 子键名的显示名
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

            // 键本身的显示名（通过注册表多层探测）
            string keyDesc = ResolveClassKeyDefaultName(keyName);

            // AssocQueryStringW 兜底（即使注册表没有 FriendlyTypeName，Windows API 也可能返回描述）
            if (string.IsNullOrWhiteSpace(keyDesc))
                keyDesc = NativeMethods.GetAssocFriendlyDocName(keyName);

            // 特殊键（非扩展名）：直接用 keyDesc 或 keyName
            if (!keyName.StartsWith(".", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(nestedProgIdDesc))
                    return $"{(keyDesc ?? keyName)} ({nestedProgIdDesc})";
                if (!string.IsNullOrWhiteSpace(keyDesc)) return keyDesc;
                // 补充映射清单兜底（保证已知特殊键如 Briefcase/ShellFolder 显示中文）
                var fb = LookupFallbackZhName(keyName);
                if (!string.IsNullOrWhiteSpace(fb)) return fb;
                return keyName;
            }

            // 扩展名：同时显示扩展名和 ProgID 描述
            var effectiveDesc = nestedProgIdDesc ?? keyDesc;
            if (!string.IsNullOrWhiteSpace(effectiveDesc))
                return $"{keyName} ({effectiveDesc})";

            // 补充映射清单兜底（如 .lnk → "快捷方式 (.lnk)"）
            var fbExt = LookupFallbackZhName(keyName);
            if (!string.IsNullOrWhiteSpace(fbExt)) return $"{keyName} ({fbExt})";

            return keyName;
        }

        /// <summary>
        /// 解析一个 HKCR 类键（扩展名 / ProgID / 特殊键）的"默认值显示名"。
        /// 若默认值是资源引用字符串（@shell32.dll,-xxxx），通过 SHLoadIndirectString 解析为中文。
        /// 按优先级依次尝试：FriendlyTypeName → LocalizedString → DefaultIcon\FriendlyTypeName →
        /// shell\open\command\FriendlyTypeName → InfoTip → 默认值 → AssocQueryStringW 兜底。
        /// 内部通过 visited 集合检测循环引用，防止 .ext → progid → progid 栈溢出。
        /// </summary>
        private static string ResolveClassKeyDefaultName(string classKeyName)
            => ResolveClassKeyDefaultNameInternal(classKeyName, null);

        private static string ResolveClassKeyDefaultNameInternal(string classKeyName, HashSet<string> visited)
        {
            if (string.IsNullOrWhiteSpace(classKeyName)) return null;

            // 循环引用检测：.ext → progid → progid 栈溢出防护
            visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!visited.Add(classKeyName)) return null;
            if (visited.Count > 3) return null;  // 最大递归深度

            try
            {
                using var k = Registry.ClassesRoot.OpenSubKey(classKeyName, false);
                if (k == null) return null;

                // ① FriendlyTypeName：Windows 官方推荐的本地化显示名位置
                var r1 = TryResolveIndirectString(k.GetValue("FriendlyTypeName") as string);
                if (!string.IsNullOrWhiteSpace(r1)) return StripAccelerator(r1);

                // ② LocalizedString：系统 Shell 扩展常用
                var r2 = TryResolveIndirectString(k.GetValue("LocalizedString") as string);
                if (!string.IsNullOrWhiteSpace(r2)) return StripAccelerator(r2);

                // ③ DefaultIcon\FriendlyTypeName（AppX 注册的 Shell 扩展多走此路径）
                using (var defIcon = k.OpenSubKey("DefaultIcon", false))
                {
                    if (defIcon != null)
                    {
                        var r3a = TryResolveIndirectString(defIcon.GetValue("FriendlyTypeName") as string);
                        if (!string.IsNullOrWhiteSpace(r3a)) return StripAccelerator(r3a);
                    }
                }

                // ④ shell\open\command\FriendlyTypeName
                using (var shellOpen = k.OpenSubKey(@"shell\open\command", false))
                {
                    if (shellOpen != null)
                    {
                        var r4a = TryResolveIndirectString(shellOpen.GetValue("FriendlyTypeName") as string);
                        if (!string.IsNullOrWhiteSpace(r4a)) return StripAccelerator(r4a);
                    }
                }

                // ⑤ InfoTip（部分文件类型在"提示信息"中存显示名）
                var r5 = TryResolveIndirectString(k.GetValue("InfoTip") as string);
                if (!string.IsNullOrWhiteSpace(r5)) return StripAccelerator(r5);

                // ⑥ 默认值：若是 ProgID 指针则递归解析；否则作为直接显示名或资源引用
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

            // ⑦ AssocQueryStringW 兜底（带缓存，避免重复调用）
            var r7 = TryAssocQueryCached(classKeyName);
            if (!string.IsNullOrWhiteSpace(r7)) return StripAccelerator(r7);

            return null;
        }

        /// <summary>
        /// 启发式判断：字符串是否为 ProgID（如 "txtfile"/"Word.Document.12"/"WinRAR"）。
        /// 要求：无空格 + 无 "@" 资源引用前缀 + 符合 ProgID 命名规范（字母数字点号下划线）。
        /// </summary>
        private static bool LooksLikeProgIdPointer(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            // 直接显示名通常含空格（如 "Text Document"、"文本文档"）
            if (s.Contains(' ')) return false;
            // 含 "@" 前缀的资源引用不是 ProgID
            if (s.StartsWith("@", StringComparison.Ordinal)) return false;
            // 路径（含 "\\" 或 "/"）不是 ProgID
            if (s.Contains('\\') || s.Contains('/')) return false;
            // 含中文字符的肯定是直接显示名，不是 ProgID
            foreach (var c in s)
                if (c > 0x4E00 && c < 0x9FFF) return false;
            // ProgID 命名规范：字母数字点号下划线（例如 "txtfile", "Word.Document.12", "7-Zip"）
            // 允许短横线因为 "7-Zip" 这种合法存在
            if (s.Length > 64) return false;  // 过长不像是 ProgID
            // 必须以字母开头（ProgID 规范）
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
