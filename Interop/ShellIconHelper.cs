using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiskCleaner.Interop
{
    /// <summary>
    /// 从文件（exe/dll）或文件类型中提取图标，并转换为 WPF 可用的 ImageSource。
    /// 用于在右键管理列表中为每一项显示对应程序的图标；无法取得时返回系统默认文件图标。
    ///
    /// 实现说明：通过 SHGetFileInfo(SHGFI_SYSICONINDEX) 取得文件在「系统图像列表」中的索引，
    /// 再用 SHGetImageList 取出高分辨率位图（优先 JUMBO 256px，回退 EXTRALARGE 48px / LARGE 32px），
    /// 因此缩放显示清晰、不模糊。相比 ExtractIconEx，系统图像列表对各类文件/SHELL 扩展更稳定。
    /// 注意：图标提取依赖 Shell，应在 UI/STA 线程上调用（见 MainViewModel 的扫描流程）。
    /// </summary>
    public static class ShellIconHelper
    {
        // ═══════════════════════════════════════════════════════════════
        //  SHGetFileInfo / 系统图像列表
        // ═══════════════════════════════════════════════════════════════
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_SYSICONINDEX = 0x000004000;     // 取得系统图像列表索引
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010; // 按扩展名取类型图标（无需文件存在）
        private const uint SHGFI_ICON = 0x000000100;             // 取得图标句柄（简单方式，兼容性最好）
        private const uint SHGFI_LARGEICON = 0x000000000;        // 大图标（32×32）
        private const uint SHGFI_SMALLICON = 0x000000001;       // 小图标（16×16）
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        // 系统图像列表尺寸档位
        private const int SHIL_LARGE = 0;      // 32×32
        private const int SHIL_EXTRALARGE = 2; // 48×48
        private const int SHIL_JUMBO = 4;      // 256×256

        private const int ILD_TRANSPARENT = 0x00000001;

        // IImageList 接口 ID（SHGetImageList 的 riid 参数）
        private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA9A41");

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

        [DllImport("comctl32.dll")]
        private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>把 GDI 句柄转换为已冻结（线程安全）的 ImageSource，并销毁原句柄。</summary>
        private static ImageSource ToImageSource(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze(); // 冻结后可在任意线程（含 UI 线程）安全使用
                return src;
            }
            catch
            {
                return null;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        /// <summary>
        /// 通过系统图像列表取得清晰图标：先拿索引，再按 JUMBO→EXTRALARGE→LARGE 顺序取最高分辨率位图。
        /// 失败时回退到 SHGFI_ICON 直接取得图标句柄（更简单、兼容性更好）。
        /// </summary>
        private static ImageSource GetIconViaImageList(string pszPath, uint fileAttributes, bool useFileAttributes)
        {
            try
            {
                var fi = new SHFILEINFO();
                uint flags = SHGFI_SYSICONINDEX | (useFileAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
                var ptr = SHGetFileInfo(pszPath, fileAttributes, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                if (ptr != IntPtr.Zero)
                {
                    int iIcon = fi.iIcon;
                    var iid = IID_IImageList;
                    foreach (int sil in new[] { SHIL_JUMBO, SHIL_EXTRALARGE, SHIL_LARGE })
                    {
                        if (SHGetImageList(sil, ref iid, out IntPtr himl) == 0 && himl != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr hIcon = ImageList_GetIcon(himl, iIcon, ILD_TRANSPARENT);
                                if (hIcon != IntPtr.Zero)
                                    return ToImageSource(hIcon);
                            }
                            finally
                            {
                                // SHGetImageList 返回 IImageList COM 接口，用完必须释放引用计数
                                Marshal.Release(himl);
                            }
                        }
                    }
                }
            }
            catch
            {
                // 忽略，尝试回退
            }

            // 回退：用 SHGFI_ICON 直接取得图标句柄（不经过系统图像列表，更简单可靠）
            return GetIconSimple(pszPath, fileAttributes, useFileAttributes);
        }

        /// <summary>
        /// 简单图标提取：直接用 SHGetFileInfo + SHGFI_ICON 取得图标句柄。
        /// 兼容性最好，作为 GetIconViaImageList 的回退方案。
        /// </summary>
        private static ImageSource GetIconSimple(string pszPath, uint fileAttributes, bool useFileAttributes)
        {
            try
            {
                var fi = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON | (useFileAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
                SHGetFileInfo(pszPath, fileAttributes, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                if (fi.hIcon != IntPtr.Zero)
                    return ToImageSource(fi.hIcon);
            }
            catch { }
            return null;
        }

        // 缓存系统默认文件图标（只需提取一次，冻结后可共享）
        private static ImageSource _defaultIcon;
        private static readonly object _defaultIconLock = new();

        /// <summary>
        /// 系统默认文件图标（兜底）。首次调用时从系统提取并缓存。
        /// 永不返回 null（极端情况下返回 1×1 透明位图，保证 Image 控件总有 Source）。
        /// </summary>
        public static ImageSource GetDefaultFileIcon()
        {
            if (_defaultIcon != null) return _defaultIcon;
            lock (_defaultIconLock)
            {
                if (_defaultIcon != null) return _defaultIcon;

                ImageSource icon = null;

                // 优先尝试通过系统图标 API 获取通用文件图标
                try
                {
                    var fi = new SHFILEINFO();
                    uint flags = SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES;
                    var ptr = SHGetFileInfo("dummy.txt", FILE_ATTRIBUTE_NORMAL, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                    if (ptr != IntPtr.Zero)
                    {
                        int iIcon = fi.iIcon;
                        var iid = IID_IImageList;
                        foreach (int sil in new[] { SHIL_JUMBO, SHIL_EXTRALARGE, SHIL_LARGE })
                        {
                            if (SHGetImageList(sil, ref iid, out IntPtr himl) == 0 && himl != IntPtr.Zero)
                            {
                                try
                                {
                                    IntPtr hIcon = ImageList_GetIcon(himl, iIcon, ILD_TRANSPARENT);
                                    if (hIcon != IntPtr.Zero)
                                    {
                                        icon = ToImageSource(hIcon);
                                        if (icon != null) break;
                                    }
                                }
                                finally
                                {
                                    Marshal.Release(himl);
                                }
                            }
                        }
                    }
                }
                catch { }

                // 回退：通过扩展名获取常见文件图标
                icon ??= GetFileTypeIcon(".bin");
                icon ??= GetFileTypeIcon(".txt");
                icon ??= GetFileTypeIcon(".exe");
                icon ??= GetFileTypeIcon("*");

                // 最终兜底
                _defaultIcon = icon ?? CreateBlankIcon() ?? CreateFallbackBitmap();
                return _defaultIcon;
            }
        }

        /// <summary>
        /// 最终兜底：创建一个可见的灰色方块位图（20×20），保证在任何极端情况下
        /// Image 控件都有可见的 Source（不会显示空白）。
        /// </summary>
        private static ImageSource CreateFallbackBitmap()
        {
            try
            {
                var bmp = BitmapSource.Create(
                    20, 20, 96, 96, PixelFormats.Bgra32, null,
                    CreateGrayPixelArray(20, 20), 20 * 4);
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static byte[] CreateGrayPixelArray(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 180;     // B
                pixels[i + 1] = 180; // G
                pixels[i + 2] = 180; // R
                pixels[i + 3] = 200; // A (半透明)
            }
            return pixels;
        }

        /// <summary>
        /// 取得某一文件类型的系统图标（按扩展名，不依赖文件真实存在）。
        /// 例如 ".docx" 返回 Word 文档图标，".exe" 返回程序图标。用于新建菜单项。
        /// </summary>
        public static ImageSource GetFileTypeIcon(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return null;
            try
            {
                if (!extension.StartsWith(".")) extension = "." + extension;
                return GetIconViaImageList("dummy" + extension, FILE_ATTRIBUTE_NORMAL, true);
            }
            catch
            {
                return null;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(
            string lpszFile,
            int nIconIndex,
            out IntPtr phiconLarge,
            out IntPtr phiconSmall,
            uint nIcons);

        /// <summary>
        /// 从可执行文件/动态库中按索引取得程序图标。
        /// 支持正数索引（0-based）与负数资源 ID（如 -28577）。
        /// 文件不存在或索引无效时返回 null。
        /// </summary>
        public static ImageSource GetFileIcon(string filePath, int iconIndex)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            try
            {
                filePath = Environment.ExpandEnvironmentVariables(filePath).Trim().Trim('"');
                if (!File.Exists(filePath)) return null;

                IntPtr hLarge = IntPtr.Zero;
                IntPtr hSmall = IntPtr.Zero;
                try
                {
                    var count = ExtractIconEx(filePath, iconIndex, out hLarge, out hSmall, 1);
                    if (count > 0 && hLarge != IntPtr.Zero)
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(
                            hLarge, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                    if (count > 0 && hSmall != IntPtr.Zero)
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(
                            hSmall, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                }
                finally
                {
                    if (hLarge != IntPtr.Zero) DestroyIcon(hLarge);
                    if (hSmall != IntPtr.Zero) DestroyIcon(hSmall);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从可执行文件/动态库中取得程序图标（经由系统图像列表，清晰且稳定）。
        /// 文件不存在时返回 null。用于 Shell 动词与 Shell 扩展。
        /// </summary>
        public static ImageSource GetFileIcon(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            try
            {
                filePath = Environment.ExpandEnvironmentVariables(filePath).Trim().Trim('"');
                if (!File.Exists(filePath)) return null;
                return GetIconViaImageList(filePath, 0, false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 极端情况下（系统图标提取全部失败）的兜底：1×1 透明位图，保证 ImageSource 永不为 null。
        /// </summary>
        private static ImageSource CreateBlankIcon()
        {
            try
            {
                var bmp = BitmapSource.Create(
                    1, 1, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { 0, 0, 0, 0 }, 4);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从 Shell 动词的命令字符串中解析出可执行文件路径。
        /// 兼容带引号（"C:\a b\c.exe" "%1"）与不带引号（C:\x.exe /y）两种形式。
        /// </summary>
        public static string ExtractExecutablePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var s = command.Trim();
            string path;
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                if (end < 0) return null;
                path = s.Substring(1, end - 1);
            }
            else
            {
                // 未加引号且可能含空格：从前向后累加空格片段，取第一个真实存在的可执行文件，
                // 避免把 "C:\Program Files\App\app.exe" 截断成 "C:\Program"（与 IsDeadFileReference 修复同思路）。
                var parts = s.Split(' ');
                var sb = new System.Text.StringBuilder();
                string found = null;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(parts[i]);
                    var cand = sb.ToString();
                    if (System.IO.File.Exists(cand)) { found = cand; break; }
                    if (System.IO.File.Exists(cand + ".exe")) { found = cand + ".exe"; break; }
                }
                path = found ?? s;
            }
            path = path.Trim().Trim('"');
            path = Environment.ExpandEnvironmentVariables(path);
            return path;
        }
    }
}
