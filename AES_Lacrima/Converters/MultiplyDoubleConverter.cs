using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AES_Lacrima.Converters;

public class MultiplyDoubleConverter : IMultiValueConverter
{
    public static readonly MultiplyDoubleConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Count == 0)
            return 1.0;

        double product = 1.0;
        foreach (var value in values)
        {
            product *= value switch
            {
                double d => d,
                float f => f,
                int i => i,
                bool b => b ? 1.0 : 0.0,
                null => 0.0,
                _ => 1.0
            };
        }

        return Math.Clamp(product, 0.0, 1.0);
    }
}
