# RayTraceVS レンダリングプロセス詳細ドキュメント

---

## 使用技術スタック一覧

本プロジェクトで使用されているすべての技術・ライブラリ・アルゴリズムを網羅的に記載します。

---

### 1. グラフィックスAPI・レンダリング技術

#### 1.1 DirectX 12 (D3D12)

| 項目 | 内容 |
|------|------|
| **概要** | Microsoftの低レベルグラフィックスAPI |
| **バージョン** | DirectX 12 (Feature Level 12_1以上推奨) |
| **使用理由** | 明示的なGPUリソース管理、マルチスレッドコマンド記録、DXRサポート |
| **主要機能** | コマンドリスト/キュー、ディスクリプタヒープ、ルートシグネチャ、PSO |
| **関連ファイル** | `DXContext.cpp`, `DXRPipeline.cpp` |

**使用しているD3D12機能**:
- `ID3D12Device` - デバイス管理
- `ID3D12CommandQueue/CommandList` - GPU命令発行
- `ID3D12DescriptorHeap` - CBV/SRV/UAV管理
- `ID3D12RootSignature` - シェーダーパラメータ定義
- `ID3D12PipelineState` - レンダリングパイプライン状態

---

#### 1.2 DXR (DirectX Raytracing)

| 項目 | 内容 |
|------|------|
| **概要** | DirectX 12のハードウェアレイトレーシング拡張 |
| **要件** | RTコア搭載GPU (NVIDIA RTX / AMD RDNA2以降) |
| **フォールバック** | DXR非対応時はCompute Shaderパスを使用 |
| **主要コンポーネント** | BLAS, TLAS, State Object, Shader Tables |
| **関連ファイル** | `DXRPipeline.cpp`, `AccelerationStructure.cpp` |

**DXRパイプライン構成**:

```
State Object (RTPSO)
├── RayGen Shader       - レイ生成
├── Miss Shader         - ミス処理（空の色）
├── Hit Group
│   ├── Intersection    - プロシージャル交差判定
│   ├── ClosestHit      - シェーディング
│   └── AnyHit          - シャドウレイ用
└── Shader Config       - ペイロードサイズ、再帰深度
```

**Shader Table構成**:
- **Ray Generation Table**: 1エントリ (RayGen)
- **Miss Table**: 1エントリ (Miss)
- **Hit Group Table**: 2エントリ (Primary + Shadow)

---

#### 1.3 アクセラレーション構造 (BVH)

| 項目 | 内容 |
|------|------|
| **概要** | レイトレーシング用空間分割構造 |
| **実装** | DXRの`RaytracingAccelerationStructure` |
| **構造** | 2レベル階層 (BLAS + TLAS) |
| **ジオメトリタイプ** | プロシージャル (AABB) |
| **関連ファイル** | `AccelerationStructure.cpp/h` |

**階層構造**:

```
TLAS (Top-Level AS) - シーン全体
└── Instance (単一、単位変換)
    └── BLAS (Bottom-Level AS) - ジオメトリ
        └── AABBs (各オブジェクトのバウンディングボックス)
            ├── Sphere AABB (center ± radius)
            ├── Plane AABB (±1000の巨大AABB)
            └── Box AABB (center ± size)
```

**ビルドフラグ**:
```cpp
D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE
```
→ トレース速度優先（構築は遅いが交差判定が速い）

---

### 2. シェーダー技術

#### 2.1 HLSL (High-Level Shading Language)

| 項目 | 内容 |
|------|------|
| **概要** | DirectX用シェーダー言語 |
| **バージョン** | Shader Model 6.3+ (DXR対応) |
| **コンパイラ** | DXC (DirectX Shader Compiler) |
| **出力形式** | DXIL (DirectX Intermediate Language) |
| **関連ファイル** | `src/Shader/*.hlsl`, `src/Shader/*.hlsli` |

**シェーダーファイル構成**:

| ファイル | 種類 | 用途 |
|---------|------|------|
| `Common.hlsli` | ヘッダー | 共通定義、構造体、ユーティリティ関数 |
| `NRDEncoding.hlsli` | ヘッダー | NRD用エンコード/デコード関数 |
| `RayGen.hlsl` | RayGeneration | プライマリレイ生成、G-Buffer出力 |
| `Intersection.hlsl` | Intersection | プロシージャル交差判定 |
| `ClosestHit.hlsl` | ClosestHit | 統合マテリアル処理（Diffuse/Metal/Glass/Emission対応） |
| `ClosestHit_Diffuse.hlsl` | ClosestHit | ディフューズ専用（レガシー） |
| `ClosestHit_Metal.hlsl` | ClosestHit | 金属専用（レガシー） |
| `Miss.hlsl` | Miss | 空の色計算 |
| `AnyHit_Shadow.hlsl` | AnyHit | シャドウレイ処理 |
| `Composite.hlsl` | Compute | 最終合成、トーンマッピング |
| `PhotonEmit.hlsl` | Compute | フォトン放出 |
| `PhotonTrace.hlsl` | ClosestHit | フォトントレース |

---

#### 2.2 DXC (DirectX Shader Compiler)

| 項目 | 内容 |
|------|------|
| **概要** | LLVM/Clangベースの新世代HLSLコンパイラ |
| **出力** | DXIL (SM6.0+) / SPIR-V |
| **API使用** | `IDxcCompiler3`, `IDxcUtils` |
| **関連ファイル** | `ShaderCache.cpp`, `DXRPipeline.cpp` |

**コンパイルオプション**:
```cpp
const wchar_t* args[] = {
    L"-T", L"lib_6_3",     // ターゲット: ライブラリ (DXR用)
    L"-Fo", outputPath,     // 出力ファイル
    L"-I", includePath      // インクルードパス
};
```

---

### 3. デノイザー技術

#### 3.1 NRD (NVIDIA Real-time Denoiser)

| 項目 | 内容 |
|------|------|
| **概要** | NVIDIAのリアルタイムレイトレーシングデノイザー |
| **バージョン** | NRD SDK (適切なバージョンをビルド) |
| **統合方法** | C++ API + プリコンパイルDXILシェーダー |
| **関連ファイル** | `NRDDenoiser.cpp/h` |

**使用デノイザー**:

| デノイザー | 用途 | 入力 | 出力 |
|-----------|------|------|------|
| **REBLUR_DIFFUSE_SPECULAR** | Diffuse/Specular | Radiance, HitDist, Normal, Roughness, ViewZ, Motion | DenoisedDiffuse, DenoisedSpecular |
| **SIGMA_SHADOW** | シャドウ | Penumbra, Translucency, ViewZ, Normal | DenoisedShadow |

