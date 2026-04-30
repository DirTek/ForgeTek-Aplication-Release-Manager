using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ForgeTekUpdatePackager.Converters;

public class FolderBoldConverter : IValueConverter
{
    public static readonly FolderBoldConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isFolder && isFolder)
            return FontWeights.Bold;
        return FontWeights.Normal;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
