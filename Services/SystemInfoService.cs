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
        /// <summary>收集本机系统/CPU/内存/显卡信息（同步，可能耗时 ~300ms，调用方应置于后台线程）。</summary>
        SystemInfo Get();
    }

    /// <summary>
    /// 收集本机系统信息，并统一格式化为「高端监控软件」风格的清爽展示文本：
    /// - 系统版本：注册表 ProductName 本地化为中文 + 版本号后置灰化
    /// - CPU：去除 (R)/(TM) 商标符号与 "CPU @ x.xxGHz" 后缀，副文本显示核心/线程/最大频率
    /// - 内存：极简规格 + 品牌中文映射（去除条数与料号）
    /// - 显卡：清理名称、为纯 Radeon 补 "AMD" 前缀、核显标注 "(核显)"
    /// </summary>
    public class SystemInfoService : ISystemInfoService
    {
        /// <summary>过滤虚拟/远程显示驱动，避免把向日葵/RDP/VNC等虚拟卡显示为真实显卡。</summary>
        private static bool IsVirtualGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var n = name.ToLowerInvariant();
            return n.Contains("oray") ||                // 向日葵 OraylddDriver
                   n.Contains("sunlogin") ||           // 向日葵旧名
                   n.Contains("todesk") ||             // ToDesk 虚拟驱动
                   n.Contains("rdp") ||                // 远程桌面 RDPUDD/RDPDD
                   n.Contains("basicdisplay") ||       // Microsoft Basic Display Driver
                   n.Contains("microsoft basic") ||    // Microsoft Basic Display Driver 别名
                   n.Contains("mirage") ||             // 镜像驱动
                   n.Contains("logmein") ||            // LogMeIn 镜像驱动
                   n.Contains("vnc") ||                // VNC 远程显示
                   n.Contains("tightvnc") ||
                   n.Contains("anydesk") ||            // AnyDesk 虚拟显示
                   n.Contains("teamviewer") ||         // TeamViewer 虚拟显示
                   n.Contains("chrome remote") ||      // Chrome 远程桌面
                   n.Contains("parsec") ||             // Parsec 虚拟显示
                   n.Contains("virtual display") ||    // 通用虚拟显示
                   n.Contains("usb display");          // USB 外接显卡/显示适配器
        }

        /// <summary>
        /// 在后台线程运行 WMI 查询并加超时兜底。WMI 在进程冷启动时可能很慢（需初始化 WmiPrvSE 提供程序），
        /// 超时后返回 fallback，避免把“系统信息”卡片卡在慢速查询上。
        /// 注意：超时后原 WMI 查询仍在后台线程跑完即被丢弃，不会无限挂起调用方。
        /// </summary>
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
                // WMI 查询在进程冷启动时可能很慢（带动 WmiPrvSE 初始化），加超时兜底，
                // 超时则用回退值（逻辑线程数），避免“CPU 详情”卡住整张卡片。
                var cpu = RunWmiWithTimeout(GetCpuInfo, 1500,
                    (Cores: Environment.ProcessorCount, Threads: Environment.ProcessorCount, MaxClockMHz: 0));
                info.CpuDetail = BuildCpuDetail(cpu.Cores, cpu.Threads, cpu.MaxClockMHz);
            }
            catch { /* 核心/线程/频率是附加信息，读取失败时保持空 */ }

            try
            {
                // 内存规格同样走 WMI（Win32_PhysicalMemory），加超时；超时回退到仅显示总容量。
                var mem = RunWmiWithTimeout(GetMemoryModel, 1500, FallbackMemory());
                info.MemoryModel = mem.Model;
                info.MemoryDetail = mem.Detail;
            }
            catch (Exception ex) { info.MemoryModel = "未知 (" + ex.GetType().Name + ")"; }

            try { info.GpuModel = GetGpuModel(); }
            catch (Exception ex) { info.GpuModel = "未知 (" + ex.GetType().Name + ")"; }

            return info;
        }

        // ── 系统版本（注册表）→ 主名 + 副文本 ──
        private static (string Name, string Detail) GetSystemVersion()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(product)) product = "Windows";

            var ver = key?.GetValue("DisplayVersion")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(ver))
                ver = key?.GetValue("ReleaseId")?.ToString()?.Trim(); // Win10 旧版使用 ReleaseId

            var build = key?.GetValue("CurrentBuild")?.ToString()?.Trim();
            var ubr = key?.GetValue("UBR")?.ToString()?.Trim();       // 修订号

            // Windows 11 兼容：注册表 ProductName 仍可能写 Windows 10，按 Build >= 22000 修正
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

        /// <summary>将英文 Windows SKU 名称翻译为更亲切的中文显示名。</summary>
        private static string TranslateProductName(string product)
        {
            if (string.IsNullOrWhiteSpace(product)) return "Windows";

            // 精确命中优先（覆盖绝大多数常见 SKU）
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

            // 兜底：基于 Windows 11 / 10 做英文 token 替换（注意顺序：先替换长词）
            if (product.IndexOf("Windows 11", StringComparison.OrdinalIgnoreCase) >= 0 ||
                product.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var s = product;
                s = Regex.Replace(s, @"\bPro for Workstations\b", "专业工作站版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bPro\b", "专业版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bHome\b", "家庭版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bEnterprise\b", "企业版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\bEducation\b", "教育版", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\s+N\b", "", RegexOptions.IgnoreCase); // 去掉 "N" 后缀
                return s.Trim();
            }

            return product;
        }

        // ── CPU 名称（注册表，去商标符与频率后缀）──
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
            // 去除商标符号 (R) (TM) (C) (M) 等
            cpu = Regex.Replace(cpu, @"\((?:R|TM|C|M)\)", "", RegexOptions.IgnoreCase);
            // 去除末尾 " CPU @ 3.80GHz" / " CPU @ 3.80 GHz"
            cpu = Regex.Replace(cpu, @"\s*CPU\s*@\s*[\d.]+\s*(GHz|MHz)", "", RegexOptions.IgnoreCase);
            // AMD 等末尾 " Processor" 后缀
            cpu = Regex.Replace(cpu, @"\s*Processor\s*$", "", RegexOptions.IgnoreCase);
            while (cpu.Contains("  ")) cpu = cpu.Replace("  ", " ");
            return cpu.Trim();
        }

        // ── CPU 核心/线程/最大频率（WMI）──
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

                // 部分机器（虚拟机/个别机型）WMI 不返回 MaxClockSpeed(=0)，
                // 降级用 CurrentClockSpeed 兜底，避免频率被静默丢弃。
                if (maxClock == 0)
                {
                    if (obj["CurrentClockSpeed"] is uint cur) maxClock = (int)cur;
                }
            }
            // WMI 失败时回退到 Environment.ProcessorCount（逻辑线程数）
            if (threads == 0) threads = Environment.ProcessorCount;
            if (cores == 0) cores = threads;
            return (cores, threads, maxClock);
        }

        /// <summary>组合 CPU 副文本：(核心线程 / 最大频率)</summary>
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

        // ── 显卡（注册表显示适配器 Class 键，失败用 WMI fallback）──
        private static string GetGpuModel()
        {
            var lines = new List<string>();
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls != null)
            {
                foreach (var sub in cls.GetSubKeyNames())
                {
                    // 适配器子键为纯数字（0000, 0001...）；跳过 "Properties" 等非数字子键
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

            // fallback：WMI Win32_VideoController
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

        /// <summary>清理单张显卡名称，并按「核显/独显」决定副文本。</summary>
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
            name = Regex.Replace(name, @"\((?:R|TM|C|M)\)", "", RegexOptions.IgnoreCase); // 去商标符
            name = Regex.Replace(name, @"\s*Series\s*$", "", RegexOptions.IgnoreCase); // 去 " Series" 后缀
            name = name.Trim();
            // 纯 "Radeon ..."（非 AMD/NVIDIA/Intel 前缀）补 "AMD" 前缀
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

        /// <summary>判断是否为核显（用于标注 "(核显)" 而非显存）。</summary>
        private static bool IsIntegratedGpu(string name)
        {
            var n = name.ToLowerInvariant();
            // Intel 核显：UHD/HD/Iris。注意不要把 Intel Arc 独显（A/B 系列）误判为核显。
            if (n.Contains("intel") &&
                (n.Contains("uhd graphics") || n.Contains("hd graphics") || n.Contains("iris")))
                return true;
            // AMD APU 核显（含 "graphics" 但非独立 RX/R9/R7 等）
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

        // ── 内存型号/类型/频率（WMI，失败回退总量）──
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

                    // 优先用实际配置频率 ConfiguredClockSpeed，回退 Speed
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

                // 副文本：品牌中文映射（去除无人懂的料号）
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

        /// <summary>将厂商英文名映射为简洁中文品牌名（未命中的短名保留，过长取首词）。</summary>
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
            // 未命中：短名称（如 "Kingston"）直接保留，过长取首词避免堆砌型号
            return manufacturer.Length <= 16 ? manufacturer : manufacturer.Split(' ')[0];
        }

        /// <summary>将 SMBIOS 内存类型枚举映射为 DDR 名（未识别返回空）。</summary>
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

        /// <summary>WMI 不可用时的回退：仅显示总容量（GlobalMemoryStatusEx）。</summary>
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
