using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DiskCleaner.Services
{
    
    
    
    
    
    public static class ThemeService
    {
        private const string ThemeFileName = "theme.txt";
        private static readonly string ThemeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleaner");

        
        private static readonly Dictionary<string, string> Dark = new()
        {
            ["bg-primary"] = "#1E1E2E",
            ["bg-secondary"] = "#313244",
            ["bg-tertiary"] = "#45475A",
            ["bg-sidebar"] = "#181825",
            ["bg-deep"] = "#11111B",
            ["bg-item"] = "#262637",
            ["bg-item-hover"] = "#2A2B3D",
            ["text-primary"] = "#CDD6F4",
            ["text-secondary"] = "#A6ADC8",
            ["text-muted"] = "#6C7086",
            ["border-color"] = "#585B70",
            ["accent-blue"] = "#89B4FA",
            ["accent-green"] = "#A6E3A1",
            ["accent-yellow"] = "#F9E2AF",
            ["accent-red"] = "#F38BA8",
            ["accent-peach"] = "#FAB387",
            ["accent-mauve"] = "#CBA6F7",
            ["accent-teal"] = "#94E2D5",
            ["text-on-accent"] = "#1E1E2E",
        };

        
        // 浅色模式：整体再压暗一档（用户反馈仍太亮），用「冷灰」而非柔白；
        // 文本色保持深黑灰，确保在更深的背景上对比度更高、不刺眼。
        private static readonly Dictionary<string, string> Light = new()
        {
            ["bg-primary"] = "#CDD2DB",
            ["bg-secondary"] = "#D8DEE7",
            ["bg-tertiary"] = "#C5CBD6",
            ["bg-sidebar"] = "#C8CED8",
            ["bg-deep"] = "#D8DEE7",
            ["bg-item"] = "#D2D8E2",
            ["bg-item-hover"] = "#C3CAD5",
            ["text-primary"] = "#1F2430",
            ["text-secondary"] = "#363E4C",
            ["text-muted"] = "#4A5160",
            ["border-color"] = "#BFC6D0",
            ["accent-blue"] = "#1E66F5",
            ["accent-green"] = "#2E7D32",
            ["accent-yellow"] = "#B86E00",
            ["accent-red"] = "#C20F33",
            ["accent-peach"] = "#E2550A",
            ["accent-mauve"] = "#7B2FE0",
            ["accent-teal"] = "#11807A",
            ["text-on-accent"] = "#FFFFFF",
        };

        private static bool _isLight;

        public static bool IsLight => _isLight;

        /// <summary>
        /// 获取当前主题下指定键的十六进制颜色值（深/浅色自动切换）。
        /// </summary>
        public static string GetColor(string key)
        {
            var map = _isLight ? Light : Dark;
            return map.TryGetValue(key, out var value) ? value : null;
        }

        static ThemeService()
        {
            try
            {
                var file = Path.Combine(ThemeDir, ThemeFileName);
                if (File.Exists(file))
                    _isLight = string.Equals(File.ReadAllText(file).Trim(), "light", StringComparison.OrdinalIgnoreCase);
            }
            catch {   }
        }

        
        
        
        
        public static void ApplyTo(ResourceDictionary dict)
        {
            if (dict == null) return;
            var map = _isLight ? Light : Dark;
            foreach (var kv in map)
            {
                var newColor = (Color)ColorConverter.ConvertFromString(kv.Value);

                if (dict.Contains(kv.Key))
                {
                    if (dict[kv.Key] is not SolidColorBrush brush) continue;

                    if (brush.IsFrozen)
                    {
                        var newBrush = brush.Clone();
                        newBrush.Color = newColor;
                        dict[kv.Key] = newBrush;
                    }
                    else
                    {
                        brush.Color = newColor;
                    }
                }
                else
                {
                    // 窗口未在自身 Resources 声明该画刷时，直接注入主题画刷，
                    // 保证深/浅色对所有 DynamicResource 引用全覆盖（避免回退到默认色导致浅色下看不清）。
                    dict[kv.Key] = new SolidColorBrush(newColor);
                }
            }
        }

        
        
        
        public static void Toggle()
        {
            _isLight = !_isLight;
            try
            {
                Directory.CreateDirectory(ThemeDir);
                File.WriteAllText(Path.Combine(ThemeDir, ThemeFileName), _isLight ? "light" : "dark");
            }
            catch {   }
        }
    }
}
