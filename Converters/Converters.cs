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

/// <summary>Converts a hex color string (e.g. #1C1C1E) to a SolidColorBrush for a preview swatch.</summary>
public class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try { return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s)); }
            catch { }
        }
        return System.Windows.Media.Brushes.Transparent;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Two-way converts between a hex color string (#RRGGBB) and a Media.Color for the color picker.</summary>
public class HexToColorConverter : IValueConverter
{
    public static readonly HexToColorConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s); }
            catch { }
        }
        return System.Windows.Media.Colors.Black;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is System.Windows.Media.Color c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "#000000";
}

/// <summary>Visible when the bound string equals one of the comma-separated values in the parameter.</summary>
public class StringMatchToVisibilityConverter : IValueConverter
{
    public static readonly StringMatchToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value as string ?? string.Empty;
        var options = (parameter as string ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return options.Any(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;
    }
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
