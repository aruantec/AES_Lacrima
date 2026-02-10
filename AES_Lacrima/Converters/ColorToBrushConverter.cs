using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace AES_Lacrima.Converters;

/// <summary>
/// Converter that maps an <see cref="Avalonia.Media.Color"/> to a
/// <see cref="SolidColorBrush"/> and vice versa.
/// Useful for binding color values to controls that expect a <see cref="Brush"/>.
/// </summary>
public class ColorToBrushConverter : IValueConverter
{
    /// <summary>
    /// Converts a <see cref="Color"/> to a <see cref="SolidColorBrush"/>.
    /// If the input is not a <see cref="Color"/>, <c>null</c> is returned.
    /// </summary>
    /// <param name="value">The source value. Expected to be a <see cref="Color"/>.</param>
    /// <param name="targetType">The target binding type (ignored).</param>
    /// <param name="parameter">Optional parameter (ignored).</param>
    /// <param name="culture">The culture to use in the converter (ignored).</param>
    /// <returns>A new <see cref="SolidColorBrush"/> when <paramref name="value"/> is a <see cref="Color"/>, otherwise <c>null</c>.</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color color)
            return new SolidColorBrush(color);
        return null;
    }

    /// <summary>
    /// Converts a <see cref="SolidColorBrush"/> back to its <see cref="Color"/>.
    /// If the input is not a <see cref="SolidColorBrush"/>, <c>null</c> is returned.
    /// </summary>
    /// <param name="value">The source value. Expected to be a <see cref="SolidColorBrush"/>.</param>
    /// <param name="targetType">The target binding type (ignored).</param>
    /// <param name="parameter">Optional parameter (ignored).</param>
    /// <param name="culture">The culture to use in the converter (ignored).</param>
    /// <returns>The <see cref="Color"/> extracted from the brush when possible; otherwise <c>null</c>.</returns>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SolidColorBrush brush)
            return brush.Color;
        return null;
    }
}
