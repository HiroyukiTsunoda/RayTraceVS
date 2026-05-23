<#
.SYNOPSIS
  リファクタリング検証スクリプト。
  現在のビルドでサンプルシーンをヘッドレスレンダリングし、ベースライン画像とピクセル比較する。

.DESCRIPTION
  レンダリングは決定的（同一入力→同一出力）なので、C#層のリファクタリングで
  エンジンへ渡すパラメータが変わっていなければ出力はベースラインと完全一致する。
  差分が出た場合はリファクタリングで挙動が変わったことを意味する。

.EXAMPLE
  .\tools\verify_render.ps1 -UpdateBaseline   # リファクタリング前に1回だけ実行してベースライン生成
  .\tools\verify_render.ps1                   # 各Phase完了後に実行してベースラインと比較
#>
param(
    [string]$Scene = "sample_scene.rtvs",
    [int]$Width = 1280,
    [int]$Height = 720,
    [int]$Passes = 4,
    [switch]$UpdateBaseline
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\RayTraceVS.WPF\bin\x64\Debug\net8.0-windows\RayTraceVS.WPF.exe"
$scenePath = if ([System.IO.Path]::IsPathRooted($Scene)) { $Scene } else { Join-Path $root $Scene }
$baselineDir = Join-Path $root "baseline"
$baseline = Join-Path $baselineDir "baseline_${Width}x${Height}_p${Passes}.png"

if (-not (Test-Path $exe)) { Write-Error "exe が見つかりません: $exe（先に build.ps1 -NoPackage でビルドしてください）"; exit 1 }
if (-not (Test-Path $scenePath)) { Write-Error "シーンが見つかりません: $scenePath"; exit 1 }

function Invoke-Render([string]$outPath) {
    if (Test-Path $outPath) { Remove-Item $outPath -Force }
    $p = Start-Process $exe -ArgumentList '--render', $scenePath, '--output', $outPath, '--width', "$Width", '--height', "$Height", '--passes', "$Passes" `
        -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { Write-Error "レンダリング失敗 (exit=$($p.ExitCode))"; exit $p.ExitCode }
}

if ($UpdateBaseline) {
    if (-not (Test-Path $baselineDir)) { New-Item -ItemType Directory -Path $baselineDir | Out-Null }
    Write-Host "ベースライン生成中: $baseline" -ForegroundColor Cyan
    Invoke-Render $baseline
    Write-Host "ベースラインを保存しました: $baseline" -ForegroundColor Green
} else {
    if (-not (Test-Path $baseline)) { Write-Error "ベースラインがありません: $baseline（先に -UpdateBaseline で生成してください）"; exit 1 }
    $current = Join-Path $env:TEMP "rtvs_verify_${Width}x${Height}_p${Passes}.png"
    Write-Host "現在のビルドでレンダリング中..." -ForegroundColor Cyan
    Invoke-Render $current
    Write-Host "ベースラインと比較中..." -ForegroundColor Cyan
    $c = Start-Process $exe -ArgumentList '--compare', $baseline, $current -WorkingDirectory $root -Wait -PassThru -NoNewWindow
    if ($c.ExitCode -eq 0) {
        Write-Host "[検証OK] 出力はベースラインと一致しています" -ForegroundColor Green
    } else {
        Write-Host "[検証NG] 出力がベースラインと異なります (compare exit=$($c.ExitCode))" -ForegroundColor Red
    }
    exit $c.ExitCode
}
