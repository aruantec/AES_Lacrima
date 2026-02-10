using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace AES_Lacrima.Converters;

/// <summary>
/// Converts a boolean value to a string based on the provided parameter.
/// The parameter should be a comma-separated string: "TrueValue, FalseValue".
/// </summary>
public class BoolToStringConverter : IValueConverter
{
    /// <summary>
    /// Gets a static instance of the <see cref="BoolToStringConverter"/>.
    /// </summary>
    public static readonly BoolToStringConverter Instance = new();

    /// <summary>
    /// Converts a boolean to its corresponding string representation.
    /// </summary>
    /// <param name="value">The boolean value to convert.</param>
    /// <param name="targetType">The type of the binding target property.</param>
    /// <param name="parameter">The converter parameter (format: "TrueValue, FalseValue").</param>
    /// <param name="culture">The culture to use in the converter.</param>
    /// <returns>The string corresponding to the boolean state.</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool vb && vb;
        string? param = parameter as string;

        if (!string.IsNullOrEmpty(param))
        {
            var parts = param.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                return b ? parts[0].Trim() : parts[1].Trim();
            }
            return b ? parts[0].Trim() : string.Empty;
        }

        return b ? "True" : "False";
    }

    /// <summary>
    /// Not implemented.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
