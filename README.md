# RayTraceVS - DirectX12 DXR Visual Raytracing

WPFベースのビジュアルスクリプティングUIと、DirectX12 DXRを使用したGPUレイトレーシングエンジンを組み合わせたWindowsアプリケーションです。

## 概要

- **プロジェクト名**: RayTraceVS
- **開発環境**: Visual Studio 2022以降
- **言語**: C# 12 (.NET 8) / C++ 20
- **グラフィックスAPI**: DirectX 12 + DXR (DirectX Raytracing) 1.1

## 主な機能

- UEのBlueprintライクなノードエディタでシーンを構築
- リアルタイムGPUレイトレーシングでのレンダリング
- 球、平面、ボックスなどの基本オブジェクト
- 複数光源のサポート（ポイントライト、ディレクショナルライト、アンビエントライト）
- 物理ベースマテリアル（Diffuse、Metal、Glass、Emission）
- 鏡面反射と屈折（透明マテリアル）
- フォトンマッピングによるグローバルイルミネーション
- NRDデノイザー対応

## プロジェクト構造

```
RayTraceVS/
├── RayTraceVS.sln                          # Visual Studioソリューション
├── src/
│   ├── RayTraceVS.WPF/                     # C# WPFプロジェクト
│   │   ├── ViewModels/                     # MVVM ViewModels
│   │   ├── Views/                          # WPF Views
│   │   ├── Converters/                     # 値コンバーター
│   │   ├── Models/                         # データモデル
│   │   │   └── Nodes/                      # ノードタイプ
│   │   └── Services/                       # サービス層
│   │
│   ├── RayTraceVS.DXEngine/                # C++ DirectX12プロジェクト
│   │   ├── DXContext.h/.cpp                # DX12初期化
│   │   ├── DXRPipeline.h/.cpp              # DXRパイプライン
│   │   ├── AccelerationStructure.h/.cpp   # BLAS/TLAS構築
│   │   ├── RenderTarget.h/.cpp             # レンダーターゲット管理
│   │   ├── ShaderCache.h/.cpp              # シェーダーキャッシュ
│   │   ├── NativeBridge.h/.cpp             # ネイティブブリッジ
│   │   ├── Denoiser/                       # NRDデノイザー
│   │   └── Shaders/                        # HLSLシェーダー
│   │
│   ├── RayTraceVS.Interop/                 # C++/CLI相互運用プロジェクト
│   │   ├── EngineWrapper.h/.cpp            # エンジンラッパー
│   │   ├── SceneData.h                     # データ構造
│   │   └── Marshalling.h/.cpp              # データ変換
│   │
│   └── Shader/                             # シェーダーソース（共有）
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
- **GPU**: DirectX 12対応GPU
  - 推奨: NVIDIA RTX 2060以降
  - 最小: NVIDIA GTX 1060 / AMD Radeon RX 580以降
- **RAM**: 8GB以上（16GB推奨）
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
- **EmissionMaterial**: 発光マテリアル
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
| **API** | DirectX 12 + DXR 1.1 |
| **シェーダーモデル** | HLSL Shader Model 6.3+ |
| **アクセラレーション構造** | BLAS/TLAS (BVH) |
| **レイタイプ** | プライマリレイ、シャドウレイ、反射/屈折レイ |
| **最大再帰深度** | 設定可能（デフォルト: 8） |

### シェーダー構成

| シェーダー | 役割 |
|------------|------|
| **RayGen.hlsl** | レイ生成、カメラからのプライマリレイ発射 |
| **ClosestHit.hlsl** | 基本ヒット処理 |
| **ClosestHit_Diffuse.hlsl** | 拡散反射マテリアル処理 |
| **ClosestHit_Metal.hlsl** | 金属マテリアル（反射）処理 |
| **ClosestHit_Glass.hlsl** | ガラスマテリアル（屈折）処理 |
| **AnyHit_Shadow.hlsl** | シャドウレイ判定 |
| **Miss.hlsl** | レイミス時の背景色処理 |
| **Intersection.hlsl** | カスタム交差判定（球、円柱等） |
| **PhotonEmit.hlsl** | フォトン放出 |
| **PhotonTrace.hlsl** | フォトントレース |
| **Composite.hlsl** | 最終合成 |
| **Common.hlsli** | 共通定義・構造体 |
| **NRDEncoding.hlsli** | NRDデノイザー用エンコーディング |

### レンダリングパイプライン

```
1. シーン評価（ノードグラフ → SceneData）
        ↓
2. アクセラレーション構造構築（BLAS/TLAS）
        ↓
3. レイ生成（RayGen）
        ↓
4. BVHトラバーサル
        ↓
5. 交差判定（Intersection / ビルトイン）
        ↓
6. シェーディング（ClosestHit_*）
   ├─ ライティング計算
   ├─ 反射レイ発射（再帰）
   └─ 屈折レイ発射（再帰）
        ↓
7. デノイズ（NRD）[オプション]
        ↓
8. 最終合成（Composite）
        ↓
9. 画面出力
```

### WPF ノードエディタ

- **UIフレームワーク**: WPF + Canvasベース
- **アーキテクチャ**: MVVM (CommunityToolkit.Mvvm)
- **データバインディング**: ObservableCollection
- **接続線描画**: ベジェ曲線
- **グラフ評価**: トポロジカルソート、依存関係解決

### パフォーマンス目安（RTX 3060の場合）

| 解像度 | FPS目安 |
|--------|---------|
| 1280x720 | 60+ FPS |
| 1920x1080 | 30-60 FPS |
| 2560x1440 | 15-30 FPS |
| 3840x2160 | 10-30 FPS |

※オブジェクト数、ライト数、再帰深度により変動

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
- [ ] アンドゥ/リドゥ
- [ ] 画像シーケンス出力
- [ ] HDR画像出力

## ライセンス

MIT License

## 作者

RayTraceVS Development Team
