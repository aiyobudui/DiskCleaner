using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DiskCleaner.Interop
{
    /// <summary>
    /// Windows 原生 API 声明。
    /// 所有 API 在 Windows 10 (build 10240+) 和 Windows 11 (build 22000+) 上行为一致，
    /// 无需版本分支，单一实现即可。
    /// </summary>
    public static class NativeMethods
    {
        // ═══════════════════════════════════════════════════════════════
        //  间接字符串解析（@dllpath,-resid 形式）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 解析形如 "@C:\Windows\system32\shell32.dll,-50332" 的间接字符串为本地化文本。
        /// 用于读取 MUIVerb / LocalizedString 等资源引用的真实显示名。
        /// </summary>
        /// <returns>解析后的文本；无法解析时返回 null。</returns>
        public static string LoadIndirectString(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || !source.StartsWith("@"))
                return source; // 非间接字符串原样返回
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

        // ═══════════════════════════════════════════════════════════════
        //  文件关联友好名（AssocQueryString）
        //  用于兜底获取 CLSID/扩展名/ProgID 对应的人类可读描述
        // ═══════════════════════════════════════════════════════════════

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AssocQueryString(
            int flags,            // ASSOCF_*
            int str,              // ASSOCSTR_*
            [MarshalAs(UnmanagedType.LPWStr)] string pszAssoc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszExtra,
            StringBuilder pszOutBuf,
            out int pcchOutBuf);

        // ASSOCSTR 常量（仅保留当前实际使用的项）
        private const int ASSOCSTR_FRIENDLYDOCNAME = 3;

        // ASSOCF 常量（仅保留当前实际使用的项）
        private const int ASSOCF_INIT_NOREMAPCLSID = 0x00000001;
        private const int ASSOCF_INIT_DEFAULTTOSTAR = 0x00000004;
        private const int ASSOCF_INIT_DEFAULTTOFOLDER = 0x00000008;
        private const int ASSOCF_NOTRUNCATE = 0x00000020;

        /// <summary>
        /// 通过 AssocQueryStringW 获取文件/CLSID/ProgID 的"友好文档名"（Windows 官方 API 兜底）。
        /// 例如："CLSID\{guid}" → 返回该 CLSID 对应的 Shell 扩展中文描述；
        ///       ".txt" → 返回"文本文档"；"txtfile" → 返回"文本文档"。
        /// </summary>
        public static string GetAssocFriendlyDocName(string assoc)
        {
            return GetAssocString(assoc, ASSOCSTR_FRIENDLYDOCNAME);
        }

        private static string GetAssocString(string assoc, int strId)
        {
            if (string.IsNullOrWhiteSpace(assoc)) return null;
            try
            {
                // 尝试多种 flags 组合，从最严格到最宽松
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
                    // 如果缓冲区不够大，hr == 1 (E_INSUFFICIENTBUFFER)，用新 size 重试一次
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

        // ═══════════════════════════════════════════════════════════════
        //  回收站
        // ═══════════════════════════════════════════════════════════════

        /// <summary>清空回收站。Win10/11 通用，自 XP 起可用。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, int flags);

        public const int SHERB_NOCONFIRMATION = 0x00000001;
        public const int SHERB_NOPROGRESSUI = 0x00000002;
        public const int SHERB_NOSOUND = 0x00000004;

        /// <summary>查询回收站占用空间与项目数。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        /// <summary>
        /// 通知 Shell 某类事件已发生，使其无需重启 explorer 即可刷新（例如右键菜单 / 文件关联变化）。
        /// 替代“结束并重启 explorer.exe”进程的做法，避免被杀软判定为“操控系统进程”。
        /// </summary>
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        // SHCNE_ASSOCCHANGED：通知 Shell 文件关联 / Shell 扩展（含右键菜单）已变更
        public const int SHCNE_ASSOCCHANGED = 0x08000000;
        // SHCNF_IDLIST：dwItem1/dwItem2 为项 ID 列表（此处传 NULL 表示全局刷新）
        public const uint SHCNF_IDLIST = 0x0000;

        // ═══════════════════════════════════════════════════════════════
        //  内存优化
        // ═══════════════════════════════════════════════════════════════

        /// <summary>释放进程工作集内存。Win10/11 通用，自 XP 起可用。</summary>
        [DllImport("psapi.dll")]
        public static extern int EmptyWorkingSet(IntPtr hProcess);

        /// <summary>打开进程句柄（用于跨进程释放工作集）。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        /// <summary>关闭进程句柄。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        // EmptyWorkingSet 需要的访问权限：查询进程信息 + 设置配额（工作集）
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_SET_QUOTA = 0x0100;

        /// <summary>获取系统默认 OEM 代码页（中文系统通常为 936，即 GBK）。</summary>
        [DllImport("kernel32.dll")]
        public static extern uint GetOEMCP();

        // ═══════════════════════════════════════════════════════════════
        //  内存状态 (GlobalMemoryStatusEx)
        //  Win10/11 通用 — 瞬时返回物理内存使用率，替代 WMI 查询 (~500ms 延迟)
        // ═══════════════════════════════════════════════════════════════

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;       // 物理内存使用率 (0-100)
            public ulong ullTotalPhys;      // 物理内存总量 (字节)
            public ulong ullAvailPhys;      // 可用物理内存
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        /// <summary>获取系统内存状态。Win10/11 通用，自 Win2K 起可用。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ═══════════════════════════════════════════════════════════════
        //  CPU 时间 (GetSystemTimes)
        //  Win10/11 通用 — 200ms 双采样计算 CPU 使用率，替代 PerformanceCounter (1秒 sleep)
        // ═══════════════════════════════════════════════════════════════

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
        }

        /// <summary>获取系统 idle/kernel/user 时间。Win10/11 通用，自 XP SP1 起可用。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(
            out FILETIME lpIdleTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        // ═══════════════════════════════════════════════════════════════
        //  文件操作 (SHFileOperation — 发送到回收站)
        //  Win10/11 通用 — 自 Win95 起可用，Win10/11 仍完全支持
        // ═══════════════════════════════════════════════════════════════

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;  // BOOL 为 4 字节，不能是 bool(1字节)，否则结构体布局错位导致 fFlags 失效
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        public const uint FO_DELETE = 0x0003;

        public const ushort FOF_ALLOWUNDO = 0x0040;       // 允许撤销 (送回收站)
        public const ushort FOF_NOCONFIRMATION = 0x0010;   // 不弹确认框
        public const ushort FOF_SILENT = 0x0004;           // 不弹进度框
        public const ushort FOF_NOERRORUI = 0x0400;         // 不弹错误框

        /// <summary>文件操作 (删除/移动/复制)。Win10/11 通用。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        /// <summary>
        /// 将文件或目录送入回收站 (安全删除，可恢复)
        /// </summary>
        public static bool SendToRecycleBin(string path)
        {
            // SHFileOperation 必须在 STA 线程执行；清理逻辑运行在线程池(MTA)线程，
            // 直接调用会死锁/卡死。统一经 StaShellExecutor 派发到专用 STA 线程。
            bool ok = false;
            bool completed = StaShellExecutor.Run(() =>
            {
                var shf = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = path + '\0' + '\0',  // 必须双 null 结尾
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                };
                ok = SHFileOperation(ref shf) == 0;
            });
            return completed && ok;
        }

        /// <summary>
        /// 批量将文件/目录送入回收站。按 batchSize 分批执行，避免 pFrom 过长导致失败。
        /// 静默、无确认、无错误弹窗；任一底层项失败不影响其余项继续处理。
        /// 整段（含全部批次）在专用 STA 线程上执行，避免 MTA 线程调用 SHFileOperation 卡死。
        /// </summary>
        public static bool SendToRecycleBin(IEnumerable<string> paths, int batchSize = 50)
        {
            if (paths == null) return false;
            var list = new List<string>(paths);
            if (list.Count == 0) return true;

            const ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;
            bool allOk = true;
            // 每批给 60 秒超时（批量文件可能较多），超时则跳过该批继续
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
                    sb.Append('\0'); // 列表结尾双 null

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
            }, timeoutMs: 60000); // 批量操作给更长超时

            return completed && allOk;
        }

        /// <summary>
        /// 在 STA 线程上执行清空回收站（SHEmptyRecycleBin 同样是 shell 调用，需 STA）。
        /// </summary>
        public static int EmptyRecycleBinSta(IntPtr hwnd, string root, int flags)
        {
            int hr = 0;
            bool completed = StaShellExecutor.Run(() => { hr = SHEmptyRecycleBin(hwnd, root, flags); });
            return completed ? hr : -1; // 超时/异常返回 -1（调用方按失败处理）
        }

        /// <summary>
        /// 在 STA 线程上执行回收站查询（SHQueryRecycleBin 同为 shell 调用，需 STA）。
        /// </summary>
        public static int QueryRecycleBinSta(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo)
        {
            int hr = 0;
            // ref 参数不能直接进入 lambda(CS1628)；用局部副本传入，结果写回。
            var info = pSHQueryRBInfo;
            bool completed = StaShellExecutor.Run(() => { hr = SHQueryRecycleBin(pszRootPath, ref info); });
            if (completed)
                pSHQueryRBInfo = info;
            return completed ? hr : -1;
        }

        // ═══════════════════════════════════════════════════════════════
        //  STA 线程执行器
        //  SHFileOperation / SHEmptyRecycleBin 等 shell 调用必须在 STA 线程
        //  上进行。清理逻辑运行在线程池(MTA)线程，直接调用会死锁/永久卡死。
        //  这里维护一条常驻 STA 后台线程，按队列同步执行 shell 操作。
        //  SHFileOperation 在 STA 线程上会自行泵消息，无需额外消息循环。
        // ═══════════════════════════════════════════════════════════════
        private sealed class StaShellExecutor
        {
            private static readonly StaShellExecutor _instance = new StaShellExecutor();

            private readonly BlockingCollection<WorkItem> _queue = new BlockingCollection<WorkItem>();
            private readonly object _gate = new object();
            // 当前活跃的 STA 线程；被重建时指向新线程，旧线程据此自行退出，避免线程堆积。
            private volatile Thread _thread;
            // 标记上一次 shell 操作在超时内未完成（STA 线程被卡死）；下一次 Run 据此重建线程。
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
                // 本线程只在自己仍是最新一条 STA 线程时消费队列；
                // 一旦被 RestartIfWedged 取代（_thread 指向新线程），旧线程在处理完
                // 当前卡死项后的下次循环退出，避免旧线程堆积占用资源。
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
                        // 取任务异常（理论不会发生）：继续循环，避免线程退出
                    }
                }
            }

            private void RestartIfWedged()
            {
                if (!_wedged) return;
                lock (_gate)
                {
                    if (!_wedged) return;
                    // 上一次 shell 操作在超时内未完成（极可能是 SHFileOperation 卡在
                    // 损坏/网络路径或弹出了不可见的模态对话框），旧 STA 线程被占用。
                    // 新建一条 STA 线程接管队列；旧线程在结束当前项后自行退出。
                    StartThread();
                }
            }

            /// <summary>
            /// 在 STA 线程上同步执行 shell 操作，调用方阻塞直到完成（或超时/异常）。
            /// 超时后不抛异常（避免中断整条清理链），仅返回 false 让调用方跳过当前项继续。
            /// </summary>
            /// <param name="action">要在 STA 线程执行的 shell 操作。</param>
            /// <param name="timeoutMs">超时毫秒数；默认 30 秒，单批回收站 60 秒。
            /// 传 ≤0 或超过 120 秒会被收敛到 120 秒上限——绝不无限等待。</param>
            /// <returns>true=在超时内完成且无异常；false=超时或执行异常。</returns>
            public static bool Run(Action action, int timeoutMs = 30000)
            {
                // 防无限等待：任何调用方传入的超时都被收敛到有限上限
                if (timeoutMs <= 0 || timeoutMs > 120000)
                    timeoutMs = 120000;

                // 若上一次执行卡死，先重建 STA 线程，避免后续操作被单个卡死项永久拖垮
                _instance.RestartIfWedged();

                var item = new WorkItem { Action = action };
                _instance._queue.Add(item);
                try
                {
                    bool completed = item.Done.Wait(timeoutMs);
                    if (!completed)
                    {
                        // 超时：STA 线程可能仍在后台执行该操作，但调用方不再等待。
                        // 标记卡死，下一次 Run 会重建线程接管。
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

        // ═══════════════════════════════════════════════════════════════
        //  磁盘空间 (GetDiskFreeSpaceEx)
        //  Win10/11 通用 — 自 Win95 OSR2 起可用
        // ═══════════════════════════════════════════════════════════════

        /// <summary>获取磁盘可用/总空间。Win10/11 通用。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        // ═══════════════════════════════════════════════════════════════
        //  WOW64 文件系统重定向安全文件检查
        //  32 位宿主进程下，对 C:\Program Files 等系统目录的访问会被系统静默重定向到
        //  C:\Program Files (x86)，导致 File.Exists 找不到 64 位已安装软件的 DLL/EXE，
        //  进而把有效右键菜单项（如 Bandizip/火绒 的 Win11 一级菜单 Shell 扩展）误判为失效而删除。
        //  临时关闭重定向再检查真实路径即可正确识别；64 位进程调用本 API 为安全空操作。
        // ═══════════════════════════════════════════════════════════════

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Wow64DisableWow64FsRedirection(out IntPtr oldValue);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Wow64RevertWow64FsRedirection(IntPtr oldValue);

        /// <summary>
        /// WOW64 重定向安全的文件存在性检查。
        /// 判定"失效引用"前必须先确认文件真的缺失：32 位宿主进程下临时关闭文件系统重定向，
        /// 检查真实的 C:\Program Files 路径，避免把 64 位已安装软件（如 Bandizip/火绒 的
        /// Win11 一级菜单 Shell 扩展 DLL）误判为失效而删除。64 位进程调用时为安全空操作
        /// （重定向本就不生效），行为与 File.Exists 一致。
        /// </summary>
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

        // ═══════════════════════════════════════════════════════════════
        //  用户已知文件夹 (Known Folders)
        //  Win10/11 通用 — SHGetKnownFolderPath / SHSetKnownFolderPath 自 Vista 起可用
        //  用于重定向 桌面/文档/下载/图片/音乐/视频 等用户文件夹位置
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 获取已知文件夹路径。Win10/11 通用，自 Vista 起可用。
        /// 调用方负责用 CoTaskMemFree 释放返回的字符串。
        /// </summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHGetKnownFolderPath(
            [In] ref Guid rfid,       // KNOWNFOLDERID
            uint dwFlags,             // 0 = 当前路径, KF_FLAG_DEFAULT_PATH = 0x400 = 默认路径
            IntPtr hToken,            // NULL = 当前用户
            out IntPtr ppszPath);     // 返回的路径字符串 (需 CoTaskMemFree 释放)

        /// <summary>
        /// 设置已知文件夹路径 (重定向)。Win10/11 通用，自 Vista 起可用。
        /// 仅修改注册表 + 通知系统，不移动文件 — 调用方负责先搬文件。
        /// </summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHSetKnownFolderPath(
            [In] ref Guid rfid,       // KNOWNFOLDERID
            uint dwFlags,             // 0
            IntPtr hToken,            // NULL = 当前用户
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath);  // 新路径

        /// <summary>释放 CoTaskMem 分配的内存。</summary>
        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr ptr);

        // KF_FLAG 常量
        public const uint KF_FLAG_DEFAULT = 0x00000000;
        public const uint KF_FLAG_DEFAULT_PATH = 0x00000400;

        // ── KNOWNFOLDERID GUID 常量 (Win10/11 通用) ──
        public static readonly Guid FOLDERID_Desktop   = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        public static readonly Guid FOLDERID_Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
        public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        public static readonly Guid FOLDERID_Pictures  = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
        public static readonly Guid FOLDERID_Music     = new("4BD8D571-6D19-48D3-BE97-422220080E43");
        public static readonly Guid FOLDERID_Videos    = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

        // ── WM_SETTINGCHANGE 广播 (通知 Explorer 刷新) ──
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,           // HWND_BROADCAST
            uint Msg,              // WM_SETTINGCHANGE
            IntPtr wParam,         // 0
            string lParam,         // "Environment"
            uint fuFlags,          // SMTO_ABORTIFHUNG
            uint uTimeout,         // 5000ms
            out IntPtr lpdwResult);

        public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        public const uint WM_SETTINGCHANGE = 0x001A;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        /// <summary>
        /// 获取已知文件夹的当前路径。Win10/11 通用。
        /// </summary>
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

        /// <summary>
        /// 获取已知文件夹的默认路径 (即未重定向时的原始路径)。Win10/11 通用。
        /// </summary>
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

        /// <summary>
        /// 重定向已知文件夹到新路径。Win10/11 通用。
        /// 仅修改注册表 + 通知系统，不移动文件。
        /// </summary>
        public static bool SetKnownFolderPath(Guid folderId, string newPath)
        {
            int hr = SHSetKnownFolderPath(ref folderId, 0, IntPtr.Zero, newPath);
            if (hr != 0)
                return false;

            // 广播 WM_SETTINGCHANGE 通知 Explorer 和其他应用刷新
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
                "Environment", SMTO_ABORTIFHUNG, 5000, out _);
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        //  重启后删除（MoveFileEx）
        // ═══════════════════════════════════════════════════════════════

        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
        private const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

        /// <summary>
        /// 标记文件或目录在下次 Windows 重启时删除。
        /// 用于当前被进程锁住无法立即删除的文件（如浏览器缓存、临时文件）。
        /// 返回 true 表示已成功注册待删项；false 表示 API 调用失败（通常权限不足）。
        /// </summary>
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
