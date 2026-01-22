using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Assimp;
using Newtonsoft.Json;
using RayTraceVS.WPF.Models.Data;

namespace RayTraceVS.WPF.Services
{
    /// <summary>
    /// FBXメッシュのキャッシュ管理サービス
    /// 起動時にFBXをスキャンし、必要に応じてキャッシュに変換する
    /// </summary>
    public class MeshCacheService
    {
        private const string CACHE_MAGIC = "RTVS";
        private const uint CACHE_VERSION = 1;
        private const int FLOATS_PER_VERTEX = 8; // position(3) + padding(1) + normal(3) + padding(1)

        private readonly Dictionary<string, CachedMeshData> _meshCache = new();
        private CacheManifest? _manifest;

        /// <summary>
        /// Modelフォルダのパス
        /// </summary>
        public static string ModelFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resource", "Model");

        /// <summary>
        /// Cacheフォルダのパス
        /// </summary>
        public static string CacheFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resource", "Model", "Cache");

        /// <summary>
        /// 利用可能なメッシュ名のリスト（キャッシュ済みのみ）
        /// </summary>
        public IReadOnlyList<string> AvailableMeshes => _meshCache.Keys.ToList();

        /// <summary>
        /// 初期化（起動時に呼び出し）
        /// FBXファイルがなくても正常に起動する
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                EnsureDirectoriesExist();
                LoadManifest();
                CleanupOrphanedCaches();
                await ConvertOutdatedFBXFilesAsync();
                LoadAllMeshesToMemory();
            }
            catch (Exception ex)
            {
                // FBXがない・変換に失敗しても起動は続行する
                // 致命的なエラーではないのでログ出力のみ
                Debug.WriteLine($"メッシュキャッシュの初期化中に問題が発生しました: {ex.Message}");
            }
        }

        /// <summary>
        /// 指定したメッシュがキャッシュに存在するかどうかを確認
        /// </summary>
        public bool HasMesh(string meshName)
        {
            return _meshCache.ContainsKey(meshName);
        }

        /// <summary>
        /// メッシュデータを取得
        /// </summary>
        public CachedMeshData? GetMesh(string meshName)
        {
            return _meshCache.TryGetValue(meshName, out var data) ? data : null;
        }

        private void EnsureDirectoriesExist()
        {
            if (!Directory.Exists(ModelFolder))
            {
                Directory.CreateDirectory(ModelFolder);
            }
            if (!Directory.Exists(CacheFolder))
            {
                Directory.CreateDirectory(CacheFolder);
            }
        }

        private void LoadManifest()
        {
            var manifestPath = Path.Combine(CacheFolder, "cache.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    _manifest = JsonConvert.DeserializeObject<CacheManifest>(json);
                }
                catch
                {
                    _manifest = new CacheManifest();
                }
            }
            else
            {
                _manifest = new CacheManifest();
            }
        }

        private void SaveManifest()
        {
            var manifestPath = Path.Combine(CacheFolder, "cache.json");
            var json = JsonConvert.SerializeObject(_manifest, Formatting.Indented);
            File.WriteAllText(manifestPath, json);
        }

        /// <summary>
        /// Modelフォルダに対応するFBXが存在しないキャッシュを削除
        /// </summary>
        private void CleanupOrphanedCaches()
        {
            if (!Directory.Exists(ModelFolder) || !Directory.Exists(CacheFolder))
                return;

            var fbxFiles = Directory.GetFiles(ModelFolder, "*.fbx")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var cacheFiles = Directory.GetFiles(CacheFolder, "*.mesh");
            foreach (var cachePath in cacheFiles)
            {
                var meshName = Path.GetFileNameWithoutExtension(cachePath);
                if (!fbxFiles.Contains(meshName))
                {
                    // 対応するFBXが存在しない → キャッシュを削除
                    try
                    {
                        File.Delete(cachePath);
                        _manifest?.Meshes.Remove(meshName);
                        Debug.WriteLine($"Deleted orphaned cache: {meshName}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to delete orphaned cache {meshName}: {ex.Message}");
                    }
                }
            }
        }

        private async Task ConvertOutdatedFBXFilesAsync()
        {
            if (!Directory.Exists(ModelFolder))
                return;

            var fbxFiles = Directory.GetFiles(ModelFolder, "*.fbx");
            foreach (var fbxPath in fbxFiles)
            {
                var meshName = Path.GetFileNameWithoutExtension(fbxPath);
                var cachePath = Path.Combine(CacheFolder, meshName + ".mesh");

                if (NeedsRebuild(fbxPath, cachePath))
                {
                    Debug.WriteLine($"Converting FBX: {meshName}");
                    await Task.Run(() => ConvertFBXToCache(fbxPath, cachePath));
                    UpdateManifest(meshName, fbxPath);
                }
            }
            SaveManifest();
        }

        private bool NeedsRebuild(string fbxPath, string cachePath)
        {
            if (!File.Exists(cachePath))
                return true;

            var fbxModified = File.GetLastWriteTimeUtc(fbxPath);
            var cacheModified = File.GetLastWriteTimeUtc(cachePath);

            return fbxModified > cacheModified;
        }

        private void ConvertFBXToCache(string fbxPath, string cachePath)
        {
            var fileName = Path.GetFileName(fbxPath);
            
            try
            {
                ConvertWithAssimp(fbxPath, cachePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Assimp failed for {fileName}: {ex.Message}");
                
                // 詳細なエラーメッセージを生成
                var isBinary = IsBinaryFbx(fbxPath);
                var fbxVersion = DetectFbxVersion(fbxPath);
                
                var errorMsg = new StringBuilder();
                errorMsg.AppendLine($"FBXファイルを読み込めませんでした: {fileName}");
                errorMsg.AppendLine();
                errorMsg.AppendLine("【検出情報】");
                errorMsg.AppendLine($"  形式: {(isBinary ? "バイナリ" : "ASCII")}");
                if (!string.IsNullOrEmpty(fbxVersion))
                {
                    errorMsg.AppendLine($"  バージョン: {fbxVersion}");
                }
                errorMsg.AppendLine();
                errorMsg.AppendLine("【エラー詳細】");
                errorMsg.AppendLine(ex.Message);
                errorMsg.AppendLine();
                errorMsg.AppendLine("【対処法】");
                errorMsg.AppendLine("このアプリはFBX 2011 (SDK 7.1) 以降の形式に対応しています。");
                errorMsg.AppendLine("古いFBXバージョンの場合、以下をお試しください：");
                errorMsg.AppendLine();
                errorMsg.AppendLine("  [Blenderを使う場合]");
                errorMsg.AppendLine("  1. Blender 2.8以降でFBXファイルを開く");
                errorMsg.AppendLine("  2. File > Export > FBX (.fbx) を選択");
                errorMsg.AppendLine("  3. 「Path Mode」を「Copy」に設定");
                errorMsg.AppendLine("  4. 「Version」を「FBX 7.4 binary」に設定");
                errorMsg.AppendLine("  5. エクスポートして再度読み込む");
                errorMsg.AppendLine();
                errorMsg.AppendLine("  [その他のツール]");
                errorMsg.AppendLine("  - Autodesk FBX Converter (無料) で変換");
                errorMsg.AppendLine("  - Maya/3ds Max でFBX 2014以降で再エクスポート");
                
                throw new InvalidOperationException(errorMsg.ToString(), ex);
            }
        }

        /// <summary>
        /// FBXファイルのバージョンを検出
        /// </summary>
        private string DetectFbxVersion(string fbxPath)
        {
            try
            {
                using var stream = new FileStream(fbxPath, FileMode.Open, FileAccess.Read);
                var header = new byte[256];
                int bytesRead = stream.Read(header, 0, Math.Min(256, (int)stream.Length));
                
                if (bytesRead < 20)
                    return "";
                
                var headerStr = Encoding.ASCII.GetString(header);
                
                // バイナリFBXの場合
                if (headerStr.StartsWith("Kaydara FBX Binary"))
                {
                    // バージョン番号は23バイト目から4バイトのリトルエンディアン整数
                    if (bytesRead >= 27)
                    {
                        int version = BitConverter.ToInt32(header, 23);
                        // バージョン番号を人間が読みやすい形式に変換
                        // 例: 7400 -> 7.4, 7500 -> 7.5
                        return $"FBX {version / 1000}.{(version % 1000) / 100}";
                    }
                }
                else
                {
                    // ASCII FBXの場合、FBXHeaderVersionを探す
                    var versionMatch = System.Text.RegularExpressions.Regex.Match(
                        headerStr, @"FBXHeaderVersion:\s*(\d+)");
                    if (versionMatch.Success)
                    {
                        int version = int.Parse(versionMatch.Groups[1].Value);
                        return $"FBX {version / 1000}.{(version % 1000) / 100}";
                    }
                }
            }
            catch
            {
                // バージョン検出に失敗しても問題なし
            }
            
            return "";
        }

        /// <summary>
        /// FBXファイルがバイナリ形式かどうかを判定
        /// </summary>
        private bool IsBinaryFbx(string fbxPath)
        {
            try
            {
                using var stream = new FileStream(fbxPath, FileMode.Open, FileAccess.Read);
                var header = new byte[20];
                stream.Read(header, 0, Math.Min(20, (int)stream.Length));
                
                // バイナリFBXは "Kaydara FBX Binary" で始まる
                var headerStr = Encoding.ASCII.GetString(header);
                return headerStr.StartsWith("Kaydara FBX Binary");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Assimpを使用してFBXを変換
        /// </summary>
        private void ConvertWithAssimp(string fbxPath, string cachePath)
        {
            using var context = new AssimpContext();
            var scene = context.ImportFile(fbxPath,
                // 必須処理
                PostProcessSteps.Triangulate |              // DXRは三角形のみ対応
                PostProcessSteps.GenerateSmoothNormals |    // スムーズシェーディング用法線
                PostProcessSteps.JoinIdenticalVertices |    // 重複頂点を結合してインデックス最適化
                // 最適化処理
                PostProcessSteps.ImproveCacheLocality |     // GPU頂点キャッシュ効率化
                PostProcessSteps.OptimizeMeshes |           // 小さなメッシュを統合
                // 座標系変換（DirectXは左手系）
                PostProcessSteps.MakeLeftHanded |           // 左手座標系に変換
                PostProcessSteps.FlipWindingOrder           // 面の巻き方向を反転（左手系用）
            );

            // シーンの検証
            if (scene == null)
            {
                throw new InvalidOperationException($"FBXファイルを読み込めませんでした: {fbxPath}");
            }

            if (!scene.HasMeshes)
            {
                throw new InvalidOperationException($"FBXファイルにメッシュが含まれていません: {fbxPath}");
            }

            var (vertices, indices, boundsMin, boundsMax) = MergeMeshesFromAssimp(scene);

            // 頂点数0のチェック
            if (vertices.Length == 0)
            {
                throw new InvalidOperationException($"有効な頂点データがありません: {fbxPath}");
            }

            WriteMeshCache(cachePath, vertices, indices, boundsMin, boundsMax);
        }

        /// <summary>
        /// Assimpシーン内の全メッシュを1つに統合
        /// GPU構造体と同じ32バイト/頂点フォーマットで出力
        /// </summary>
        private (float[] vertices, uint[] indices, Vector3 boundsMin, Vector3 boundsMax) MergeMeshesFromAssimp(Scene scene)
        {
            var vertices = new List<float>();
            var indices = new List<uint>();

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            uint baseVertex = 0;

            foreach (var mesh in scene.Meshes)
            {
                // 頂点データを追加（32バイト/頂点フォーマット）
                for (int i = 0; i < mesh.VertexCount; i++)
                {
                    var v = mesh.Vertices[i];
                    var n = mesh.HasNormals ? mesh.Normals[i] : new Vector3D(0, 1, 0);

                    // Position (12 bytes)
                    vertices.Add(v.X);
                    vertices.Add(v.Y);
                    vertices.Add(v.Z);
                    // Padding1 (4 bytes)
                    vertices.Add(0.0f);
                    // Normal (12 bytes)
                    vertices.Add(n.X);
                    vertices.Add(n.Y);
                    vertices.Add(n.Z);
                    // Padding2 (4 bytes)
                    vertices.Add(0.0f);

                    // バウンディングボックス更新
                    min = Vector3.Min(min, new Vector3(v.X, v.Y, v.Z));
                    max = Vector3.Max(max, new Vector3(v.X, v.Y, v.Z));
                }

                // インデックスを追加（baseVertexでオフセット）
                foreach (var face in mesh.Faces)
                {
                    foreach (var idx in face.Indices)
                    {
                        indices.Add(baseVertex + (uint)idx);
                    }
                }

                baseVertex += (uint)mesh.VertexCount;
            }

            return (vertices.ToArray(), indices.ToArray(), min, max);
        }

        private void WriteMeshCache(string cachePath, float[] vertices, uint[] indices, Vector3 boundsMin, Vector3 boundsMax)
        {
            using var stream = new FileStream(cachePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            // Header (40 bytes)
            writer.Write(CACHE_MAGIC.ToCharArray());  // 4 bytes
            writer.Write(CACHE_VERSION);              // 4 bytes
            writer.Write((uint)(vertices.Length / FLOATS_PER_VERTEX)); // VertexCount (4 bytes)
            writer.Write((uint)indices.Length);       // IndexCount (4 bytes)
            writer.Write(boundsMin.X);                // BoundsMin (12 bytes)
            writer.Write(boundsMin.Y);
            writer.Write(boundsMin.Z);
            writer.Write(boundsMax.X);                // BoundsMax (12 bytes)
            writer.Write(boundsMax.Y);
            writer.Write(boundsMax.Z);

            // Vertices (VertexCount * 32 bytes)
            foreach (var v in vertices)
            {
                writer.Write(v);
            }

            // Indices (IndexCount * 4 bytes)
            foreach (var idx in indices)
            {
                writer.Write(idx);
            }
        }

        private void UpdateManifest(string meshName, string fbxPath)
        {
            if (_manifest == null) return;

            var cachePath = Path.Combine(CacheFolder, meshName + ".mesh");
            var meshData = ReadMeshCacheHeader(cachePath);

            _manifest.Meshes[meshName] = new MeshManifestEntry
            {
                SourceFile = Path.GetFileName(fbxPath),
                SourceModified = File.GetLastWriteTimeUtc(fbxPath),
                CacheFile = meshName + ".mesh",
                VertexCount = meshData?.VertexCount ?? 0,
                IndexCount = meshData?.IndexCount ?? 0
            };
        }

        private CachedMeshData? ReadMeshCacheHeader(string cachePath)
        {
            if (!File.Exists(cachePath))
                return null;

            try
            {
                using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);

                // Header
                var magic = new string(reader.ReadChars(4));
                if (magic != CACHE_MAGIC)
                    return null;

                var version = reader.ReadUInt32();
                if (version != CACHE_VERSION)
                    return null;

                var vertexCount = reader.ReadUInt32();
                var indexCount = reader.ReadUInt32();
                var boundsMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var boundsMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                return new CachedMeshData
                {
                    Vertices = new float[vertexCount * FLOATS_PER_VERTEX],
                    Indices = new uint[indexCount],
                    BoundsMin = boundsMin,
                    BoundsMax = boundsMax
                };
            }
            catch
            {
                return null;
            }
        }

        private void LoadAllMeshesToMemory()
        {
            if (!Directory.Exists(CacheFolder))
                return;

            var cacheFiles = Directory.GetFiles(CacheFolder, "*.mesh");
            foreach (var cachePath in cacheFiles)
            {
                var meshName = Path.GetFileNameWithoutExtension(cachePath);
                var meshData = LoadMeshFromCache(cachePath);
                if (meshData != null)
                {
                    _meshCache[meshName] = meshData;
                    Debug.WriteLine($"Loaded mesh: {meshName} ({meshData.VertexCount} vertices, {meshData.TriangleCount} triangles)");
                }
            }
        }

        private CachedMeshData? LoadMeshFromCache(string cachePath)
        {
            if (!File.Exists(cachePath))
                return null;

            try
            {
                using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);

                // Header
                var magic = new string(reader.ReadChars(4));
                if (magic != CACHE_MAGIC)
                    return null;

                var version = reader.ReadUInt32();
                if (version != CACHE_VERSION)
                    return null;

                var vertexCount = reader.ReadUInt32();
                var indexCount = reader.ReadUInt32();
                var boundsMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var boundsMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                // Vertices
                var vertices = new float[vertexCount * FLOATS_PER_VERTEX];
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = reader.ReadSingle();
                }

                // Indices
                var indices = new uint[indexCount];
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = reader.ReadUInt32();
                }

                return new CachedMeshData
                {
                    Vertices = vertices,
                    Indices = indices,
                    BoundsMin = boundsMin,
                    BoundsMax = boundsMax
                };
            }
            catch
            {
                return null;
            }
        }

        private void ShowErrorAndExit(string title, Exception ex)
        {
            var message = $"{title}\n\n詳細: {ex.Message}";

            MessageBox.Show(message, "FBXキャッシュエラー",
                            MessageBoxButton.OK, MessageBoxImage.Error);

            Application.Current.Shutdown(1);
        }
    }

    /// <summary>
    /// キャッシュマニフェスト
    /// </summary>
    public class CacheManifest
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("meshes")]
        public Dictionary<string, MeshManifestEntry> Meshes { get; set; } = new();
    }

    /// <summary>
    /// マニフェストエントリ
    /// </summary>
    public class MeshManifestEntry
    {
        [JsonProperty("sourceFile")]
        public string SourceFile { get; set; } = "";

        [JsonProperty("sourceModified")]
        public DateTime SourceModified { get; set; }

        [JsonProperty("cacheFile")]
        public string CacheFile { get; set; } = "";

        [JsonProperty("vertexCount")]
        public int VertexCount { get; set; }

        [JsonProperty("indexCount")]
        public int IndexCount { get; set; }
    }
}
