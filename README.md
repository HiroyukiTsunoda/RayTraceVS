# RayTraceVS - DirectX12 DXR Visual Raytracing

WPFベースのビジュアルスクリプティングUIと、DirectX12 DXRを使用したGPUレイトレーシングエンジンを組み合わせたWindowsアプリケーションです。

## 概要

- **プロジェクト名**: RayTraceVS
- **開発環境**: Visual Studio 2022以降
- **言語**: C# 12 (.NET 8) / C++ 20
- **グラフィックスAPI**: DirectX 12 + DXR (DirectX Raytracing) 1.1

## 主な機能

- UEのBlueprintライクなノードエディタでシーンを構築
- リアルタイムGPUレイトレーシング (DirectX 12 DXR)
- 球、平面、ボックスのプロシージャルジオメトリ
- 複数光源のサポート（ポイントライト、ディレクショナルライト、アンビエントライト）
- 物理ベースマテリアル（PBR: Metallic/Roughness/Transmission/IOR/Emission）
- 発光マテリアル（Emission）による自己発光オブジェクト
- 鏡面反射（Fresnelシュリック近似）
- ソフトシャドウ（エリアライトサンプリング）
- フォトンマッピングによるコースティクス
- NRDデノイザー統合（REBLUR + SIGMA）
- トーンマッピング（Reinhard / ACES Filmic）
- 被写界深度（DoF）シミュレーション

## プロジェクト構造

```
RayTraceVS/
├── RayTraceVS.sln                          # Visual Studioソリューション
├── src/
│   ├── RayTraceVS.WPF/                     # C# WPFプロジェクト
│   │   ├── ViewModels/                     # MVVM ViewModels
│   │   ├── Views/                          # WPF Views
│   │   │   └── Handlers/                   # 入力ハンドラー（接続/ドラッグ/パン等）
│   │   ├── Converters/                     # 値コンバーター
│   │   ├── Commands/                       # コマンドシステム
│   │   ├── Utils/                          # ユーティリティ
│   │   ├── Models/                         # データモデル
│   │   │   ├── Nodes/                      # ノードタイプ（オブジェクト/マテリアル/数学等）
│   │   │   ├── Data/                       # データ型定義
│   │   │   └── Serialization/              # シリアライゼーション
│   │   └── Services/                       # サービス層
│   │       └── Interfaces/                 # サービスインターフェース
│   │
│   ├── RayTraceVS.DXEngine/                # C++ DirectX12プロジェクト
│   │   ├── DXContext.h/.cpp                # DX12初期化
│   │   ├── DXRPipeline.h/.cpp              # DXRパイプライン
│   │   ├── AccelerationStructure.h/.cpp    # BLAS/TLAS構築
│   │   ├── RenderTarget.h/.cpp             # レンダーターゲット管理
│   │   ├── ShaderCache.h/.cpp              # シェーダーキャッシュ（DXC）
│   │   ├── NativeBridge.h/.cpp             # ネイティブブリッジ
│   │   ├── Denoiser/                       # NRDデノイザー（REBLUR + SIGMA）
│   │   └── Scene/Objects/                  # シーンオブジェクト（Sphere/Plane/Box）
│   │
│   ├── RayTraceVS.Interop/                 # C++/CLI相互運用プロジェクト
│   │   ├── EngineWrapper.h/.cpp            # エンジンラッパー
│   │   ├── SceneData.h                     # データ構造
│   │   └── Marshalling.h/.cpp              # データ変換
│   │
│   └── Shader/                             # シェーダーソース（共有）
│       ├── Common.hlsli                    # 共通定義（空間ハッシュ対応）
│       ├── NRDEncoding.hlsli               # NRDエンコーディング
│       ├── RayGen.hlsl                     # レイ生成 + G-Buffer出力
│       ├── ClosestHit.hlsl                 # 統合マテリアル処理
│       ├── Intersection.hlsl               # 交差判定（球/平面/ボックス）
│       ├── Composite.hlsl                  # 最終合成 + トーンマッピング
│       ├── PhotonEmit.hlsl                 # フォトン放出（コースティクス）
│       └── Cache/                          # コンパイル済みシェーダーキャッシュ
│
├── sample_scene.rtvs                       # サンプルシーンファイル
└── ドキュメント
    ├── BUILD_GUIDE.md                      # ビルドガイド
    ├── USAGE.md                            # 使い方ガイド
    ├── NODE_EDITOR_GUIDE.md                # ノードエディタガイド
    ├── RENDERING_IMPLEMENTATION.md         # レンダリング実装詳細
    └── IMPLEMENTATION_SUMMARY.md           # 実装サマリー
```

