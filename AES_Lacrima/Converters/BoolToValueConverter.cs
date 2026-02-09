using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace AES_Lacrima.Converters;

public class BoolToValueConverter : IValueConverter
{
    public static readonly BoolToValueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b) return parameter;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}