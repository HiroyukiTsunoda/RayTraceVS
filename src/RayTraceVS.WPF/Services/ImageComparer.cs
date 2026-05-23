using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RayTraceVS.WPF.Services
{
    /// <summary>
    /// 2つの画像のピクセル差分を比較する（リファクタリング前後の出力同一性検証用）。
    ///
    /// 使い方:
    ///   RayTraceVS.WPF.exe --compare &lt;reference.png&gt; &lt;target.png&gt; [--threshold N]
    ///
    /// 終了コード: 0=一致(完全 or 微小差), 1=差分あり, 2=サイズ不一致/引数不正, 3=例外
    /// </summary>
    public static class ImageComparer
    {
        /// <summary>
        /// 引数が --compare を含むなら比較を実行して true を返す。含まなければ false（=他モード）。
        /// </summary>
        public static bool TryParseAndRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            string? refPath = null;
            string? targetPath = null;
            int threshold = 0;
            bool isCompare = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--compare":
                        isCompare = true;
                        refPath = (i + 1 < args.Length) ? args[++i] : null;
                        targetPath = (i + 1 < args.Length) ? args[++i] : null;
                        break;
                    case "--threshold":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var t)) threshold = t;
                        break;
                }
            }

            if (!isCompare)
                return false;

            if (string.IsNullOrEmpty(refPath) || string.IsNullOrEmpty(targetPath))
            {
                Console.WriteLine("[Compare] 使い方: --compare <reference.png> <target.png> [--threshold N]");
                exitCode = 2;
                return true;
            }

            exitCode = Compare(refPath, targetPath, threshold);
            return true;
        }

        /// <summary>
        /// 2画像を比較し統計を出力する。戻り値は終了コード。
        /// </summary>
        public static int Compare(string refPath, string targetPath, int threshold)
        {
            try
            {
                if (!File.Exists(refPath)) { Console.WriteLine($"[Compare] 参照画像が見つかりません: {refPath}"); return 2; }
                if (!File.Exists(targetPath)) { Console.WriteLine($"[Compare] 対象画像が見つかりません: {targetPath}"); return 2; }

                var (a, wa, ha) = LoadBgra(refPath);
                var (b, wb, hb) = LoadBgra(targetPath);

                if (wa != wb || ha != hb)
                {
                    Console.WriteLine($"[Compare] サイズ不一致: {wa}x{ha} vs {wb}x{hb}");
                    return 2;
                }

                long sumDiff = 0;
                int maxDiff = 0;
                int diffPixels = 0;
                int pixelCount = wa * ha;

                for (int idx = 0; idx < pixelCount; idx++)
                {
                    int o = idx * 4;
                    int pixelMax = 0;
                    for (int c = 0; c < 3; c++) // B,G,R（アルファは除外）
                    {
                        int d = Math.Abs(a[o + c] - b[o + c]);
                        sumDiff += d;
                        if (d > pixelMax) pixelMax = d;
                    }
                    if (pixelMax > maxDiff) maxDiff = pixelMax;
                    if (pixelMax > threshold) diffPixels++;
                }

                double meanDiff = (double)sumDiff / ((long)pixelCount * 3);
                double diffPct = 100.0 * diffPixels / pixelCount;

                Console.WriteLine($"[Compare] 解像度  : {wa}x{ha} ({pixelCount} px)");
                Console.WriteLine($"[Compare] 最大差分: {maxDiff} / 255");
                Console.WriteLine($"[Compare] 平均差分: {meanDiff:F4} / 255");
                Console.WriteLine($"[Compare] 差分px  : {diffPixels} ({diffPct:F4}%) [閾値>{threshold}]");

                if (maxDiff == 0)
                {
                    Console.WriteLine("[Compare] => 完全一致（ピクセル単位で同一）");
                    return 0;
                }
                if (maxDiff <= 4 && diffPct < 0.1)
                {
                    Console.WriteLine("[Compare] => 視覚的に同等（微小差のみ）");
                    return 0;
                }
                Console.WriteLine("[Compare] => 差分あり（要確認）");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Compare] 例外: {ex.Message}");
                return 3;
            }
        }

        /// <summary>
        /// PNG等を読み込み BGRA32 のバイト列として返す。
        /// </summary>
        private static (byte[] pixels, int width, int height) LoadBgra(string path)
        {
            var decoder = BitmapDecoder.Create(
                new Uri(Path.GetFullPath(path)),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapSource src = decoder.Frames[0];
            if (src.Format != PixelFormats.Bgra32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            int w = src.PixelWidth;
            int h = src.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[h * stride];
            src.CopyPixels(pixels, stride, 0);
            return (pixels, w, h);
        }
    }
}
