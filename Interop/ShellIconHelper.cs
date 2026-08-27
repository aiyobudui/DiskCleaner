using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiskCleaner.Interop
{
    
    
    
    
    
    
    
    
    
    public static class ShellIconHelper
    {
        
        
        
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

        private const uint SHGFI_SYSICONINDEX = 0x000004000;     
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010; 
        private const uint SHGFI_ICON = 0x000000100;             
        private const uint SHGFI_LARGEICON = 0x000000000;        
        private const uint SHGFI_SMALLICON = 0x000000001;       
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        
        private const int SHIL_LARGE = 0;      
        private const int SHIL_EXTRALARGE = 2; 
        private const int SHIL_JUMBO = 4;      

        private const int ILD_TRANSPARENT = 0x00000001;

        
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

        
        private static ImageSource ToImageSource(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze(); 
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
                                
                                Marshal.Release(himl);
                            }
                        }
                    }
                }
            }
            catch
            {
                
            }

            
            return GetIconSimple(pszPath, fileAttributes, useFileAttributes);
        }

        
        
        
        
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

        
        private static ImageSource _defaultIcon;
        private static readonly object _defaultIconLock = new();

        
        
        
        
        public static ImageSource GetDefaultFileIcon()
        {
            if (_defaultIcon != null) return _defaultIcon;
            lock (_defaultIconLock)
            {
                if (_defaultIcon != null) return _defaultIcon;

                ImageSource icon = null;

                
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

                
                icon ??= GetFileTypeIcon(".bin");
                icon ??= GetFileTypeIcon(".txt");
                icon ??= GetFileTypeIcon(".exe");
                icon ??= GetFileTypeIcon("*");

                
                _defaultIcon = icon ?? CreateBlankIcon() ?? CreateFallbackBitmap();
                return _defaultIcon;
            }
        }

        
        
        
        
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
                pixels[i] = 180;     
                pixels[i + 1] = 180; 
                pixels[i + 2] = 180; 
                pixels[i + 3] = 200; 
            }
            return pixels;
        }

        
        
        
        
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
