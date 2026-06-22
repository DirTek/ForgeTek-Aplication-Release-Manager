using System.Globalization;
using System.Windows.Data;

namespace ForgeTekApplicationReleaseManager.Converters;

public class ProgressBarWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double value || values[1] is not double actualWidth)
            return 0.0;
        return (value / 100.0) * actualWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
