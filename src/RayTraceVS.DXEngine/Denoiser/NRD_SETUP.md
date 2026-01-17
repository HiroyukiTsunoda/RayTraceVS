# NRD (NVIDIA Real-Time Denoisers) セットアップガイド

## 現在の状態

✅ **NRDは有効化されています**

- NRDライブラリ: ビルド済み (`_Build/NRD.dll`)
- `NRD_ENABLED=1`: 設定済み
- `ENABLE_NRD_GBUFFER=1`: シェーダーに設定済み
- NRD.dll: ビルド時に出力ディレクトリにコピーされます

## NRDライブラリのビルド手順

### 前提条件
- CMake 3.22以上
- Visual Studio 2022以降
- RTX Kit 2025.4（インストール済み: `C:\ProgramData\NVIDIA Corporation\SDKs\RTX_Kit_2025.4_Windows`）

### 手順

1. **NRDソースディレクトリに移動**
   ```cmd
   cd "C:\ProgramData\NVIDIA Corporation\SDKs\RTX_Kit_2025.4_Windows\RealTimeDenoiser"
   ```

2. **Deploy スクリプトを実行（依存関係の取得）**
   ```cmd
   1-Deploy.bat
   ```

3. **ビルド スクリプトを実行**
   ```cmd
   2-Build.bat
   ```
   
   または、CMakeを直接使用：
   ```cmd
   mkdir _Build
   cd _Build
   cmake .. -G "Visual Studio 17 2022" -A x64 ^
       -DNRD_NORMAL_ENCODING=2 ^
       -DNRD_ROUGHNESS_ENCODING=1 ^
       -DNRD_EMBEDS_DXBC_SHADERS=OFF ^
       -DNRD_EMBEDS_DXIL_SHADERS=OFF ^
       -DNRD_EMBEDS_SPIRV_SHADERS=OFF
   cmake --build . --config Release
   ```

4. **SDK準備 スクリプトを実行**
   ```cmd
   3-PrepareSDK.bat
   ```
   
   これにより `_NRD_SDK` フォルダが生成されます。

### プロジェクトへの統合

1. **vcxproj を編集**

   `RayTraceVS.DXEngine.vcxproj` で以下の変更を行います：
   
   a. ライブラリパスを追加：
   ```xml
   <Link>
     <AdditionalLibraryDirectories>
       C:\ProgramData\NVIDIA Corporation\SDKs\RTX_Kit_2025.4_Windows\RealTimeDenoiser\_NRD_SDK\lib\Release;
       %(AdditionalLibraryDirectories)
     </AdditionalLibraryDirectories>
     <AdditionalDependencies>
       NRD.lib;%(AdditionalDependencies)
     </AdditionalDependencies>
   </Link>
   ```
   
   b. `NRD_ENABLED=1` をプリプロセッサ定義に追加：
   ```xml
   <PreprocessorDefinitions>
     NRD_ENABLED=1;%(PreprocessorDefinitions)
   </PreprocessorDefinitions>
   ```

2. **シェーダーのG-Buffer出力を有効化**

   シェーダーコンパイル時に `ENABLE_NRD_GBUFFER` を定義：
   ```xml
   <FxCompile Include="Shaders\RayGen.hlsl">
     <PreprocessorDefinitions>ENABLE_NRD_GBUFFER</PreprocessorDefinitions>
     ...
   </FxCompile>
   ```

3. **DXRPipeline.h で denoiserEnabled を true に設定**

   ```cpp
   bool denoiserEnabled = true;  // Enable denoiser
   ```

4. **NRDシェーダーのコンパイル**

   NRDは内部的に多くのコンピュートシェーダーを使用します。これらは通常、NRDビルド時に事前コンパイルされてライブラリに埋め込まれます。`NRD_EMBEDS_DXIL_SHADERS=ON` でビルドすることを推奨します。

## ファイル構成

```
Denoiser/
├── NRDDenoiser.h       - デノイザーラッパークラス（D3D12用）
├── NRDDenoiser.cpp     - 実装（NRD_ENABLED=0でスタブ動作）
└── NRD_SETUP.md        - このファイル

Shaders/
├── NRDEncoding.hlsli   - NRD用エンコード/デコードヘルパー関数
├── Composite.hlsl      - デノイズ結果の合成シェーダー
└── Common.hlsli        - G-Buffer UAV宣言（ENABLE_NRD_GBUFFER有効時）
```

## NRD入力フォーマット

| バッファ | フォーマット | 内容 |
|---------|-------------|------|
| DiffuseRadianceHitDist | RGBA16F | RGB: 拡散反射光, A: ヒット距離 |
| SpecularRadianceHitDist | RGBA16F | RGB: 鏡面反射光, A: ヒット距離 |
| NormalRoughness | RGBA8 | XY: 法線(オクタヘドロン), Z: sign, W: ラフネス |
| ViewZ | R32F | 線形ビュー深度 |
| MotionVectors | RG16F | スクリーンスペースモーションベクター |

## トラブルシューティング

### `ml.h` が見つからない
MathLibパスがインクルードディレクトリに含まれていることを確認：
```
$(NRD_SDK_PATH)\..\MathLib
```

### リンクエラー
NRDライブラリが正しくビルドされ、ライブラリパスが設定されていることを確認してください。

### シェーダーコンパイルエラー
NRDエンコーディング設定（`NRD_NORMAL_ENCODING`, `NRD_ROUGHNESS_ENCODING`）がC++側とシェーダー側で一致していることを確認してください。

## 参考リンク

- [NRD GitHub](https://github.com/NVIDIA-RTX/NRD)
- [NRD Sample](https://github.com/NVIDIA-RTX/NRD-Sample)
- [RTX Kit Documentation](https://developer.nvidia.com/rtx/ray-tracing/rtx-kit)
