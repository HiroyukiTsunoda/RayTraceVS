using System.IO;
using System.Windows;
using RayTraceVS.WPF.Services;

namespace RayTraceVS.WPF
{
    public partial class App : Application
    {
        private const string DebugLogPath = @"C:\git\RayTraceVS\debug.log";

        /// <summary>
        /// メッシュキャッシュサービス（アプリ全体で共有）
        /// </summary>
        public static MeshCacheService MeshCacheService { get; private set; } = null!;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // アプリケーション起動時にデバッグログファイルをクリア
            ClearDebugLog();
            
            // メッシュキャッシュを初期化（FBX変換）
            // 重要: MainWindow表示前に完了させる必要がある
            MeshCacheService = new MeshCacheService();
            await MeshCacheService.InitializeAsync();
            
            // キャッシュ初期化完了後にMainWindowを表示
            // StartupUriを使わず手動で表示することで、初期化完了を保証
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

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

        protected override void OnExit(ExitEventArgs e)
        {
            // クリーンアップ処理
            base.OnExit(e);
        }
    }
}
