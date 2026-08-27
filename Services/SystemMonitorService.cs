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
        
        Task<(double cpu, double ram, double disk)> GetCpuRamDiskAsync();

        
        Task<double> GetGpuUsageAsync();
    }

    public class SystemMonitorService : ISystemMonitorService
    {
        
        
        
        
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

        
        
        
        
        public async Task<double> GetGpuUsageAsync()
        {
            try
            {
                
                var gpuTask = System.Threading.Tasks.Task.Run(() => GetGpuUsageViaWmi());
                if (await System.Threading.Tasks.Task.WhenAny(gpuTask, System.Threading.Tasks.Task.Delay(3000)) == gpuTask)
                    return await gpuTask;
                return 0;
            }
            catch { return 0; }
        }

        
        
        
        
        private double GetCpuUsageViaGetSystemTimes()
        {
            try
            {
                
                NativeMethods.GetSystemTimes(out var idle1, out var kernel1, out var user1);
                System.Threading.Thread.Sleep(200); 

                
                NativeMethods.GetSystemTimes(out var idle2, out var kernel2, out var user2);

                
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
