using System;
using System.Globalization;
using System.Windows.Data;

namespace RayTraceVS.WPF.Converters
{
    public class IntToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int paramInt))
            {
                return intValue == paramInt;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue && parameter is string paramStr && int.TryParse(paramStr, out int paramInt))
            {
                return paramInt;
            }
            return Binding.DoNothing;
        }
    }
}
