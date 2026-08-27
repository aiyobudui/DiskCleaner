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
        /// <summary>
        /// 执行系统优化操作，实时把命令输出作为文字提示回传到 UI（progress）。
        /// percentProgress 用于回传可解析的百分比（如 DISM 的 [====xx%====]、
        /// SFC 的 "Verification xx% complete."），调用方据此显示确定性百分比进度条。
        /// </summary>
        Task<string> ExecuteAsync(string action, IProgress<string> progress = null, IProgress<int> percentProgress = null, CancellationToken cancellationToken = default);
    }

    public class SystemOptimizeService : ISystemOptimizeService
    {
        private readonly ILogger _logger;

        public SystemOptimizeService(ILogger logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// 执行系统优化操作，实时输出命令执行结果到 UI
        /// </summary>
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
                            // 默认使用安全模式：不带 /resetbase
                            // /resetbase 会移除组件存储中所有累积更新的元数据，使已安装的更新无法卸载
                            // 仅在确认系统不再需要卸载旧更新时才使用
                            // DISM 在控制台输出原生进度条 [=====xx%=====] 与阶段文字；
                            // 这里逐行解析：进度条行提取百分比回传（百分比进度条），其余行作为文字提示显示，
                            // 既保留真实进度，又不让 [===xx%===] 原始字符污染卡片文字。
                            bool ok = RunProcessWithOutput("dism", "/online /cleanup-image /startcomponentcleanup", line =>
                            {
                                if (line.Contains('[') && line.Contains(']') && line.Contains('%'))
                                {
                                    var m = Regex.Match(line, @"(\d+)\s*%");
                                    if (m.Success && int.TryParse(m.Groups[1].Value, out int p))
                                        percentProgress?.Report(p);
                                    return;
                                }
                                if (!string.IsNullOrWhiteSpace(line)) Report(line);
                            }, timeoutMs: 600000, cancellationToken: cancellationToken);
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

                            // 安全检查：如果 Windows Update 服务正在运行且有活动，不应清理
                            // 这可以防止在更新进行中时清理导致损坏
                            if (IsWindowsUpdateActive())
                            {
                                Report("[警告] 检测到 Windows Update 正在运行或有活动任务，建议稍后再执行清理。");
                                Report("仍可选择强制清理，但可能导致当前更新失败需要重新下载。");
                                // 不自动跳过，让用户看到警告
                            }

                            long doSize = 0;
                            if (Directory.Exists(doPath)) doSize += DiskUtils.DirSize(doPath);
                            if (Directory.Exists(downPath)) doSize += DiskUtils.DirSize(downPath);
                            Report($"正在清理 Delivery Optimization 缓存 ({DiskUtils.FormatSize(doSize)})...");

                            // 安全处理：先停止 BITS 服务（如果正在运行）
                            bool wasServiceStopped = false;
                            ServiceController bitsController = null;
                            try
                            {
                                bitsController = new ServiceController("BITS");
                                if (bitsController.Status == ServiceControllerStatus.Running)
                                {
                                    Report("已暂停 BITS 服务（后台下载服务），清理后将自动恢复。");
                                    bitsController.Stop();
                                    // 手动等待服务停止（最多30秒）
                                    int waitMs = 0;
                                    while (bitsController.Status != ServiceControllerStatus.Stopped && waitMs < 30000)
                                    {
                                        System.Threading.Thread.Sleep(500);
                                        waitMs += 500;
                                        bitsController.Refresh();
                                    }
                                    // 只有确认服务确实已停止，才在 finally 里尝试恢复
                                    wasServiceStopped = bitsController.Status == ServiceControllerStatus.Stopped;
                                }
                            }
                            catch { /* 服务可能不存在或无权限，不阻塞清理 */ }

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
                                // 恢复 BITS 服务
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
                                    catch { /* 恢复失败不影响清理结果 */ }
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
                            // SFC 实时输出分三类：
                            // ① 控制台方块 / 方括号进度条（■□░▒▓█ 等图形字符，靠 \r 原地重绘）→ 含 % 时仅提取百分比，绝不显示原文（否则乱码 / 刷屏）；
                            // ② “Verification xx% complete.” / “验证 xx% 已完成。” 这类百分比进度文字 → 仅提取百分比喂进度条，文字与进度条重复，不显示；
                            // ③ 真正的阶段 / 结果文字（开始扫描、开始验证、最终“Windows 资源保护…”结论）→ 原样显示并存入摘要。
                            // 这样进度条显示真实百分比、状态文字干净无乱码，不会因 \r 重绘被刷屏。
                            RunProcessWithOutput("sfc", "/scannow", line =>
                            {
                                // 含方块图形字符，或含百分比的进度行 → 只取百分比，不显示原始文本
                                if (ContainsProgressGlyph(line) || line.Contains('%'))
                                {
                                    var m = Regex.Match(line, @"(\d+)\s*%");
                                    if (m.Success && int.TryParse(m.Groups[1].Value, out int p))
                                        percentProgress?.Report(p);
                                    return;
                                }
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    Report(line);
                                    sfcLines.Add(line);
                                }
                            }, timeoutMs: 600000, cancellationToken: cancellationToken);
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

        /// <summary>
        /// 运行外部进程，实时捕获 stdout/stderr 逐行回传。
        /// 支持超时（timeoutMs &gt; 0 时，超时后真正终止进程，避免 sfc/dism 卡死在后台无限运行）
        /// 与取消（cancellationToken 触发时终止进程并以 false 返回）。
        /// </summary>
        private bool RunProcessWithOutput(string fileName, string args, Action<string> report, Encoding encoding = null, int timeoutMs = 0, CancellationToken cancellationToken = default)
        {
            // 用 cmd /c chcp 65001 强制子进程以 UTF-8 输出，再执行命令；
            // 彻底避免 DISM/SFC 等旧控制台工具按系统 OEM 代码页（中文系统 936/GBK）输出导致的中文乱码。
            // 此方式与 app.manifest 的 activeCodePage 解耦，无论运行副本是否嵌入 manifest 都能正确解码。
            encoding ??= Encoding.UTF8;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // chcp 65001 切换当前 cmd 会话代码页为 UTF-8，>nul 隐藏 chcp 自身输出；
                    // 用 && 串联，仅当 chcp 成功才执行目标命令，目标文件名加引号以兼容含空格路径。
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

                // 关键修复：SFC / DISM 的进度条靠「回车符 \r」在同一行原地重绘，并不以换行结尾。
                // 旧实现用 OutputDataReceived（仅按 \n 切行），这些 \r 进度块被合并到下一次 \n 才回传，
                // 导致 SFC 全程只收到结尾的 “100% complete.”，因此一直是不确定态跑马灯。
                // 这里改为手动读原始流、按 \r 与 \n 同时切行，让每个 “Verification xx% complete.”
                // 进度块都作为独立回调上报，从而还原 DISM 风格的确定性百分比进度条。
                var outTask = Task.Run(() => PumpOutputLines(proc.StandardOutput, report, cancellationToken));
                var errTask = Task.Run(() => PumpOutputLines(proc.StandardError, line => report($"[stderr] {line}"), cancellationToken));

                // 用后台任务包装 WaitForExit，再用可取消/可超时的 Wait 实现；
                // 超时或取消后真正终止进程（proc.Kill），避免 sfc/dism 在后台无限挂起。
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
                    // 区分“超时”与“用户取消”
                    bool byTimeout = cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    report(byTimeout
                        ? $"[警告] 进程执行超时（{timeoutMs / 1000}s），已终止。"
                        : "[警告] 操作已取消，已终止进程。");
                    return false;
                }

                // 进程已退出，等输出流读取完毕（管道关闭）再返回，避免丢失最后一行。
                try { Task.WaitAll(outTask, errTask); } catch { }
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                report($"启动失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从进程输出流逐字符读取，按 \r 与 \n 同时切行后逐行回传。
        /// 与 OutputDataReceived（只按 \n 切行）不同，本方法能捕获控制台工具用 \r 原地重绘的进度条
        /// （例如 sfc 的 “Verification xx% complete.”），从而让调用方解析出实时百分比。
        /// </summary>
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
                            // \r\n 视为一个换行，跳过紧邻的 \n
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
            catch (ObjectDisposedException) { /* 进程被终止，管道关闭 */ }
            catch (IOException) { /* 进程被终止，管道关闭 */ }
        }

        /// <summary>
        /// 检查 Windows Update 是否正在进行活动（下载/安装等）。
        /// 检查方法：查询 BITS 服务和 Windows Update 服务是否在运行。
        /// </summary>
        private static bool IsWindowsUpdateActive()
        {
            try
            {
                // 检查 BITS 服务（后台智能传输服务，负责下载更新）
                using (var bits = new ServiceController("BITS"))
                {
                    if (bits.Status == ServiceControllerStatus.Running)
                        return true;
                }

                // 检查 Windows Update 服务
                using (var wu = new ServiceController("wuauserv"))
                {
                    if (wu.Status == ServiceControllerStatus.Running)
                        return true;
                }

                return false;
            }
            catch
            {
                // 服务可能不存在（如精简版系统），保守返回 false
                return false;
            }
        }

        /// <summary>
        /// 判断一行是否包含控制台进度条的方块 / 方括号 / 制表线等图形字符。
        /// 这些字符来自 sfc / 旧控制台工具的 \r 原地重绘进度条，显示到 WPF 文本里会变成乱码或方块，需过滤。
        /// </summary>
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

        // ── 提权检测（与 App.xaml.cs 共用同一实现，单一事实来源） ──
        private const int TokenElevation = 20;
        private const uint TokenQuery = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr hProcess, uint desiredAccess, out IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr hToken, int tokenInfoClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 用 TokenElevation 检测是否真正拿到管理员高完整性令牌。
        /// 不能用 WindowsPrincipal.IsInRole(Administrator)：非提权的管理员用户
        /// 令牌里仍带 Administrators 组（deny-only），会误判为已提权。
        /// </summary>
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

        /// <summary>
        /// 内存优化：清理【系统内所有进程】的工作集，而不是只清理本程序自身。
        /// 对每个可访问的进程调用 EmptyWorkingSet，把其工作集页归还到待机/页面池，
        /// 从而降低整体物理内存占用。受保护进程（如系统关键进程）打开句柄会失败，自动跳过。
        /// 返回 (结果文本, 成功优化的进程数, 释放的 MB 数)。
        /// </summary>
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
            return ($"内存优化完成 — 已清理 {optimized} 个进程，释放约 {freedMB:F1} MB", optimized, freedMB);
        }

    }
}
