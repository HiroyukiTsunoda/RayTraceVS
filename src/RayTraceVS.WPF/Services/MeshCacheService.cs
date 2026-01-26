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
    /// メッシュデータは遅延読み込み（Lazy Loading）で必要時に読み込む
    /// </summary>
    public class MeshCacheService
    {
        private const string CACHE_MAGIC = "RTVS";
        private const uint CACHE_VERSION = 1;
        private const int FLOATS_PER_VERTEX = 8; // position(3) + padding(1) + normal(3) + padding(1)

        // メタデータのみを保持（実際のデータは遅延読み込み）
        private readonly Dictionary<string, MeshMetadata> _meshMetadata = new();
        // 読み込み済みのメッシュデータをキャッシュ
        private readonly Dictionary<string, CachedMeshData> _loadedMeshes = new();
        private readonly object _loadLock = new();
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
        public IReadOnlyList<string> AvailableMeshes => _meshMetadata.Keys.ToList();

        /// <summary>
        /// 初期化（起動時に呼び出し）
        /// FBXファイルがなくても正常に起動する
        /// メッシュデータは遅延読み込みのため、起動時はメタデータのみスキャン
        /// </summary>
        public async Task InitializeAsync()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                EnsureDirectoriesExist();
                LoadManifest();
                CleanupOrphanedCaches();
                await ConvertOutdatedFBXFilesAsync();
                ScanAllMeshFiles(); // メタデータのみスキャン（高速）
                Debug.WriteLine($"MeshCacheService初期化完了: {sw.ElapsedMilliseconds}ms, メッシュ数: {_meshMetadata.Count}");
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
            return _meshMetadata.ContainsKey(meshName);
        }

        /// <summary>
        /// メッシュデータを取得（遅延読み込み）
        /// 初回アクセス時にファイルから読み込み、以降はメモリキャッシュを返す
        /// </summary>
        public CachedMeshData? GetMesh(string meshName)
        {
            // まず読み込み済みキャッシュをチェック
            if (_loadedMeshes.TryGetValue(meshName, out var cachedData))
            {
                return cachedData;
            }

            // メタデータが存在するかチェック
            if (!_meshMetadata.TryGetValue(meshName, out var metadata))
            {
                return null;
            }

            // ファイルから読み込み（スレッドセーフ）
            lock (_loadLock)
            {
                // ダブルチェック（他のスレッドが読み込み済みかもしれない）
                if (_loadedMeshes.TryGetValue(meshName, out cachedData))
                {
                    return cachedData;
                }

                var sw = Stopwatch.StartNew();
                var meshData = LoadMeshFromCache(metadata.CachePath);
                if (meshData != null)
                {
                    _loadedMeshes[meshName] = meshData;
                    Debug.WriteLine($"Loaded mesh on demand: {meshName} ({meshData.VertexCount} vertices, {meshData.TriangleCount} triangles) in {sw.ElapsedMilliseconds}ms");
                }
                return meshData;
            }
        }

        /// <summary>
        /// メッシュのメタデータを取得（読み込みなし）
        /// </summary>
        public MeshMetadata? GetMeshMetadata(string meshName)
        {
            return _meshMetadata.TryGetValue(meshName, out var metadata) ? metadata : null;
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
                    
                    // タイムアウト付きで変換を実行（60秒）
                    var convertTask = Task.Run(() => ConvertFBXToCache(fbxPath, cachePath));
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                    
                    var completedTask = await Task.WhenAny(convertTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        // タイムアウト発生
                        var errorMsg = $"FBX変換がタイムアウトしました: {meshName}\nファイルが大きすぎるか、破損している可能性があります。";
                        Debug.WriteLine(errorMsg);
                        
                        // エラーダイアログを表示（UIスレッドで）
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show(errorMsg, "FBX変換タイムアウト", 
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                        
                        continue; // 次のファイルへ
                    }
                    
                    // 変換タスクの例外をチェック
                    try
                    {
                        await convertTask; // 例外があればここで再スロー
                        UpdateManifest(meshName, fbxPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"FBX変換エラー: {meshName} - {ex.Message}");
                        
                        // エラーダイアログを表示（UIスレッドで）
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show(ex.Message, "FBX変換エラー", 
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
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
            var fileName = Path.GetFileName(fbxPath);
            var sw = Stopwatch.StartNew();
            
            Debug.WriteLine($"[Assimp] 開始: {fileName}");
            
            using var context = new AssimpContext();
            
            Debug.WriteLine($"[Assimp] ImportFile開始: {fileName}");
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
            Debug.WriteLine($"[Assimp] ImportFile完了: {fileName} ({sw.ElapsedMilliseconds}ms)");

            // シーンの検証
            if (scene == null)
            {
                throw new InvalidOperationException($"FBXファイルを読み込めませんでした: {fbxPath}");
            }

            if (!scene.HasMeshes)
            {
                throw new InvalidOperationException($"FBXファイルにメッシュが含まれていません: {fbxPath}");
            }

            Debug.WriteLine($"[Assimp] メッシュ数: {scene.MeshCount}, マージ開始: {fileName}");
            var (vertices, indices, boundsMin, boundsMax) = MergeMeshesFromAssimp(scene);
            Debug.WriteLine($"[Assimp] マージ完了: {fileName} - 頂点数: {vertices.Length / FLOATS_PER_VERTEX}, インデックス数: {indices.Length} ({sw.ElapsedMilliseconds}ms)");

            // 頂点数0のチェック
            if (vertices.Length == 0)
            {
                throw new InvalidOperationException($"有効な頂点データがありません: {fbxPath}");
            }

            Debug.WriteLine($"[Assimp] キャッシュ書き込み開始: {fileName}");
            WriteMeshCache(cachePath, vertices, indices, boundsMin, boundsMax);
            Debug.WriteLine($"[Assimp] 完了: {fileName} (総時間: {sw.ElapsedMilliseconds}ms)");
        }

        /// <summary>
        /// Assimpシーン内の全メッシュを1つに統合
        /// GPU構造体と同じ32バイト/頂点フォーマットで出力
        /// </summary>
        private (float[] vertices, uint[] indices, Vector3 boundsMin, Vector3 boundsMax) MergeMeshesFromAssimp(Scene scene)
        {
            // 総頂点数を先に計算してキャパシティを確保
            int totalVertexCount = 0;
            int totalFaceCount = 0;
            foreach (var mesh in scene.Meshes)
            {
                totalVertexCount += mesh.VertexCount;
                totalFaceCount += mesh.FaceCount;
            }
            
            Debug.WriteLine($"[Assimp] 統合予定: 総頂点数={totalVertexCount}, 総フェイス数={totalFaceCount}");
            
            // 大きすぎるメッシュの警告
            if (totalVertexCount > 1000000)
            {
                Debug.WriteLine($"[Assimp] 警告: 非常に大きなメッシュです（{totalVertexCount}頂点）。処理に時間がかかる可能性があります。");
            }
            
            var vertices = new List<float>(totalVertexCount * FLOATS_PER_VERTEX);
            var indices = new List<uint>(totalFaceCount * 3); // 三角形なので3倍

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            uint baseVertex = 0;
            int meshIndex = 0;

            foreach (var mesh in scene.Meshes)
            {
                Debug.WriteLine($"[Assimp] メッシュ {meshIndex + 1}/{scene.MeshCount}: {mesh.Name ?? "(unnamed)"} - 頂点数={mesh.VertexCount}");
                
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
                meshIndex++;
            }

            Debug.WriteLine($"[Assimp] 統合完了: 頂点数={vertices.Count / FLOATS_PER_VERTEX}, インデックス数={indices.Count}");
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

        /// <summary>
        /// キャッシュフォルダ内のすべてのメッシュファイルをスキャンし、メタデータのみを読み込む
        /// 実際のメッシュデータは遅延読み込みで必要時に読み込む
        /// </summary>
        private void ScanAllMeshFiles()
        {
            if (!Directory.Exists(CacheFolder))
                return;

            var cacheFiles = Directory.GetFiles(CacheFolder, "*.mesh");
            foreach (var cachePath in cacheFiles)
            {
                var meshName = Path.GetFileNameWithoutExtension(cachePath);
                var metadata = ReadMeshMetadata(cachePath);
                if (metadata != null)
                {
                    _meshMetadata[meshName] = metadata;
                    Debug.WriteLine($"Scanned mesh: {meshName} ({metadata.VertexCount} vertices, {metadata.TriangleCount} triangles)");
                }
            }
        }

        /// <summary>
        /// メッシュファイルからメタデータ（ヘッダー）のみを読み込む
        /// </summary>
        private MeshMetadata? ReadMeshMetadata(string cachePath)
        {
            if (!File.Exists(cachePath))
                return null;

            try
            {
                using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(stream);

                // Header (40 bytes)
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

                return new MeshMetadata
                {
                    CachePath = cachePath,
                    VertexCount = (int)vertexCount,
                    IndexCount = (int)indexCount,
                    TriangleCount = (int)(indexCount / 3),
                    BoundsMin = boundsMin,
                    BoundsMax = boundsMax
                };
            }
            catch
            {
                return null;
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

    /// <summary>
    /// メッシュのメタデータ（遅延読み込み用）
    /// ヘッダー情報のみを保持し、実際のデータはGetMesh()時に読み込む
    /// </summary>
    public class MeshMetadata
    {
        /// <summary>
        /// キャッシュファイルのパス
        /// </summary>
        public string CachePath { get; set; } = "";

        /// <summary>
        /// 頂点数
        /// </summary>
        public int VertexCount { get; set; }

        /// <summary>
        /// インデックス数
        /// </summary>
        public int IndexCount { get; set; }

        /// <summary>
        /// 三角形数
        /// </summary>
        public int TriangleCount { get; set; }

        /// <summary>
        /// バウンディングボックス最小値
        /// </summary>
        public Vector3 BoundsMin { get; set; }

        /// <summary>
        /// バウンディングボックス最大値
        /// </summary>
        public Vector3 BoundsMax { get; set; }
    }
}
