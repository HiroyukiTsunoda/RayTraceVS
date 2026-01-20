namespace RayTraceVS.WPF.Services.Interfaces
{
    /// <summary>
    /// アプリケーション設定サービスのインターフェース
    /// </summary>
    public interface ISettingsService
    {
        /// <summary>
        /// レンダリング幅
        /// </summary>
        int RenderWidth { get; set; }

        /// <summary>
        /// レンダリング高さ
        /// </summary>
        int RenderHeight { get; set; }

        /// <summary>
        /// サンプル数/ピクセル
        /// </summary>
        int SamplesPerPixel { get; set; }

        /// <summary>
        /// 最大バウンス数
        /// </summary>
        int MaxBounces { get; set; }

        /// <summary>
        /// 自動リフレッシュが有効かどうか
        /// </summary>
        bool AutoRefresh { get; set; }

        /// <summary>
        /// デノイザーが有効かどうか
        /// </summary>
        bool UseDenoiser { get; set; }

        /// <summary>
        /// 設定を保存する
        /// </summary>
        void Save();

        /// <summary>
        /// 設定を読み込む
        /// </summary>
        void Load();

        /// <summary>
        /// 設定をデフォルトにリセットする
        /// </summary>
        void ResetToDefaults();
    }
}
