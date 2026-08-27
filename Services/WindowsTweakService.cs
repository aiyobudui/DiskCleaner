using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskCleaner.Services
{
    
    
    
    public enum UacLevel
    {
        AlwaysNotify = 0,       
        DefaultNotify = 1,      
        DefaultNotifyNoDim = 2, 
        NeverNotify = 3         
    }

    
    
    
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
        int GetTaskbarGlom();    
        void SetTaskbarGlom(int level);
        int GetStartAlign();     
        void SetStartAlign(int align);
        bool GetDesktopIcon(string guid);
        void SetDesktopIcon(string guid, bool visible);
        bool GetShowFileExt();
        void SetShowFileExt(bool show);
        bool GetShowHidden();
        void SetShowHidden(bool show);
        bool GetShowSuperHidden();
        void SetShowSuperHidden(bool show);

        

        
        bool GetNotepadOpenInNewTab();
        bool SetNotepadOpenInNewTab(bool value);
        bool GetNotepadNewSession();
        bool SetNotepadNewSession(bool value);
        bool RestartNotepad();
    }

    
    
    
    
    
    public class WindowsTweakService : IWindowsTweakService
    {
        private const string UacKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string SmartScreenKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
        private const string AdvKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string DesktopIconKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";
        private const string NotepadKey = @"HKEY_CURRENT_USER\Software\Microsoft\Notepad";

        
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
                default: cpa = 2; pod = 1; break; 
            }
            Registry.SetValue(UacKey, "ConsentPromptBehaviorAdmin", cpa, RegistryValueKind.DWord);
            Registry.SetValue(UacKey, "PromptOnSecureDesktop", pod, RegistryValueKind.DWord);
        }

        
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

        
        public int GetTaskbarGlom() => (int?)Registry.GetValue(AdvKey, "TaskbarGlomLevel", 0) ?? 0;

        public void SetTaskbarGlom(int level)
        {
            Registry.SetValue(AdvKey, "TaskbarGlomLevel", level, RegistryValueKind.DWord);
            RefreshShell();
        }

        
        public int GetStartAlign() => (int?)Registry.GetValue(AdvKey, "TaskbarAl", 1) ?? 1;

        public void SetStartAlign(int align)
        {
            Registry.SetValue(AdvKey, "TaskbarAl", align, RegistryValueKind.DWord);
            RefreshShell();
        }

        
        public bool GetDesktopIcon(string guid)
        {
            try
            {
                var v = Registry.GetValue(DesktopIconKey + @"\NewStartPanel", guid, null);
                if (v == null) return true; 
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

        

        
        
        public bool GetNotepadOpenInNewTab() => ((int?)Registry.GetValue(NotepadKey, "AlwaysOpenInNewTab", 1) ?? 1) == 1;

        public bool SetNotepadOpenInNewTab(bool value)
        {
            Registry.SetValue(NotepadKey, "AlwaysOpenInNewTab", value ? 1 : 0, RegistryValueKind.DWord);
            return RestartNotepad();
        }

        
        
        public bool GetNotepadNewSession() => ((int?)Registry.GetValue(NotepadKey, "RestorePreviousSession", 1) ?? 1) == 0;

        public bool SetNotepadNewSession(bool value)
        {
            
            Registry.SetValue(NotepadKey, "RestorePreviousSession", value ? 0 : 1, RegistryValueKind.DWord);
            return RestartNotepad();
        }

        
        
        
        public bool RestartNotepad()
        {
            try
            {
                // 关闭所有正在运行的记事本进程，使注册表改动在下次启动时生效
                foreach (var p in Process.GetProcessesByName("notepad"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }
                // 重新启动记事本（Windows 11 上 notepad.exe 即新版多标签页记事本）
                Process.Start("notepad.exe");
                return true;
            }
            catch
            {
                return false;
            }
        }

        
        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private static void RefreshShell()
        {
            
            try { SHChangeNotify(0x08000000  , 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }

            
            
            try { SendMessageTimeout((IntPtr)0xFFFF  , 0x001A  , IntPtr.Zero, null, 0x0002  , 1000, out _); } catch { }
        }
    }
}
