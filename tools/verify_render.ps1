<#
.SYNOPSIS
  リファクタリング検証スクリプト。
  現在のビルドでサンプルシーンをヘッドレスレンダリングし、ベースライン画像とピクセル比較する。

.DESCRIPTION
  サンプルシーンでは、画像の寸法、透明度、明るさも検査する。
  -Smokeを指定すると、ベースラインを使わずに描画結果を検査できる。
  通常の比較では、同じGPU、ドライバー、設定で作成したベースラインを使用する。

.EXAMPLE
  .\tools\verify_render.ps1 -UpdateBaseline   # リファクタリング前に1回だけ実行してベースライン生成
  .\tools\verify_render.ps1                   # 各Phase完了後に実行してベースラインと比較
  .\tools\verify_render.ps1 -Smoke -Configuration Release -Width 640 -Height 360
#>
param(
    [string]$Scene = "sample_scene.rtvs",
    [ValidateRange(1, 16384)]
    [int]$Width = 1280,
    [ValidateRange(1, 16384)]
    [int]$Height = 720,
    [ValidateRange(1, 2147483647)]
    [int]$Passes = 4,
    [switch]$UpdateBaseline,
    [switch]$Smoke,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\RayTraceVS.WPF\bin\x64\$Configuration\net8.0-windows\RayTraceVS.WPF.exe"
$scenePath = if ([System.IO.Path]::IsPathRooted($Scene)) { $Scene } else { Join-Path $root $Scene }
$isSampleScene = [System.IO.Path]::GetFullPath($scenePath) -eq (Join-Path $root 'sample_scene.rtvs')
$baselineDir = Join-Path $root "baseline"
$baseline = Join-Path $baselineDir "baseline_${Width}x${Height}_p${Passes}.png"

if ($Smoke -and (-not $isSampleScene -or $UpdateBaseline)) {
    throw '-Smokeはsample_scene.rtvs専用です。-UpdateBaselineとの同時指定はできません。'
}
if (-not (Test-Path $exe)) { Write-Error "exe が見つかりません: $exe（先に build.ps1 -NoPackage でビルドしてください）"; exit 1 }
if (-not (Test-Path $scenePath)) { Write-Error "シーンが見つかりません: $scenePath"; exit 1 }

function Assert-SampleImage([string]$path, [int]$expectedWidth, [int]$expectedHeight) {
    Add-Type -AssemblyName PresentationCore
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        [Uri][System.IO.Path]::GetFullPath($path),
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $frame = $decoder.Frames[0]
    if ($frame.PixelWidth -ne $expectedWidth -or $frame.PixelHeight -ne $expectedHeight) {
        throw "画像の寸法が指定値と異なります: $($frame.PixelWidth)x$($frame.PixelHeight)"
    }
    $bitmap = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
        $frame, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $pixels = [byte[]]::new($expectedWidth * $expectedHeight * 4)
    $bitmap.CopyPixels($pixels, $expectedWidth * 4, 0)
    $visible = 0
    $transparent = 0
    $minBrightness = 765
    $maxBrightness = 0
    for ($i = 0; $i -lt $pixels.Length; $i += 4) {
        if ($pixels[$i + 3] -ne 255) { $transparent++ }
        if ($pixels[$i] -ge 8 -or $pixels[$i + 1] -ge 8 -or $pixels[$i + 2] -ge 8) { $visible++ }
        $brightness = [int]$pixels[$i] + [int]$pixels[$i + 1] + [int]$pixels[$i + 2]
        if ($brightness -lt $minBrightness) { $minBrightness = $brightness }
        if ($brightness -gt $maxBrightness) { $maxBrightness = $brightness }
    }
    # サンプルの描画失敗を検出する。画質やGPU間の微小な差は比較しない。
    $visibleRatio = $visible / ($expectedWidth * $expectedHeight)
    if ($transparent -gt 0 -or $visibleRatio -lt 0.01 -or $maxBrightness - $minBrightness -lt 24) {
        throw "サンプル画像が黒、透明、または単色です: 非黒率=$($visibleRatio.ToString('P2')) 透明画素=$transparent 明るさ=$minBrightness..$maxBrightness"
    }
    Write-Host "[画像検証OK] 非黒率=$($visibleRatio.ToString('P2')) 明るさ=$minBrightness..$maxBrightness" -ForegroundColor Green
}

function Invoke-Render([string]$outPath) {
    if (Test-Path $outPath) { Remove-Item $outPath -Force }
    $p = Start-Process $exe -ArgumentList '--render', ('"{0}"' -f $scenePath), '--output', ('"{0}"' -f $outPath), '--width', "$Width", '--height', "$Height", '--passes', "$Passes" `
        -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { Write-Error "レンダリング失敗 (exit=$($p.ExitCode))"; exit $p.ExitCode }
    if (-not (Test-Path -LiteralPath $outPath -PathType Leaf)) { throw "出力画像がありません: $outPath" }
    if ($isSampleScene) { Assert-SampleImage $outPath $Width $Height }
}

if ($Smoke) {
    $artifactDir = Join-Path $root 'artifacts'
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
    $current = Join-Path $artifactDir "render_smoke_${Configuration}_${Width}x${Height}_p${Passes}.png"
    Invoke-Render $current
    Write-Host "描画検証に成功しました: $current" -ForegroundColor Green
    exit 0
}

if ($UpdateBaseline) {
    if (-not (Test-Path $baselineDir)) { New-Item -ItemType Directory -Path $baselineDir | Out-Null }
    Write-Host "ベースライン生成中: $baseline" -ForegroundColor Cyan
    $candidate = Join-Path $baselineDir "candidate_$([Guid]::NewGuid().ToString('N')).png"
    try {
        Invoke-Render $candidate
        Move-Item -LiteralPath $candidate -Destination $baseline -Force
    }
    finally {
        if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Force }
    }
    Write-Host "ベースラインを保存しました: $baseline" -ForegroundColor Green
} else {
    if (-not (Test-Path $baseline)) { Write-Error "ベースラインがありません: $baseline（先に -UpdateBaseline で生成してください）"; exit 1 }
    $current = Join-Path $env:TEMP "rtvs_verify_${Width}x${Height}_p${Passes}.png"
    Write-Host "現在のビルドでレンダリング中..." -ForegroundColor Cyan
    Invoke-Render $current
    Write-Host "ベースラインと比較中..." -ForegroundColor Cyan
    $c = Start-Process $exe -ArgumentList '--compare', ('"{0}"' -f $baseline), ('"{0}"' -f $current) -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($c.ExitCode -eq 0) {
        Write-Host "[検証OK] 出力はベースラインと一致しています" -ForegroundColor Green
    } else {
        Write-Host "[検証NG] 出力がベースラインと異なります (compare exit=$($c.ExitCode))" -ForegroundColor Red
    }
    exit $c.ExitCode
}
