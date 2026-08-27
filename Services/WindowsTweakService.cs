using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 用户账户控制（UAC）通知级别。
    /// </summary>
    public enum UacLevel
    {
        AlwaysNotify = 0,       // 始终通知（最安全）
        DefaultNotify = 1,      // 仅在应用尝试更改设置时通知（系统默认）
        DefaultNotifyNoDim = 2, // 仅在应用尝试更改时通知，但不暗化桌面
        NeverNotify = 3         // 从不通知（等效关闭 UAC）
    }

    /// <summary>
    /// 常见桌面图标的注册表 GUID。
    /// </summary>
    public static class DesktopIconGuids
    {
        public const string ThisPC = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        public const string RecycleBin = "{645FF040-5081-101B-9F08-00AA002F954E}";
        public const string Network = "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
        public const string ControlPanel = "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";
        public const string UserFolder = "{59031a47-3f72-44a7-89c5-5595fe6b30ee}";
    }

    public interface IWindowsTweakService
    {
        UacLevel GetUacLevel();
        void SetUacLevel(UacLevel level);
        bool GetSmartScreenEnabled();
        void SetSmartScreenEnabled(bool enabled);
        int GetTaskbarGlom();    // 0 始终合并 1 填满时合并 2 从不合并
        void SetTaskbarGlom(int level);
        int GetStartAlign();     // 0 左对齐 1 居中对齐
        void SetStartAlign(int align);
        bool GetDesktopIcon(string guid);
        void SetDesktopIcon(string guid, bool visible);
        bool GetShowFileExt();
        void SetShowFileExt(bool show);
        bool GetShowHidden();
        void SetShowHidden(bool show);
        bool GetShowSuperHidden();
        void SetShowSuperHidden(bool show);

        // ---------- 任务栏项 ----------

        // ---------- 记事本 ----------
        bool GetNotepadOpenInNewTab();
        bool SetNotepadOpenInNewTab(bool value);
        bool GetNotepadNewSession();
        bool SetNotepadNewSession(bool value);
        bool RestartNotepad();
    }

    /// <summary>
    /// 系统设置（Windows 个性化/安全策略）的注册表读写封装。
    /// UAC / SmartScreen 写 HKLM 需要管理员权限（本程序以管理员身份运行）；
    /// 所有操作均 try/catch，失败时由调用方提示。
    /// </summary>
    public class WindowsTweakService : IWindowsTweakService
    {
        private const string UacKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string SmartScreenKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
        private const string AdvKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string DesktopIconKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";
        private const string NotepadKey = @"HKEY_CURRENT_USER\Software\Microsoft\Notepad";

        // ---------- UAC ----------
        public UacLevel GetUacLevel()
        {
            try
            {
                var cpa = (int?)Registry.GetValue(UacKey, "ConsentPromptBehaviorAdmin", 2) ?? 2;
                var pod = (int?)Registry.GetValue(UacKey, "PromptOnSecureDesktop", 1) ?? 1;
                if (cpa == 0 && pod == 0) return UacLevel.NeverNotify;
                if (cpa == 5 && pod == 0) return UacLevel.DefaultNotifyNoDim;
                if (cpa == 5 && pod == 1) return UacLevel.DefaultNotify;
                return UacLevel.AlwaysNotify;
            }
            catch { return UacLevel.DefaultNotify; }
        }

        public void SetUacLevel(UacLevel level)
        {
            int cpa, pod;
            switch (level)
            {
                case UacLevel.NeverNotify: cpa = 0; pod = 0; break;
                case UacLevel.DefaultNotifyNoDim: cpa = 5; pod = 0; break;
                case UacLevel.DefaultNotify: cpa = 5; pod = 1; break;
                default: cpa = 2; pod = 1; break; // AlwaysNotify
            }
            Registry.SetValue(UacKey, "ConsentPromptBehaviorAdmin", cpa, RegistryValueKind.DWord);
            Registry.SetValue(UacKey, "PromptOnSecureDesktop", pod, RegistryValueKind.DWord);
        }

        // ---------- SmartScreen ----------
        public bool GetSmartScreenEnabled()
        {
            try
            {
                var v = (string)Registry.GetValue(SmartScreenKey, "SmartScreenEnabled", "RequireAdmin");
                return !string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        public void SetSmartScreenEnabled(bool enabled)
        {
            Registry.SetValue(SmartScreenKey, "SmartScreenEnabled",
                enabled ? "RequireAdmin" : "Off");
        }

        // ---------- 任务栏合并 ----------
        public int GetTaskbarGlom() => (int?)Registry.GetValue(AdvKey, "TaskbarGlomLevel", 0) ?? 0;

        public void SetTaskbarGlom(int level)
        {
            Registry.SetValue(AdvKey, "TaskbarGlomLevel", level, RegistryValueKind.DWord);
            RefreshShell();
        }

        // ---------- 开始菜单对齐 ----------
        public int GetStartAlign() => (int?)Registry.GetValue(AdvKey, "TaskbarAl", 1) ?? 1;

        public void SetStartAlign(int align)
        {
            Registry.SetValue(AdvKey, "TaskbarAl", align, RegistryValueKind.DWord);
            RefreshShell();
        }

        // ---------- 桌面图标 ----------
        public bool GetDesktopIcon(string guid)
        {
            try
            {
                var v = Registry.GetValue(DesktopIconKey + @"\NewStartPanel", guid, null);
                if (v == null) return true; // 未设置时默认显示
                return (int)v == 0;
            }
            catch { return true; }
        }

        public void SetDesktopIcon(string guid, bool visible)
        {
            var val = visible ? 0 : 1;
            Registry.SetValue(DesktopIconKey + @"\NewStartPanel", guid, val, RegistryValueKind.DWord);
            Registry.SetValue(DesktopIconKey + @"\ClassicStartMenu", guid, val, RegistryValueKind.DWord);
            RefreshShell();
        }

        // ---------- 文件资源管理器 ----------
        public bool GetShowFileExt() => ((int?)Registry.GetValue(AdvKey, "HideFileExt", 1) ?? 1) == 0;

        public void SetShowFileExt(bool show)
        {
            Registry.SetValue(AdvKey, "HideFileExt", show ? 0 : 1, RegistryValueKind.DWord);
            RefreshShell();
        }

        public bool GetShowHidden() => ((int?)Registry.GetValue(AdvKey, "Hidden", 2) ?? 2) == 1;

        public void SetShowHidden(bool show)
        {
            Registry.SetValue(AdvKey, "Hidden", show ? 1 : 2, RegistryValueKind.DWord);
            RefreshShell();
        }

        public bool GetShowSuperHidden() => ((int?)Registry.GetValue(AdvKey, "ShowSuperHidden", 0) ?? 0) == 1;

        public void SetShowSuperHidden(bool show)
        {
            Registry.SetValue(AdvKey, "ShowSuperHidden", show ? 1 : 0, RegistryValueKind.DWord);
            RefreshShell();
        }

        // ---------- 任务栏项 ----------

        // ---------- 记事本 ----------
        // AlwaysOpenInNewTab: 1=新选项卡打开, 0=新窗口打开
        public bool GetNotepadOpenInNewTab() => ((int?)Registry.GetValue(NotepadKey, "AlwaysOpenInNewTab", 1) ?? 1) == 1;

        public bool SetNotepadOpenInNewTab(bool value)
        {
            Registry.SetValue(NotepadKey, "AlwaysOpenInNewTab", value ? 1 : 0, RegistryValueKind.DWord);
            return RestartNotepad();
        }

        // RestorePreviousSession: 1=继续上一个会话, 0=启动新会话（放弃未保存更改）
        // GetNotepadNewSession 返回 true 表示"启动新会话"（即 RestorePreviousSession=0）
        public bool GetNotepadNewSession() => ((int?)Registry.GetValue(NotepadKey, "RestorePreviousSession", 1) ?? 1) == 0;

        public bool SetNotepadNewSession(bool value)
        {
            // value=true → 启动新会话 → RestorePreviousSession=0
            Registry.SetValue(NotepadKey, "RestorePreviousSession", value ? 0 : 1, RegistryValueKind.DWord);
            return RestartNotepad();
        }

        /// <summary>
        /// 出于数据安全考虑不再强制结束记事本进程；返回 false 表示设置将在下次打开记事本时生效。
        /// </summary>
        public bool RestartNotepad()
        {
            // 不再强制结束正在运行的记事本进程：那样会丢失未保存的文档。
            // 返回 false 表示“未重启”，调用方会提示下次打开记事本时生效。
            return false;
        }

        // ---------- 通知 Shell 刷新（让桌面/资源管理器设置立即生效） ----------
        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private static void RefreshShell()
        {
            // SHCNE_ASSOCCHANGED：刷新文件关联 / 图标缓存
            try { SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }

            // 广播 WM_SETTINGCHANGE，lParam 传 null 表示“通用设置已变更”，
            // 比只传 "Policy" 覆盖更广，能让 Explorer 重新读取任务栏/桌面等当前用户设置。
            try { SendMessageTimeout((IntPtr)0xFFFF /*HWND_BROADCAST*/, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, null, 0x0002 /*SMTO_ABORTIFHUNG*/, 1000, out _); } catch { }
        }
    }
}
