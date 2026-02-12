using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace AES_Lacrima.Converters;

/// <summary>
/// Converts a duration expressed in seconds (as a <see cref="double"/>)
/// to a formatted time string. Hours are included when the duration is one hour or more.
/// </summary>
public class TimeSpanConverter : IValueConverter
{
    /// <summary>
    /// Converts a value representing seconds to a time string.
    /// </summary>
    /// <param name="value">The value to convert. Expected to be a <see cref="double"/> representing seconds.</param>
    /// <param name="targetType">The target type (ignored).</param>
    /// <param name="parameter">An optional parameter (ignored).</param>
    /// <param name="culture">The culture to use (ignored).</param>
    /// <returns>
    /// A formatted time string. If the duration is one hour or more the format is "hh:mm:ss",
    /// otherwise the format is "mm:ss". Returns an empty string for non-numeric input.
    /// </returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double seconds)
        {
            // Convert seconds to TimeSpan
            var timeSpan = TimeSpan.FromSeconds(seconds);

            // Check if there are any hours
            if (timeSpan.TotalHours >= 1)
            {
                // Format as hh:mm:ss if 1 hour or more
                return timeSpan.ToString(@"hh\:mm\:ss");
            }
            else
            {
                // Format as mm:ss if less than an hour
                return timeSpan.ToString(@"mm\:ss");
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// ConvertBack is not implemented because the converter is one-way.
    /// </summary>
    /// <returns>Always returns <c>null</c>.</returns>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null; // We only care about converting forward, not back
    }
}