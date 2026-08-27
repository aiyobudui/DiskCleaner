using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiskCleaner.Interop;

namespace DiskCleaner.Services
{
    public interface ISystemMonitorService
    {
        /// <summary>仅用原生 API 快速取 CPU / 内存 / 磁盘占用（毫秒级，无 WMI）。用于首屏秒出，不阻塞 UI。</summary>
        Task<(double cpu, double ram, double disk)> GetCpuRamDiskAsync();

        /// <summary>单独取 GPU 占用（WMI 性能计数器，冷启动可能 1~3s）。带超时，超时返回 0。</summary>
        Task<double> GetGpuUsageAsync();
    }

    public class SystemMonitorService : ISystemMonitorService
    {
        /// <summary>
        /// 仅原生 API：CPU（GetSystemTimes 两次采样，含 200ms 间隔）、内存（GlobalMemoryStatusEx）、磁盘（GetDiskFreeSpaceEx）。
        /// 全部为毫秒级调用，无 WMI，确保仪表盘首屏立即有数据。
        /// </summary>
        public async Task<(double cpu, double ram, double disk)> GetCpuRamDiskAsync()
        {
            return await Task.Run(() =>
            {
                var cpu = GetCpuUsageViaGetSystemTimes();
                var ram = GetRamUsageViaGlobalMemoryStatusEx();
                var disk = GetDiskUsage();
                return (cpu, ram, disk);
            });
        }

        /// <summary>
        /// 单独取 GPU 占用。因为底层 WMI 性能计数器冷启动很慢（首次会带动 WmiPrvSE 进程初始化，
        /// 常需 1~3 秒），这里用后台任务 + 超时包裹，避免阻塞调用方；超时或失败返回 0。
        /// </summary>
        public async Task<double> GetGpuUsageAsync()
        {
            try
            {
                // 异步等待 WMI 结果，最多 3 秒；超时则放弃（结果不被采用），返回 0，不阻塞任何线程。
                var gpuTask = System.Threading.Tasks.Task.Run(() => GetGpuUsageViaWmi());
                if (await System.Threading.Tasks.Task.WhenAny(gpuTask, System.Threading.Tasks.Task.Delay(3000)) == gpuTask)
                    return await gpuTask;
                return 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 使用 GetSystemTimes 原生 API 获取 CPU 使用率
        /// 原理: 采样两次间隔的 (idle差) / (total差)，1 - idle/total = CPU使用率
        /// </summary>
        private double GetCpuUsageViaGetSystemTimes()
        {
            try
            {
                // 第一次采样
                NativeMethods.GetSystemTimes(out var idle1, out var kernel1, out var user1);
                System.Threading.Thread.Sleep(200); // 200ms 采样间隔，远快于 PerformanceCounter 的1秒

                // 第二次采样
                NativeMethods.GetSystemTimes(out var idle2, out var kernel2, out var user2);

                // 计算差值 (100ns 单位)
                ulong idle = idle2.ToUInt64() - idle1.ToUInt64();
                ulong kernel = kernel2.ToUInt64() - kernel1.ToUInt64();
                ulong user = user2.ToUInt64() - user1.ToUInt64();
                ulong total = kernel + user;

                if (total == 0) return 0;
                double usage = (1.0 - (double)idle / total) * 100.0;
                return Math.Round(Math.Max(0, Math.Min(100, usage)), 1);
            }
            catch { return 0; }
        }

        /// <summary>
        /// 使用 GlobalMemoryStatusEx 原生 API 获取内存使用率。
        /// 直接按 (总量-可用)/总量 计算，与任务管理器“使用中/总量”一致
        /// （比 Windows 的 dwMemoryLoad 估算在大内存时更准）。
        /// </summary>
        private double GetRamUsageViaGlobalMemoryStatusEx()
        {
            try
            {
                var memStatus = new NativeMethods.MEMORYSTATUSEX();
                memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();
                if (NativeMethods.GlobalMemoryStatusEx(ref memStatus))
                {
                    ulong total = memStatus.ullTotalPhys;
                    if (total == 0) return 0;
                    ulong inUse = total - memStatus.ullAvailPhys;
                    double pct = (double)inUse / (double)total * 100.0;
                    return Math.Round(Math.Max(0, Math.Min(100, pct)), 1);
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 使用 GetDiskFreeSpaceEx 原生 API 获取磁盘使用率。
        /// 取 Windows 所在系统盘（通常是 C:），不再硬编码盘符。
        /// </summary>
        private double GetDiskUsage()
        {
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(root)) root = "C:\\";
                if (NativeMethods.GetDiskFreeSpaceEx(root, out ulong freeBytes, out ulong totalBytes, out _))
                {
                    if (totalBytes == 0) return 0;
                    return Math.Round((1.0 - (double)freeBytes / (double)totalBytes) * 100, 1);
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 使用 WMI GPU 性能计数器获取 GPU 3D 引擎利用率（与任务管理器一致）。
        /// 优先取 Name 包含 "engtype_3D" 的引擎；若没有则取所有引擎最大值。
        /// 若计数器不可用则返回 0。
        /// </summary>
        private double GetGpuUsageViaWmi()
        {
            try
            {
                double max3D = 0;
                double maxAny = 0;
                bool has3D = false;
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
                using var results = searcher.Get();
                foreach (System.Management.ManagementObject obj in results)
                {
                    var nameObj = obj["Name"];
                    var valObj = obj["UtilizationPercentage"];
                    if (nameObj == null || valObj == null) continue;
                    string name = nameObj.ToString() ?? "";
                    if (!uint.TryParse(valObj.ToString(), out uint usage)) continue;
                    double d = Math.Min(100, usage);
                    if (d > maxAny) maxAny = d;
                    if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        has3D = true;
                        if (d > max3D) max3D = d;
                    }
                }
                return Math.Round(has3D ? max3D : maxAny, 1);
            }
            catch { }
            return 0;
        }

    }
}
