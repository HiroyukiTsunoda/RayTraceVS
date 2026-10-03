using System;
using System.Globalization;
using System.Windows.Data;

namespace RayTraceVS.WPF.Converters
{
    /// <summary>
    /// ソケット数からノードの高さを計算するコンバーター
    /// </summary>
    public class SocketCountToHeightConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return 60.0; // デフォルト高さ

            int inputCount = 0;
            int outputCount = 0;

            if (values[0] is System.Collections.ICollection inputSockets)
                inputCount = inputSockets.Count;

            if (values[1] is System.Collections.ICollection outputSockets)
                outputCount = outputSockets.Count;

            // ヘッダー30 + ソケット1つあたり20 + パディング10
            int maxSocketCount = Math.Max(inputCount, outputCount);
            double height = 30 + (maxSocketCount * 20) + 10;

            // 最小高さは60
            return Math.Max(height, 60.0);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
