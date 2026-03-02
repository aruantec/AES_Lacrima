using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using System.Globalization;

namespace AES_Lacrima.Converters
{
    /// <summary>
    /// Converts a control width (double) into a <see cref="bool"/> value suitable
    /// for binding to <see cref="Avalonia.Controls.Control.IsVisible"/>. The
    /// converter parameter is treated as a numeric threshold; when the width is
    /// strictly less than the threshold the result is <c>false</c> (collapsed),
    /// otherwise <c>true</c> (visible).
    /// </summary>
    public class WidthToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double width)
            {
                double threshold = 0;
                if (parameter != null && double.TryParse(parameter.ToString(), out var t))
                {
                    threshold = t;
                }

                return width >= threshold;
            }

            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
