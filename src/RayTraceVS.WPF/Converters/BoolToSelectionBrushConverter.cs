using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RayTraceVS.WPF.Converters
{
    public class BoolToSelectionBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isSelected && isSelected)
            {
                return new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)); // AccentBrush
            }
            return new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)); // BorderBrush
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
