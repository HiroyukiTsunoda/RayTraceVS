using System.Windows;

namespace RayTraceVS.WPF
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // アプリケーション起動時の初期化処理
            // ログ設定、設定ファイル読み込みなど
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // クリーンアップ処理
            base.OnExit(e);
        }
    }
}
