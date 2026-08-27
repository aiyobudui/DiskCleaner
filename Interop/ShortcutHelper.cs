using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskCleaner.Interop
{
    
    
    
    
    
    
    
    
    
    
    
    public static class ShortcutHelper
    {
        private const uint STGM_READ = 0x00000000;

        
        private const uint SLGP_RAWPATH = 0x00000004;

        
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        [ClassInterface(ClassInterfaceType.None)]
        private class CShellLink { }

        
        
        [ComImport]
        [Guid("0000010b-0000-0000-c000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        }

        
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

        
        
        
        
        public static string ResolveShortcutTarget(string lnkPath)
        {
            if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
                return null;

            CShellLink shellLink = null;
            IPersistFile persistFile = null;
            IShellLinkW link = null;
            try
            {
                
                
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
                
                return null;
            }
            finally
            {
                
                
                if (shellLink != null) { try { Marshal.ReleaseComObject(shellLink); } catch { } }
            }
        }
    }
}
