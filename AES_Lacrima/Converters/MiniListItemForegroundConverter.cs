using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AES_Lacrima.Converters;

/// <summary>
/// Chooses list-item text color for mini mode: loaded-row foreground, then selected-row foreground, else white.
/// </summary>
public sealed class MiniListItemForegroundConverter : IMultiValueConverter
{
    public static readonly MiniListItemForegroundConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Count < 5)
            return Brushes.White;

        var item = values[0];
        var loadedItem = values[1];
        var loadedForeground = values[2] as IBrush ?? Brushes.White;
        var isSelected = values[3] is true;
        var selectionForeground = values[4] as IBrush ?? Brushes.White;

        if (item != null && loadedItem != null && item.Equals(loadedItem))
            return loadedForeground;

        if (isSelected)
            return selectionForeground;

        if (values.Count >= 6 && values[5] is IBrush idleForeground)
            return idleForeground;

        return Brushes.White;
    }
}
