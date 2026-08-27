using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskCleaner.Interop
{
    /// <summary>
    /// 原生 .lnk 快捷方式解析：通过 Shell 自身的 IShellLinkW + IPersistFile COM 接口读取快捷方式目标路径。
    ///
    /// 取代此前用 WScript.Shell（Windows Script Host）解析 .lnk 的做法 —— 后者因调用脚本宿主
    /// （Type.GetTypeFromProgID("WScript.Shell") + dynamic 晚期绑定）会被杀毒软件启发式扫描判定为
    /// "MSIL.Wsh" 家族木马（HEUR:Trojan/MSIL.Wsh）。改用 Shell 原生 COM 接口既消除误报，
    /// 又是解析快捷方式的官方推荐方式（由 windows.storage.dll 直接实现，无脚本宿主介入）。
    ///
    /// 线程：可在任意线程调用（含后台 MTA 线程）。COM 运行时会按需跨套间列集调用。
    /// 失败时返回 null，调用方据此判定“无法确定目标”，不会误删合法快捷方式。
    /// </summary>
    public static class ShortcutHelper
    {
        private const uint STGM_READ = 0x00000000;

        /// <summary>SLGP_RAWPATH：返回 .lnk 中存储的原始目标路径（不解析、不展开环境变量），与 WScript.Shell.TargetPath 行为一致。</summary>
        private const uint SLGP_RAWPATH = 0x00000004;

        // ── CLSID_ShellLink（Shell 快捷方式对象，由 windows.storage.dll 实现）──
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        [ClassInterface(ClassInterfaceType.None)]
        private class CShellLink { }

        // ── IPersistFile：把 .lnk 文件加载到 Shell Link 对象 ──
        // vtable 顺序必须与 IPersist→IPersistFile 一致，声明到所需方法（Load）即可。
        [ComImport]
        [Guid("0000010b-0000-0000-c000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        }

        // ── IShellLinkW：读取快捷方式属性（仅需 GetPath，其在 vtable 中为 IUnknown 之后的首个方法）──
        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig] int GetPath(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                int cch,
                ref WIN32_FIND_DATAW pfd,
                uint fFlags);
        }

        // 用两个 uint 表示 FILETIME（对齐 4 字节），与原生 WIN32_FIND_DATAW 布局逐字段一致，
        // 避免 long（对齐 8 字节）在 dwFileAttributes 后引入填充导致结构体错位。
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        /// <summary>
        /// 解析 .lnk 快捷方式的目标路径（原始存储路径，不解析、不展开环境变量）。
        /// 失败返回 null（调用方据此判定“无法确定”，不会误删合法快捷方式）。
        /// </summary>
        public static string ResolveShortcutTarget(string lnkPath)
        {
            if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
                return null;

            CShellLink shellLink = null;
            IPersistFile persistFile = null;
            IShellLinkW link = null;
            try
            {
                // new CShellLink() 对 [ComImport] CoClass 由编译器生成 CoCreateInstance 调用：
                // 编译期绑定 CLSID，不经过 GetTypeFromProgID / Activator / dynamic，规避脚本宿主启发式特征。
                shellLink = new CShellLink();

                persistFile = (IPersistFile)shellLink;
                int loadHr = persistFile.Load(lnkPath, STGM_READ);
                if (loadHr != 0)
                    return null;

                link = (IShellLinkW)shellLink;
                var sb = new StringBuilder(260);
                var fd = new WIN32_FIND_DATAW();
                int getPathHr = link.GetPath(sb, sb.Capacity, ref fd, SLGP_RAWPATH);
                if (getPathHr != 0)
                    return null;
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch
            {
                // 加载/读取失败（如非标准 .lnk、跨套间列集失败）一律视为“无法确定”，返回 null。
                return null;
            }
            finally
            {
                // shellLink / persistFile / link 实际引用同一个 RCW（接口转换不创建新 RCW），
                // 只释放一次即可；多次 ReleaseComObject 会抛 ArgumentException 并被吞掉。
                if (shellLink != null) { try { Marshal.ReleaseComObject(shellLink); } catch { } }
            }
        }
    }
}
