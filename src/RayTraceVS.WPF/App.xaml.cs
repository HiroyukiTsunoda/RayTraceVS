using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using RayTraceVS.WPF.Services;

namespace RayTraceVS.WPF
{
    public partial class App : Application
    {
#if DEBUG
        // Debug log path relative to the executable location
        private static readonly string DebugLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
#endif

        /// <summary>
        /// メッシュキャッシュサービス（アプリ全体で共有）
        /// </summary>
        public static MeshCacheService MeshCacheService { get; private set; } = null!;

        // CLIから起動された場合に親プロセスのコンソールへ出力するための P/Invoke
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // コマンドライン引数を解析（CLI検証モード）
            //   --render <scene> --output <png> : ヘッドレスレンダリング
            //   --compare <ref> <target>        : 画像比較（出力同一性の検証用）
            //   --resave <in> <out>             : シーン再保存（シリアライズ往復の検証用）
            var headless = HeadlessRenderer.ParseArgs(e.Args);
            bool isCompare = Array.Exists(e.Args, a => a.Equals("--compare", StringComparison.OrdinalIgnoreCase));
            bool isResave = Array.Exists(e.Args, a => a.Equals("--resave", StringComparison.OrdinalIgnoreCase));

            if (headless != null || isCompare || isResave)
            {
                // CLIから起動された場合、親プロセスのコンソールに進捗/エラーを出力できるようにする
                AttachConsole(ATTACH_PARENT_PROCESS);
            }

            // 画像比較モード（レンダリング不要なので最優先で処理して終了）
            if (ImageComparer.TryParseAndRun(e.Args, out int compareExit))
            {
                Shutdown(compareExit);
                return;
            }

#if DEBUG
            // アプリケーション起動時にデバッグログファイルをクリア
            ClearDebugLog();
#endif

            // メッシュキャッシュを初期化（FBX変換）
            // 重要: MainWindow表示前 / ヘッドレスレンダリング前 / 再保存前に完了させる必要がある
            MeshCacheService = new MeshCacheService();
            await MeshCacheService.InitializeAsync();

            // Model層（FBXMeshNode）へメッシュキャッシュを注入（App層への直接依存を断つ）
            Models.Node.MeshCacheProvider = MeshCacheService;

            // シーン再保存モード（MeshCache初期化後＝LoadSceneがFBXキャッシュ判定に使う）
            if (SceneResaver.TryParseAndRun(e.Args, out int resaveExit))
            {
                Shutdown(resaveExit);
                return;
            }

            if (headless != null)
            {
                // ヘッドレスモード: レンダリングして画像保存後、終了コードを返してプロセス終了
                int exitCode = headless.Run();
                Shutdown(exitCode);
                return;
            }

            // 通常起動: キャッシュ初期化完了後にMainWindowを表示
            // StartupUriを使わず手動で表示することで、初期化完了を保証
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

#if DEBUG
        private void ClearDebugLog()
        {
            try
            {
                File.WriteAllText(DebugLogPath, string.Empty);
            }
            catch
            {
                // ログファイルのクリアに失敗しても続行
            }
        }
#endif

        protected override void OnExit(ExitEventArgs e)
        {
            // クリーンアップ処理
            base.OnExit(e);
        }
    }
}
