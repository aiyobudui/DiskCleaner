using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskCleaner.Services;

namespace DiskCleaner.Converters
{
    /// <summary>
    /// 百分比转 StrokeDashArray，用于圆形仪表盘。
    /// Grid 100×100，Ellipse 100×100，StrokeThickness=7，描边中心线半径 = 50 - 7/2 = 46.5，
    /// 中心线周长 ≈ 292.07 像素。
    /// 注意：WPF StrokeDashArray 的数值单位是“线宽倍数”（1 = StrokeThickness），
    /// 所以最终结果要除以 7，否则低百分比会被放大 7 倍、看起来接近全满。
    /// </summary>
    public class PercentToDashConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double percent = value == null ? 0 : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            percent = Math.Max(0, Math.Min(100, percent));
            double strokeThickness = 7;
            double radius = 50 - strokeThickness / 2.0; // 描边中心线半径
            double circumference = 2 * Math.PI * radius;
            double filledPixels = percent / 100.0 * circumference;
            double gapPixels = Math.Max(0, circumference - filledPixels);
            // WPF dash 数值 = 像素长度 / 线宽
            return new DoubleCollection { filledPixels / strokeThickness, gapPixels / strokeThickness };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");
    }

    /// <summary>
    /// 将进度值转换为像素宽度，用于自定义 ProgressBar 的确定态填充。
    /// values[0]=Value, values[1]=Maximum, values[2]=容器 ActualWidth。
    /// 返回 Value/Maximum 比例对应的实际像素宽度（0~ActualWidth），容器尚未布局（ActualWidth=0）时返回 0。
    /// 相对“Star 列宽”方案，像素级更准确，且不受无限 Grid 缩放影响。
    /// </summary>
    public class ProgressToWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double value = values.Length > 0 ? ToDouble(values[0]) : 0;
            double maximum = values.Length > 1 ? ToDouble(values[1]) : 100;
            double actualWidth = values.Length > 2 ? ToDouble(values[2]) : 0;
            if (maximum <= 0 || actualWidth <= 0) return 0.0;
            double ratio = Math.Max(0, Math.Min(1, value / maximum));
            return Math.Max(0, ratio * actualWidth);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");

        private static double ToDouble(object v)
        {
            if (v == null) return 0;
            try { return System.Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }

    /// <summary>
    /// 非空字符串返回 Visible，空或纯空白返回 Collapsed。
    /// </summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool inverted = string.Equals(parameter?.ToString(), "inverted", StringComparison.OrdinalIgnoreCase);
            bool hasText = value is string s && !string.IsNullOrWhiteSpace(s);
            bool visible = inverted ? !hasText : hasText;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");
    }

    /// <summary>
    /// int → Visibility：大于 0 时 Visible，否则 Collapsed。
    /// 用于「已删除 N 个文件」这类计数，仅在确有删除时显示。
    /// </summary>
    public class IntToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int n = value is int i ? i : 0;
            return n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");
    }

    /// <summary>
    /// bool → Visibility。默认 true→Visible / false→Collapsed；
    /// 传入参数 "inverted" 时反转（true→Collapsed / false→Visible）。
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool inverted = string.Equals(parameter?.ToString(), "inverted", StringComparison.OrdinalIgnoreCase);
            bool b = value is bool vb && vb;
            bool visible = inverted ? !b : b;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");
    }

    /// <summary>
    /// ScanStatus → Visibility：
    /// 默认仅在 Cleaning / Cleaned / Error 时显示（清理反馈状态）。
    /// ConverterParameter="error" 时仅在 Error 时显示；
    /// ConverterParameter="noterror" 时在非 Error 时显示。
    /// </summary>
    public class ScanStatusToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DiskCleaner.Models.ScanStatus status)
            {
                string mode = parameter?.ToString()?.ToLowerInvariant() ?? string.Empty;
                if (mode == "error")
                    return status == DiskCleaner.Models.ScanStatus.Error ? Visibility.Visible : Visibility.Collapsed;
                if (mode == "noterror")
                    return status != DiskCleaner.Models.ScanStatus.Error ? Visibility.Visible : Visibility.Collapsed;

                bool visible = status == DiskCleaner.Models.ScanStatus.Cleaning
                            || status == DiskCleaner.Models.ScanStatus.Cleaned
                            || status == DiskCleaner.Models.ScanStatus.Error;
                return visible ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");
    }

    /// <summary>
    /// 管理员锁定徽标可见性：RequiresAdmin 且未提权（!IsElevated）时 Visible，否则 Collapsed。
    /// values[0]=RequiresAdmin(bool)，values[1]=IsElevated(bool)。
    /// 用于极限清理中 DISM/SFC 等需管理员工具未提权时显示“🔒 需管理员”提示。
    /// </summary>
    public class AdminLockVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool requiresAdmin = values.Length > 0 && values[0] is bool ra && ra;
            bool isElevated = values.Length > 1 && values[1] is bool ie && ie;
            return (requiresAdmin && !isElevated) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");
    }

    /// <summary>
    /// 管理员锁定按钮可用性：RequiresAdmin 且未提权（!IsElevated）时禁用，否则启用。
    /// values[0]=RequiresAdmin(bool)，values[1]=IsElevated(bool)。
    /// 用于极限清理中需管理员工具未提权时禁用“执行”按钮。
    /// </summary>
    public class AdminLockEnabledConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool requiresAdmin = values.Length > 0 && values[0] is bool ra && ra;
            bool isElevated = values.Length > 1 && values[1] is bool ie && ie;
            return !requiresAdmin || isElevated;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");
    }

}
