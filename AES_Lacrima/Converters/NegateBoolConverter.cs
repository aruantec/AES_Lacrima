using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AES_Lacrima.Converters;

public class NegateBoolConverter : IValueConverter
{
    public static readonly NegateBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : value;
    }
}
