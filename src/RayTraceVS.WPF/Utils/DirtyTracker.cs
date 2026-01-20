using System;
using System.Collections.Generic;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Utils
{
    /// <summary>
    /// ノードグラフのDirty状態を管理するクラス。
    /// 非再帰的な伝播処理と重複防止を提供する。
    /// </summary>
    public class DirtyTracker
    {
        private readonly HashSet<Guid> _dirtyNodes = new();
        private readonly Func<Node, IEnumerable<Node>> _getDownstreamNodes;

        /// <summary>
        /// DirtyTrackerを初期化する。
        /// </summary>
        /// <param name="getDownstreamNodes">下流ノードを取得するデリゲート</param>
        public DirtyTracker(Func<Node, IEnumerable<Node>> getDownstreamNodes)
        {
            _getDownstreamNodes = getDownstreamNodes ?? throw new ArgumentNullException(nameof(getDownstreamNodes));
        }

        /// <summary>
        /// ノードをDirtyとしてマークし、下流ノードにも伝播する（非再帰的）。
        /// </summary>
        /// <param name="node">Dirtyにするノード</param>
        public void MarkDirty(Node node)
        {
            if (node == null || _dirtyNodes.Contains(node.Id))
                return;

            // 幅優先探索で下流ノードを収集（非再帰）
            var queue = new Queue<Node>();
            queue.Enqueue(node);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                
                // 既にDirtyなら処理済みとしてスキップ（重複防止）
                if (!_dirtyNodes.Add(current.Id))
                    continue;

                // ノードのキャッシュを無効化（イベント発火なし）
                current.InvalidateCacheOnly();

                // 下流ノードをキューに追加
                foreach (var downstream in _getDownstreamNodes(current))
                {
                    if (!_dirtyNodes.Contains(downstream.Id))
                    {
                        queue.Enqueue(downstream);
                    }
                }
            }
        }

        /// <summary>
        /// 指定したノードがDirty状態かどうかを確認する。
        /// </summary>
        public bool IsDirty(Node node)
        {
            return node != null && _dirtyNodes.Contains(node.Id);
        }

        /// <summary>
        /// 指定したノードIDがDirty状態かどうかを確認する。
        /// </summary>
        public bool IsDirty(Guid nodeId)
        {
            return _dirtyNodes.Contains(nodeId);
        }

        /// <summary>
        /// 評価後にDirtyセットをクリアする。
        /// </summary>
        public void ClearAfterEvaluation()
        {
            _dirtyNodes.Clear();
        }

        /// <summary>
        /// すべてのノードをDirtyセットに追加する（完全再評価用）。
        /// </summary>
        public void MarkAllDirty(IEnumerable<Node> nodes)
        {
            foreach (var node in nodes)
            {
                _dirtyNodes.Add(node.Id);
                node.InvalidateCacheOnly();
            }
        }

        /// <summary>
        /// Dirtyなノードの数を取得する。
        /// </summary>
        public int DirtyCount => _dirtyNodes.Count;
    }
}
