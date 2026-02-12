using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace AES_Lacrima.Converters;

/// <summary>
/// A value converter that converts a string to a boolean value.
/// Returns true if the string is not null or empty; otherwise, false.
/// </summary>
public class StringNotEmptyToBoolConverter : IValueConverter
{
    /// <summary>
    /// Converts a string value to a boolean indicating if it's not empty.
    /// </summary>
    /// <param name="value">The string value to convert.</param>
    /// <param name="targetType">The type of the binding target property.</param>
    /// <param name="parameter">An optional parameter to use in the converter logic (unused).</param>
    /// <param name="culture">The culture to use in the converter (unused).</param>
    /// <returns>True if the string is not null or empty; otherwise, false.</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !string.IsNullOrEmpty(value as string);
    }

    /// <summary>
    /// Conversion back is not implemented.
    /// </summary>
    /// <exception cref="NotImplementedException">Always thrown.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}