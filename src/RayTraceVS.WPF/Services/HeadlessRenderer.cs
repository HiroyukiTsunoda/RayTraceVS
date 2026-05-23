using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RayTraceVS.WPF.ViewModels;

namespace RayTraceVS.WPF.Services
{
    /// <summary>
    /// GUIを表示せずにシーンファイルをレンダリングしてPNGに保存するヘッドレスレンダラー。
    /// リファクタリング前後で出力（レンダリング結果）が変わっていないことを検証するための土台。
    ///
    /// 使い方:
    ///   RayTraceVS.WPF.exe --render &lt;scene.rtvs&gt; --output &lt;out.png&gt; [--width W] [--height H] [--passes N]
    /// </summary>
    public sealed class HeadlessRenderer
    {
        public const int DefaultPasses = 16;

        public string ScenePath { get; init; } = string.Empty;
        public string OutputPath { get; init; } = string.Empty;
        public int? Width { get; init; }
        public int? Height { get; init; }
        public int Passes { get; init; } = DefaultPasses;

        /// <summary>
        /// コマンドライン引数を解析する。--render が無ければ null（=通常GUI起動）を返す。
        /// </summary>
        public static HeadlessRenderer? ParseArgs(string[] args)
        {
            string? scene = null;
            string? output = null;
            int? width = null;
            int? height = null;
            int passes = DefaultPasses;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--render":
                        scene = NextArg(args, ref i);
                        break;
                    case "--output":
                        output = NextArg(args, ref i);
                        break;
                    case "--width":
                        if (int.TryParse(NextArg(args, ref i), out var w)) width = w;
                        break;
                    case "--height":
                        if (int.TryParse(NextArg(args, ref i), out var h)) height = h;
                        break;
                    case "--passes":
                        if (int.TryParse(NextArg(args, ref i), out var p)) passes = Math.Max(1, p);
                        break;
                }
            }

            if (string.IsNullOrEmpty(scene))
                return null;

            // 出力先が未指定ならシーン名から推定（scene.rtvs -> scene.png）
            output ??= Path.ChangeExtension(scene, ".png");