---

#### 3.2 REBLUR (Recurrent Blur)

| 項目 | 内容 |
|------|------|
| **概要** | テンポラル+スペーシャルデノイザー |
| **アルゴリズム** | 再帰的ブラーフィルタ + テンポラル累積 |
| **特徴** | ヒット距離ベースのフィルタリング、アンチファイアフライ |

**REBLUR設定**:
```cpp
nrd::ReblurSettings reblurSettings = {};
reblurSettings.hitDistanceReconstructionMode = nrd::HitDistanceReconstructionMode::AREA_3X3;
reblurSettings.enableAntiFirefly = true;
reblurSettings.maxBlurRadius = 30.0f;
reblurSettings.minBlurRadius = 0.0f;  // roughness=0でブラーなし
reblurSettings.responsiveAccumulationRoughnessThreshold = 0.05f;
reblurSettings.maxAccumulatedFrameNum = 16;
reblurSettings.maxFastAccumulatedFrameNum = 4;
```

**重要**: NRDはRadiance（アルベドなし）を入力として期待。アルベド乗算はComposite.hlslで実行。

---

#### 3.3 SIGMA (Shadow Denoiser)

| 項目 | 内容 |
|------|------|
| **概要** | シャドウ専用テンポラルデノイザー |
| **入力** | ペナンブラ、トランスルーセンシー、ViewZ |
| **出力** | デノイズされたシャドウ可視性 |
| **特徴** | エッジ保存、ペナンブラ考慮 |

**SIGMA設定**:
```cpp
nrd::SigmaSettings sigmaSettings = {};
sigmaSettings.lightDirection[0] = 0.0f;  // ライト方向 (正規化)
sigmaSettings.lightDirection[1] = -1.0f;
sigmaSettings.lightDirection[2] = 0.0f;
sigmaSettings.planeDistanceSensitivity = 0.02f;
sigmaSettings.maxStabilizedFrameNum = 5;
```

**SIGMAパッキング関数** (`NRDEncoding.hlsli`):
```hlsl
// フロントエンド (シェーダー→NRD)
float SIGMA_FrontEnd_PackPenumbra(float distToOccluder, float tanOfLightSize);
float4 SIGMA_FrontEnd_PackTranslucency(float distToOccluder, float3 translucency);

// バックエンド (NRD→シェーダー)
float SIGMA_BackEnd_UnpackShadow(float4 packedShadow);
```

---

### 4. G-Buffer構成

| バッファ | フォーマット | 内容 | NRD ResourceType |
|---------|-------------|------|-----------------|
| DiffuseRadianceHitDist | RGBA16F | RGB=Diffuse Radiance, A=HitDist | IN_DIFF_RADIANCE_HITDIST |
| SpecularRadianceHitDist | RGBA16F | RGB=Specular Radiance, A=HitDist | IN_SPEC_RADIANCE_HITDIST |
| NormalRoughness | RGBA8 | XYZ=Normal (Oct), W=Roughness | IN_NORMAL_ROUGHNESS |
| ViewZ | R32F | Linear view depth | IN_VIEWZ |
| MotionVectors | RG16F | Screen-space motion | IN_MV |
| Albedo | RGBA8 | Surface albedo | (独自) |
| ShadowData | RG16F | X=Penumbra, Y=Visibility | IN_PENUMBRA |
| ShadowTranslucency | RGBA16F | SIGMA translucency | IN_TRANSLUCENCY |
| RawSpecularBackup | RGBA16F | ミラーバイパス用バックアップ | (独自) |

---

### 5. ライティング・マテリアル技術

#### 5.1 PBR (Physically Based Rendering)

| パラメータ | 範囲 | 説明 |
|-----------|------|------|
| **Metallic** | 0.0-1.0 | 金属度 (0=誘電体, 1=金属) |
| **Roughness** | 0.0-1.0 | 粗さ (0=鏡面, 1=完全拡散) |
| **Transmission** | 0.0-1.0 | 透過度 (ガラス用) |
| **IOR** | 1.0-3.0 | 屈折率 (Index of Refraction) |
| **Specular** | 0.0-1.0 | スペキュラ強度 |
| **Albedo** | RGB | ベースカラー |
| **Emission** | RGB | 発光色（自己発光マテリアル用） |

**GPU構造体サイズ**:
| 構造体 | サイズ | アライメント |
|--------|--------|-------------|
| `GPUSphere` | 80 bytes | 16-byte |
| `GPUPlane` | 80 bytes | 16-byte |
| `GPUBox` | 96 bytes | 16-byte |

---

#### 5.2 Fresnel (フレネル反射)

| 項目 | 内容 |
|------|------|
| **アルゴリズム** | Schlick近似 |
| **用途** | 視線角度に応じた反射率計算 |

**実装**:
```hlsl
float FresnelSchlick(float cosTheta, float f0)
{
    return f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
}

// 誘電体: f0 = 0.04 (固定)
// 金属: f0 = baseColor (RGB)
```

---

#### 5.3 Blinn-Phong スペキュラ

| 項目 | 内容 |
|------|------|
| **用途** | 直接光スペキュラハイライト |
| **shininess** | `lerp(256.0, 8.0, roughness)` |

```hlsl
float3 H = normalize(L + V);
float NdotH = saturate(dot(normal, H));
float specTerm = pow(NdotH, shininess) * NdotL;
```

---

#### 5.4 ソフトシャドウ

| 項目 | 内容 |
|------|------|
| **アルゴリズム** | エリアライトサンプリング |
| **サンプル数** | 1-16 (設定可能) |
| **閾値** | `light.radius > 0.001` でソフトシャドウ有効 |

**ライトタイプ別サンプリング**:
- **ポイントライト**: 球面上のランダムサンプリング
- **ディレクショナルライト**: コーン角度内の方向サンプリング

---

### 6. グローバルイルミネーション技術

#### 6.1 フォトンマッピング (Caustics)

| 項目 | 内容 |
|------|------|
| **概要** | コースティクス表現用フォトンマップ |
| **最大フォトン数** | 262,144 (256K) |
| **検索半径** | 0.5 (設定可能) |
| **最大バウンス** | 8 |
| **関連ファイル** | `PhotonEmit.hlsl`, `PhotonTrace.hlsl`, `Common.hlsli` |

