using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace AES_Lacrima.Converters
{
    /// <summary>
    /// Converts a boolean to a SolidColorBrush. ConverterParameter can specify
    /// the colors for true and false as "trueColor,falseColor" (e.g. "#F0F0F0,DimGray").
    /// </summary>
    public class BoolToBrushConverter : IValueConverter
    {
        public static readonly BoolToBrushConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool b = value is bool vb && vb;

            string? param = parameter as string;
            string trueColor = "#F0F0F0";
            string falseColor = "DimGray";
            if (!string.IsNullOrEmpty(param))
            {
                var parts = param.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) trueColor = parts[0].Trim();
                if (parts.Length > 1) falseColor = parts[1].Trim();
            }

            var colorStr = b ? trueColor : falseColor;
            try
            {
                var col = Color.Parse(colorStr);
                return new SolidColorBrush(col);
            }
            catch
            {
                return Brushes.Transparent;
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