            return new HeadlessRenderer
            {
                ScenePath = scene,
                OutputPath = output!,
                Width = width,
                Height = height,
                Passes = passes
            };
        }

        private static string? NextArg(string[] args, ref int i)
            => (i + 1 < args.Length) ? args[++i] : null;

        /// <summary>
        /// レンダリングを実行する。戻り値: 0=成功, 非0=失敗（終了コード）。
        /// 必ずUI(STA)スレッドから呼ぶこと。
        /// </summary>
        public int Run()
        {
            Window? hwndWindow = null;
            RenderService? renderService = null;
            try
            {
                if (!File.Exists(ScenePath))
                {
                    Log($"エラー: シーンファイルが見つかりません: {ScenePath}");
                    return 2;
                }

                // 1) シーン読込（MainWindowと同じ経路: SceneFileService → MainViewModel）
                var sceneService = new SceneFileService();
                var (nodes, connections, viewportState) = sceneService.LoadScene(ScenePath);

                var viewModel = new MainViewModel();
                foreach (var node in nodes)
                    viewModel.AddNode(node);
                foreach (var connection in connections)
                    viewModel.AddConnection(connection);

                if (sceneService.RemovedNodeInfos.Count > 0)
                    Log($"警告: 一部のノードが除外されました: {string.Join(", ", sceneService.RemovedNodeInfos)}");

                // 2) 解像度決定（コマンドライン引数 > シーン保存値 > 既定1920x1080）
                int width = Width ?? viewportState?.RenderWidth ?? 1920;
                int height = Height ?? viewportState?.RenderHeight ?? 1080;

                // 3) 非表示ウィンドウでHWNDを確保
                //    DXContext::CreateSwapChain が IsWindow(hwnd) を要求するため IntPtr.Zero は不可。
                //    Show() せずに EnsureHandle() で実体のあるウィンドウハンドルだけ生成する。
                hwndWindow = new Window
                {
                    Width = 1,
                    Height = 1,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -32000,
                    Top = -32000,
                    Visibility = Visibility.Hidden
                };
                IntPtr hwnd = new WindowInteropHelper(hwndWindow).EnsureHandle();
                if (hwnd == IntPtr.Zero)
                {
                    Log("エラー: ウィンドウハンドルの確保に失敗しました。");
                    return 3;
                }

                // 4) レンダリングエンジン初期化
                renderService = new RenderService();
                if (!renderService.Initialize(hwnd, width, height))
                {
                    Log("エラー: DirectXレンダリングエンジンの初期化に失敗しました（DXR対応GPUが必要）。");
                    return 4;
                }

                // 5) シーン評価（ノードグラフ → エンジンへ渡すパラメータ）
                var evaluator = new SceneEvaluator();
                var ev = evaluator.EvaluateScene(viewModel.NodeGraph);

                // 6) Nパスレンダリング
                //    最初のパスはシェーダーコンパイル（ウォームアップ）を兼ねる。
                //    GUIと同様、デノイザーのテンポラル蓄積のため複数パスを実行する。
                Log($"レンダリング開始: {Path.GetFileName(ScenePath)} {width}x{height} passes={Passes}");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < Passes; i++)
                {
                    renderService.UpdateScene(
                        ev.Item1, ev.Item2, ev.Item3, ev.Item4, ev.Item5,
                        ev.Item6, ev.Item7,
                        ev.SamplesPerPixel, ev.MaxBounces, ev.TraceRecursionDepth,
                        ev.Exposure, ev.ToneMapOperator,
                        ev.DenoiserStabilization, ev.ShadowStrength, ev.ShadowAbsorptionScale,
                        ev.EnableDenoiser, ev.Gamma,
                        0, 1.0f, // photonDebugMode, photonDebugScale（デバッグ表示なし）
                        ev.LightAttenuationConstant, ev.LightAttenuationLinear, ev.LightAttenuationQuadratic,
                        ev.MaxShadowLights, ev.NRDBypassDistance, ev.NRDBypassBlendRange);
                    renderService.Render();
                }
                sw.Stop();

                // 7) ピクセル取得（RGBA）
                byte[]? rgba = renderService.GetPixelData();
                if (rgba == null || rgba.Length < (long)width * height * 4)
                {
                    Log("エラー: ピクセルデータの取得に失敗しました。");
                    return 5;
                }

                // 8) RGBA → BGRA変換してPNG保存
                SaveRgbaAsPng(rgba, width, height, OutputPath);
                Log($"完了: {OutputPath} ({sw.Elapsed.TotalMilliseconds:F0} ms)");
                return 0;
            }
            catch (Exception ex)
            {
                Log($"例外: {ex.Message}\n{ex.StackTrace}");
                return 1;
            }
            finally
            {
                renderService?.Dispose();
                hwndWindow?.Close();
            }
        }

        /// <summary>
        /// RGBA バイト列を BGRA32 の PNG ファイルとして保存する。
        /// 変換は RenderWindow.UpdateDisplay と同じ R/B スワップ。
        /// </summary>
        private static void SaveRgbaAsPng(byte[] rgba, int width, int height, string outputPath)
        {
            int pixelCount = width * height;
            var bgra = new byte[pixelCount * 4];
            for (int idx = 0; idx < pixelCount; idx++)
            {
                int o = idx * 4;
                byte r = rgba[o + 0];
                byte g = rgba[o + 1];
                byte b = rgba[o + 2];
                byte a = rgba[o + 3];
                bgra[o + 0] = b;
                bgra[o + 1] = g;
                bgra[o + 2] = r;
                bgra[o + 3] = a;
            }

            var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
            bmp.Freeze();

            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = File.Create(outputPath);
            encoder.Save(stream);
        }

        private static void Log(string message)
        {
            Console.WriteLine($"[Headless] {message}");
            System.Diagnostics.Debug.WriteLine($"[Headless] {message}");
        }
    }
}