**フォトン構造体**:
```hlsl
struct Photon
{
    float3 position;    // ディフューズ面でのヒット位置
    float power;        // フォトンパワー
    float3 direction;   // 入射方向
    uint flags;         // 0=空, 1=有効コースティクフォトン
    float3 color;       // 色 (ライト×表面相互作用)
    float padding;
};
```

**空間ハッシュ (Spatial Hash) - O(1)ルックアップ最適化**:
```hlsl
#define PHOTON_HASH_TABLE_SIZE 65536    // 2^16 ハッシュバケット
#define MAX_PHOTONS_PER_CELL 64         // セルあたり最大フォトン数
#define PHOTON_HASH_CELL_SIZE 1.0       // デフォルトセルサイズ

struct PhotonHashCell
{
    uint count;                                     // このセルのフォトン数
    uint photonIndices[MAX_PHOTONS_PER_CELL];       // PhotonMapへのインデックス
};
```

**フォトン収集** (`GatherPhotons`):
```hlsl
float3 GatherPhotons(float3 hitPosition, float3 normal, float radius)
{
    // 空間ハッシュを使用してO(1)でフォトンを検索
    // 距離内のフォトンを収集、密度推定でコースティクス計算
    return causticColor * Scene.CausticIntensity;
}
```

---

### 7. ポストプロセス技術

#### 7.1 トーンマッピング

| 手法 | 概要 | 選択値 |
|------|------|--------|
| **Reinhard** | シンプルなグローバルオペレータ | `ToneMapOperator < 0.5` |
| **ACES Filmic** | 映画品質のフィルミックカーブ | `0.5 <= ToneMapOperator < 1.5` |
| **なし** | HDRそのまま (クランプのみ) | `ToneMapOperator >= 1.5` |

**実装**:
```hlsl
// Reinhard
float3 ReinhardToneMap(float3 color) {
    return color / (1.0 + color);
}

// ACES Filmic
float3 ACESFilm(float3 x) {
    float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}
```

---

#### 7.2 ガンマ補正

| 項目 | 内容 |
|------|------|
| **変換** | Linear → sRGB |
| **ガンマ値** | 2.4 (sRGB標準) |

```hlsl
float3 LinearToSRGB(float3 color) {
    return color < 0.0031308 
        ? 12.92 * color 
        : 1.055 * pow(color, 1.0 / 2.4) - 0.055;
}
```

---

#### 7.3 被写界深度 (Depth of Field)

| パラメータ | 説明 |
|-----------|------|
| **ApertureSize** | 絞りサイズ (0=DoF無効, 大きいほどボケ強) |
| **FocusDistance** | フォーカス面までの距離 |

**アルゴリズム**: 薄レンズモデルのシミュレーション
```hlsl
if (apertureSize > 0.001) {
    float3 focusPoint = cameraPos + rayDir * focusDistance;
    float2 diskOffset = RandomOnDisk(seed) * apertureSize;
    rayOrigin = cameraPos + cameraRight * diskOffset.x + cameraUp * diskOffset.y;
    rayDir = normalize(focusPoint - rayOrigin);
}
```

---

### 8. エンコーディング技術

#### 8.1 オクタヘドロン法線エンコーディング

| 項目 | 内容 |
|------|------|
| **概要** | 3D単位法線を2D座標に圧縮 |
| **精度** | RGBA8で十分な品質 |
| **用途** | NRD G-Buffer、法線マップ圧縮 |

**NRDパッキング関数**:
```hlsl
float4 NRD_FrontEnd_PackNormalAndRoughness(float3 normal, float roughness);
void NRD_FrontEnd_UnpackNormalAndRoughness(float4 packed, out float3 normal, out float roughness);
```

---

### 9. アプリケーション層技術

#### 9.1 WPF (Windows Presentation Foundation)

| 項目 | 内容 |
|------|------|
| **概要** | .NET UI フレームワーク |
| **用途** | ノードエディタ、プロパティパネル、レンダービュー |
| **言語** | C# + XAML |
| **関連ファイル** | `src/RayTraceVS.WPF/**` |

---

#### 9.2 C++/CLI (相互運用層)

| 項目 | 内容 |
|------|------|
| **概要** | マネージド/ネイティブブリッジ |
| **用途** | C# WPF ↔ C++ DirectX間のデータ受け渡し |
| **関連ファイル** | `src/RayTraceVS.Interop/**` |

**主要クラス**:
- `EngineWrapper` - レンダリングエンジンのマネージドラッパー
- `SceneData.h` - Interop用データ構造体

---

### 10. 技術依存関係図

```
┌─────────────────────────────────────────────────────────────────────┐
│                         WPF Application (C#)                        │
│   ┌─────────────┐  ┌──────────────┐  ┌────────────────────────┐   │
│   │NodeEditor   │  │PropertyPanel │  │RenderWindow            │   │
│   └─────────────┘  └──────────────┘  └────────────────────────┘   │
└────────────────────────────────┬────────────────────────────────────┘
                                 │ C++/CLI Interop
                                 ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    DirectX 12 Engine (C++)                          │
│   ┌───────────────────────────────────────────────────────────┐    │
│   │                    DXRPipeline                             │    │
│   │  ┌─────────────┐  ┌─────────────┐  ┌─────────────────┐   │    │
│   │  │DXContext    │  │AccelStruct  │  │ShaderCache      │   │    │
│   │  │(D3D12)      │  │(BLAS/TLAS)  │  │(DXC)            │   │    │
│   │  └─────────────┘  └─────────────┘  └─────────────────┘   │    │
│   └───────────────────────────────────────────────────────────┘    │
│   ┌───────────────────────────────────────────────────────────┐    │
│   │                    NRDDenoiser                             │    │
│   │  ┌─────────────────────┐  ┌─────────────────────────┐    │    │
│   │  │REBLUR               │  │SIGMA                     │    │    │
│   │  │(Diffuse/Specular)   │  │(Shadow)                  │    │    │
│   │  └─────────────────────┘  └─────────────────────────┘    │    │
│   └───────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────────┘
                                 │ DispatchRays / Dispatch
                                 ▼
┌─────────────────────────────────────────────────────────────────────┐
│                      HLSL Shaders (GPU)                             │
│   ┌─────────────┐  ┌──────────────┐  ┌─────────────────────────┐  │
│   │RayGen       │  │Intersection  │  │ClosestHit (Diffuse/Metal)│  │
│   └─────────────┘  └──────────────┘  └─────────────────────────┘  │
│   ┌─────────────┐  ┌──────────────┐  ┌─────────────────────────┐  │
│   │PhotonEmit   │  │PhotonTrace   │  │Composite                 │  │
│   └─────────────┘  └──────────────┘  └─────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
```

