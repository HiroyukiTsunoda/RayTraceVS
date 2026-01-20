using System.IO;
using System.Windows;

namespace RayTraceVS.WPF
{
    public partial class App : Application
    {
        private const string DebugLogPath = @"C:\git\RayTraceVS\debug_log.txt";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // アプリケーション起動時にデバッグログファイルをクリア
            ClearDebugLog();
            
            // アプリケーション起動時の初期化処理
            // ログ設定、設定ファイル読み込みなど
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
