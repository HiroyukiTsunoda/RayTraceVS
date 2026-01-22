using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;
using RayTraceVS.WPF.Models.Data;
using RayTraceVS.WPF.Models.Serialization;

namespace RayTraceVS.WPF.Models.Nodes
{
    /// <summary>
    /// FBXメッシュノード
    /// キャッシュ済みのFBXメッシュを配置するためのノード
    /// </summary>
    public class FBXMeshNode : Node, ISerializableNode
    {
        private string _meshName = "";
        private Transform _objectTransform = Transform.Identity;

        /// <summary>
        /// メッシュ名（キャッシュ参照キー）
        /// </summary>
        public string MeshName
        {
            get => _meshName;
            set
            {
                if (_meshName != value)
                {
                    _meshName = value;
                    Title = string.IsNullOrEmpty(value) ? "FBXMesh" : value;
                    OnPropertyChanged(nameof(MeshName));
                    OnPropertyChanged(nameof(VertexCount));
                    OnPropertyChanged(nameof(TriangleCount));
                    OnPropertyChanged(nameof(BoundsSizeText));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Transform（位置・回転・スケール）
        /// </summary>
        public Transform ObjectTransform
        {
            get => _objectTransform;
            set
            {
                if (!_objectTransform.Equals(value))
                {
                    _objectTransform = value;
                    OnPropertyChanged(nameof(ObjectTransform));
                    MarkDirty();
                }
            }
        }

        #region プロパティパネル用（読み取り専用）

        /// <summary>
        /// 頂点数
        /// </summary>
        public int VertexCount => App.MeshCacheService?.GetMesh(MeshName)?.VertexCount ?? 0;

        /// <summary>
        /// 三角形数
        /// </summary>
        public int TriangleCount => App.MeshCacheService?.GetMesh(MeshName)?.TriangleCount ?? 0;

        /// <summary>
        /// バウンディングボックスサイズ（表示用テキスト）
        /// </summary>
        public string BoundsSizeText
        {
            get
            {
                var mesh = App.MeshCacheService?.GetMesh(MeshName);
                if (mesh == null) return "N/A";
                var size = mesh.BoundsSize;
                return $"({size.X:F2}, {size.Y:F2}, {size.Z:F2})";
            }
        }

        #endregion

        /// <summary>
        /// デフォルトコンストラクタ（シリアライゼーション用）
        /// </summary>
        public FBXMeshNode() : base("FBXMesh", NodeCategory.Object)
        {
            InitializeSockets();
        }

        /// <summary>
        /// メッシュ名指定コンストラクタ（UI追加用）
        /// </summary>
        public FBXMeshNode(string meshName) : base(meshName, NodeCategory.Object)
        {
            _meshName = meshName;
            InitializeSockets();
        }

        private void InitializeSockets()
        {
            // 入力ソケット
            AddInputSocket("Transform", SocketType.Transform);
            AddInputSocket("Material", SocketType.Material);

            // 出力ソケット
            AddOutputSocket("Object", SocketType.Object);
        }

        public override object? Evaluate(Dictionary<Guid, object?> inputValues)
        {
            // キャッシュからメッシュデータを取得
            var meshData = App.MeshCacheService?.GetMesh(MeshName);
            if (meshData == null) return null;  // キャッシュにない場合はnull

            // Transform入力を取得（未接続の場合は内部プロパティを使用）
            var transformSocket = InputSockets.FirstOrDefault(s => s.Name == "Transform");
            Transform transform = ObjectTransform;
            
            if (transformSocket != null && inputValues.TryGetValue(transformSocket.Id, out var transformVal) && transformVal is Transform inputTransform)
            {
                transform = inputTransform;
            }

            // マテリアル入力を取得（未接続の場合はデフォルト）
            var material = GetInputValue<MaterialData?>("Material", inputValues) ?? MaterialData.Default;

            return new MeshObjectData
            {
                MeshName = MeshName,
                Transform = transform,
                Material = material
            };
        }

        #region ISerializableNode

        public void SerializeProperties(JObject json)
        {
            json["meshName"] = MeshName;
            json["transform"] = ObjectTransform.ToJson();
        }

        public void DeserializeProperties(JObject json)
        {
            MeshName = json["meshName"]?.ToString() ?? "";
            ObjectTransform = json["transform"].ToTransform();
        }

        #endregion
    }
}