## 必須要件

### ハードウェア
- **GPU**: ハードウェアレイトレーシング対応GPU（**必須**）
  - **NVIDIA**: RTX 20シリーズ以降（RTX 2060, RTX 3060, RTX 4060等）
  - **AMD**: RX 6000シリーズ以降（RX 6600, RX 6700 XT, RX 7600等）
  - **Intel**: Arc Aシリーズ（A750, A770等）
  
  > ⚠️ **注意**: GTX 10シリーズやRX 5000シリーズ以前のGPUはDXRをソフトウェアエミュレーションで実行するため、実用的なパフォーマンスは得られません。
  
  > 💡 **NRDデノイザー**: NVIDIA RTX Kitを使用しているため、NVIDIA GPUで最適なパフォーマンスが得られます。AMD/Intel GPUでも動作しますが、一部最適化が効かない場合があります。

- **RAM**: 8GB以上（16GB推奨）
- **VRAM**: 6GB以上推奨（4K解像度では8GB以上推奨）
- **ストレージ**: 5GB以上の空き容量

### ソフトウェア
- **OS**: Windows 10 2004（ビルド19041）以降、または Windows 11
- **Visual Studio**: Visual Studio 2022 (v17.0) 以降
  - 必須ワークロード: .NET デスクトップ開発、C++によるデスクトップ開発
- **.NET SDK**: .NET 8.0 SDK
- **Windows SDK**: 最新版（10.0.22621.0以降推奨）
- **グラフィックスドライバ**: NVIDIA Driver 450.82以降 / AMD Driver 20.11.2以降

## ビルド手順

1. Visual Studio 2022以降を起動
2. `RayTraceVS.sln` を開く
3. ソリューション構成: **Release** (または Debug)
4. プラットフォーム: **x64**（必須）
5. ビルド → ソリューションのビルド（F7）

詳細は **[BUILD_GUIDE.md](BUILD_GUIDE.md)** を参照してください。

## 実行方法

- スタートアッププロジェクト: **RayTraceVS.WPF**
- F5キーでデバッグ実行
- Ctrl+F5でデバッグなし実行

詳しい使い方については **[USAGE.md](USAGE.md)** を参照してください。

## クイックスタート

### サンプルシーンを試す

1. アプリケーションを起動
2. メニューから「ファイル → 開く」を選択
3. `sample_scene.rtvs` を開く
4. ノードエディタにサンプルシーンが表示されます
5. メニューから「レンダリング → レンダリング開始」を選択
6. レンダリングウィンドウが開き、レイトレーシング結果が表示されます

### ノードエディタ

1. 左パネルのコンポーネントパレットからノードを選択
2. ノードがキャンバスに自動配置されます
3. ノード間のソケットをドラッグして接続を作成
4. 右パネルでノードのプロパティを確認

## ノードタイプ一覧

### オブジェクトノード
- **Sphere（球）**: 球体オブジェクト
- **Plane（平面）**: 無限平面
- **Box（ボックス）**: 直方体オブジェクト

### マテリアルノード
- **DiffuseMaterial**: 拡散反射マテリアル
- **MetalMaterial**: 金属マテリアル（反射）
- **GlassMaterial**: ガラスマテリアル（屈折）
- **EmissionMaterial**: 発光マテリアル（全オブジェクトでEmissionパラメータ対応）
- **MaterialBSDF**: BSDFマテリアル

### ライトノード
- **Light（ポイントライト）**: 点光源
- **DirectionalLight**: 方向性ライト（太陽光など）
- **AmbientLight**: 環境光

### トランスフォームノード
- **Transform**: 位置・回転・スケール変換
- **CombineTransform**: トランスフォームの合成

