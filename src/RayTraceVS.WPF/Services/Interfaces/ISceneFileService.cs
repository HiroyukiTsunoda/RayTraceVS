using System.Collections.ObjectModel;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Services.Interfaces
{
    /// <summary>
    /// シーンファイルの保存・読み込みサービスのインターフェース
    /// </summary>
    public interface ISceneFileService
    {
        /// <summary>
        /// 現在のファイルパス
        /// </summary>
        string? CurrentFilePath { get; }

        /// <summary>
        /// 変更があるかどうか
        /// </summary>
        bool HasChanges { get; }

        /// <summary>
        /// シーンをファイルに保存する
        /// </summary>
        /// <param name="filePath">保存先パス</param>
        /// <param name="nodes">ノードコレクション</param>
        /// <param name="connections">接続コレクション</param>
        void Save(string filePath, ObservableCollection<Node> nodes, ObservableCollection<NodeConnection> connections);

        /// <summary>
        /// シーンをファイルから読み込む
        /// </summary>
        /// <param name="filePath">読み込み元パス</param>
        /// <param name="nodes">ノードを格納するコレクション</param>
        /// <param name="connections">接続を格納するコレクション</param>
        /// <param name="nodeGraph">ノードグラフ</param>
        void Load(string filePath, ObservableCollection<Node> nodes, ObservableCollection<NodeConnection> connections, NodeGraph nodeGraph);

        /// <summary>
        /// 新規シーンを作成する
        /// </summary>
        void NewScene();

        /// <summary>
        /// 変更をマークする
        /// </summary>
        void MarkAsChanged();

        /// <summary>
        /// 変更を保存済みとしてマークする
        /// </summary>
        void MarkAsSaved();
    }
}
