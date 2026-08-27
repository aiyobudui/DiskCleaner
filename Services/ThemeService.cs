using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 主题服务：在深色 / 浅色两套画刷颜色之间切换。
    /// 切换时通过修改 Window.Resources 中对应 SolidColorBrush 的 Color 实现，
    /// 所有以 DynamicResource 引用这些画刷的控件会自动刷新。
    /// </summary>
    public static class ThemeService
    {
        private const string ThemeFileName = "theme.txt";
        private static readonly string ThemeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleaner");

        // 深色模式（与 MainWindow.xaml 中 Window.Resources 默认定义一致）
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
            ["text-on-accent"] = "#FFFFFF",
        };

        // 浅色模式：浅灰 / 白底、深色文字、高对比强调色（避免 pastel 色在浅底上看不清）
        private static readonly Dictionary<string, string> Light = new()
        {
            ["bg-primary"] = "#EFF1F5",
            ["bg-secondary"] = "#FFFFFF",
            ["bg-tertiary"] = "#E6E9EF",
            ["bg-sidebar"] = "#E6E9EF",
            ["bg-deep"] = "#F5F7FA",
            ["bg-item"] = "#FAFBFC",
            ["bg-item-hover"] = "#E9EDF4",
            ["text-primary"] = "#2E3440",
            ["text-secondary"] = "#4C566A",
            ["text-muted"] = "#5C6370",
            ["border-color"] = "#C7CDD8",
            ["accent-blue"] = "#1E66F5",
            ["accent-green"] = "#40A02B",
            ["accent-yellow"] = "#DF8E1D",
            ["accent-red"] = "#D20F39",
            ["accent-peach"] = "#FE640B",
            ["accent-mauve"] = "#8839EF",
            ["accent-teal"] = "#179299",
            ["text-on-accent"] = "#1E1E2E",
        };

        private static bool _isLight;

        public static bool IsLight => _isLight;

        static ThemeService()
        {
            try
            {
                var file = Path.Combine(ThemeDir, ThemeFileName);
                if (File.Exists(file))
                    _isLight = string.Equals(File.ReadAllText(file).Trim(), "light", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* 读取失败则保持默认深色 */ }
        }

        /// <summary>
        /// 将当前主题的颜色应用到给定的资源字典（如 MainWindow.Resources）。
        /// 如果画刷已被冻结（只读），则克隆一份未冻结的画刷并替换字典中的值。
        /// </summary>
        public static void ApplyTo(ResourceDictionary dict)
        {
            if (dict == null) return;
            var map = _isLight ? Light : Dark;
            foreach (var kv in map)
            {
                if (!dict.Contains(kv.Key)) continue;
                if (dict[kv.Key] is not SolidColorBrush brush) continue;

                var newColor = (Color)ColorConverter.ConvertFromString(kv.Value);
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
        }

        /// <summary>
        /// 切换主题并持久化选择。
        /// </summary>
        public static void Toggle()
        {
            _isLight = !_isLight;
            try
            {
                Directory.CreateDirectory(ThemeDir);
                File.WriteAllText(Path.Combine(ThemeDir, ThemeFileName), _isLight ? "light" : "dark");
            }
            catch { /* 持久化失败不影响本次切换 */ }
        }
    }
}
