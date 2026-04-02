using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AES_Lacrima.Converters;

public class GameplayPreviewWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3)
            return 0d;

        var baseWidth = ToDouble(values[0]);
        var baseHeight = ToDouble(values[1]);
        var aspectRatio = ToDouble(values[2]);

        if (baseWidth <= 0)
            return 0d;

        if (baseHeight <= 0 || aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
            return baseWidth;

        var expandedWidth = Math.Round(baseHeight * aspectRatio, 2);
        return Math.Max(baseWidth, expandedWidth);
    }

    private static double ToDouble(object? value)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => 0d
        };
    }
}
