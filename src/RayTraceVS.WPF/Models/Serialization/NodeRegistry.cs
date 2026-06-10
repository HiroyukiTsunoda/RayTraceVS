using System;
using System.Collections.Generic;
using RayTraceVS.WPF.Models.Nodes;

namespace RayTraceVS.WPF.Models.Serialization
{
    /// <summary>
    /// ノードタイプの登録メタデータ。シリアライズ名・パレット表示・ファクトリを一元管理する。
    /// </summary>
    /// <param name="TypeName">シリアライズ時の論理型名（例: "Sphere"）</param>
    /// <param name="DisplayName">パレットのボタン表示名（例: "Universal PBR"）</param>
    /// <param name="Category">パレットのカテゴリ</param>
    /// <param name="SortOrder">カテゴリ内の表示順</param>
    /// <param name="ShowInPalette">パレットに表示するか（FBXMesh等、別経路で生成するノードはfalse）</param>
    /// <param name="Factory">ノード生成ファクトリ</param>
    public record NodeRegistration(
        string TypeName,
        string DisplayName,
        NodeCategory Category,
        int SortOrder,
        bool ShowInPalette,
        Func<Node> Factory);

    /// <summary>
    /// ノードタイプの登録と生成を管理するレジストリ
    /// 新しいノードタイプを追加する際は、ここに登録するだけでシリアライズ/デシリアライズと
    /// コンポーネントパレットへの表示が可能になる
    /// </summary>
    public static class NodeRegistry
    {
        private static readonly Dictionary<string, Func<Node>> _nodeFactories = new();
        private static readonly Dictionary<Type, string> _typeToName = new();
        /// <summary>
        /// クラス名（GetType().Name）からノード生成するファクトリ（保存形式互換用）
        /// </summary>
        private static readonly Dictionary<string, Func<Node>> _classNameFactories = new();
        /// <summary>
        /// 登録メタデータの一覧（パレット自動生成用）
        /// </summary>
        private static readonly List<NodeRegistration> _registrations = new();
        private static bool _initialized = false;

        /// <summary>
        /// 組み込みノードタイプを登録する（アプリケーション起動時に一度だけ呼び出す）
        /// SortOrderはカテゴリ内のパレット表示順
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            // オブジェクトノード
            Register<SphereNode>("Sphere", "Sphere", NodeCategory.Object, 0);
            Register<PlaneNode>("Plane", "Plane", NodeCategory.Object, 1);
            Register<BoxNode>("Box", "Box", NodeCategory.Object, 2);
            // FBXメッシュはメッシュキャッシュからパレットを動的生成するため通常ボタンは出さない
            Register<FBXMeshNode>("FBXMesh", "FBXMesh", NodeCategory.Object, 99, showInPalette: false);

            // マテリアルノード
            Register<UniversalPBRNode>("UniversalPBR", "Universal PBR", NodeCategory.Material, 0);
            Register<MaterialBSDFNode>("MaterialBSDF", "BSDF", NodeCategory.Material, 1);
            Register<EmissionMaterialNode>("Emission", "Emission", NodeCategory.Material, 2);

            // ライトノード
            Register<AmbientLightNode>("AmbientLight", "Ambient Light", NodeCategory.Light, 0);
            Register<DirectionalLightNode>("DirectionalLight", "Directional Light", NodeCategory.Light, 1);
            Register<PointLightNode>("PointLight", "Point Light", NodeCategory.Light, 2);

            // カメラ・シーンノード
            Register<CameraNode>("Camera", "Camera", NodeCategory.Camera, 0);
            Register<SceneNode>("Scene", "Scene", NodeCategory.Scene, 0);

            // 算術ノード
            Register<FloatNode>("Float", "Float", NodeCategory.Math, 0);
            Register<Vector3Node>("Vector3", "Vector3", NodeCategory.Math, 1);
            Register<Vector4Node>("Vector4", "Vector4", NodeCategory.Math, 2);
            Register<ColorNode>("Color", "Color", NodeCategory.Math, 3);

            // トランスフォームノード
            Register<TransformNode>("Transform", "Transform", NodeCategory.Math, 4);
            Register<CombineTransformNode>("CombineTransform", "Combine Transform", NodeCategory.Math, 5);

            Register<AddNode>("Add", "Add", NodeCategory.Math, 6);
            Register<SubNode>("Sub", "Sub", NodeCategory.Math, 7);
            Register<MulNode>("Mul", "Mul", NodeCategory.Math, 8);
            Register<DivNode>("Div", "Div", NodeCategory.Math, 9);

            // 後方互換性: 旧LightNodeはPointLightNodeとして読み込む
            _classNameFactories["LightNode"] = () => new PointLightNode();

            _initialized = true;
        }

        /// <summary>
        /// ノードタイプを登録する
        /// </summary>
        /// <typeparam name="T">ノードの型</typeparam>
        /// <param name="typeName">シリアライズ時の型名</param>
        /// <param name="displayName">パレットのボタン表示名</param>
        /// <param name="category">パレットのカテゴリ</param>
        /// <param name="sortOrder">カテゴリ内の表示順</param>
        /// <param name="showInPalette">パレットに表示するか</param>
        public static void Register<T>(string typeName, string displayName, NodeCategory category,
            int sortOrder, bool showInPalette = true) where T : Node, new()
        {
            Func<Node> factory = () => new T();
            _nodeFactories[typeName] = factory;
            _typeToName[typeof(T)] = typeName;
            // クラス名でも引けるように登録
            _classNameFactories[typeof(T).Name] = factory;
            _registrations.Add(new NodeRegistration(typeName, displayName, category, sortOrder, showInPalette, factory));
        }

        /// <summary>
        /// 登録されているすべてのノードメタデータを取得する（パレット自動生成用）
        /// </summary>
        public static IReadOnlyList<NodeRegistration> GetRegistrations()
        {
            EnsureInitialized();
            return _registrations;
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
        /// クラス名（GetType().Name）からノードを生成する
        /// 保存ファイルの Type フィールドはクラス名で保存されているため、こちらを使う
        /// </summary>
        /// <param name="className">クラス名（例: "SphereNode"）</param>
        /// <returns>生成されたノード、または登録されていない場合はnull</returns>
        public static Node? CreateNodeByClassName(string className)
        {
            EnsureInitialized();
            return _classNameFactories.TryGetValue(className, out var factory) ? factory() : null;
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
        /// 指定したクラス名が登録されているかどうかを確認する
        /// </summary>
        public static bool IsClassNameRegistered(string className)
        {
            EnsureInitialized();
            return _classNameFactories.ContainsKey(className);
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