---

### 11. バージョン・要件

| コンポーネント | 最小要件 | 推奨 |
|---------------|---------|------|
| **Windows** | Windows 10 1903+ | Windows 11 |
| **DirectX** | DirectX 12 FL 12_1 | FL 12_2 |
| **GPU** | DXR 1.0対応 | RTX 30/40シリーズ |
| **.NET** | .NET 6.0+ | .NET 8.0 |
| **Visual Studio** | 2022 | 2022 最新 |
| **Windows SDK** | 10.0.19041.0+ | 10.0.22621.0 |

---

## 条件分岐・例外処理一覧

このセクションでは、レンダリングパイプライン全体の条件分岐と例外処理を網羅的に記載します。
設計レビューの際にはこのリストを参照して、各分岐の妥当性を検証してください。

### 凡例
- **[BRANCH]** 通常の条件分岐
- **[FALLBACK]** フォールバック処理
- **[BYPASS]** 特定条件でのバイパス処理
- **[THRESHOLD]** 閾値による分岐
- **[EARLY-OUT]** 早期リターン

---

### 1. C++ エンジン層 (DXRPipeline.cpp)

#### 1.1 パイプライン選択
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CPP-001 | `dxContext->IsDXRSupported()` | DXR有効 → `CreateDXRPipeline()` | [BRANCH] |
| CPP-002 | `!dxContext->IsDXRSupported()` | DXRなし → Compute Shaderフォールバック | [FALLBACK] |
| CPP-003 | `dxrPipelineReady == true` | `RenderWithDXR()` 使用 | [BRANCH] |
| CPP-004 | `dxrPipelineReady == false` | `RenderWithComputeShader()` 使用 | [FALLBACK] |

#### 1.2 デノイザー初期化
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CPP-005 | `denoiserEnabled && !denoiser` | デノイザー初期化を試みる | [BRANCH] |
| CPP-006 | `InitializeDenoiser()` 失敗 | `denoiserEnabled = false` で続行 | [FALLBACK] |
| CPP-007 | `denoiserEnabled && denoiser && denoiser->IsReady()` | デノイズ処理実行 | [BRANCH] |

#### 1.3 アクセラレーション構造
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CPP-008 | `needsAccelerationStructureRebuild \|\| scene != lastScene` | BLAS/TLAS再構築 | [BRANCH] |
| CPP-009 | `BuildAccelerationStructures()` 失敗 | Computeシェーダーにフォールバック | [FALLBACK] |

#### 1.4 コースティクス (フォトンマッピング)
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CPP-010 | `causticsEnabled && photonStateObject` | `EmitPhotons()` 実行 | [BRANCH] |
| CPP-011 | コースティクス無効時 | `PhotonMapSize = 0` 設定 | [FALLBACK] |

#### 1.5 バッファ・リソース
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CPP-012 | `!sphereBuffer` | バッファ作成 | [BRANCH] |
| CPP-013 | `spheres.empty()` | 球データアップロードスキップ | [EARLY-OUT] |
| CPP-014 | `Scene.NumLights == 0` | フォールバックライト使用 | [FALLBACK] |

---

### 2. RayGen.hlsl (レイ生成シェーダー)

#### 2.1 サンプリング
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| RG-001 | `sampleCount > 1` | ピクセル内ランダムオフセット | [BRANCH] |
| RG-002 | `sampleCount == 1` | ピクセル中心 (0.5, 0.5) | [FALLBACK] |

#### 2.2 被写界深度 (DoF)
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| RG-003 | `apertureSize > 0.001` | DoF有効、レンズシミュレーション | [THRESHOLD] | 0.001 |
| RG-004 | `apertureSize <= 0.001` | DoF無効、ピンホールカメラ | [FALLBACK] | - |

#### 2.3 ヒット判定
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| RG-005 | `payload.hit && !anyHit` | 最初のヒットからNRDデータ取得 | [BRANCH] |
| RG-006 | `!anyHit` (ループ後) | デフォルト法線/アルベド使用 | [FALLBACK] |

#### 2.4 ViewZ計算
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| RG-007 | `anyHit` | ヒット位置から正のViewZ計算 | [BRANCH] |
| RG-008 | `!anyHit` | `viewZ = 10000.0` (遠方) | [FALLBACK] |

---

### 3. Intersection.hlsl (交差判定シェーダー)

#### 3.1 オブジェクトタイプ判定
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| INT-001 | `primitiveIndex < sphereCount` | 球の交差判定 (2次方程式) | [BRANCH] |
| INT-002 | `primitiveIndex < sphereCount + planeCount` | 平面の交差判定 | [BRANCH] |
| INT-003 | `primitiveIndex < sphereCount + planeCount + boxCount` | ボックスの交差判定 (スラブ法) | [BRANCH] |

#### 3.2 球の交差判定
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| INT-004 | `discriminant >= 0.0` | ヒットあり、ReportHit | [BRANCH] |
| INT-005 | `t < RayTMin()` | t1からt2に切り替え | [FALLBACK] |

#### 3.3 平面の交差判定
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| INT-006 | `abs(denom) > 0.0001` | 交差計算実行 | [THRESHOLD] | 0.0001 |

#### 3.4 ボックスの交差判定
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| INT-007 | `tNear <= tFar && tFar >= RayTMin()` | ヒットあり | [BRANCH] |
| INT-008 | `t < RayTMin()` | 内部からの交差 (tFar使用) | [BRANCH] |

#### 3.5 ボックス法線計算
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| INT-009 | `absNormalized.x >= maxComponent - FACE_BIAS` | X面法線 | [THRESHOLD] | 0.001 |
| INT-010 | `absNormalized.y >= maxComponent - FACE_BIAS` | Y面法線 | [THRESHOLD] | 0.001 |
| INT-011 | それ以外 | Z面法線 | [FALLBACK] | - |
| INT-012 | `length(normal) < 0.5` | 中心からの外向き方向にフォールバック | [FALLBACK] | 0.5 |

---

### 4. ClosestHit_Diffuse.hlsl (ディフューズマテリアル)

#### 4.1 マテリアル取得
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CD-001 | `objectType == OBJECT_TYPE_SPHERE` | 球マテリアル取得 | [BRANCH] |
| CD-002 | `objectType == OBJECT_TYPE_PLANE` | 平面マテリアル+チェッカーパターン | [BRANCH] |
| CD-003 | それ以外 | ボックスマテリアル取得 | [FALLBACK] |

