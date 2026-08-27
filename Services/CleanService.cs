using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using DiskCleaner.Interop;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 清理核心：提供目录/文件删除的静态方法，所有删除动作统一走这里。
    /// 支持回收站模式、逐文件进度上报、部分成功报告、重试与重启后删除兜底。
    /// </summary>
    public static class CleanService
    {
        /// <summary>
        /// 全局开关: 清理时是否送入回收站而非直接删除。
        /// 始终为 true —— 程序删除任何文件都先移入回收站，便于误删恢复。
        /// </summary>
        public static bool UseRecycleBin { get; set; } = true;

        /// <summary>清理操作的结果统计（支持部分成功）。</summary>
        public readonly struct CleanResult
        {
            public int FilesSucceeded { get; init; }
            public int FilesFailed { get; init; }
            public long BytesFreed { get; init; }
            public int ScheduledForReboot { get; init; }

            /// <summary>全部成功（零失败）。</summary>
            public bool FullySucceeded => FilesFailed == 0 && FilesSucceeded > 0;
            /// <summary>部分成功（有成功也有失败）。</summary>
            public bool PartiallySucceeded => FilesSucceeded > 0 && FilesFailed > 0;
            /// <summary>全部失败（零成功）。</summary>
            public bool FullyFailed => FilesSucceeded == 0 && FilesFailed > 0;
            /// <summary>无操作（目录为空或不存在）。</summary>
            public bool NoOperation => FilesSucceeded == 0 && FilesFailed == 0;

            public override string ToString()
            {
                if (NoOperation) return "无需清理";
                if (FullySucceeded && ScheduledForReboot == 0)
                    return $"已释放 {DiskUtils.FormatSize(BytesFreed)}";
                if (FullySucceeded && ScheduledForReboot > 0)
                    return $"已释放 {DiskUtils.FormatSize(BytesFreed)}（{ScheduledForReboot} 项待重启删除）";
                if (PartiallySucceeded)
                {
                    var parts = $"已释放 {DiskUtils.FormatSize(BytesFreed)}（{FilesSucceeded} 成功";
                    if (ScheduledForReboot > 0) parts += $"/{ScheduledForReboot} 待重启删除";
                    parts += $"/{FilesFailed} 失败）";
                    return parts;
                }
                if (FullyFailed) return $"清理失败（{FilesFailed} 项均无法删除）";
                return string.Empty;
            }
        }

        /// <summary>
        /// 枚举阶段缓存的文件条目：路径 + 已在 FindFirstFile 枚举时取到的长度。
        /// 避免回收站批量清理前再用 new FileInfo(path).Length 对每个文件重复做一次 stat。
        /// </summary>
        private readonly struct FileEntry
        {
            public string Path { get; }
            public long Length { get; }
            public FileEntry(string path, long length)
            {
                Path = path;
                Length = length;
            }
        }

        /// <summary>
        /// 删除目录内容（保留目录本身），返回详细结果。
        /// 当 useRecycleBin=true 时，文件按批量（默认每批 100 个）移入回收站；
        /// 批量发送后会对仍存在的项目逐个兜底重试。未被占用的文件可正常清理，
        /// 被占用/受保护的文件会先重试一次（等 300ms），若仍失败则尝试标记为重启后删除。
        /// 最终返回成功/失败/待重启删除的精确计数，不再因个别文件失败而将整项标记为"清理失败"。
        /// </summary>
        public static CleanResult DeleteDirContents(string path, ILogger logger, bool useRecycleBin, IProgress<string> progress)
        {
            if (!Directory.Exists(path)) return default;
            var result = new CleanResult();
            try
            {
                DeleteDirContentsInternal(path, useRecycleBin, logger, progress, ref result);
            }
            catch (Exception ex)
            {
                // 仅在灾难性错误（如路径不可访问）时记录日志，不抛出——让调用方根据 result 判断
                logger?.Error($"删除目录内容异常: {path}", ex);
            }
            return result;
        }

        /// <summary>
        /// 递归删除目录内的文件与空子目录，记录成功/失败/待重启删除的精确计数。
        /// 始终保留传入的 root 目录本身。
        /// 回收站模式下先批量发送文件，再对残留文件逐个兜底（含重试与重启删除）。
        /// </summary>
        private static void DeleteDirContentsInternal(string path, bool useRecycleBin, ILogger logger, IProgress<string> progress, ref CleanResult result)
        {
            if (!Directory.Exists(path)) return;

            var allFiles = new List<FileEntry>();
            CollectFilesRecursively(path, allFiles, logger);

            if (useRecycleBin)
            {
                // 每批 500 个文件，且整批作为【一次】SHFileOperation 调用送入 STA 线程，
                // 大幅减少 STA 往返次数（临时目录常含数万文件，小批次会非常慢）。
                const int batchSize = 500;
                for (int i = 0; i < allFiles.Count; i += batchSize)
                {
                    var batch = allFiles.GetRange(i, Math.Min(batchSize, allFiles.Count - i));
                    foreach (var entry in batch)
                        progress?.Report(entry.Path);

                    // 字节数在枚举阶段已缓存于 FileEntry.Length，无需在送入回收站前
                    // 再对每个文件 new FileInfo().Length 做一次 stat（大目录可省数万次 stat）。
                    try
                    {
                        // 整批一次送入回收站，但内部按 50 个一组拆分为单个 SHFileOperation，
                        // 避免 500 个长路径塞进一个 pFrom 缓冲区超过 SHFileOperation 上限。
                        bool batchOk = NativeMethods.SendToRecycleBin(batch.Select(e => e.Path), 50);
                        if (!batchOk)
                            logger?.Warning($"批量送入回收站未完全成功 (batch {i / batchSize + 1})，将逐个兜底重试");
                    }
                    catch (Exception ex)
                    {
                        logger?.Error($"批量删除文件失败 (batch {i / batchSize + 1})", ex);
                    }

                    // 批量发送后：已被移入回收站（不再存在）的文件直接计入成功 + 释放字节；
                    // 仍存在的才走逐个兜底重试（含重启删除）。
                    foreach (var entry in batch)
                    {
                        if (!File.Exists(entry.Path))
                        {
                            result = result with
                            {
                                FilesSucceeded = result.FilesSucceeded + 1,
                                BytesFreed = result.BytesFreed + entry.Length
                            };
                        }
                        else
                        {
                            TryDeleteFileWithFallback(entry.Path, useRecycleBin, logger, progress, ref result);
                        }
                    }
                }
            }
            else
            {
                foreach (var entry in allFiles)
                {
                    TryDeleteFileDirect(entry.Path, logger, progress, ref result);
                }
            }

            DeleteEmptySubdirectories(path, useRecycleBin, logger, progress, ref result);
        }

        /// <summary>
        /// 尝试删除单个文件（回收站模式）。
        /// 流程：直接尝试 → 失败则等 300ms 重试 → 仍失败则尝试标记重启后删除 → 最终记录结果。
        /// </summary>
        private static void TryDeleteFileWithFallback(string file, bool useRecycleBin, ILogger logger, IProgress<string> progress, ref CleanResult result)
        {
            if (!File.Exists(file)) return;

            long fileSize = 0;
            try { fileSize = new FileInfo(file).Length; } catch { }

            // 第一次尝试
            if (TrySendToRecycleBinOrDelete(file, useRecycleBin, logger))
            {
                progress?.Report(file);
                result = result with { FilesSucceeded = result.FilesSucceeded + 1, BytesFreed = result.BytesFreed + fileSize };
                return;
            }

            // 立即再试一次（不再 Thread.Sleep(300)）：
            // 之前每个被占用的文件都会空等 300ms，清理大量锁定文件时会累积成分钟级延迟。
            // 短暂锁在两次调用间隙同样有机会释放，无需主动 sleep。
            if (TrySendToRecycleBinOrDelete(file, useRecycleBin, logger))
            {
                progress?.Report(file);
                result = result with { FilesSucceeded = result.FilesSucceeded + 1, BytesFreed = result.BytesFreed + fileSize };
                return;
            }

            // 仍失败：尝试标记为"下次重启时删除"
            bool scheduled = false;
            try
            {
                var attr = File.GetAttributes(file);
                // 系统文件不标记重启删除（风险太高），只对普通/只读/临时文件操作
                if ((attr & FileAttributes.System) != FileAttributes.System && File.Exists(file))
                {
                    scheduled = NativeMethods.ScheduleDeleteOnReboot(file);
                }
            }
            catch { }

            if (scheduled)
            {
                logger?.Warning($"文件被占用，已标记为重启后删除: {file}");
                // 注意：空间需重启后才真正释放，不计入即时 BytesFreed（仅计为待重启删除项）。
                result = result with { FilesSucceeded = result.FilesSucceeded + 1, ScheduledForReboot = result.ScheduledForReboot + 1 };
            }
            else
            {
                logger?.Error($"删除文件失败（已重试）: {file}");
                result = result with { FilesFailed = result.FilesFailed + 1 };
            }
        }

        /// <summary>回收站模式：先 SendToRecycleBin，失败则 File.Delete 兜底。</summary>
        private static bool TrySendToRecycleBinOrDelete(string file, bool useRecycleBin, ILogger logger)
        {
            try
            {
                if (!File.Exists(file)) return true;
                if (useRecycleBin)
                {
                    if (NativeMethods.SendToRecycleBin(file))
                        return true;
                    // 回收站失败（如回收站禁用/满）→ 直接删除兜底，同样先去掉只读属性
                    var attr = File.GetAttributes(file);
                    if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
                    File.Delete(file);
                    return true;
                }
                else
                {
                    var attr = File.GetAttributes(file);
                    if ((attr & FileAttributes.System) == FileAttributes.System) return false;
                    if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
                    File.Delete(file);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 尝试删除单个文件（非回收站模式，无重试——由调用方决定是否重试）。
        /// 用于 DeleteFile 单文件调用路径。
        /// </summary>
        private static void TryDeleteFileDirect(string file, ILogger logger, IProgress<string> progress, ref CleanResult result)
        {
            if (!File.Exists(file)) return;

            long fileSize = 0;
            try { fileSize = new FileInfo(file).Length; } catch { }

            try
            {
                progress?.Report(file);
                var attr = File.GetAttributes(file);
                if ((attr & FileAttributes.System) == FileAttributes.System) return;
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
                File.Delete(file);
                result = result with { FilesSucceeded = result.FilesSucceeded + 1, BytesFreed = result.BytesFreed + fileSize };
            }
            catch (Exception ex)
            {
                // 立即再试一次（不再 sleep）：直接删除路径主要处理临时文件，
                // 锁定文件占比较低但绝对数量可能很大，去掉 300ms 空等可显著提速。
                try
                {
                    File.Delete(file);
                    result = result with { FilesSucceeded = result.FilesSucceeded + 1, BytesFreed = result.BytesFreed + fileSize };
                }
                catch
                {
                    logger?.Error($"删除文件失败: {file}", ex);
                    result = result with { FilesFailed = result.FilesFailed + 1 };
                }
            }
        }

        /// <summary>
        /// 判断路径是否为重解析点（junction / symlink / mount point）。
        /// 清理时必须跳过，否则可能跟随链接进入非预期目录，误删系统/用户关键数据。
        /// 统一复用 DiskUtils.IsReparsePoint，避免多份实现出现行为分歧。
        /// </summary>
        private static bool IsReparsePoint(string path) => DiskUtils.IsReparsePoint(path);

        /// <summary>
        /// 递归收集目录下所有文件（遇到无权限目录或重解析点则跳过并记录）。
        /// </summary>
        private static void CollectFilesRecursively(string path, List<FileEntry> files, ILogger logger)
        {
            try
            {
                // 单次枚举 + 原生跳过 reparse 点（junction/symlink/mount），避免旧实现：
                // 逐文件 EnumerateFiles + 每子目录单独 IsReparsePoint 各做一次 GetAttributes stat。
                // FileSystemInfo.Length/Attributes 在 Windows 目录枚举时已由 FindFirstFile 缓存，无需再次 stat。
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opts))
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        CollectFilesRecursively(entry.FullName, files, logger);
                    }
                    else
                    {
                        long len = 0;
                        if (entry is FileInfo fi)
                        {
                            try { len = fi.Length; } catch { /* 取不到按 0 计 */ }
                        }
                        files.Add(new FileEntry(entry.FullName, len));
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"枚举目录失败: {path}", ex);
            }
        }

        /// <summary>
        /// 自下而上递归删除空子目录（重解析点不跟随、不删除）。
        /// </summary>
        private static void DeleteEmptySubdirectories(string path, bool useRecycleBin, ILogger logger, IProgress<string> progress, ref CleanResult result)
        {
            // 单次枚举 + 原生跳过 reparse 点，避免 Directory.GetDirectories 全量数组 + 每目录单独 IsReparsePoint 做一次 stat。
            var emptyOpts = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var dirInfo in new DirectoryInfo(path).EnumerateDirectories("*", emptyOpts))
            {
                var dir = dirInfo.FullName;
                DeleteEmptySubdirectories(dir, useRecycleBin, logger, progress, ref result);
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    if (Directory.GetFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly).Length > 0) continue;

                    progress?.Report(dir);
                    if (useRecycleBin)
                    {
                        if (!NativeMethods.SendToRecycleBin(dir))
                            Directory.Delete(dir);
                    }
                    else
                    {
                        var attr = File.GetAttributes(dir);
                        if ((attr & FileAttributes.System) == FileAttributes.System) continue;
                        if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            File.SetAttributes(dir, attr & ~FileAttributes.ReadOnly);
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error($"删除目录失败: {dir}", ex);
                    // 空目录删除失败不记为 FilesFailed（非关键路径，且通常可忽略）
                }
            }
        }

        /// <summary>
        /// 删除目录内容（保留目录本身）。useRecycleBin 取自全局开关 UseRecycleBin。
        /// </summary>
        public static CleanResult DeleteDirContents(string path, ILogger logger)
            => DeleteDirContents(path, logger, UseRecycleBin, null);

        /// <summary>
        /// 删除目录根部残留的重解析点（junction / symlink / mount point）链接本身。
        /// 仅移除链接条目，绝不跟随进入目标目录，避免误删链接指向的系统/用户关键数据。
        /// 用于 DeleteDir：清完真实文件与空真实子目录后，把仍残留的 junction 链接一并移除，
        /// 否则根目录非空，后续 Directory.Delete 会失败（整项删除被吞掉）。
        /// </summary>
        private static void RemoveReparsePointChildren(string path, ILogger logger)
        {
            try
            {
                foreach (var child in Directory.GetDirectories(path))
                {
                    if (!IsReparsePoint(child)) continue;
                    try
                    {
                        // recursive:false —— 仅删除链接本身，不递归进入目标（防止误删目标内容）。
                        Directory.Delete(child, false);
                    }
                    catch (Exception ex)
                    {
                        logger?.Error($"删除重解析点链接失败: {child}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"枚举重解析点子目录失败: {path}", ex);
            }
        }

        /// <summary>
        /// 删除整个目录。当 useRecycleBin=true 时通过 SHFileOperation 送入回收站（可恢复）。
        /// 在送回收站 / 删内容的兜底流程中，会先把残留的重解析点（junction/symlink）链接本身移除，
        /// 但绝不跟随进入目标目录，避免 Windows.old 等含大量链接的目录误删系统/用户数据。
        /// </summary>
        public static void DeleteDir(string path, ILogger logger, bool useRecycleBin, IProgress<string> progress)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                if (useRecycleBin)
                {
                    progress?.Report(path);
                    if (!NativeMethods.SendToRecycleBin(path))
                    {
                        // 送回收站失败则兜底：先删真实内容，再移除残留的重解析点链接，最后删目录本身。
                        DeleteDirContents(path, logger, false, progress);
                        RemoveReparsePointChildren(path, logger);
                        DeleteEmptyRoot(path, logger);
                    }
                    return;
                }
                DeleteDirContents(path, logger, false, progress);
                RemoveReparsePointChildren(path, logger);
                DeleteEmptyRoot(path, logger);
            }
            catch (Exception ex)
            {
                logger?.Error($"删除目录失败: {path}", ex);
                throw new IOException($"无法删除目录 {path}，可能被其它程序占用或受系统保护。", ex);
            }
        }

        /// <summary>
        /// 删除已清空（仅剩无害残留）的根目录本身。
        /// 根目录不是系统属性时才删除；只读则先清除只读标记。
        /// 此处不使用递归删除：所有真实内容与重解析点链接已在之前步骤被显式移除，
        /// 仅对「确实为空」的根目录执行删除，杜绝任何跟随链接误删目标的风险。
        /// </summary>
        private static void DeleteEmptyRoot(string path, ILogger logger)
        {
            if (!Directory.Exists(path)) return;
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.System) == FileAttributes.System) return;
            if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
            Directory.Delete(path);
        }

        /// <summary>
        /// 删除整个目录。useRecycleBin 取自全局开关 UseRecycleBin。
        /// </summary>
        public static void DeleteDir(string path, ILogger logger)
            => DeleteDir(path, logger, UseRecycleBin, null);

        /// <summary>
        /// 删除单个文件。useRecycleBin=true 时送入回收站，失败则兜底直接删除。
        /// </summary>
        public static void DeleteFile(string path, ILogger logger, bool useRecycleBin, IProgress<string> progress)
        {
            if (!File.Exists(path)) return;
            try
            {
                progress?.Report(path);
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.System) == FileAttributes.System) return;
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                if (useRecycleBin)
                {
                    if (!NativeMethods.SendToRecycleBin(path))
                        File.Delete(path);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"删除文件失败: {path}", ex);
                throw new IOException($"无法删除文件 {path}，可能被其它程序占用或受系统保护。", ex);
            }
        }

        /// <summary>删除单个文件。useRecycleBin 取自全局开关 UseRecycleBin。</summary>
        public static void DeleteFile(string path, ILogger logger)
            => DeleteFile(path, logger, UseRecycleBin, null);

        /// <summary>
        /// 清空回收站（彻底删除已移入回收站的垃圾）。
        /// 注意：使用 SHEmptyRecycleBin(IntPtr.Zero, null, ...) 会清空【所有磁盘】的回收站，
        /// 包括用户手动删除的文件（不仅限于本工具移入的项）。属高风险动作，调用方须在 UI 上
        /// 明确提示并获得用户确认（对应清理项已设为 RiskLevel.Caution 且不自动勾选）。
        /// 使用 SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND，静默无确认、无进度窗、无声音。
        /// 失败向上抛异常，交由调用方标记该项为“失败”。
        /// </summary>
        public static void EmptyRecycleBin(ILogger logger = null)
        {
            try
            {
                const int flags = NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND;
                // SHEmptyRecycleBin 是 shell 调用，必须在 STA 线程执行；否则在 MTA(线程池)线程上会卡死。
                int hr = NativeMethods.EmptyRecycleBinSta(IntPtr.Zero, null, flags);
                if (hr != 0)
                    throw new IOException($"清空回收站失败（HRESULT: 0x{hr:X8}）。");
            }
            catch (Exception ex)
            {
                logger?.Error("清空回收站失败", ex);
                throw new IOException("清空回收站失败，可能回收站正被占用或无法访问。", ex);
            }
        }

        /// <summary>
        /// 获取回收站总占用大小（字节）。调用 SHQueryRecycleBin，跨驱动器汇总。
        /// 查询失败返回 0。SHQueryRecycleBin 同为 shell 调用，经 STA 线程执行以防 MTA 卡死。
        /// </summary>
        public static long GetRecycleBinSize()
        {
            try
            {
                var info = new NativeMethods.SHQUERYRBINFO
                {
                    cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>()
                };
                int hr = NativeMethods.QueryRecycleBinSta(null, ref info);
                if (hr == 0)
                    return info.i64Size;
            }
            catch { }
            return 0;
        }
    }
}
