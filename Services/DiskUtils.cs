using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 磁盘/文件大小相关工具方法，供 ViewModel 与 Service 共享，避免重复实现。
    /// </summary>
    public static class DiskUtils
    {
        /// <summary>
        /// 判断路径是否为重解析点（junction / symlink / mount point）。
        /// 清理/递归枚举时必须跳过，否则会跟随链接进入非预期目录，甚至造成死循环或误删。
        /// 取属性失败时视为非重解析点（保守返回 false，不阻断主流程）。
        /// </summary>
        public static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch { return false; }
        }

        public static long DirSize(string path)
        {
            long size = 0;
            try
            {
                var di = new DirectoryInfo(path);
                if (!di.Exists) return 0;

                // 顶层不递归：把一级子目录分摊到多个线程并行统计，SSD 上可显著缩短扫描时间；
                // HDD 上受限于并发磁头争抢，用保守并发度 4 避免反而更慢。
                var topOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                var entries = di.EnumerateFileSystemInfos("*", topOpts).ToList();
                var subDirs = new List<string>();

                foreach (var entry in entries)
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        subDirs.Add(entry.FullName);
                    }
                    else if (entry is FileInfo fi)
                    {
                        try { size += fi.Length; } catch { }
                    }
                }

                if (subDirs.Count > 0)
                {
                    var recOpts = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };
                    long subTotal = 0;
                    Parallel.ForEach(
                        subDirs,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount))
                        },
                        dir =>
                        {
                            long local = 0;
                            try
                            {
                                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", recOpts))
                                {
                                    if (entry is FileInfo fi)
                                    {
                                        try { local += fi.Length; } catch { }
                                    }
                                }
                            }
                            catch { }
                            Interlocked.Add(ref subTotal, local);
                        });
                    size += subTotal;
                }
            }
            catch { }
            return size;
        }

        /// <summary>
        /// 估算单目录下（不递归）匹配通配符的文件总大小。
        /// 用 EnumerateFileSystemInfos 一次性枚举并读取缓存的 Length，避免逐文件重复 stat。
        /// 仅返回文件（AttributesToSkip 含 Directory）。
        /// </summary>
        public static long FilesSize(string dir, string pattern = "*")
        {
            long size = 0;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return 0;
            try
            {
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory
                };
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos(pattern, opts))
                {
                    // 同上：FileSystemInfo 无 Length，按 FileInfo 转型取大小（AttributesToSkip 已排除目录）。
                    if (entry is FileInfo fi)
                    {
                        try { size += fi.Length; } catch { }
                    }
                }
            }
            catch { }
            return size;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
