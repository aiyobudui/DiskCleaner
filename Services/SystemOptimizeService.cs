using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiskCleaner.Interop;

namespace DiskCleaner.Services
{
    public interface ISystemOptimizeService
    {
        
        
        
        
        
        Task<string> ExecuteAsync(string action, IProgress<string> progress = null, IProgress<int> percentProgress = null, CancellationToken cancellationToken = default);
    }

    public class SystemOptimizeService : ISystemOptimizeService
    {
        private readonly ILogger _logger;

        public SystemOptimizeService(ILogger logger = null)
        {
            _logger = logger;
        }

        
        
        
        public async Task<string> ExecuteAsync(string action, IProgress<string> progress = null, IProgress<int> percentProgress = null, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    void Report(string msg)
                    {
                        progress?.Report(msg);
                    }

                    switch (action.ToLower())
                    {
                        case "hibernate":
                            return RunProcessWithOutput("powercfg", "-h off", Report) ? "休眠已关闭。" : "操作失败。";

                        case "hibernate_on":
                            return RunProcessWithOutput("powercfg", "-h on", Report) ? "休眠已开启。" : "操作失败。";

                        case "dism":
                            Report("正在执行 DISM 组件清理（可能需要数分钟）...");
                            
                            
                            
                            
                            
                            
                            bool ok = RunLongRunningUtf8Tool("dism", "/online /cleanup-image /startcomponentcleanup", line =>
                            {
                                if (line.Contains('[') && line.Contains(']') && line.Contains('%') && TryParsePercent(line, out int p))
                                {
                                    percentProgress?.Report(p);
                                    return;
                                }
                                if (!string.IsNullOrWhiteSpace(line)) Report(line);
                            }, cancellationToken);
                            if (!ok)
                            {
                                Report("DISM 组件清理可能未完全成功，请检查系统日志。");
                                return "DISM 组件清理完成（部分项可能未清理）。";
                            }
                            return "DISM 组件清理完成。";

                        case "windowsold":
                            var old = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Windows.old");
                            if (Directory.Exists(old))
                            {
                                Report($"正在删除 {old} ...");
                                long size = DiskUtils.DirSize(old);
                                CleanService.DeleteDir(old, _logger);
                                Report($"已删除 Windows.old，释放 {DiskUtils.FormatSize(size)}");
                                return $"Windows.old 已删除，释放 {DiskUtils.FormatSize(size)}。";
                            }
                            return "未找到 Windows.old。";

                        case "do":
                            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                            var doPath = Path.Combine(winDir, "SoftwareDistribution", "DeliveryOptimization");
                            var downPath = Path.Combine(winDir, "SoftwareDistribution", "Download");

                            
                            
                            if (IsWindowsUpdateActive())
                            {
                                Report("[警告] 检测到 Windows Update 正在运行或有活动任务，建议稍后再执行清理。");
                                Report("仍可选择强制清理，但可能导致当前更新失败需要重新下载。");
                                
                            }

                            long doSize = 0;
                            if (Directory.Exists(doPath)) doSize += DiskUtils.DirSize(doPath);
                            if (Directory.Exists(downPath)) doSize += DiskUtils.DirSize(downPath);
                            Report($"正在清理 Delivery Optimization 缓存 ({DiskUtils.FormatSize(doSize)})...");

                            
                            bool wasServiceStopped = false;
                            ServiceController bitsController = null;
                            try
                            {
                                bitsController = new ServiceController("BITS");
                                if (bitsController.Status == ServiceControllerStatus.Running)
                                {
                                    Report("已暂停 BITS 服务（后台下载服务），清理后将自动恢复。");
                                    bitsController.Stop();
                                    
                                    int waitMs = 0;
                                    while (bitsController.Status != ServiceControllerStatus.Stopped && waitMs < 30000)
                                    {
                                        System.Threading.Thread.Sleep(500);
                                        waitMs += 500;
                                        bitsController.Refresh();
                                    }
                                    
                                    wasServiceStopped = bitsController.Status == ServiceControllerStatus.Stopped;
                                }
                            }
                            catch {   }

                            try
                            {
                                if (Directory.Exists(doPath))
                                    CleanService.DeleteDirContents(doPath, _logger);
                                if (Directory.Exists(downPath))
                                    CleanService.DeleteDirContents(downPath, _logger);
                                return $"Delivery Optimization 缓存已清理，释放 {DiskUtils.FormatSize(doSize)}。";
                            }
                            finally
                            {
                                
                                if (wasServiceStopped && bitsController != null)
                                {
                                    try
                                    {
                                        bitsController.Start();
                                        int waitMs = 0;
                                        while (bitsController.Status != ServiceControllerStatus.Running && waitMs < 30000)
                                        {
                                            System.Threading.Thread.Sleep(500);
                                            waitMs += 500;
                                            bitsController.Refresh();
                                        }
                                    }
                                    catch {   }
                                    finally { bitsController.Dispose(); }
                                }
                                else if (bitsController != null)
                                {
                                    bitsController.Dispose();
                                }
                            }

                        case "dns":
                            Report("正在刷新 DNS 缓存...");
                            RunProcessWithOutput("ipconfig", "/flushdns", Report);
                            return "DNS 缓存已刷新。";

                        case "sfc":
                            Report("正在执行系统文件检查 (sfc /scannow)，需要数分钟...");
                            var sfcLines = new List<string>();
                            
                            
                            
                            
                            
                            RunLongRunningUnicodeTool("sfc", "/scannow", line =>
                            {
                                
                                if ((ContainsProgressGlyph(line) || line.Contains('%')) && TryParsePercent(line, out int p))
                                {
                                    percentProgress?.Report(p);
                                    return;
                                }
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    Report(line);
                                    sfcLines.Add(line);
                                }
                            }, cancellationToken);
                            var summary = sfcLines.LastOrDefault(l =>
                                l.Contains("资源保护") || l.Contains("完整性") || l.Contains("损坏文件") || l.Contains("冲突"))
                                ?? "系统文件检查完成。";
                            return summary;

                        default:
                            return "未知操作。";
                    }
                }
                catch (Exception ex)
                {
                    return $"错误: {ex.Message}";
                }
            });
        }

        
        
        
        
        
        private bool RunProcessWithOutput(string fileName, string args, Action<string> report, Encoding encoding = null, int timeoutMs = 0, CancellationToken cancellationToken = default)
        {
            
            
            
            encoding ??= Encoding.UTF8;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    
                    
                    Arguments = $"/c chcp 65001 >nul && \"{fileName}\" {args}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = encoding,
                    StandardErrorEncoding = encoding
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                
                
                
                
                
                var outTask = Task.Run(() => PumpOutputLines(proc.StandardOutput, report, cancellationToken));
                var errTask = Task.Run(() => PumpOutputLines(proc.StandardError, line => report($"[stderr] {line}"), cancellationToken));

                
                
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (timeoutMs > 0)
                    cts.CancelAfter(timeoutMs);

                var waitTask = Task.Run(() => proc.WaitForExit());
                try
                {
                    waitTask.Wait(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    
                    bool byTimeout = cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    report(byTimeout
                        ? $"[警告] 进程执行超时（{timeoutMs / 1000}s），已终止。"
                        : "[警告] 操作已取消，已终止进程。");
                    return false;
                }

                
                try { Task.WaitAll(outTask, errTask); } catch { }
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                report($"启动失败: {ex.Message}");
                return false;
            }
        }

        
        
        
        
        
        
        private bool RunLongRunningTool(string fileName, string args, Action<string> report, Encoding encoding, CancellationToken cancellationToken)
            => RunProcessWithOutput(fileName, args, report, encoding, timeoutMs: 600000, cancellationToken: cancellationToken);

        
        private bool RunLongRunningUnicodeTool(string fileName, string args, Action<string> report, CancellationToken cancellationToken)
            => RunLongRunningTool(fileName, args, report, Encoding.Unicode, cancellationToken);

        
        private bool RunLongRunningUtf8Tool(string fileName, string args, Action<string> report, CancellationToken cancellationToken)
            => RunLongRunningTool(fileName, args, report, Encoding.UTF8, cancellationToken);

        private static bool TryParsePercent(string line, out int percent)
        {
            percent = 0;
            var m = Regex.Match(line, @"(\d+)\s*%");
            return m.Success && int.TryParse(m.Groups[1].Value, out percent);
        }

        
        
        
        
        private static void PumpOutputLines(StreamReader reader, Action<string> report, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new char[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = reader.Read(buf, 0, buf.Length);
                    if (read == 0) break;
                    for (int i = 0; i < read; i++)
                    {
                        char c = buf[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (sb.Length > 0)
                            {
                                report(sb.ToString());
                                sb.Clear();
                            }
                            
                            if (c == '\r' && i + 1 < read && buf[i + 1] == '\n')
                                i++;
                        }
                        else
                        {
                            sb.Append(c);
                        }
                    }
                }
                if (sb.Length > 0) report(sb.ToString());
            }
            catch (ObjectDisposedException) {   }
            catch (IOException) {   }
        }

        
        
        
        
        private static bool IsWindowsUpdateActive()
        {
            try
            {
                
                using (var bits = new ServiceController("BITS"))
                {
                    if (bits.Status == ServiceControllerStatus.Running)
                        return true;
                }

                
                using (var wu = new ServiceController("wuauserv"))
                {
                    if (wu.Status == ServiceControllerStatus.Running)
                        return true;
                }

                return false;
            }
            catch
            {
                
                return false;
            }
        }

        
        
        
        
        private static readonly string ProgressGlyphs = "◆■□░▒▓█─│┌┐└┘├┤┬┴┼═";
        private static bool ContainsProgressGlyph(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            foreach (var c in line)
            {
                if (ProgressGlyphs.Contains(c)) return true;
            }
            return false;
        }

        
        private const int TokenElevation = 20;
        private const uint TokenQuery = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr hProcess, uint desiredAccess, out IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr hToken, int tokenInfoClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        
        
        
        
        
        public static bool IsRunningAsAdministrator()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                if (!OpenProcessToken(process.Handle, TokenQuery, out var hToken))
                    return false;
                try
                {
                    uint returnLength = 0;
                    var ptr = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        bool ok = GetTokenInformation(hToken, TokenElevation, ptr, (uint)sizeof(int), out returnLength);
                        if (!ok) return false;
                        int elevation = Marshal.ReadInt32(ptr);
                        return elevation != 0;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
            catch { return false; }
        }

        
        
        
        
        
        
        public static (string message, int optimized, double freedMB) OptimizeMemory(Action<string> report)
        {
            int optimized = 0;
            long freedKB = 0;

            report("正在释放系统内存（清理各进程工作集）...");

            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        long before = p.WorkingSet64;
                        IntPtr h = NativeMethods.OpenProcess(
                            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                            false, p.Id);
                        if (h == IntPtr.Zero) continue;

                        try
                        {
                            NativeMethods.EmptyWorkingSet(h);
                        }
                        finally
                        {
                            NativeMethods.CloseHandle(h);
                        }

                        // 必须刷新 Process 缓存，否则 WorkingSet64 读取的是调用前的旧值，会永远得到 0
                        p.Refresh();
                        long after = p.WorkingSet64;
                        if (after < before)
                        {
                            freedKB += (before - after) / 1024;
                            optimized++;
                        }
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                report($"释放系统内存失败: {ex.Message}");
            }

            double freedMB = freedKB / 1024.0;
            if (optimized > 0)
                report($"完成: 已优化 {optimized} 个进程，释放约 {freedMB:F1} MB");
            else
                report("没有可释放的进程内存。");
            return ($"已清理 {optimized} 个进程，释放约 {freedMB:F1} MB", optimized, freedMB);
        }

    }
}
