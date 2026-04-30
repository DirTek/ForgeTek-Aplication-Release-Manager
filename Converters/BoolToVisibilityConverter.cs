using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ForgeTekUpdatePackager.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "invert";
        if (value is bool b)
            return (b ^ invert) ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
