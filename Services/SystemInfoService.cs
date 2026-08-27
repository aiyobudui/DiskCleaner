using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DiskCleaner.Interop;
using DiskCleaner.Models;
using Microsoft.Win32;

namespace DiskCleaner.Services
{
    public interface ISystemInfoService
    {
        
        SystemInfo Get();
    }

    
    
    
    
    
    
    
    public class SystemInfoService : ISystemInfoService
    {
        
        private static bool IsVirtualGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var n = name.ToLowerInvariant();
            return n.Contains("oray") ||                
                   n.Contains("sunlogin") ||           
                   n.Contains("todesk") ||             
                   n.Contains("rdp") ||                
                   n.Contains("basicdisplay") ||       
                   n.Contains("microsoft basic") ||    
                   n.Contains("mirage") ||             
                   n.Contains("logmein") ||            
                   n.Contains("vnc") ||                
                   n.Contains("tightvnc") ||
                   n.Contains("anydesk") ||            
                   n.Contains("teamviewer") ||         
                   n.Contains("chrome remote") ||      
                   n.Contains("parsec") ||             
                   n.Contains("virtual display") ||    
                   n.Contains("usb display");          
        }

        
        
        
        
        
        private static T RunWmiWithTimeout<T>(Func<T> work, int milliseconds, T fallback)
        {
            try
            {
                var task = System.Threading.Tasks.Task.Run(work);
                return task.Wait(milliseconds) ? task.Result : fallback;
            }
            catch { return fallback; }
        }

