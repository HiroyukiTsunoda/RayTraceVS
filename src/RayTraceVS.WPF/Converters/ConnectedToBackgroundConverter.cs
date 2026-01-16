using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RayTraceVS.WPF.Converters
{
    /// <summary>
    /// 接続状態に応じて背景色を変換するコンバーター
    /// 接続されている場合は暗い背景、そうでなければ通常の背景
    /// </summary>
    public class ConnectedToBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isConnected && isConnected)
            {
                // 接続されている場合は暗い背景
                return new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            }
            // 通常の背景
            return new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
