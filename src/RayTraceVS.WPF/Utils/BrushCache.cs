using System.Collections.Generic;
using System.Windows.Media;

namespace RayTraceVS.WPF.Utils
{
    /// <summary>
    /// SolidColorBrushの静的キャッシュ。
    /// 同じ色のBrushを繰り返し生成することを防ぎ、GC負荷を軽減する。
    /// </summary>
    public static class BrushCache
    {
        private static readonly Dictionary<Color, SolidColorBrush> _cache = new();
        private static readonly object _lock = new();

        /// <summary>
        /// 指定した色のBrushを取得する。キャッシュにあればそれを返し、なければ新規作成してキャッシュする。
        /// </summary>
        public static SolidColorBrush Get(Color color)
        {
            lock (_lock)
            {
                if (!_cache.TryGetValue(color, out var brush))
                {
                    brush = new SolidColorBrush(color);
                    brush.Freeze(); // Freezeでスレッドセーフ＆パフォーマンス向上
                    _cache[color] = brush;
                }
                return brush;
            }
        }

        /// <summary>
        /// RGB値からBrushを取得する。
        /// </summary>
        public static SolidColorBrush Get(byte r, byte g, byte b)
        {
            return Get(Color.FromRgb(r, g, b));
        }

        /// <summary>
        /// ARGB値からBrushを取得する。
        /// </summary>
        public static SolidColorBrush Get(byte a, byte r, byte g, byte b)
        {
            return Get(Color.FromArgb(a, r, g, b));
        }

        /// <summary>
        /// キャッシュをクリアする（通常は不要）。
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
            }
        }
    }
}
