using System;
using System.Windows.Media.Imaging;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Services.Interfaces
{
    /// <summary>
    /// レンダリングサービスのインターフェース
    /// </summary>
    public interface IRenderService : IDisposable
    {
        /// <summary>
        /// レンダリングが進行中かどうか
        /// </summary>
        bool IsRendering { get; }

        /// <summary>
        /// 現在の累積サンプル数
        /// </summary>
        int AccumulatedSamples { get; }

        /// <summary>
        /// レンダリングを開始する
        /// </summary>
        /// <param name="nodeGraph">レンダリングするノードグラフ</param>
        /// <param name="width">出力幅</param>
        /// <param name="height">出力高さ</param>
        /// <param name="onFrameReady">フレーム準備完了時のコールバック</param>
        void StartRendering(NodeGraph nodeGraph, int width, int height, Action<WriteableBitmap>? onFrameReady = null);

        /// <summary>
        /// レンダリングを停止する
        /// </summary>
        void StopRendering();

        /// <summary>
        /// 1フレームだけレンダリングする
        /// </summary>
        WriteableBitmap? RenderOnce(NodeGraph nodeGraph, int width, int height);

        /// <summary>
        /// 累積レンダリングをリセットする
        /// </summary>
        void ResetAccumulation();

        /// <summary>
        /// レンダリング完了時に発火するイベント
        /// </summary>
        event EventHandler<WriteableBitmap>? FrameRendered;

        /// <summary>
        /// エラー発生時に発火するイベント
        /// </summary>
        event EventHandler<Exception>? RenderError;
    }
}
