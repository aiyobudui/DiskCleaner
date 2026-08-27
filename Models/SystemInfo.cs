namespace DiskCleaner.Models;

/// <summary>
/// 仪表盘顶部展示的本机硬件/系统信息。
/// 由 SystemInfoService 在后台收集并格式化后一次性填充。
/// 每张卡片拆为「主文本」(Model 字段) + 「灰化副文本」(Detail 字段)，
/// 副文本用于版本号、核心/线程/频率、内存品牌等次要信息。
/// </summary>
public class SystemInfo
{
    /// <summary>操作系统名称（已本地化），例如 "Windows 11 专业工作站版"</summary>
    public string SystemVersion { get; set; } = "检测中...";

    /// <summary>系统版本副文本，例如 "(25H2 / Build 26200.8894)"（灰化显示）</summary>
    public string SystemVersionDetail { get; set; } = "";

    /// <summary>CPU 型号（已去除商标符号与频率后缀），例如 "Intel Core i7-14700K"</summary>
    public string CpuModel { get; set; } = "检测中...";

    /// <summary>CPU 副文本，例如 "(20核28线程 / 最高 5.6GHz)"（灰化显示）</summary>
    public string CpuDetail { get; set; } = "";

    /// <summary>内存规格主文本，例如 "48 GB DDR5 6400MHz"</summary>
    public string MemoryModel { get; set; } = "检测中...";

    /// <summary>内存副文本，例如 "[光威]"（灰化显示）</summary>
    public string MemoryDetail { get; set; } = "";

    /// <summary>显卡型号（多卡以换行分隔，已清理并标注核显），例如 "AMD Radeon RX 580 (8GB)\nIntel UHD Graphics 770 (核显)"</summary>
    public string GpuModel { get; set; } = "检测中...";
}
