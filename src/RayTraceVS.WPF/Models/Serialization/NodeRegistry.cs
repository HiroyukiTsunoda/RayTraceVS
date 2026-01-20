using System;
using System.Collections.Generic;
using RayTraceVS.WPF.Models.Nodes;

namespace RayTraceVS.WPF.Models.Serialization
{
    /// <summary>
    /// ノードタイプの登録と生成を管理するレジストリ
    /// 新しいノードタイプを追加する際は、ここに登録するだけでシリアライズ/デシリアライズが可能になる
    /// </summary>
    public static class NodeRegistry
    {
        private static readonly Dictionary<string, Func<Node>> _nodeFactories = new();
        private static readonly Dictionary<Type, string> _typeToName = new();
        private static bool _initialized = false;

        /// <summary>
        /// 組み込みノードタイプを登録する（アプリケーション起動時に一度だけ呼び出す）
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            
            // オブジェクトノード
            Register<SphereNode>("Sphere");
            Register<PlaneNode>("Plane");
            Register<BoxNode>("Box");
            
            // マテリアルノード
            Register<EmissionMaterialNode>("Emission");
            Register<MaterialBSDFNode>("MaterialBSDF");
            Register<UniversalPBRNode>("UniversalPBR");
            
            // ライトノード
            Register<PointLightNode>("PointLight");
            Register<DirectionalLightNode>("DirectionalLight");
            Register<AmbientLightNode>("AmbientLight");
            
            // カメラ・シーンノード
            Register<CameraNode>("Camera");
            Register<SceneNode>("Scene");
            
            // 算術ノード
            Register<FloatNode>("Float");
            Register<Vector3Node>("Vector3");
            Register<Vector4Node>("Vector4");
            Register<ColorNode>("Color");
            Register<AddNode>("Add");
            Register<SubNode>("Sub");
            Register<MulNode>("Mul");
            Register<DivNode>("Div");
            
            // トランスフォームノード
            Register<TransformNode>("Transform");
            Register<CombineTransformNode>("CombineTransform");
            
            _initialized = true;
        }

        /// <summary>
        /// ノードタイプを登録する
        /// </summary>
        /// <typeparam name="T">ノードの型</typeparam>
        /// <param name="typeName">シリアライズ時の型名</param>
        public static void Register<T>(string typeName) where T : Node, new()
        {
            _nodeFactories[typeName] = () => new T();
            _typeToName[typeof(T)] = typeName;
        }

        /// <summary>
        /// 型名からノードを生成する
        /// </summary>
        /// <param name="typeName">型名</param>
        /// <returns>生成されたノード、または登録されていない場合はnull</returns>
        public static Node? CreateNode(string typeName)
        {
            EnsureInitialized();
            return _nodeFactories.TryGetValue(typeName, out var factory) ? factory() : null;
        }

        /// <summary>
        /// ノードの型からシリアライズ用の型名を取得する
        /// </summary>
        /// <param name="node">ノード</param>
        /// <returns>型名、または登録されていない場合は型の短縮名</returns>
        public static string GetTypeName(Node node)
        {
            EnsureInitialized();
            var type = node.GetType();
            return _typeToName.TryGetValue(type, out var name) ? name : type.Name;
        }

        /// <summary>
        /// 指定した型名が登録されているかどうかを確認する
        /// </summary>
        public static bool IsRegistered(string typeName)
        {
            EnsureInitialized();
            return _nodeFactories.ContainsKey(typeName);
        }

        /// <summary>
        /// 登録されているすべての型名を取得する
        /// </summary>
        public static IEnumerable<string> GetRegisteredTypeNames()
        {
            EnsureInitialized();
            return _nodeFactories.Keys;
        }

        private static void EnsureInitialized()
        {
            if (!_initialized)
            {
                Initialize();
            }
        }
    }
}
