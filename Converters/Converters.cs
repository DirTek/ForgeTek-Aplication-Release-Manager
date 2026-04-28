using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ForgeTekUpdatePackager.Converters;

/// <summary>Converts an int count of 0 to Visible, anything else to Collapsed (for "empty" labels).</summary>
public class ZeroToVisibleConverter : IValueConverter
{
    public static readonly ZeroToVisibleConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts bool IsDebug=true to a subtle purple row background.</summary>
public class DebugRowBackgroundConverter : IValueConverter
{
    public static readonly DebugRowBackgroundConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(40, 191, 90, 242))
            : System.Windows.Media.Brushes.Transparent;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
