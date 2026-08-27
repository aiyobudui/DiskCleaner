namespace DiskCleaner.Models;







public class SystemInfo
{
    
    public string SystemVersion { get; set; } = "检测中...";

    
    public string SystemVersionDetail { get; set; } = "";

    
    public string CpuModel { get; set; } = "检测中...";

    
    public string CpuDetail { get; set; } = "";

    
    public string MemoryModel { get; set; } = "检测中...";

    
    public string MemoryDetail { get; set; } = "";

    
    public string GpuModel { get; set; } = "检测中...";
}
