using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AES_Lacrima.Converters
{
    /// <summary>
    /// Converts a boolean value to an opacity (double) value and back.
    /// </summary>
    /// <remarks>
    /// This converter returns 1.0 when the input boolean is <c>true</c>, and 0.0 when <c>false</c>.
    /// When converting back it treats any double > 0.5 as <c>true</c>.
    /// </remarks>
    public class BoolToOpacityConverter : IValueConverter
    {
        /// <summary>
        /// Shared instance of the converter for XAML usage.
        /// Use the static instance to avoid allocating multiple converters.
        /// </summary>
        public static readonly BoolToOpacityConverter Instance = new();

        /// <summary>
        /// Converts a boolean value to an opacity <see cref="double"/> value.
        /// </summary>
        /// <param name="value">The value produced by the binding source. Expected to be a <see cref="bool"/>.</param>
        /// <param name="targetType">The type of the binding target property (ignored).</param>
        /// <param name="parameter">An optional parameter (ignored).</param>
        /// <param name="culture">The culture to use in the converter (ignored).</param>
        /// <returns>Returns <c>1.0</c> when <paramref name="value"/> is <c>true</c>; otherwise <c>0.0</c>.</returns>
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return 1.0;
            return 0.0;
        }

        /// <summary>
        /// Converts an opacity value back to a boolean.
        /// </summary>
        /// <param name="value">The value produced by the binding target. Expected to be a <see cref="double"/> or <see cref="bool"/>.</param>
        /// <param name="targetType">The type to convert to (ignored).
        /// </param>
        /// <param name="parameter">An optional parameter (ignored).</param>
        /// <param name="culture">The culture to use in the converter (ignored).</param>
        /// <returns>Returns <c>true</c> when the numeric opacity is greater than 0.5, otherwise <c>false</c>.</returns>
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double d)
                return d > 0.5;
            if (value is bool b)
                return b;
            return false;
        }
    }
}