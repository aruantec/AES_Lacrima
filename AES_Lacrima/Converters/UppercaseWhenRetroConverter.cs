using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AES_Lacrima.Converters;

/// <summary>
/// Uppercases text when retro mini mode is active.
/// </summary>
public sealed class UppercaseWhenRetroConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Count < 2)
            return values?[0]?.ToString() ?? string.Empty;

        var text = values[0]?.ToString() ?? string.Empty;
        if (values[0] is int trackCount)
            text = $"{trackCount} Tracks";
        else if (values[0] is long trackCountLong)
            text = $"{trackCountLong} Tracks";

        var isRetro = values[1] is true;
        return isRetro ? text.ToUpperInvariant() : text;
    }
}
