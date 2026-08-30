using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using DiskCleaner.Interop;

namespace DiskCleaner.Services
{
    
    
    
    
    public static class CleanService
    {
        
        
        
        
        
        public readonly struct CleanResult
        {
            public int FilesSucceeded { get; init; }
            public int FilesFailed { get; init; }
            public long BytesFreed { get; init; }
            public int ScheduledForReboot { get; init; }
            public ImmutableList<string> FailedPaths { get; init; }

            
            public bool FullySucceeded => FilesFailed == 0 && FilesSucceeded > 0;
            
            public bool PartiallySucceeded => FilesSucceeded > 0 && FilesFailed > 0;
            
            public bool FullyFailed => FilesSucceeded == 0 && FilesFailed > 0;
            
            public bool NoOperation => FilesSucceeded == 0 && FilesFailed == 0;

            public override string ToString()
            {
                if (NoOperation) return "无需清理";
                if (FullySucceeded && ScheduledForReboot == 0)
                    return $"已释放 {DiskUtils.FormatSize(BytesFreed)}";
                if (FullySucceeded && ScheduledForReboot > 0)
                    return $"已释放 {DiskUtils.FormatSize(BytesFreed)}（{ScheduledForReboot} 项待重启删除）";
                if (PartiallySucceeded)
                {
                    var parts = $"已释放 {DiskUtils.FormatSize(BytesFreed)}（{FilesSucceeded} 成功";
                    if (ScheduledForReboot > 0) parts += $"/{ScheduledForReboot} 待重启删除";
                    parts += $"/{FilesFailed} 失败）";
                    return parts;
                }
                if (FullyFailed) return $"清理失败（{FilesFailed} 项均无法删除）";
                return string.Empty;
            }

            /// <summary>记录一条失败路径，并同步递增 FilesFailed。</summary>
            public CleanResult WithFailedPath(string path)
                => this with
                {
                    FilesFailed = FilesFailed + 1,
                    FailedPaths = (FailedPaths ?? ImmutableList<string>.Empty).Add(path)
                };

            /// <summary>生成失败路径明细文本，列出全部失败路径（窗口内带 ScrollViewer，无需截断）。</summary>
            public string GetFailureDetails()
            {
                if (FailedPaths == null || FailedPaths.Count == 0) return string.Empty;
                var sb = new StringBuilder();
                sb.AppendLine("失败项：");
                foreach (var p in FailedPaths)
                    sb.AppendLine(p);
                return sb.ToString();
            }
        }

        
        
        
        
