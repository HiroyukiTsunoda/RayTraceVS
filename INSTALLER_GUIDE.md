# RayTraceVS インストーラー作成ガイド

このドキュメントでは、RayTraceVSのMSIXインストーラーを作成する手順を説明します。

## 前提条件

### 必須ソフトウェア

1. **Visual Studio 2022** (v17.0以降) 以下のワークロードをインストール：
   - .NET デスクトップ開発
   - C++によるデスクトップ開発
   - **Windows アプリケーション開発**（MSIX Packaging Tools含む）

2. **.NET 8.0 SDK**

3. **Windows 10 SDK** (10.0.19041.0以降)

### Visual Studio ワークロードの確認

Visual Studio Installerを開き、以下が有効になっていることを確認してください：

```
[x] .NET デスクトップ開発
[x] C++によるデスクトップ開発
    [x] C++/CLI サポート
[x] Windows アプリケーション開発
    [x] MSIX Packaging Tools
```

## プロジェクト構成

```
RayTraceVS/
├── src/
│   ├── RayTraceVS.WPF/           # メインWPFアプリケーション
│   ├── RayTraceVS.DXEngine/      # DirectX12レイトレーシングエンジン
│   ├── RayTraceVS.Interop/       # C++/CLI相互運用層
│   └── RayTraceVS.Package/       # MSIXパッケージングプロジェクト
│       ├── Package.appxmanifest  # アプリマニフェスト
│       ├── RayTraceVS.Package.wapproj
│       └── Images/               # アプリアイコン
└── build-msix.ps1                # ビルドスクリプト
```

## インストーラーの作成方法

### 方法1: Visual Studioから作成（推奨）

1. **ソリューションを開く**
   - Visual Studio 2022で `RayTraceVS.sln` を開く

2. **構成を選択**
   - 構成: `Release`
   - プラットフォーム: `x64`

3. **パッケージングプロジェクトをスタートアップに設定**
   - ソリューションエクスプローラーで `RayTraceVS.Package` を右クリック
   - 「スタートアッププロジェクトに設定」を選択

4. **アプリパッケージを作成**
   - `RayTraceVS.Package` を右クリック
   - 「発行」→「アプリパッケージの作成...」を選択
   - 配布方法を選択：
     - **サイドローディング**: 社内配布や直接配布向け
     - **Microsoft Store**: ストア配布向け

5. **署名の設定**
   - 開発用: 自己署名証明書を作成
   - 本番用: 信頼された証明書を使用

6. **パッケージの作成**
   - ウィザードに従ってパッケージを作成
   - 出力先: `src\RayTraceVS.Package\AppPackages\`

### 方法2: コマンドラインから作成

```powershell
# ビルドスクリプトを実行
.\build-msix.ps1 -Configuration Release

# 開発用証明書を作成する場合
.\build-msix.ps1 -Configuration Release -CreateCertificate
```

### 方法3: MSBuildから直接作成

```powershell
# NuGetパッケージの復元
dotnet restore RayTraceVS.sln

# ソリューション全体をビルド
msbuild RayTraceVS.sln /p:Configuration=Release /p:Platform=x64 /t:Build

# MSIXパッケージの作成
msbuild src\RayTraceVS.Package\RayTraceVS.Package.wapproj `
    /p:Configuration=Release `
    /p:Platform=x64 `
    /p:UapAppxPackageBuildMode=SideloadOnly `
    /p:AppxPackageSigningEnabled=true
```

## コード署名について

### 開発用（自己署名証明書）

```powershell
# PowerShellで自己署名証明書を作成
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=RayTraceVS" `
    -KeyUsage DigitalSignature `
    -FriendlyName "RayTraceVS Development" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

# 証明書をエクスポート
Export-Certificate -Cert $cert -FilePath "RayTraceVS_Dev.cer"

# インストール時に証明書を信頼済みルートに追加する必要があります
```

### 本番用

本番配布には、以下のいずれかを使用してください：
- Microsoft Partner Center で取得した証明書（Store配布用）
- 信頼された認証局（CA）から購入した証明書（サイドローディング用）
- 企業のコード署名証明書

## MSIXパッケージの内容

作成されるパッケージには以下が含まれます：

| ファイル | 説明 |
|---------|------|
| `RayTraceVS.WPF.exe` | メインアプリケーション |
| `RayTraceVS.DXEngine.dll` | DirectX12レイトレーシングエンジン |
| `RayTraceVS.Interop.dll` | C++/CLI相互運用層 |
| `NRD.dll` | NVIDIA Real-time Denoiser |
| `Shader/` | シェーダーファイル（.hlsl, .hlsli, .cso） |
| `sample_scene.rtvs` | サンプルシーン |
| `Resource/` | リソースファイル |

## インストーラーのテスト

### サイドローディングでのインストール

1. **開発者モードを有効化**（Windows設定 → 更新とセキュリティ → 開発者向け）

2. **証明書のインストール**
   ```powershell
   # 自己署名証明書の場合
   Import-Certificate -FilePath "RayTraceVS_Dev.cer" -CertStoreLocation Cert:\LocalMachine\Root
   ```

3. **パッケージのインストール**
   - `.msix` または `.msixbundle` ファイルをダブルクリック
   - または PowerShell から:
   ```powershell
   Add-AppxPackage -Path "RayTraceVS_1.0.0.0_x64.msix"
   ```

### アンインストール

```powershell
# アプリの削除
Get-AppxPackage *RayTraceVS* | Remove-AppxPackage
```

## トラブルシューティング

### "Microsoft.DesktopBridge.props が見つかりません"

**原因**: MSIX Packaging Toolsがインストールされていない

**解決策**:
1. Visual Studio Installerを開く
2. 「変更」をクリック
3. 「Windows アプリケーション開発」ワークロードを選択
4. 「MSIX Packaging Tools」コンポーネントが有効になっていることを確認
5. インストールを実行

### パッケージの署名エラー

**原因**: 有効な証明書がない

**解決策**:
- 開発用: `New-SelfSignedCertificate` で自己署名証明書を作成
- `Package.appxmanifest` の Publisher が証明書のSubjectと一致することを確認

### インストール時の "信頼されていない発行元"

**原因**: 自己署名証明書が信頼されていない

**解決策**:
1. 証明書 (.cer) をエクスポート
2. 管理者権限で「信頼されたルート証明機関」ストアにインストール

## ファイル関連付け

インストール後、`.rtvs` ファイルは自動的にRayTraceVSに関連付けられます：
- `.rtvs` ファイルをダブルクリックするとRayTraceVSで開きます
- ファイルアイコンはRayTraceVSのアイコンで表示されます

## 配布チェックリスト

- [ ] Release構成でビルド完了
- [ ] すべての依存ファイルがパッケージに含まれている
- [ ] 有効な証明書で署名されている
- [ ] テスト環境でインストール・起動を確認
- [ ] .rtvs ファイルの関連付けが動作することを確認
- [ ] アンインストールが正常に完了することを確認
