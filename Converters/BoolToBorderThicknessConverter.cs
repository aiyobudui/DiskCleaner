using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiskCleaner.Converters
{
    /// <summary>
    /// bool → BorderThickness：true 为底部 1px 分隔线，false 无边框。
    /// 用于「仅当分组展开时显示标题下划线」。
    /// </summary>
    public class BoolToBorderThicknessConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return new Thickness(0, 0, 0, 1);
            return new Thickness(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
