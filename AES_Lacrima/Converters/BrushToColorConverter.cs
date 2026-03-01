using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace AES_Lacrima.Converters
{
    public class BrushToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SolidColorBrush solidColorBrush)
            {
                return solidColorBrush.Color;
            }
            return Avalonia.Media.Colors.Transparent; // Or a default color
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Color color)
            {
                return new SolidColorBrush(color);
            }
            return null;
        }
    }
}
