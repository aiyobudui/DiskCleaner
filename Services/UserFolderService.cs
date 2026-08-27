using DiskCleaner.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DiskCleaner.Interop;

namespace DiskCleaner.Services
{
    public interface IUserFolderService
    {
        List<UserFolderInfo> GetAllFolders();
        long GetFolderSize(string path);
        (bool success, string message) MoveFolder(Guid folderId, string newPath, IProgress<string> progress = null);
        (bool success, string message) RestoreFolder(Guid folderId, IProgress<string> progress = null);
    }

    
    
    
    
    
    public class UserFolderService : IUserFolderService
    {
        private static readonly (string name, string icon, Guid guid)[] KnownFolders =
        {
            ("桌面",   "🖥️", NativeMethods.FOLDERID_Desktop),
            ("文档",   "📄", NativeMethods.FOLDERID_Documents),
            ("下载",   "📥", NativeMethods.FOLDERID_Downloads),
            ("图片",   "🖼️", NativeMethods.FOLDERID_Pictures),
            ("音乐",   "🎵", NativeMethods.FOLDERID_Music),
            ("视频",   "🎬", NativeMethods.FOLDERID_Videos),
        };

        public List<UserFolderInfo> GetAllFolders()
        {
            var list = new List<UserFolderInfo>();
            foreach (var (name, icon, guid) in KnownFolders)
            {
                string current = NativeMethods.GetKnownFolderPath(guid) ?? "";
                string defaultPath = NativeMethods.GetKnownFolderDefaultPath(guid)
                    ?? GetProfileDefaultPath(guid);

                list.Add(new UserFolderInfo
                {
                    Name = name,
                    Icon = icon,
                    FolderId = guid,
                    CurrentPath = current,
                    DefaultPath = defaultPath,
                    StatusText = "",
                    SizeText = ""
                });
            }

            
            
            
            
            Parallel.For(0, list.Count, i =>
            {
                list[i].SizeText = DiskUtils.FormatSize(GetFolderSize(list[i].CurrentPath));
            });

            return list;
        }

        
        
        
        
        private static string GetProfileDefaultPath(Guid folderId)
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
                profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            if (string.IsNullOrEmpty(profile))
                return "";

            string subDir = folderId switch
            {
                var g when g == NativeMethods.FOLDERID_Desktop => "Desktop",
                var g when g == NativeMethods.FOLDERID_Documents => "Documents",
                var g when g == NativeMethods.FOLDERID_Downloads => "Downloads",
                var g when g == NativeMethods.FOLDERID_Pictures => "Pictures",
                var g when g == NativeMethods.FOLDERID_Music => "Music",
                var g when g == NativeMethods.FOLDERID_Videos => "Videos",
                _ => null
            };

            if (subDir == null)
                return "";
            return Path.Combine(profile, subDir);
        }

        public long GetFolderSize(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return 0;
            long size = 0;
            try
            {
                
                
                var di = new DirectoryInfo(path);
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var entry in di.EnumerateFileSystemInfos("*", opts))
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

        
        
        
        
        public (bool success, string message) MoveFolder(Guid folderId, string newPath, IProgress<string> progress = null)
        {
            string folderName = GetFolderName(folderId);
            string oldPath = NativeMethods.GetKnownFolderPath(folderId);

            if (string.IsNullOrEmpty(oldPath))
                return (false, "无法获取当前路径。");

            if (string.IsNullOrWhiteSpace(newPath))
                return (false, "新路径不能为空。");

            newPath = newPath.Trim();

            
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                return (false, "新路径与当前路径相同。");

            
            
            var oldWithSep = oldPath.EndsWith(Path.DirectorySeparatorChar)
                ? oldPath
                : oldPath + Path.DirectorySeparatorChar;
            if (newPath.StartsWith(oldWithSep, StringComparison.OrdinalIgnoreCase))
                return (false, "新路径不能是当前路径的子目录。");

            
            
            var newWithSep = newPath.EndsWith(Path.DirectorySeparatorChar)
                ? newPath
                : newPath + Path.DirectorySeparatorChar;
            if (oldPath.StartsWith(newWithSep, StringComparison.OrdinalIgnoreCase))
                return (false, "新路径不能是当前路径的父目录。");

            progress?.Report($"正在准备搬迁 {folderName}...");

            
            try
            {
                if (!Directory.Exists(newPath))
                    Directory.CreateDirectory(newPath);
            }
            catch (Exception ex)
            {
                return (false, $"创建新目录失败: {ex.Message}");
            }

            
            
            
            progress?.Report($"正在搬运文件: {oldPath} → {newPath}");

            var psi = new ProcessStartInfo
            {
                FileName = "robocopy",
                Arguments = $"\"{oldPath}\" \"{newPath}\" /E /MOVE /COPYALL /R:1 /W:1 /MT:8 /NFL /NDL /NJH /NJS",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            
            int exitCode;
            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                    return (false, "无法启动 robocopy。");

                
                
                proc.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        progress?.Report(e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        progress?.Report(e.Data);
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }
            catch (Exception ex)
            {
                return (false, $"robocopy 执行失败: {ex.Message}");
            }

            if (exitCode >= 8)
                return (false, $"文件搬运失败 (robocopy 退出码: {exitCode})。部分文件可能已被移动，请手动检查。");

            if (exitCode == 0)
                progress?.Report("源文件夹没有需要搬运的文件，继续重定向...");
            else
                progress?.Report("文件搬运完成，正在重定向文件夹...");

            
            if (!NativeMethods.SetKnownFolderPath(folderId, newPath))
                return (false, "文件已搬运，但重定向失败。请手动在文件夹属性中设置位置。");

            progress?.Report($"✓ {folderName} 已成功搬迁到 {newPath}");
            return (true, $"{folderName} 搬迁完成。");
        }

        
        
        
        public (bool success, string message) RestoreFolder(Guid folderId, IProgress<string> progress = null)
        {
            string folderName = GetFolderName(folderId);
            string currentPath = NativeMethods.GetKnownFolderPath(folderId);
            string defaultPath = NativeMethods.GetKnownFolderDefaultPath(folderId);

            if (string.IsNullOrEmpty(defaultPath))
                return (false, "无法获取默认路径。");

            if (string.Equals(currentPath, defaultPath, StringComparison.OrdinalIgnoreCase))
                return (false, "当前已是默认路径。");

            progress?.Report($"正在恢复 {folderName} 到默认位置: {defaultPath}");

            
            var result = MoveFolder(folderId, defaultPath, progress);
            if (result.success)
                progress?.Report($"✓ {folderName} 已恢复到默认位置");
            return result;
        }

        private static string GetFolderName(Guid folderId)
        {
            if (folderId == NativeMethods.FOLDERID_Desktop) return "桌面";
            if (folderId == NativeMethods.FOLDERID_Documents) return "文档";
            if (folderId == NativeMethods.FOLDERID_Downloads) return "下载";
            if (folderId == NativeMethods.FOLDERID_Pictures) return "图片";
            if (folderId == NativeMethods.FOLDERID_Music) return "音乐";
            if (folderId == NativeMethods.FOLDERID_Videos) return "视频";
            return "未知文件夹";
        }
    }
}
