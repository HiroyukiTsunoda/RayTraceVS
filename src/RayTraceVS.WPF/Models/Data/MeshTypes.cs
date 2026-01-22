using System;
using System.Numerics;

namespace RayTraceVS.WPF.Models.Data
{
    /// <summary>
    /// キャッシュから読み込んだメッシュデータ
    /// GPU構造体と同じ32バイト/頂点フォーマット
    /// </summary>
    public class CachedMeshData
    {
        /// <summary>
        /// 頂点データ（Position + Padding + Normal + Padding）
        /// 32バイト/頂点: float3 position, float padding1, float3 normal, float padding2
        /// </summary>
        public float[] Vertices { get; set; } = Array.Empty<float>();

        /// <summary>
        /// インデックスデータ
        /// </summary>
        public uint[] Indices { get; set; } = Array.Empty<uint>();

        /// <summary>
        /// バウンディングボックス最小点
        /// </summary>
        public Vector3 BoundsMin { get; set; }

        /// <summary>
        /// バウンディングボックス最大点
        /// </summary>
        public Vector3 BoundsMax { get; set; }

        /// <summary>
        /// 頂点数（32バイト単位で計算）
        /// </summary>
        public int VertexCount => Vertices.Length / 8; // 8 floats per vertex (32 bytes)

        /// <summary>
        /// インデックス数
        /// </summary>
        public int IndexCount => Indices.Length;

        /// <summary>
        /// 三角形数
        /// </summary>
        public int TriangleCount => IndexCount / 3;

        /// <summary>
        /// バウンディングボックスサイズ
        /// </summary>
        public Vector3 BoundsSize => BoundsMax - BoundsMin;
    }

    /// <summary>
    /// シーン評価時に使用するメッシュオブジェクトデータ
    /// </summary>
    public struct MeshObjectData
    {
        /// <summary>
        /// メッシュ名（キャッシュ参照キー）
        /// </summary>
        public string MeshName;

        /// <summary>
        /// トランスフォーム
        /// </summary>
        public Transform Transform;

        /// <summary>
        /// マテリアル
        /// </summary>
        public MaterialData Material;
    }
}
