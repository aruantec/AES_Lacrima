using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace AES_Lacrima.Converters;

/// <summary>
/// Converts a boolean value to an arbitrary object value when true.
/// If the input value is <c>true</c> the converter returns the
/// provided <paramref name="parameter"/>; otherwise it returns <c>null</c>.
/// This is useful in XAML when a resource or value should be applied only
/// when a boolean condition is met.
/// </summary>
public class BoolToValueConverter : IValueConverter
{
    /// <summary>
    /// A shared instance of the converter for XAML usage.
    /// </summary>
    public static readonly BoolToValueConverter Instance = new();

    /// <summary>
    /// Converts a boolean to the provided parameter when <c>true</c>.
    /// </summary>
    /// <param name="value">The value produced by the binding source.</param>
    /// <param name="targetType">The type of the binding target property.</param>
    /// <param name="parameter">An optional parameter to return when <paramref name="value"/> is <c>true</c>.</param>
    /// <param name="culture">The culture to use in the converter.</param>
    /// <returns>The <paramref name="parameter"/> if <paramref name="value"/> is <c>true</c>; otherwise <c>null</c>.</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b) return parameter;
        return null;
    }

    /// <summary>
    /// ConvertBack is not implemented for this one-way converter and will throw.
    /// </summary>
    /// <exception cref="NotImplementedException">Always thrown.</exception>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