#### 4.2 平面チェッカーパターン
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CD-004 | `hitDistance < fadeStart` | フルコントラストチェッカー | [THRESHOLD] | 10.0 |
| CD-005 | `hitDistance > fadeEnd` | 低コントラスト (0.2) | [THRESHOLD] | 100.0 |
| CD-006 | 中間距離 | 線形補間コントラスト | [BRANCH] | - |

#### 4.3 フレネル反射
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CD-007 | `payload.depth < MAX_RECURSION_DEPTH` | フレネル計算実行 | [THRESHOLD] | 5 |
| CD-008 | `fresnel > 0.05` | 反射レイ発射 | [THRESHOLD] | 0.05 |

#### 4.4 NRD出力条件
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CD-009 | `payload.depth == 0` | NRD G-Bufferデータ出力 | [BRANCH] |
| CD-010 | `payload.depth > 0` | NRDデータ出力スキップ | [EARLY-OUT] |

#### 4.5 スペキュラヒット距離
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CD-011 | `fresnel > 0.01 && roughness < 0.9 && reflectPayload.hit` | 反射レイのヒット距離使用 | [THRESHOLD] | fresnel>0.01, roughness<0.9 |
| CD-012 | それ以外 | プライマリレイのヒット距離使用 | [FALLBACK] | - |

#### 4.6 環境反射
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CD-013 | `fresnel > 0.01 && roughness < 0.9` | 反射スペキュラ計算 | [THRESHOLD] | fresnel>0.01, roughness<0.9 |

---

### 4.5 ClosestHit.hlsl (統合マテリアルシェーダー)

#### 4.5.1 再帰深度制御
| ID | 条件 | 処理 | タイプ | 備考 |
|----|------|------|--------|------|
| CH-000 | `payload.depth >= SHADOW_RAY_DEPTH (100)` | シャドウレイ処理、即リターン | [EARLY-OUT] | シャドウレイ判定 |
| CH-001 | `payload.depth >= Scene.MaxBounces` | 最大深度到達、空+マテリアルカラーで近似 | [FALLBACK] | デフォルト5 |

#### 4.5.2 マテリアルタイプ判定
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CH-002 | `objectType == OBJECT_TYPE_SPHERE` | 球マテリアル取得（Emission含む） | [BRANCH] |
| CH-003 | `objectType == OBJECT_TYPE_PLANE` | 平面マテリアル+チェッカーパターン | [BRANCH] |
| CH-004 | `objectType == OBJECT_TYPE_BOX` | ボックスマテリアル取得 | [BRANCH] |

#### 4.5.3 マテリアル分岐
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CH-005 | `transmission > 0.01` | ガラスマテリアル処理 | [THRESHOLD] | 0.01 |
| CH-006 | `metallic > 0.5` | 金属マテリアル処理 | [THRESHOLD] | 0.5 |
| CH-007 | それ以外 | ディフューズマテリアル処理 | [FALLBACK] | - |

#### 4.5.4 ラフネス摂動 (GGX-like)
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CH-008 | `roughness < 0.01` | 完全鏡面反射（摂動なし） | [THRESHOLD] | 0.01 |
| CH-009 | `roughness >= 0.01` | GGX近似の摂動適用 | [BRANCH] | - |

```hlsl
// GGX-like roughness perturbation
float3 PerturbReflection(float3 reflectDir, float3 normal, float roughness, float2 seed)
{
    if (roughness < 0.01) return reflectDir;
    
    float angle = r1 * 6.28318;
    float radius = roughness * roughness * r2;  // perceptually linear
    float3 perturbed = normalize(reflectDir + offset);
    
    // Ensure hemisphere
    if (dot(perturbed, normal) < 0.0)
        perturbed = reflect(perturbed, normal);
    return perturbed;
}
```

#### 4.5.5 Emission（発光）処理
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CH-010 | `emission != (0,0,0)` | 最終カラーにEmission加算 | [BRANCH] |
| CH-011 | NRD出力時 | `diffuseRadiance += emission` | [BRANCH] |

#### 4.5.6 最終カラー合成
```hlsl
float3 finalColor = ambient 
                  + directDiffuse * directWeight 
                  + directSpecular 
                  + reflectColor * reflectionWeight
                  + emission;  // 発光マテリアル
```

---

### 5. ClosestHit_Metal.hlsl (金属マテリアル・レガシー)

#### 5.1 ラフネス摂動
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CM-001 | `roughness < 0.01` | 完全鏡面反射 (摂動なし) | [THRESHOLD] | 0.01 |
| CM-002 | `roughness >= 0.01` | ラフネスに基づく摂動 | [BRANCH] | - |

#### 5.2 反射レイ深度
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CM-003 | `payload.depth < MAX_RECURSION_DEPTH` | 反射レイ発射 | [THRESHOLD] |
| CM-004 | `payload.depth >= MAX_RECURSION_DEPTH` | 空の色でフォールバック | [FALLBACK] |

#### 5.3 反射レイミス
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CM-005 | `reflectPayload.hit` | 反射先のカラー使用 | [BRANCH] |
| CM-006 | `!reflectPayload.hit` | 空の色使用 (`GetSkyColor`) | [FALLBACK] |

#### 5.4 粗い金属のブレンド
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CM-007 | `roughness > 0.1` | ディフューズとブレンド | [THRESHOLD] | 0.1 |

#### 5.5 最大深度時の近似反射
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CM-008 | `payload.depth >= MAX_RECURSION_DEPTH` | 空の色+ディフューズで近似 | [FALLBACK] |

---

### 6. Common.hlsli (共通ユーティリティ)

#### 6.1 コースティクス計算
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| COM-001 | `Scene.PhotonMapSize == 0` | コースティクス計算スキップ | [EARLY-OUT] |

#### 6.2 フォトン収集
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| COM-002 | `p.flags == 0` | 無効フォトンスキップ | [EARLY-OUT] |
| COM-003 | `distSq < radiusSq` | 距離内フォトンのみ収集 | [THRESHOLD] |
| COM-004 | `dotN > 0.0` | 同一面側フォトンのみ | [BRANCH] |

