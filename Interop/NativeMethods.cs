using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DiskCleaner.Interop
{
    
    
    
    
    
    public static class NativeMethods
    {
        
        
        

        
        
        
        
        
        public static string LoadIndirectString(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || !source.StartsWith("@"))
                return source; 
            try
            {
                var sb = new StringBuilder(1024);
                int hr = SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero);
                if (hr == 0 && sb.Length > 0)
                    return sb.ToString();
            }
            catch { }
            return null;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(
            [MarshalAs(UnmanagedType.LPWStr)] string pszSource,
            [Out] StringBuilder pszOutBuf,
            int cchOutBuf,
            IntPtr ppvReserved);

        
        
        
        

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AssocQueryString(
            int flags,            
            int str,              
            [MarshalAs(UnmanagedType.LPWStr)] string pszAssoc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszExtra,
            StringBuilder pszOutBuf,
            out int pcchOutBuf);

        
        private const int ASSOCSTR_FRIENDLYDOCNAME = 3;

        
        private const int ASSOCF_INIT_NOREMAPCLSID = 0x00000001;
        private const int ASSOCF_INIT_DEFAULTTOSTAR = 0x00000004;
        private const int ASSOCF_INIT_DEFAULTTOFOLDER = 0x00000008;
        private const int ASSOCF_NOTRUNCATE = 0x00000020;

        
        
        
        
        
        public static string GetAssocFriendlyDocName(string assoc)
        {
            return GetAssocString(assoc, ASSOCSTR_FRIENDLYDOCNAME);
        }

        private static string GetAssocString(string assoc, int strId)
        {
            if (string.IsNullOrWhiteSpace(assoc)) return null;
            try
            {
                
                int[] flagCombo = {
                    ASSOCF_INIT_NOREMAPCLSID | ASSOCF_NOTRUNCATE,
                    ASSOCF_INIT_DEFAULTTOFOLDER | ASSOCF_NOTRUNCATE,
                    ASSOCF_INIT_DEFAULTTOSTAR | ASSOCF_NOTRUNCATE,
                };
                foreach (var flags in flagCombo)
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    int hr = AssocQueryString(flags, strId, assoc, null, sb, out size);
                    if (hr == 0 && sb.Length > 0)
                        return sb.ToString();
                    
                    if (hr == 1 && size > 0 && size <= 4096)
                    {
                        var sb2 = new StringBuilder(size + 8);
                        int size2 = sb2.Capacity;
                        int hr2 = AssocQueryString(flags, strId, assoc, null, sb2, out size2);
                        if (hr2 == 0 && sb2.Length > 0)
                            return sb2.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        
        
        

        
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, int flags);

        public const int SHERB_NOCONFIRMATION = 0x00000001;
        public const int SHERB_NOPROGRESSUI = 0x00000002;
        public const int SHERB_NOSOUND = 0x00000004;

        
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        
        
        
        
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        
        public const int SHCNE_ASSOCCHANGED = 0x08000000;
        
        public const uint SHCNF_IDLIST = 0x0000;

        
        
        

        
        [DllImport("psapi.dll")]
        public static extern int EmptyWorkingSet(IntPtr hProcess);

        
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_SET_QUOTA = 0x0100;

        
        [DllImport("kernel32.dll")]
        public static extern uint GetOEMCP();

        
        
        
        

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;       
            public ulong ullTotalPhys;      
            public ulong ullAvailPhys;      
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        
        
        
        

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
        }

        
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(
            out FILETIME lpIdleTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        
        
        
        

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;  
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        public const uint FO_DELETE = 0x0003;

        public const ushort FOF_ALLOWUNDO = 0x0040;       
        public const ushort FOF_NOCONFIRMATION = 0x0010;   
        public const ushort FOF_SILENT = 0x0004;           
        public const ushort FOF_NOERRORUI = 0x0400;         

        
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        
        
        
        public static bool SendToRecycleBin(string path)
        {
            
            
            bool ok = false;
            bool completed = StaShellExecutor.Run(() =>
            {
                var shf = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = path + '\0' + '\0',  
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                };
                ok = SHFileOperation(ref shf) == 0;
            });
            return completed && ok;
        }

        
        
        
        
        
        public static bool SendToRecycleBin(IEnumerable<string> paths, int batchSize = 50)
        {
            if (paths == null) return false;
            var list = new List<string>(paths);
            if (list.Count == 0) return true;

            const ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;
            bool allOk = true;
            
            bool completed = StaShellExecutor.Run(() =>
            {
                for (int i = 0; i < list.Count; i += batchSize)
                {
                    var batch = list.GetRange(i, Math.Min(batchSize, list.Count - i));
                    var sb = new System.Text.StringBuilder();
                    foreach (var p in batch)
                    {
                        sb.Append(p);
                        sb.Append('\0');
                    }
                    sb.Append('\0'); 

                    var shf = new SHFILEOPSTRUCT
                    {
                        wFunc = FO_DELETE,
                        pFrom = sb.ToString(),
                        fFlags = flags
                    };
                    int hr = SHFileOperation(ref shf);
                    if (hr != 0 || shf.fAnyOperationsAborted != 0)
                        allOk = false;
                }
            }, timeoutMs: 60000); 

            return completed && allOk;
        }

        
        
        
        public static int EmptyRecycleBinSta(IntPtr hwnd, string root, int flags)
        {
            int hr = 0;
            bool completed = StaShellExecutor.Run(() => { hr = SHEmptyRecycleBin(hwnd, root, flags); });
            return completed ? hr : -1; 
        }

        
        
        
        public static int QueryRecycleBinSta(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo)
        {
            int hr = 0;
            
            var info = pSHQueryRBInfo;
            bool completed = StaShellExecutor.Run(() => { hr = SHQueryRecycleBin(pszRootPath, ref info); });
            if (completed)
                pSHQueryRBInfo = info;
            return completed ? hr : -1;
        }

        
        
        
        
        
        
        
        private sealed class StaShellExecutor
        {
            private static readonly StaShellExecutor _instance = new StaShellExecutor();

            private readonly BlockingCollection<WorkItem> _queue = new BlockingCollection<WorkItem>();
            private readonly object _gate = new object();
            
            private volatile Thread _thread;
            
            private volatile bool _wedged;

            private sealed class WorkItem
            {
                public Action Action = null!;
                public Exception Exception;
                public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            }

            private StaShellExecutor()
            {
                StartThread();
            }

            private void StartThread()
            {
                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "DiskCleaner-StaShell"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                _wedged = false;
            }

            private void Loop()
            {
                
                
                
                while (_thread == Thread.CurrentThread)
                {
                    try
                    {
                        if (_queue.TryTake(out var item, 250))
                        {
                            try { item.Action(); }
                            catch (Exception ex) { item.Exception = ex; }
                            finally { item.Done.Set(); }
                        }
                    }
                    catch
                    {
                        
                    }
                }
            }

            private void RestartIfWedged()
            {
                if (!_wedged) return;
                lock (_gate)
                {
                    if (!_wedged) return;
                    
                    
                    
                    StartThread();
                }
            }

            
            
            
            
            
            
            
            
            public static bool Run(Action action, int timeoutMs = 30000)
            {
                
                if (timeoutMs <= 0 || timeoutMs > 120000)
                    timeoutMs = 120000;

                
                _instance.RestartIfWedged();

                var item = new WorkItem { Action = action };
                _instance._queue.Add(item);
                try
                {
                    bool completed = item.Done.Wait(timeoutMs);
                    if (!completed)
                    {
                        
                        
                        _instance._wedged = true;
                        return false;
                    }
                    if (item.Exception != null)
                        return false;
                    return true;
                }
                finally
                {
                    item.Done.Dispose();
                }
            }
        }

        
        
        
        

        
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        
        
        
        
        
        
        

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Wow64DisableWow64FsRedirection(out IntPtr oldValue);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Wow64RevertWow64FsRedirection(IntPtr oldValue);

        
        
        
        
        
        
        
        public static bool FileExistsResilient(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            bool disabled = false;
            IntPtr old = IntPtr.Zero;
            try { disabled = Wow64DisableWow64FsRedirection(out old); }
            catch { disabled = false; }
            try
            {
                if (File.Exists(path)) return true;
                if (!Path.HasExtension(path) && File.Exists(path + ".exe")) return true;
            }
            finally
            {
                if (disabled)
                {
                    try { Wow64RevertWow64FsRedirection(old); } catch { }
                }
            }
            return false;
        }

        
        
        
        
        

        
        
        
        
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHGetKnownFolderPath(
            [In] ref Guid rfid,       
            uint dwFlags,             
            IntPtr hToken,            
            out IntPtr ppszPath);     

        
        
        
        
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHSetKnownFolderPath(
            [In] ref Guid rfid,       
            uint dwFlags,             
            IntPtr hToken,            
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath);  

        
        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr ptr);

        
        public const uint KF_FLAG_DEFAULT = 0x00000000;
        public const uint KF_FLAG_DEFAULT_PATH = 0x00000400;

        
        public static readonly Guid FOLDERID_Desktop   = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        public static readonly Guid FOLDERID_Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
        public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        public static readonly Guid FOLDERID_Pictures  = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
        public static readonly Guid FOLDERID_Music     = new("4BD8D571-6D19-48D3-BE97-422220080E43");
        public static readonly Guid FOLDERID_Videos    = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

        
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,           
            uint Msg,              
            IntPtr wParam,         
            string lParam,         
            uint fuFlags,          
            uint uTimeout,         
            out IntPtr lpdwResult);

        public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        public const uint WM_SETTINGCHANGE = 0x001A;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        
        
        
        public static string GetKnownFolderPath(Guid folderId)
        {
            int hr = SHGetKnownFolderPath(ref folderId, KF_FLAG_DEFAULT, IntPtr.Zero, out IntPtr pPath);
            if (hr != 0 || pPath == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(pPath);
            }
            finally
            {
                CoTaskMemFree(pPath);
            }
        }

        
        
        
        public static string GetKnownFolderDefaultPath(Guid folderId)
        {
            int hr = SHGetKnownFolderPath(ref folderId, KF_FLAG_DEFAULT_PATH, IntPtr.Zero, out IntPtr pPath);
            if (hr != 0 || pPath == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(pPath);
            }
            finally
            {
                CoTaskMemFree(pPath);
            }
        }

        
        
        
        
        public static bool SetKnownFolderPath(Guid folderId, string newPath)
        {
            int hr = SHSetKnownFolderPath(ref folderId, 0, IntPtr.Zero, newPath);
            if (hr != 0)
                return false;

            
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
                "Environment", SMTO_ABORTIFHUNG, 5000, out _);
            return true;
        }

        
        
        

        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
        private const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

        
        
        
        
        
        public static bool ScheduleDeleteOnReboot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch { return false; }
        }
    }
}