        private readonly struct FileEntry
        {
            public string Path { get; }
            public long Length { get; }
            public FileEntry(string path, long length)
            {
                Path = path;
                Length = length;
            }
        }

        
        
        
        
        
        
        
        public static CleanResult DeleteDirContents(string path, ILogger logger, IProgress<string> progress)
        {
            if (!Directory.Exists(path)) return default;
            var result = new CleanResult();
            try
            {
                DeleteDirContentsInternal(path, logger, progress, ref result);
            }
            catch (Exception ex)
            {
                
                logger?.Error($"删除目录内容异常: {path}", ex);
            }
            return result;
        }

        
        
        
        
        
        private static void DeleteDirContentsInternal(string path, ILogger logger, IProgress<string> progress, ref CleanResult result)
        {
            if (!Directory.Exists(path)) return;

            var allFiles = new List<FileEntry>();
            CollectFilesRecursively(path, allFiles, logger);

            foreach (var entry in allFiles)
            {
                TryDeleteFile(entry.Path, logger, progress, ref result);
            }

            DeleteEmptySubdirectories(path, logger, progress, ref result);
        }

        
        
        
        
        
        private static void TryDeleteFile(string file, ILogger logger, IProgress<string> progress, ref CleanResult result)
        {
            if (!File.Exists(file)) return;

            long fileSize = 0;
            try { fileSize = new FileInfo(file).Length; } catch { }

            if (TryDeleteFileDirect(file, logger))
            {
                progress?.Report(file);
                result = result with { FilesSucceeded = result.FilesSucceeded + 1, BytesFreed = result.BytesFreed + fileSize };
                return;
            }

            if (TryTruncateFile(file, logger))
            {
                progress?.Report(file);
                result = result with { FilesSucceeded = result.FilesSucceeded + 1, BytesFreed = result.BytesFreed + fileSize };
                return;
            }

            
            if (TryScheduleDeleteOnReboot(file, logger))
            {
                
                result = result with { FilesSucceeded = result.FilesSucceeded + 1, ScheduledForReboot = result.ScheduledForReboot + 1 };
            }
            else
            {
                logger?.Error($"删除文件失败（已尝试删除/截断/重启删除）: {file}");
                result = result.WithFailedPath(file);
            }
        }

        
        private static bool TryDeleteFileDirect(string file, ILogger logger)
        {
            try
            {
                if (!File.Exists(file)) return true;
                if (DiskUtils.IsReparsePoint(file)) return true;      // 重解析点（App 执行别名 wt.exe、符号链接、junction 等）一律不删
                if (IsWindowsAppsPath(file)) return true;            // 不删除 WindowsApps 内的打包应用文件
                var attr = File.GetAttributes(file);
                if ((attr & FileAttributes.System) == FileAttributes.System) return false;
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
                File.Delete(file);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断路径是否位于打包应用目录 WindowsApps 内（含 C:\Program Files\WindowsApps 与
        /// %LOCALAPPDATA%\Microsoft\WindowsApps）。这些路径由系统/应用商店管理，清理工具不应触碰，
        /// 否则会破坏 Windows Terminal 等 AppX 应用的执行别名与注册。
        /// </summary>
        private static bool IsWindowsAppsPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var p = path.Replace('/', '\\').ToLowerInvariant();
            return p.Contains(@"\windowsapps\");
        }

        
        
        
        
        

        
        
        
        private static bool TryTruncateFile(string file, ILogger logger)
        {
            try
            {
                if (DiskUtils.IsReparsePoint(file)) return false;   // 重解析点不截断
                if (IsWindowsAppsPath(file)) return false;
                var attr = File.GetAttributes(file);
                if ((attr & FileAttributes.System) == FileAttributes.System) return false;
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);

                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.SetLength(0);
                }
                logger?.Info($"文件被占用，已清空内容释放空间: {file}");
                return true;
            }
            catch
            {
                logger?.Debug($"截断文件失败: {file}");
                return false;
            }
        }

        
        
        
        private static bool TryScheduleDeleteOnReboot(string file, ILogger logger)
        {
            try
            {
                if (DiskUtils.IsReparsePoint(file)) return false;   // 重解析点不安排重启删除
                if (IsWindowsAppsPath(file)) return false;
                var attr = File.GetAttributes(file);
                if ((attr & FileAttributes.System) == FileAttributes.System) return false;
                if (File.Exists(file) && NativeMethods.ScheduleDeleteOnReboot(file))
                {
                    logger?.Warning($"文件被占用，已标记为重启后删除: {file}");
                    return true;
                }
            }
            catch { }
            return false;
        }

        
        
        
        
        
        private static bool IsReparsePoint(string path) => DiskUtils.IsReparsePoint(path);

        
        
        
        private static void CollectFilesRecursively(string path, List<FileEntry> files, ILogger logger)
        {
            try
            {
                
                
                
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opts))
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        CollectFilesRecursively(entry.FullName, files, logger);
                    }
                    else
                    {
                        long len = 0;
                        if (entry is FileInfo fi)
                        {
                            try { len = fi.Length; } catch {   }
                        }
                        files.Add(new FileEntry(entry.FullName, len));
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"枚举目录失败: {path}", ex);
            }
        }

        
        
        
        private static void DeleteEmptySubdirectories(string path, ILogger logger, IProgress<string> progress, ref CleanResult result)
        {
            
            var emptyOpts = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var dirInfo in new DirectoryInfo(path).EnumerateDirectories("*", emptyOpts))
            {
                var dir = dirInfo.FullName;
                DeleteEmptySubdirectories(dir, logger, progress, ref result);
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    if (Directory.GetFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly).Length > 0) continue;

                    progress?.Report(dir);
                    var attr = File.GetAttributes(dir);
                    if ((attr & FileAttributes.System) == FileAttributes.System) continue;
                    if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(dir, attr & ~FileAttributes.ReadOnly);
                    Directory.Delete(dir);
                }
                catch (Exception ex)
                {
                    logger?.Error($"删除目录失败: {dir}", ex);
                    
                }
            }
        }

        
        
        
        public static CleanResult DeleteDirContents(string path, ILogger logger)
            => DeleteDirContents(path, logger, null);


        // 清理 Windows 日志目录（C:\Windows\Logs）。
        // 先尝试停止会锁定日志文件的 Windows 更新相关服务（WaaSMedic / Windows Update / Update Orchestrator），
        // 清理完成后再按停止前的状态把它们恢复。停止/启动服务需要管理员权限；
        // 若权限不足或服务受保护无法停止，则退化为 best-effort 删除（被锁定的文件会记录在失败路径中）。
        public static CleanResult CleanWindowsLogs(ILogger logger, IProgress<string> progress = null)
        {
            var logsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs");

            // 安全护栏：若系统正在更新（下载/安装/servicing 进行中），此时停止更新相关服务
            // 可能中断更新、导致损坏或需重新下载，因此跳过停服、仅做 best-effort 删除。
            if (IsWindowsUpdateInProgress(out var updateReason))
            {
                logger?.Warning($"检测到系统当前正在更新（{updateReason}），为安全起见跳过停止更新相关服务，仅做 best-effort 清理（部分日志文件可能仍被锁定）。");
                return DeleteDirContents(logsPath, logger, progress);
            }

            // 仅处理这些服务；每项记录停止前的运行状态，便于清理后精确恢复。
            var serviceSpecs = new[]
            {
                ("WaaSMedicSvc", "Windows Update Medic 服务"),
                ("wuauserv",     "Windows Update 服务"),
                ("UsoSvc",       "Update Orchestrator 服务"),
            };

            var stopped = new List<(string Name, string Label, bool WasRunning)>();

            foreach (var (name, label) in serviceSpecs)
            {
                try
                {
                    using var sc = new ServiceController(name);
                    bool running = sc.Status == ServiceControllerStatus.Running
                                || sc.Status == ServiceControllerStatus.StartPending;
                    if (running)
                    {
                        logger?.Info($"正在停止 {label}（{name}）以释放被锁定的日志文件…");
                        sc.Stop();
                        WaitForServiceStatus(sc, ServiceControllerStatus.Stopped, 15000);
                        logger?.Info($"{label}（{name}）已停止。");
                        stopped.Add((name, label, true));
                    }
                    else
                    {
                        stopped.Add((name, label, false));
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warning($"无法停止 {label}（{name}）：{ex.Message}（将以 best-effort 方式继续清理）");
                    stopped.Add((name, label, false));
                }
            }

            // 给操作系统一点时间释放被服务占用的文件句柄，再开始删除。
            if (stopped.Count > 0) Thread.Sleep(800);

            CleanResult result;
            try
            {
                result = DeleteDirContents(logsPath, logger, progress);
            }
            finally
            {
                // 恢复：仅重启清理前正在运行的服务，不打扰原本就停止的服务。
                foreach (var (name, label, wasRunning) in stopped)
                {
                    if (!wasRunning) continue;
                    try
                    {
                        using var sc = new ServiceController(name);
                        if (sc.Status == ServiceControllerStatus.Stopped)
                        {
                            logger?.Info($"正在恢复 {label}（{name}）…");
                            sc.Start();
                            WaitForServiceStatus(sc, ServiceControllerStatus.Running, 15000);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Warning($"无法恢复 {label}（{name}）：{ex.Message}");
                    }
                }
            }
            return result;
        }

        private static void WaitForServiceStatus(ServiceController sc, ServiceControllerStatus target, int timeoutMs)
        {
            int waited = 0;
            while (sc.Status != target && waited < timeoutMs)
            {
                Thread.Sleep(300);
                waited += 300;
                try { sc.Refresh(); } catch { break; }
            }
        }

        // 判断 Windows 是否正在下载/安装更新或正在 servicing。
        // 返回 true 表示更新活动活跃，此时不应停止更新相关服务。
        private static bool IsWindowsUpdateInProgress(out string reason)
        {
            reason = null;
            try
            {
                // 1) 通过 WUA COM 查询下载器/安装器是否忙碌——这是“正在更新”最权威的信号。
                var t = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (t != null)
                {
                    object session = Activator.CreateInstance(t);
                    var dyn = (dynamic)session;
                    try
                    {
                        var dl = dyn.CreateUpdateDownloader();
                        if (dl != null && (bool)dl.IsBusy) { reason = "Windows Update 正在下载更新"; return true; }
                    }
                    catch { }
                    try
                    {
                        var inst = dyn.CreateUpdateInstaller();
                        if (inst != null && (bool)inst.IsBusy) { reason = "Windows Update 正在安装更新"; return true; }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                // 2) 组件基于服务(CBS)正在处理更新包（安装/卸载进行中）。
                using var cbs = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\PackagesPending", false);
                if (cbs != null) { reason = "系统组件服务(CBS)正在处理更新包"; return true; }
            }
            catch { }

            try
            {
                // 3) 更新已安装、等待重启完成（servicing 尚未结束，停服务仍不安全）。
                using var rb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\PendingReboot", false);
                if (rb != null) { reason = "系统已安装更新、等待重启"; return true; }
            }
            catch { }

            return false;
        }

        
        
        
        
        
        
        private static void RemoveReparsePointChildren(string path, ILogger logger)
        {
            try
            {
                foreach (var child in Directory.GetDirectories(path))
                {
                    if (!IsReparsePoint(child)) continue;
                    try
                    {
                        
                        Directory.Delete(child, false);
                    }
                    catch (Exception ex)
                    {
                        logger?.Error($"删除重解析点链接失败: {child}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"枚举重解析点子目录失败: {path}", ex);
            }
        }

        
        
        
        
        
        public static void DeleteDir(string path, ILogger logger, IProgress<string> progress)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                DeleteDirContents(path, logger, progress);
                RemoveReparsePointChildren(path, logger);
                DeleteEmptyRoot(path, logger);
            }
            catch (Exception ex)
            {
                logger?.Error($"删除目录失败: {path}", ex);
                throw new IOException($"无法删除目录 {path}，可能被其它程序占用或受系统保护。", ex);
            }
        }

        
        
        
        
        
        
        private static void DeleteEmptyRoot(string path, ILogger logger)
        {
            if (!Directory.Exists(path)) return;
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.System) == FileAttributes.System) return;
            if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
            Directory.Delete(path);
        }

        
        
        
        public static void DeleteDir(string path, ILogger logger)
            => DeleteDir(path, logger, null);

        
        
        
        public static void DeleteFile(string path, ILogger logger, IProgress<string> progress)
        {
            if (!File.Exists(path)) return;
            try
            {
                progress?.Report(path);
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.System) == FileAttributes.System) return;
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                
                if (TryTruncateFile(path, logger))
                    return;

                logger?.Error($"删除文件失败: {path}", ex);
                throw new IOException($"无法删除文件 {path}，可能被其它程序占用或受系统保护。", ex);
            }
        }

        
        public static void DeleteFile(string path, ILogger logger)
            => DeleteFile(path, logger, null);

        
        
        
        
        
        
        
        
        public static void EmptyRecycleBin(ILogger logger = null)
        {
            try
            {
                const int flags = NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND;
                
                int hr = NativeMethods.EmptyRecycleBinSta(IntPtr.Zero, null, flags);
                if (hr != 0)
                    throw new IOException($"清空回收站失败（HRESULT: 0x{hr:X8}）。");
            }
            catch (Exception ex)
            {
                logger?.Error("清空回收站失败", ex);
                throw new IOException("清空回收站失败，可能回收站正被占用或无法访问。", ex);
            }
        }

        
        
        
        
        public static long GetRecycleBinSize()
        {
            try
            {
                var info = new NativeMethods.SHQUERYRBINFO
                {
                    cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>()
                };
                int hr = NativeMethods.QueryRecycleBinSta(null, ref info);
                if (hr == 0)
                    return info.i64Size;
            }
            catch { }
            return 0;
        }
    }
}