#### 6.3 ソフトシャドウ計算
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| COM-005 | `light.type == LIGHT_TYPE_AMBIENT` | シャドウなし (visibility=1.0) | [EARLY-OUT] | - |
| COM-006 | `light.type == LIGHT_TYPE_DIRECTIONAL` | 方向性ライトシャドウ | [BRANCH] | - |
| COM-007 | `light.type == LIGHT_TYPE_POINT` | ポイントライトシャドウ | [BRANCH] | - |
| COM-008 | `light.radius <= 0.001` | ハードシャドウ (単一レイ) | [THRESHOLD] | 0.001 |
| COM-009 | `light.radius > 0.001` | ソフトシャドウ (複数サンプル) | [BRANCH] | - |

#### 6.4 シャドウサンプル
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| COM-010 | `dot(sampleDir, normal) > 0.0` | サーフェス上のサンプルのみ | [BRANCH] |

#### 6.5 プライマリライト選択 (SIGMA用)
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| COM-011 | `Scene.NumLights > 0` | 最も影響の強いライトを選択 | [BRANCH] |
| COM-012 | `Scene.NumLights == 0` | フォールバックライト使用 | [FALLBACK] |
| COM-013 | `light.type == LIGHT_TYPE_AMBIENT` | Ambientライトスキップ | [EARLY-OUT] |
| COM-014 | `ndotl <= 0.0` | 背面ライトスキップ | [EARLY-OUT] |

---

### 7. Composite.hlsl (最終合成シェーダー)

#### 7.1 デバッグモード
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CMP-001 | `DebugMode == 2` | 入力シャドウ可視化 | [BRANCH] |
| CMP-002 | `DebugMode == 3` | デノイズ後シャドウ可視化 | [BRANCH] |
| CMP-003 | `DebugMode == 4` | 分割画面比較 | [BRANCH] |
| CMP-004 | `DebugMode == 5` | マゼンタ (サニティチェック) | [BRANCH] |
| CMP-005 | `DebugMode == 6` | ペナンブラ可視化 | [BRANCH] |
| CMP-006 | `DebugMode > 0` | デバッグタイル表示 | [BRANCH] |

#### 7.2 ミラー/ガラスバイパス ★重要★
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| **CMP-007** | `roughness < mirrorThreshold && viewZ > 0.0` | **NRDバイパス、RawSpecularBackup使用** | **[BYPASS]** | **0.05** |
| CMP-008 | `roughness >= mirrorThreshold` | 通常のNRDパス | [BRANCH] | - |

#### 7.3 シャドウ適用
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| CMP-009 | `UseDenoisedShadow > 0` | SIGMAデノイズシャドウ使用 | [BRANCH] |
| CMP-010 | `UseDenoisedShadow == 0` | シャドウなし | [FALLBACK] |

#### 7.4 トーンマッピング
| ID | 条件 | 処理 | タイプ | 閾値 |
|----|------|------|--------|------|
| CMP-011 | `ToneMapOperator < 0.5` | Reinhard | [THRESHOLD] | 0.5 |
| CMP-012 | `ToneMapOperator < 1.5` | ACES Filmic | [THRESHOLD] | 1.5 |
| CMP-013 | `ToneMapOperator >= 1.5` | トーンマッピングなし | [FALLBACK] | - |

---

### 8. ライティング計算 (ClosestHit_Diffuse.hlsl)

#### 8.1 ライトタイプ分岐
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| LT-001 | `light.type == LIGHT_TYPE_AMBIENT` | アンビエント加算のみ | [BRANCH] |
| LT-002 | `light.type == LIGHT_TYPE_DIRECTIONAL` | 方向性ライト計算 | [BRANCH] |
| LT-003 | `light.type == LIGHT_TYPE_POINT` | ポイントライト計算 (減衰あり) | [BRANCH] |

#### 8.2 シャドウ可視性
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| LT-004 | `shadow.visibility > 0.0` | ライティング計算実行 | [BRANCH] |
| LT-005 | `shadow.visibility == 0.0` | 完全シャドウ、スキップ | [EARLY-OUT] |

#### 8.3 フォールバックライト
| ID | 条件 | 処理 | タイプ |
|----|------|------|--------|
| LT-006 | `Scene.NumLights == 0` | シーン定数のフォールバックライト使用 | [FALLBACK] |

---

### 9. 潜在的な設計上の問題点

#### 9.1 要検討事項

| ID | 問題 | 現状 | 推奨 |
|----|------|------|------|
| ISSUE-001 | ミラーバイパス閾値 | `roughness < 0.05` | 調整可能にするか検討 |
| ~~ISSUE-002~~ | ~~MAX_RECURSION_DEPTH~~ | ~~固定値5~~ | ✅ **解決済み**: Scene.MaxBouncesで動的設定可能 |
| ISSUE-003 | フォールバックライト | 常に使用可能 | 明示的なライトなし警告 |
| ISSUE-004 | チェッカーパターン | 平面のみ | オブジェクトごとにUV対応 |
| ISSUE-005 | ソフトシャドウサンプル数 | `clamp(1, 16)` | 品質設定で調整可能に |

#### 9.2 一貫性チェック

| チェック項目 | 状態 | 備考 |
|-------------|------|------|
| NRD出力は`depth==0`のみ | ✅ 一貫 | Diffuse, Metal, 統合シェーダー全てで確認 |
| ミラーバイパス閾値統一 | ✅ 一貫 | Composite.hlslで0.05 |
| フォールバックライト条件 | ✅ 一貫 | `NumLights==0`で発動 |
| 空の色取得 | ✅ 一貫 | `GetSkyColor()` 使用 |
| hitDistance設定 | ✅ 一貫 | 統合シェーダーで全レイに対応 |
| MaxBounces対応 | ✅ 一貫 | `Scene.MaxBounces`（デフォルト5） |
| Emission対応 | ✅ 一貫 | 全オブジェクト（Sphere/Plane/Box）で対応 |
| 空間ハッシュ | ✅ 実装済み | フォトンマッピングO(1)ルックアップ |

---

## 全体アーキテクチャ

```
WPF UI Layer (C#)
├── RenderWindow
├── SceneEvaluator
└── RenderService
        │
        ▼
C++/CLI Interop Layer
└── EngineWrapper
        │
        ▼
DirectX 12 Native Layer (C++)
├── DXContext
├── DXRPipeline
├── AccelerationStructure
├── RenderTarget
└── NRDDenoiser
        │
        ▼
HLSL Shaders
├── RayGen.hlsl
├── Intersection.hlsl
├── ClosestHit_*.hlsl
└── Composite.hlsl
```

---

## Phase 1: シーン評価 (WPF C# Layer)

