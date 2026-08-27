using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskCleaner.Services;

namespace DiskCleaner.Converters
{
    
    
    
    
    
    
    
    public class PercentToDashConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double percent = value == null ? 0 : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            percent = Math.Max(0, Math.Min(100, percent));
            double strokeThickness = 7;
            double radius = 50 - strokeThickness / 2.0; 
            double circumference = 2 * Math.PI * radius;
            double filledPixels = percent / 100.0 * circumference;
            double gapPixels = Math.Max(0, circumference - filledPixels);
            
            return new DoubleCollection { filledPixels / strokeThickness, gapPixels / strokeThickness };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException("该转换器仅支持单向（OneWay）绑定，不支持反向转换。");
    }

    
    
    
    
    
    
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