        public SystemInfo Get()
        {
            var info = new SystemInfo();
            try
            {
                var sys = GetSystemVersion();
                info.SystemVersion = sys.Name;
                info.SystemVersionDetail = sys.Detail;
            }
            catch (Exception ex) { info.SystemVersion = "未知 (" + ex.GetType().Name + ")"; }

            try { info.CpuModel = GetCpuModel(); }
            catch (Exception ex) { info.CpuModel = "未知 (" + ex.GetType().Name + ")"; }

            try
            {
                
                
                var cpu = RunWmiWithTimeout(GetCpuInfo, 1500,
                    (Cores: Environment.ProcessorCount, Threads: Environment.ProcessorCount, MaxClockMHz: 0));
                info.CpuDetail = BuildCpuDetail(cpu.Cores, cpu.Threads, cpu.MaxClockMHz);
            }
            catch {   }

            try
            {
                
                var mem = RunWmiWithTimeout(GetMemoryModel, 1500, FallbackMemory());
                info.MemoryModel = mem.Model;
                info.MemoryDetail = mem.Detail;
            }
            catch (Exception ex) { info.MemoryModel = "未知 (" + ex.GetType().Name + ")"; }

            try { info.GpuModel = GetGpuModel(); }
            catch (Exception ex) { info.GpuModel = "未知 (" + ex.GetType().Name + ")"; }

            return info;
        }

        
        private static (string Name, string Detail) GetSystemVersion()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(product)) product = "Windows";

            var ver = key?.GetValue("DisplayVersion")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(ver))
                ver = key?.GetValue("ReleaseId")?.ToString()?.Trim(); 

            var build = key?.GetValue("CurrentBuild")?.ToString()?.Trim();
            var ubr = key?.GetValue("UBR")?.ToString()?.Trim();       

            
            if (product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(build, out int buildNum) && buildNum >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            var name = TranslateProductName(product);

            var detail = "";
            if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(build))
                detail = $"({ver} / Build {build}{(string.IsNullOrWhiteSpace(ubr) ? "" : "." + ubr)})";
            else if (!string.IsNullOrWhiteSpace(build))
                detail = $"(Build {build}{(string.IsNullOrWhiteSpace(ubr) ? "" : "." + ubr)})";

            return (name, detail);
        }

        
        private static string TranslateProductName(string product)
        {
            if (string.IsNullOrWhiteSpace(product)) return "Windows";

            
            var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Windows 11 Pro for Workstations"] = "Windows 11 专业工作站版",
                ["Windows 11 Pro"] = "Windows 11 专业版",
                ["Windows 11 Home"] = "Windows 11 家庭版",
                ["Windows 11 Enterprise"] = "Windows 11 企业版",
                ["Windows 11 Education"] = "Windows 11 教育版",
                ["Windows 11 Pro Education"] = "Windows 11 专业教育版",
                ["Windows 11 Pro N"] = "Windows 11 专业版 N",
                ["Windows 10 Pro for Workstations"] = "Windows 10 专业工作站版",
                ["Windows 10 Pro"] = "Windows 10 专业版",
                ["Windows 10 Home"] = "Windows 10 家庭版",
                ["Windows 10 Enterprise"] = "Windows 10 企业版",
                ["Windows 10 Education"] = "Windows 10 教育版",
            };
            if (exact.TryGetValue(product, out var zh)) return zh;

            
            if (product.IndexOf("Windows 11", StringComparison.OrdinalIgnoreCase) >= 0 ||
                product.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var s = product;
                s = Regex.Replace(s, @"\bPro for Workstations\b", "专业工作站版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bPro\b", "专业版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bHome\b", "家庭版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bEnterprise\b", "企业版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bEducation\b", "教育版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\s+N\b", "", RegexOptions.IgnoreCase); 
                return s.Trim();
            }

            return product;
        }

        
        private static string GetCpuModel()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var cpu = key?.GetValue("ProcessorNameString")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(cpu)) return "未知 CPU";
            return CleanCpuName(cpu);
        }

        private static string CleanCpuName(string cpu)
        {
            if (string.IsNullOrWhiteSpace(cpu)) return cpu;
            
            cpu = Regex.Replace(cpu, @"\((?:R|TM|C|M)\)", "", RegexOptions.IgnoreCase);
            
            cpu = Regex.Replace(cpu, @"\s*CPU\s*@\s*[\d.]+\s*(GHz|MHz)", "", RegexOptions.IgnoreCase);
            
            cpu = Regex.Replace(cpu, @"\s*Processor\s*$", "", RegexOptions.IgnoreCase);
            while (cpu.Contains("  ")) cpu = cpu.Replace("  ", " ");
            return cpu.Trim();
        }

        
        private static (int Cores, int Threads, int MaxClockMHz) GetCpuInfo()
        {
            int cores = 0, threads = 0, maxClock = 0;
            using var searcher = new ManagementObjectSearcher(
                "SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                if (obj["NumberOfCores"] is uint c) cores += (int)c;
                if (obj["NumberOfLogicalProcessors"] is uint t) threads += (int)t;
                if (obj["MaxClockSpeed"] is uint m) maxClock = Math.Max(maxClock, (int)m);

                
                
                if (maxClock == 0)
                {
                    if (obj["CurrentClockSpeed"] is uint cur) maxClock = (int)cur;
                }
            }
            
            if (threads == 0) threads = Environment.ProcessorCount;
            if (cores == 0) cores = threads;
            return (cores, threads, maxClock);
        }

        
        private static string BuildCpuDetail(int cores, int threads, int maxClockMHz)
        {
            var parts = new List<string>();
            if (cores > 0 && threads > 0)
                parts.Add($"{cores}核{threads}线程");
            if (maxClockMHz >= 1000)
                parts.Add($"最高 {maxClockMHz / 1000.0:0.#}GHz");
            else if (maxClockMHz > 0)
                parts.Add($"最高 {maxClockMHz}MHz");
            return parts.Count > 0 ? "(" + string.Join(" / ", parts) + ")" : "";
        }

        
        private static string GetGpuModel()
        {
            var lines = new List<string>();
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls != null)
            {
                foreach (var sub in cls.GetSubKeyNames())
                {
                    
                    if (sub.Length == 0 || !char.IsDigit(sub[0])) continue;
                    using var subKey = cls.OpenSubKey(sub);
                    var desc = subKey?.GetValue("DriverDesc")?.ToString()
                               ?? subKey?.GetValue("Device Description")?.ToString();
                    if (string.IsNullOrWhiteSpace(desc) || IsVirtualGpuName(desc)) continue;
                    var line = FormatGpuLine(desc, ReadRegistryGpuMemoryBytes(subKey));
                    if (!lines.Contains(line))
                        lines.Add(line);
                }
            }
            if (lines.Count > 0) return string.Join("\n", lines);

            
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM FROM Win32_VideoController");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    var name = obj["Name"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(name) || IsVirtualGpuName(name)) continue;
                    ulong ram = 0;
                    var r = obj["AdapterRAM"];
                    if (r != null && ulong.TryParse(r.ToString(), out var rr)) ram = rr;
                    var line = FormatGpuLine(name, ram);
                    if (!lines.Contains(line))
                        lines.Add(line);
                }
                if (lines.Count > 0) return string.Join("\n", lines);
            }
            catch { }
            return "未知显卡";
        }

        
        private static string FormatGpuLine(string rawName, ulong memBytes)
        {
            var name = CleanGpuName(rawName);
            if (IsIntegratedGpu(name))
                return name + " (核显)";
            var mem = FormatBytesAsMemory(memBytes);
            return string.IsNullOrWhiteSpace(mem) ? name : name + " (" + mem + ")";
        }

        private static string CleanGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            name = Regex.Replace(name, @"\((?:R|TM|C|M)\)", "", RegexOptions.IgnoreCase); 
            name = Regex.Replace(name, @"\s*Series\s*$", "", RegexOptions.IgnoreCase); 
            name = name.Trim();
            
            if (name.StartsWith("Radeon", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("AMD", StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0)
            {
                name = "AMD " + name;
            }
            while (name.Contains("  ")) name = name.Replace("  ", " ");
            return name.Trim();
        }

        
        private static bool IsIntegratedGpu(string name)
        {
            var n = name.ToLowerInvariant();
            
            if (n.Contains("intel") &&
                (n.Contains("uhd graphics") || n.Contains("hd graphics") || n.Contains("iris")))
                return true;
            
            if (n.Contains("radeon") && n.Contains("graphics") &&
                !n.Contains("rx ") && !n.Contains("radeon r") && !n.Contains("radeon hd"))
                return true;
            return false;
        }

        private static ulong ReadRegistryGpuMemoryBytes(RegistryKey subKey)
        {
            if (subKey == null) return 0;
            var memObj = subKey.GetValue("HardwareInformation.qwMemorySize");
            if (memObj is ulong u) return u;
            if (memObj is byte[] bytes && bytes.Length >= 8) return BitConverter.ToUInt64(bytes, 0);
            if (memObj != null && ulong.TryParse(memObj.ToString(), out var parsed)) return parsed;
            return 0;
        }

        private static string FormatBytesAsMemory(ulong bytes)
        {
            if (bytes <= 0) return "";
            double gb = bytes / (1024.0 * 1024 * 1024);
            if (gb >= 1) return gb.ToString("F0") + "GB";
            double mb = bytes / (1024.0 * 1024);
            return mb >= 1 ? mb.ToString("F0") + "MB" : "";
        }

        
        private static (string Model, string Detail) GetMemoryModel()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Manufacturer, PartNumber, Speed, ConfiguredClockSpeed, Capacity, SMBIOSMemoryType FROM Win32_PhysicalMemory");
                using var results = searcher.Get();

                ulong totalBytes = 0;
                uint speed = 0;
                uint smbiosType = 0;
                string manufacturer = "";
                int count = 0;

                foreach (ManagementObject mo in results)
                {
                    count++;
                    var cap = mo["Capacity"];
                    if (cap != null && ulong.TryParse(cap.ToString(), out var c)) totalBytes += c;

                    
                    var csp = mo["ConfiguredClockSpeed"];
                    if (csp != null && uint.TryParse(csp.ToString(), out var cs) && cs > 0) speed = Math.Max(speed, cs);
                    var sp = mo["Speed"];
                    if (sp != null && uint.TryParse(sp.ToString(), out var s)) speed = Math.Max(speed, s);

                    var st = mo["SMBIOSMemoryType"];
                    if (st != null && uint.TryParse(st.ToString(), out var t)) smbiosType = t;

                    if (string.IsNullOrWhiteSpace(manufacturer))
                        manufacturer = (mo["Manufacturer"]?.ToString() ?? "").Trim();
                }

                if (count == 0) return FallbackMemory();

                double totalGB = totalBytes / (1024.0 * 1024 * 1024);
                var typeName = MapMemoryType(smbiosType);

                var model = totalGB >= 1
                    ? totalGB.ToString("G0") + " GB"
                    : (totalBytes / (1024.0 * 1024)).ToString("G0") + " MB";

                if (!string.IsNullOrWhiteSpace(typeName)) model += " " + typeName;
                if (speed > 0) model += " " + speed + "MHz";

                
                var detail = "";
                var brand = MapMemoryBrand(manufacturer);
                if (!string.IsNullOrWhiteSpace(brand)) detail = "[" + brand + "]";

                return (model, detail);
            }
            catch
            {
                return FallbackMemory();
            }
        }

        
        private static string MapMemoryBrand(string manufacturer)
        {
            if (string.IsNullOrWhiteSpace(manufacturer) ||
                manufacturer.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return "";

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Gloway International Co Ltd"] = "光威",
                ["Gloway"] = "光威",
                ["Kingston"] = "金士顿",
                ["Kingston Technology"] = "金士顿",
                ["Corsair"] = "海盗船",
                ["G.Skill"] = "芝奇",
                ["Gskill"] = "芝奇",
                ["Crucial"] = "英睿达",
                ["Micron"] = "美光",
                ["Samsung"] = "三星",
                ["SK Hynix"] = "海力士",
                ["Hynix"] = "海力士",
                ["ADATA"] = "威刚",
                ["Team"] = "十铨",
                ["Team Group"] = "十铨",
                ["Apacer"] = "宇瞻",
                ["Transcend"] = "创见",
                ["Patriot"] = "博帝",
                ["Lexar"] = "雷克沙",
                ["Acer"] = "宏碁",
                ["Kingtiger"] = "金泰克",
                ["Ramaxel"] = "记忆科技",
            };
            if (map.TryGetValue(manufacturer, out var zh)) return zh;
            
            return manufacturer.Length <= 16 ? manufacturer : manufacturer.Split(' ')[0];
        }

        
        private static string MapMemoryType(uint t) => t switch
        {
            20 or 21 or 22 => "DDR2",
            23 or 24 or 25 => "DDR3",
            26 or 27 or 28 or 29 or 30 or 31 => "DDR4",
            33 => "LPDDR4",
            34 or 35 => "DDR5",
            36 => "LPDDR5",
            _ => ""
        };

        
        private static (string Model, string Detail) FallbackMemory()
        {
            try
            {
                var mem = new NativeMethods.MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();
                if (NativeMethods.GlobalMemoryStatusEx(ref mem))
                {
                    double gb = mem.ullTotalPhys / (1024.0 * 1024 * 1024);
                    var model = gb >= 1 ? gb.ToString("G0") + " GB" : (mem.ullTotalPhys / (1024.0 * 1024)).ToString("G0") + " MB";
                    return (model, "");
                }
            }
            catch { }
            return ("未知", "");
        }
    }
}