### 1.1 RenderWindow.xaml.cs

- **ファイル**: `src/RayTraceVS.WPF/Views/RenderWindow.xaml.cs`
- **解像度**: 固定 1920x1080
- **テンポラルデノイズ**: 最低5パス連続描画で安定化

```csharp
// レンダリング実行フロー
var (spheres, planes, boxes, camera, lights, ...) = sceneEvaluator.EvaluateScene(nodeGraph);
renderService.UpdateScene(spheres, planes, boxes, camera, lights, ...);
renderService.Render();
var pixelData = renderService.GetPixelData(); // RGBA -> BGRA変換後に表示
```

### 1.2 SceneEvaluator.cs

- **ファイル**: `src/RayTraceVS.WPF/Services/SceneEvaluator.cs`
- ノードグラフからシーンデータを生成
- SceneNodeが存在する場合: グラフ評価（増分評価対応）
- SceneNodeがない場合: フォールバックモードで全ノードを収集

**出力データ構造**:

- `InteropSphereData[]` - 球データ
- `InteropPlaneData[]` - 平面データ
- `InteropBoxData[]` - ボックスデータ
- `InteropCameraData` - カメラ設定
- `InteropLightData[]` - ライトデータ
- レンダリングパラメータ (SamplesPerPixel, MaxBounces, Exposure, ToneMapOperator, etc.)

---

## Phase 2: C++/CLI Interop Layer

### 2.1 EngineWrapper

- **ファイル**: `src/RayTraceVS.Interop/EngineWrapper.h`
- C# マネージドコードと C++ ネイティブコードの橋渡し
- データのマーシャリング（マネージド配列 ↔ ネイティブポインタ）

---

## Phase 3: DirectX 12 DXR Engine (C++ Native)

### 3.1 初期化フロー (DXRPipeline::Initialize)

```
InitializeShaderPath
       │
       ▼
ShaderCache初期化
       │
       ▼
CreateComputePipeline
       │
       ▼
   DXR対応？ ──Yes──► CreateDXRPipeline
       │
      No
       │
       ▼
  Compute Fallback
```

### 3.2 メインレンダー関数 (DXRPipeline::Render)

```cpp
void DXRPipeline::Render(RenderTarget* renderTarget, Scene* scene)
{
    if (dxrPipelineReady)
        RenderWithDXR(renderTarget, scene);
    else
        RenderWithComputeShader(renderTarget, scene);
}
```

### 3.3 DXRレンダリングパス (RenderWithDXR)

#### Pass 1: フォトン放出 (Caustics用)

- `EmitPhotons()` でフォトンマップ生成
- 最大262,144フォトン
- コースティクス表現に使用

#### Pass 2: メインレイトレーシング

1. **シーンデータ更新**: `UpdateSceneData()`
2. **アクセラレーション構造構築**: BLAS/TLAS
3. **ディスクリプタ更新**: G-Buffer UAV, SRV設定
4. **DispatchRays実行**: レイトレーシング本体

#### Pass 3: デノイジング (NRD)

- REBLUR: Diffuse/Specular デノイズ
- SIGMA: シャドウデノイズ
- `ApplyDenoising()` → `CompositeOutput()`

---

## Phase 4: シェーダーパイプライン詳細

### 4.1 共通定義 (Common.hlsli)

**定数**:

```hlsl
#define MAX_RECURSION_DEPTH 5           // デフォルト再帰深度（Scene.MaxBouncesで上書き可能）
#define MAX_PHOTONS 262144              // 256K フォトン
#define PHOTON_HASH_TABLE_SIZE 65536    // 空間ハッシュテーブルサイズ
#define MAX_PHOTONS_PER_CELL 64         // セルあたり最大フォトン数

#define OBJECT_TYPE_SPHERE 0
#define OBJECT_TYPE_PLANE 1
#define OBJECT_TYPE_BOX 2

#define LIGHT_TYPE_AMBIENT 0
#define LIGHT_TYPE_POINT 1
#define LIGHT_TYPE_DIRECTIONAL 2
```

**主要構造体**:

- `RayPayload` - NRDデノイザー用フィールド含む拡張ペイロード
- `SceneConstantBuffer` - カメラ、ライト、レンダリングパラメータ
- `SphereData/PlaneData/BoxData` - PBRマテリアル付きジオメトリ
- `LightData` - ライト情報（ソフトシャドウ対応）
- `Photon` - フォトンマッピング用

**リソースバインディング**:

| レジスタ | 内容 |
|---------|------|
| t0 | SceneBVH (アクセラレーション構造) |
| t1-t4 | Spheres, Planes, Boxes, Lights |
| u0 | RenderTarget |
| u1-u2 | PhotonMap, PhotonCounter |
| u3-u10 | NRD G-Buffer (Diffuse, Specular, Normal, ViewZ, Motion, Albedo, Shadow) |

### 4.2 RayGen.hlsl - レイ生成シェーダー

**機能**:

- カメラからプライマリレイ発射
- マルチサンプリング（アンチエイリアシング）
- 被写界深度 (DoF) シミュレーション
- NRD G-Buffer出力

```hlsl
[shader("raygeneration")]
void RayGen()
{
    // 1. ピクセル座標取得
    uint2 launchIndex = DispatchRaysIndex().xy;
    
    // 2. サンプルループ
    for (uint s = 0; s < sampleCount; s++)
    {
        // 2a. AAオフセット計算
        float2 offset = RandomInPixel(launchIndex, s);
        
        // 2b. NDC座標からレイ方向計算
        float3 rayDir = cameraForward + cameraRight * ndc.x + cameraUp * ndc.y;
        
        // 2c. DoF (アパーチャシミュレーション)
        if (dofEnabled) { /* フォーカス平面計算 */ }
        
        // 2d. TraceRay実行
        TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, ray, payload);
        
        // 2e. 結果蓄積
        accumulatedColor += payload.color;
        accumulatedDiffuse += payload.diffuseRadiance;
        accumulatedSpecular += payload.specularRadiance;
    }
    
    // 3. 出力
    RenderTarget[launchIndex] = float4(finalColor, 1.0);
    GBuffer_DiffuseRadianceHitDist[launchIndex] = ...;
    GBuffer_SpecularRadianceHitDist[launchIndex] = ...;
    GBuffer_NormalRoughness[launchIndex] = NRD_FrontEnd_PackNormalAndRoughness(...);
    GBuffer_ViewZ[launchIndex] = ...;
    GBuffer_MotionVectors[launchIndex] = ...;
    GBuffer_Albedo[launchIndex] = ...;
    GBuffer_ShadowData[launchIndex] = ...;
}
```

