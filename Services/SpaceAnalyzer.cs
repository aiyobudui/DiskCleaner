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
        // 受系统保护的目录（不扫描、不展示），按大小写不敏感的尾段匹配，兼容任意盘符。
        // 这些目录下的空文件夹对普通用户无意义，且删除可能破坏系统，故整体跳过、不递归。
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

        private static bool IsExcludedDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            // 仅在有斜杠时才做分隔符归一化，避免每次都 ToLowerInvariant 全盘分配。
            if (path.IndexOf('/') >= 0)
                path = path.Replace('/', '\\');

            // 受保护的系统目录都是「盘符根目录下的顶层目录」（如 C:\Windows、C:\Program Files）。
            // 必须按「去盘符后的路径段」精确匹配，否则会把用户自建的 D:\Games\windows 这类
            // 嵌套目录也误判为系统目录而整棵跳过（导致该目录下的空文件夹从不被扫描列出）。
            var root = Path.GetPathRoot(path);
            var withoutRoot = root != null && path.Length > root.Length
                ? path.Substring(root.Length)
                : path;

            foreach (var suffix in _excludedDirSuffixes)
            {
                // suffix 形如 "\windows" 或 "\windows\winsxs\temp"，去掉前导反斜杠后比较：
                //   ① 去盘符后的路径等于该段（如 C:\Windows → "Windows" == "windows"）
                //   ② 去盘符后的路径以该段 + '\' 开头（如 C:\Windows\winsxs\temp 的子目录）
                var rel = suffix.TrimStart('\\');
                if (withoutRoot.Length < rel.Length) continue;
                if (string.Equals(withoutRoot, rel, StringComparison.OrdinalIgnoreCase)) return true;
                if (withoutRoot.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public async Task<List<string>> GetEmptyFoldersAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            // 在后台线程执行，避免阻塞 UI；多级并行枚举显著提升扫描速度
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
                            // 排除受系统保护的路径（如 \Windows\WinSxS\Temp），既不列为结果也不向下递归
                            if (IsExcludedDir(dir))
                                return;

                            // 重解析点（junction / symlink）可能指回自身，导致死循环，直接跳过。
                            // 仅对根目录判定：子目录若在父级枚举时命中 reparse 分支，根本不会被加入 next，
                            // 因此非根目录无需重复 stat（否则在成千上万目录的并行扫描里白做大量 GetAttributes）。
                            if (string.Equals(
                                    Path.TrimEndingDirectorySeparator(dir),
                                    Path.TrimEndingDirectorySeparator(rootPath),
                                    StringComparison.OrdinalIgnoreCase)
                                && DiskUtils.IsReparsePoint(dir))
                                return;

                            bool hasFile = false, hasDir = false;
                            // 一次性枚举并读取属性，避免每条目重复 stat
                            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                            {
                                var attrs = entry.Attributes;
                                if ((attrs & FileAttributes.ReparsePoint) != 0)
                                {
                                    // 重解析点视为"非空"，且不跟随，避免无限递归
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