### 数学ノード
- **Vector3**: 3Dベクトル値
- **Vector4**: 4Dベクトル値
- **Float**: 浮動小数点値
- **Color**: 色値
- **Add（加算）**: 値の加算
- **Sub（減算）**: 値の減算
- **Mul（乗算）**: 値の乗算
- **Div（除算）**: 値の除算

### その他
- **Camera（カメラ）**: 視点設定
- **Scene（シーン）**: 最終出力ノード

## 技術サマリー

### アーキテクチャ

```
┌─────────────────────────────────────────────────────────────┐
│                    RayTraceVS.WPF (C#)                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Views     │  │ ViewModels  │  │     Services        │  │
│  │  (XAML/UI)  │◄─┤   (MVVM)    │◄─┤ (Render/Evaluate)   │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────┬───────────────────────────────┘
                              │ C++/CLI Interop
┌─────────────────────────────▼───────────────────────────────┐
│                 RayTraceVS.Interop (C++/CLI)                │
│  ┌─────────────────┐  ┌────────────────┐  ┌──────────────┐  │
│  │  EngineWrapper  │  │   SceneData    │  │  Marshalling │  │
│  └─────────────────┘  └────────────────┘  └──────────────┘  │
└─────────────────────────────┬───────────────────────────────┘
                              │ Native C++
┌─────────────────────────────▼───────────────────────────────┐
│                RayTraceVS.DXEngine (C++)                    │
│  ┌───────────┐  ┌─────────────┐  ┌────────────────────────┐ │
│  │ DXContext │  │ DXRPipeline │  │ AccelerationStructure  │ │
│  │  (DX12)   │  │   (DXR)     │  │     (BLAS/TLAS)        │ │
│  └───────────┘  └─────────────┘  └────────────────────────┘ │
│  ┌───────────┐  ┌─────────────┐  ┌────────────────────────┐ │
│  │  Shaders  │  │ ShaderCache │  │   NRD Denoiser         │ │
│  │  (HLSL)   │  │             │  │                        │ │
│  └───────────┘  └─────────────┘  └────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

### DirectX12 DXR レイトレーシング

| 項目 | 詳細 |
|------|------|
| **API** | DirectX 12 + DXR (DirectX Raytracing) |
| **シェーダーモデル** | HLSL Shader Model 6.3+ |
| **シェーダーコンパイラ** | DXC (DirectX Shader Compiler) → DXIL出力 |
| **アクセラレーション構造** | BLAS/TLAS (BVH)、プロシージャルAABB |
| **レイタイプ** | プライマリレイ、シャドウレイ、反射レイ |
| **最大再帰深度** | Scene.MaxBounces（デフォルト5、設定可能） |
| **反射モデル** | GGX-like roughness perturbation |
| **デノイザー** | NRD (NVIDIA Real-time Denoiser) |
| ├─ REBLUR | Diffuse/Specularデノイズ |
| └─ SIGMA | シャドウデノイズ |
| **フォトンマップ最適化** | 空間ハッシュ（O(1)ルックアップ） |

### シェーダー構成

| シェーダー | 役割 |
|------------|------|
| **RayGen.hlsl** | レイ生成、カメラからのプライマリレイ発射、G-Buffer出力 |
| **ClosestHit.hlsl** | 統合マテリアル処理（Diffuse/Metal/Glass/Emission対応） |
| **ClosestHit_Diffuse.hlsl** | 拡散反射マテリアル処理（レガシー、コースティクス対応） |
| **ClosestHit_Metal.hlsl** | 金属マテリアル処理（レガシー、反射） |
| **AnyHit_Shadow.hlsl** | シャドウレイ判定 |
| **Miss.hlsl** | レイミス時の背景色処理 |
| **Intersection.hlsl** | プロシージャル交差判定（球、平面、ボックス） |
| **PhotonEmit.hlsl** | フォトン放出（コースティクス用） |
| **PhotonTrace.hlsl** | フォトントレース |
| **Composite.hlsl** | 最終合成、トーンマッピング、ガンマ補正 |
| **Common.hlsli** | 共通定義・構造体（80/80/96 bytes）・ユーティリティ関数 |
| **NRDEncoding.hlsli** | NRDデノイザー用エンコーディング（Oct法線、SIGMA等） |

### レンダリングパイプライン

```
1. シーン評価（ノードグラフ → SceneData）
        ↓