### 4.3 Intersection.hlsl - 交差判定シェーダー

**サポートジオメトリ**:

- **球**: 2次方程式による解析的交差
- **平面**: レイ-平面交差
- **ボックス (AABB)**: スラブ法

```hlsl
[shader("intersection")]
void SphereIntersection()
{
    if (primitiveIndex < sphereCount)
    {
        // 球の交差判定
        float discriminant = b*b - 4*a*c;
        if (discriminant >= 0) ReportHit(t, 0, attribs);
    }
    else if (primitiveIndex < sphereCount + planeCount)
    {
        // 平面の交差判定
        float t = dot(p0, n) / denom;
        if (t valid) ReportHit(t, 0, attribs);
    }
    else
    {
        // AABB交差判定 (スラブ法)
        ReportHit(t, 0, attribs);
    }
}
```

### 4.4 ClosestHit シェーダー群

#### ClosestHit_Diffuse.hlsl

- **用途**: 非金属、不透明マテリアル
- **機能**:
  - ソフトシャドウ計算
  - コースティクス (フォトンマップ参照)
  - フレネル反射 (誘電体)
  - NRDデータ出力 (Radiance分離: アルベドなし)

```hlsl
[shader("closesthit")]
void ClosestHit_Diffuse(inout RayPayload payload, in ProceduralAttributes attribs)
{
    // 1. ライティング計算 (アルベドなしのRadiance)
    DiffuseLightingResult lighting = CalculateDiffuseLightingWithCaustics(
        hitPosition, normal, float3(1,1,1), roughness, seed);
    
    // 2. フレネル反射
    if (fresnel > 0.05 && payload.depth < MAX_RECURSION_DEPTH)
    {
        TraceRay(SceneBVH, ..., reflectRay, reflectPayload);
        specularColor = reflectPayload.color * fresnel * (1-roughness);
    }
    
    // 3. NRD出力 (プライマリレイのみ)
    payload.diffuseRadiance = diffuseLightingNoShadow;  // アルベドなし
    payload.specularRadiance = directSpecularRadiance + reflectionSpecularRadiance;
    payload.albedo = color.rgb;
}
```

#### ClosestHit_Metal.hlsl

- **用途**: 金属マテリアル
- **機能**:
  - 金属フレネル (F0 = ベースカラー)
  - ラフネスによる反射方向の摂動
  - 粗い金属のディフューズブレンド

#### ソフトシャドウ計算 (Common.hlsli)

```hlsl
SoftShadowResult CalculateSoftShadow(float3 hitPos, float3 normal, LightData light, inout uint seed)
{
    // ポイントライト: 球面上のサンプリング
    // ディレクショナルライト: コーン角度内の方向サンプリング
    // 複数サンプルの平均でペナンブラ計算
}
```

### 4.5 Composite.hlsl - 最終合成シェーダー

**機能**:

- NRDデノイズ出力の合成
- アルベド乗算 (デノイズ後)
- SIGMA シャドウ適用
- トーンマッピング (Reinhard / ACES)
- ガンマ補正

```hlsl
[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    // 1. デノイズ済みRadiance取得
    float3 denoisedRadiance = DenoisedDiffuse.Sample(uv).rgb;
    float3 denoisedSpecular = DenoisedSpecular.Sample(uv).rgb;
    float3 albedo = AlbedoTexture.Sample(uv).rgb;
    
    // 2. パーフェクトミラーバイパス (roughness < 0.05)
    if (roughness < mirrorThreshold)
    {
        // NRDを通さず生のスペキュラを使用 (ブレ防止)
        OutputTexture[pixel] = LinearToSRGB(saturate(RawSpecularBackup.Sample(uv).rgb));
        return;
    }
    
    // 3. シーンカラー合成
    sceneColor = denoisedRadiance * albedo + denoisedSpecular;
    
    // 4. シャドウ適用
    float transmittance = SIGMA_BackEnd_UnpackShadow(DenoisedShadow.Sample(uv));
    finalRadiance = sceneColor * transmittance * ExposureValue;
    
    // 5. トーンマッピング + ガンマ補正
    finalColor = LinearToSRGB(ACESFilm(finalRadiance));
}
```

---

## Phase 5: NRDデノイザー統合

### 5.1 G-Buffer構成

| バッファ | フォーマット | 内容 |
|---------|-------------|------|
| DiffuseRadianceHitDist | RGBA16F | RGB=ディフューズRadiance, A=ヒット距離 |
| SpecularRadianceHitDist | RGBA16F | RGB=スペキュラRadiance, A=ヒット距離 |
| NormalRoughness | RGBA8 | XYZ=法線(Oct), W=ラフネス |
| ViewZ | R32F | リニア深度 |
| MotionVectors | RG16F | スクリーン空間モーション |
| Albedo | RGBA8 | アルベドカラー |
| ShadowData | RG16F | シャドウ可視性/ペナンブラ |

### 5.2 デノイザーワークフロー

1. **REBLUR**: Diffuse/Specular テンポラルデノイズ
2. **SIGMA**: シャドウデノイズ
3. **Composite**: 最終合成 (アルベド乗算はここで)

---

## データフロー図

```
RenderWindow
    │
    ▼ EvaluateScene(nodeGraph)
SceneEvaluator
    │
    ▼ (spheres, planes, boxes, camera, lights)
EngineWrapper
    │
    ▼ UpdateScene + Render
DXRPipeline
    │
    ├──► BuildAccelerationStructures
    │
    ▼ DispatchRays
GPU/Shaders (RayGen → Intersection → ClosestHit)
    │
    ▼ G-Buffer出力
NRDDenoiser
    │
    ▼ Denoise (REBLUR + SIGMA)
Composite.hlsl
    │
    ▼ Final RGBA
RenderTarget
    │
    ▼ GetPixelData
RenderWindow (RGBA→BGRA変換 + 表示)
```

---

## パフォーマンス最適化ポイント

1. **シェーダーキャッシュ**: `.cso`ファイルでコンパイル済みシェーダーを再利用
2. **SoA (Structure of Arrays)**: DXR用に最適化されたメモリレイアウト
3. **増分評価**: ノードグラフのDirtyフラグで不要な再評価を回避
4. **テンポラルデノイズ**: 5パス連続描画で安定化
5. **パーフェクトミラーバイパス**: 低ラフネス面はNRDをスキップ
