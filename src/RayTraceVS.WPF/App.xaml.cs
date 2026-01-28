using System;
using System.IO;
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

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
#if DEBUG
            // アプリケーション起動時にデバッグログファイルをクリア
            ClearDebugLog();
#endif
            
            // メッシュキャッシュを初期化（FBX変換）
            // 重要: MainWindow表示前に完了させる必要がある
            MeshCacheService = new MeshCacheService();
            await MeshCacheService.InitializeAsync();
            
            // キャッシュ初期化完了後にMainWindowを表示
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