2. アクセラレーション構造構築（BLAS/TLAS）
        ↓
3. フォトン放出（コースティクス用、オプション）
        ↓
4. レイ生成（RayGen）+ G-Buffer出力
        ↓
5. BVHトラバーサル
        ↓
6. 交差判定（Intersection: 球/平面/ボックス）
        ↓
7. シェーディング（ClosestHit: 統合マテリアル処理）
   ├─ ライティング計算（ソフトシャドウ対応）
   ├─ コースティクス（空間ハッシュによるO(1)フォトン検索）
   ├─ 反射レイ発射（GGX-like摂動、Scene.MaxBounces設定可能）
   └─ Emission（自己発光）加算
        ↓
8. デノイズ（NRD）
   ├─ REBLUR: Diffuse/Specularデノイズ
   └─ SIGMA: シャドウデノイズ
        ↓
9. 最終合成（Composite）
   ├─ アルベド乗算
   ├─ トーンマッピング（Reinhard/ACES）
   └─ ガンマ補正（sRGB）
        ↓
10. 画面出力
```

### WPF ノードエディタ

- **UIフレームワーク**: WPF + Canvasベース
- **アーキテクチャ**: MVVM (CommunityToolkit.Mvvm)
- **データバインディング**: ObservableCollection
- **接続線描画**: ベジェ曲線
- **グラフ評価**: トポロジカルソート、依存関係解決

### パフォーマンス目安

**NVIDIA RTX 3060（12GB）の場合：**

| 解像度 | FPS目安 |
|--------|---------|
| 1280x720 | 60+ FPS |
| 1920x1080 | 30-60 FPS |
| 2560x1440 | 15-30 FPS |
| 3840x2160 | 10-30 FPS |

**GPU別の相対性能（1080p基準）：**

| GPU | 相対性能 | 備考 |
|-----|---------|------|
| RTX 4070 | 150-180% | NRD最適化済み |
| RTX 3060 | 100% | 基準 |
| RTX 2060 | 60-70% | NRD最適化済み |
| RX 6700 XT | 80-90% | AMD対応 |
| Arc A770 | 70-80% | Intel対応 |

※オブジェクト数、ライト数、再帰深度、デノイザー設定により変動

## ドキュメント

- **[BUILD_GUIDE.md](BUILD_GUIDE.md)** - 詳細なビルド手順とトラブルシューティング
- **[USAGE.md](USAGE.md)** - アプリケーションの使い方
- **[NODE_EDITOR_GUIDE.md](NODE_EDITOR_GUIDE.md)** - ノードエディタの詳細ガイド
- **[RENDERING_IMPLEMENTATION.md](RENDERING_IMPLEMENTATION.md)** - レンダリング実装の技術詳細
- **[IMPLEMENTATION_SUMMARY.md](IMPLEMENTATION_SUMMARY.md)** - 実装状況サマリー

## 今後の拡張予定

- [ ] メッシュインポート（.obj, .fbx）
- [ ] テクスチャマッピング
- [ ] 法線マップ
- [ ] パストレーシング（より正確なGI）
- [ ] アニメーション機能
- [ ] リアルタイムプレビュー（低解像度）
- [ ] アンドゥ/リドゥ（コマンドシステム基盤は実装済み）
- [ ] 画像シーケンス出力
- [ ] HDR画像出力

## 実装済みの主要機能

- [x] NRDデノイザー統合（REBLUR + SIGMA）
- [x] フォトンマッピングによるコースティクス（空間ハッシュ最適化）
- [x] PBRマテリアルシステム（Metallic/Roughness/Transmission/IOR/Emission）
- [x] ソフトシャドウ（エリアライトサンプリング）
- [x] 被写界深度（DoF）シミュレーション
- [x] トーンマッピング（Reinhard / ACES Filmic）
- [x] シェーダーキャッシュ（DXC + JSON管理）
- [x] GGX-like roughness perturbation

## ライセンス

MIT License

## 作者

RayTraceVS Development Team
