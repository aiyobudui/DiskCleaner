using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiskCleaner.Services
{
    
    
    
    public static class DiskUtils
    {
        
        
        
        
        
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
                    foreach (var dir in subDirs)
                    {
                        try
                        {
                            
                            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", recOpts))
                            {
                                if (entry is FileInfo fi)
                                {
                                    try { size += fi.Length; } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return size;
        }

        
        
        
        
        
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
