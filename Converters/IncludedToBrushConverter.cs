using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ForgeTekUpdatePackager.Converters;

public class IncludedToBrushConverter : IValueConverter
{
    public static readonly IncludedToBrushConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool included && !included)
            return new SolidColorBrush(Color.FromRgb(152, 152, 157)); // #98989D (TextSecondaryBrush)
        return System.Windows.Application.Current.TryFindResource("TextBrush") as SolidColorBrush ?? Brushes.White;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
