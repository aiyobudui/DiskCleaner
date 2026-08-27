namespace DiskCleaner.Models
{
    public class Config
    {
        public bool IsAdvancedMode { get; set; }
        public string Theme { get; set; } = "dark";

        /// <summary>
        /// 极速清理模式：Safe 级缓存直接删除（不进回收站），Caution 级仍进回收站。
        /// 默认 false，保持「全部先进回收站」的安全行为。
        /// </summary>
        public bool FastClean { get; set; }
    }
}
