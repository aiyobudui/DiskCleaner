using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiskCleaner.Services
{
    public interface ISpaceAnalyzer
    {
        Task<List<string>> GetEmptyFoldersAsync(string rootPath, CancellationToken cancellationToken = default);
    }

    public class SpaceAnalyzer : ISpaceAnalyzer
    {
        
        
        private static readonly string[] _excludedDirSuffixes =
        {
            @"\windows\winsxs\temp",
            @"\windows",
            @"\program files",
            @"\program files (x86)",
            @"\programdata",
            @"\system volume information",
            @"\$recycle.bin",
            @"\recovery",
            @"\documents and settings"
        };

        private static readonly HashSet<string> _protectedUserFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Desktop", "Downloads", "Favorites", "Links", "Music",
            "Pictures", "Videos", "Documents", "Contacts", "Searches", "Saved Games"
        };

        private static bool IsExcludedDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            
            if (path.IndexOf('/') >= 0)
                path = path.Replace('/', '\\');

            
            
            
            var root = Path.GetPathRoot(path);
            var withoutRoot = root != null && path.Length > root.Length
                ? path.Substring(root.Length)
                : path;

            foreach (var suffix in _excludedDirSuffixes)
            {
                
                
                
                var rel = suffix.TrimStart('\\');
                if (withoutRoot.Length < rel.Length) continue;
                if (string.Equals(withoutRoot, rel, StringComparison.OrdinalIgnoreCase)) return true;
                if (withoutRoot.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return IsProtectedUserFolder(path);
        }

        private static bool IsProtectedUserFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var normalized = path.Replace('/', '\\');
            var usersIdx = normalized.IndexOf(@"\Users\", StringComparison.OrdinalIgnoreCase);
            if (usersIdx < 0) return false;

            var afterUsers = normalized.Substring(usersIdx + @"\Users\".Length);
            var parts = afterUsers.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;

            return _protectedUserFolderNames.Contains(parts[1]);
        }

        public async Task<List<string>> GetEmptyFoldersAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            
            return await Task.Run(() =>
            {
                var result = new ConcurrentBag<string>();
                var level = new List<string> { rootPath };
                while (level.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var next = new ConcurrentBag<string>();
                    Parallel.ForEach(level, dir =>
                    {
                        try
                        {
                            
                            if (IsExcludedDir(dir))
                                return;

                            
                            
                            
                            if (string.Equals(
                                    Path.TrimEndingDirectorySeparator(dir),
                                    Path.TrimEndingDirectorySeparator(rootPath),
                                    StringComparison.OrdinalIgnoreCase)
                                && DiskUtils.IsReparsePoint(dir))
                                return;

                            bool hasFile = false, hasDir = false;
                            
                            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                            {
                                var attrs = entry.Attributes;
                                if ((attrs & FileAttributes.ReparsePoint) != 0)
                                {
                                    
                                    hasFile = true;
                                }
                                else if ((attrs & FileAttributes.Directory) != 0)
                                {
                                    hasDir = true;
                                    next.Add(entry.FullName);
                                }
                                else
                                {
                                    hasFile = true;
                                }
                            }

                            if (!hasFile && !hasDir && dir != rootPath)
                                result.Add(dir);
                        }
                        catch { }
                    });
                    level = next.ToList();
                }
                return result.ToList();
            }, cancellationToken);
        }
    }
}
