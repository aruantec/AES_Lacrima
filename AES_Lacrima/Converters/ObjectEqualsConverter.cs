using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AES_Lacrima.Converters
{
    public class ObjectEqualsConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values == null || values.Count < 2) return false;
            
            // Basic null check for both
            if (values[0] == null && values[1] == null) return true;
            if (values[0] == null || values[1] == null) return false;

            return values[0]!.Equals(values[1]);
        }
    }
}
