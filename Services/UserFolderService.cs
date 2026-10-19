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

            // 并行计算每个文件夹大小（只读，安全）
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

            // —— 路径规范化：必须先做，否则“相同/子目录/父目录”判断会因正斜杠/尾斜杠而漏判或误判 ——
            if (!Path.IsPathRooted(newPath.Trim()))
                return (false, "请填写完整路径（如 D:\\Downloads）。");

            string normalized;
            try { normalized = Path.GetFullPath(newPath.Trim()); }
            catch (Exception ex) { return (false, $"新路径格式无效: {ex.Message}"); }

            // 磁盘根目录作为目标会导致与盘上其他内容混放，拒绝
            if (Path.GetPathRoot(normalized)?.Equals(normalized, StringComparison.OrdinalIgnoreCase) == true)
                return (false, "不能迁移到磁盘根目录，请选择盘内的具体文件夹。");

            newPath = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            oldPath = oldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                return (false, "新路径与当前路径相同。");

            // 规范化后均为“反斜杠、无尾斜杠”，以下判断可靠
            if (newPath.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (false, "新路径不能是当前路径的子目录。");
            if (oldPath.StartsWith(newPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (false, "新路径不能是当前路径的父目录。");

            // —— 目标目录：必须不存在或为空，避免与已有内容混放 ——
            try
            {
                if (Directory.Exists(newPath))
                {
                    if (Directory.GetFileSystemEntries(newPath).Length > 0)
                        return (false, "目标目录已存在且不为空，为避免文件混放，请选择空目录或不存在的位置。");
                }
                else
                {
                    Directory.CreateDirectory(newPath);
                }
            }
            catch (Exception ex)
            {
                return (false, $"创建新目录失败: {ex.Message}");
            }

            // —— 目标盘文件系统检测：非 NTFS 不支持复制 ACL/所有者，降级为 COPY:DAT ——
            string copyFlags;
            try
            {
                var fs = new DriveInfo(Path.GetPathRoot(newPath)).DriveFormat;
                copyFlags = string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase)
                    ? "/COPYALL"
                    : "/COPY:DAT";
                if (copyFlags == "/COPY:DAT")
                    progress?.Report($"目标盘为 {fs}，不支持复制文件权限，将使用兼容模式搬运...");
            }
            catch { copyFlags = "/COPYALL"; }

            progress?.Report($"正在搬运 {folderName}...");

            // —— 两阶段迁移：先纯复制（不删源），验证成功后再移除源，避免中途失败导致源被删一半 ——
            // 阶段 1：复制（不删源）。失败时源文件夹保持原样，无数据风险。
            int copyExit = RunRobocopy(oldPath, newPath,
                $"/E {copyFlags} /XJ /R:1 /W:1 /MT:8 /NFL /NDL /NJH /NJS", progress);
            if (copyExit >= 8)
            {
                progress?.Report("复制阶段失败，源文件夹未受影响，请检查错误后再试。");
                return (false, $"文件复制失败 (robocopy 退出码: {copyExit})。源文件夹保持原样，未做删除。");
            }
            progress?.Report(copyExit == 0
                ? "源文件夹没有需要搬运的文件，继续重定向..."
                : "文件复制完成，正在移除源文件夹中的已搬运文件...");

            // 阶段 2：移除源。robocopy 对已与目标相同的文件只删除源、不重复复制，因此不会重复拷贝数据。
            int moveExit = RunRobocopy(oldPath, newPath,
                $"/E /MOVE {copyFlags} /XJ /R:1 /W:1 /MT:8 /NFL /NDL /NJH /NJS", progress);
            if (moveExit >= 8)
                progress?.Report($"部分源文件未能移除（退出码 {moveExit}），将继续扫描确认残留。");

            // —— 迁移自验证：扫描源目录残留，明确告知搬迁是否完整 ——
            string residual = VerifySourceCleared(oldPath);
            if (residual != null)
                progress?.Report($"⚠ {residual}。通常是文件被占用，不影响新位置使用；重启后可在旧位置手动删除。");
            else
                progress?.Report("✓ 源文件夹已清空，搬迁完整。");

            // —— 重定向（写注册表 + 刷新环境变量） ——
            if (!NativeMethods.SetKnownFolderPath(folderId, newPath))
            {
                // 重定向失败：尝试把目标内容搬回源，避免“文件在别处但系统找不到”
                progress?.Report("重定向失败，正在尝试把文件搬回原位置...");
                int rollback = RunRobocopy(newPath, oldPath,
                    "/E /MOVE /XJ /R:1 /W:1 /MT:8 /NFL /NDL /NJH /NJS", progress);
                return (false, rollback < 8
                    ? "重定向失败，已自动把文件搬回原位置，请检查后重试。"
                    : "重定向失败，且搬回原位置未完全成功，请手动检查目标目录与源目录。");
            }

            progress?.Report($"✓ {folderName} 已成功搬迁到 {newPath}");
            return (true, $"{folderName} 搬迁完成。");
        }

        // 执行一次 robocopy 并返回退出码（<8 视为成功，>=8 表示有失败；0=无变化，1=已复制）。
        // 用 /XJ 排除 junction/符号链接，避免跟随链接复制到链接目标或死循环。
        private static int RunRobocopy(string source, string dest, string options, IProgress<string> progress)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "robocopy",
                Arguments = $"\"{source}\" \"{dest}\" {options}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return 16;
                proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                progress?.Report($"robocopy 执行失败: {ex.Message}");
                return 16;
            }
        }

        // 迁移自验证：统计源目录（迁移后）残留的文件数与大小，返回提示文本；无残留返回 null。
        private static string VerifySourceCleared(string source)
        {
            if (!Directory.Exists(source)) return null;   // 源目录已被整体移除，视为无残留
            long count = 0; long bytes = 0;
            var sample = new List<string>();
            try
            {
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var e in new DirectoryInfo(source).EnumerateFileSystemInfos("*", opts))
                {
                    if (e is FileInfo fi)
                    {
                        count++;
                        bytes += fi.Length;
                        if (sample.Count < 3) sample.Add(e.Name);
                    }
                }
            }
            catch { }
            if (count == 0) return null;
            var text = $"源文件夹未完全清空：残留 {count} 个文件（约 {DiskUtils.FormatSize(bytes)}）";
            if (sample.Count > 0) text += "，如 " + string.Join("、", sample);
            return text;
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

            // 复用 MoveFolder：同样具备路径校验、两阶段迁移、回滚能力
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
