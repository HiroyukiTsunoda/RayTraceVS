# Build script for RayTraceVS
# Usage: .\build.ps1 [-Clean] [-Run]

param(
    [switch]$Clean,
    [switch]$Run,
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"

# Find MSBuild
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -property installationPath
    $msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
} else {
    # Fallback to known path
    $msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
}

if (-not (Test-Path $msbuild)) {
    Write-Error "MSBuild not found at: $msbuild"
    exit 1
}

Write-Host "Using MSBuild: $msbuild" -ForegroundColor Cyan

$solution = "RayTraceVS.sln"
$config = "Debug"
$platform = "x64"

# Clean if requested
if ($Clean -or $Rebuild) {
    Write-Host "`nCleaning solution..." -ForegroundColor Yellow
    & $msbuild $solution /t:Clean /p:Configuration=$config /p:Platform=$platform /v:quiet /nologo
}

# Build
$target = if ($Rebuild) { "Rebuild" } else { "Build" }
Write-Host "`nBuilding solution ($target)..." -ForegroundColor Green
& $msbuild $solution /t:$target /p:Configuration=$config /p:Platform=$platform /v:minimal /nologo

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "`nBuild successful!" -ForegroundColor Green

# Output directory
$wpfOutput = "src\RayTraceVS.WPF\bin\x64\Debug\net8.0-windows"

# Verify required shader files exist
Write-Host "`nVerifying shader files..." -ForegroundColor Cyan
$requiredShaders = @(
    "RayGen.cso",
    "ClosestHit.cso", 
    "Miss.cso",
    "Intersection.cso",
    "AnyHit_Shadow.cso",
    "PhotonEmit.cso",
    "PhotonTrace.cso",
    "RayTraceCompute.cso",
    "Composite.cso"
)

$missingShaders = @()
foreach ($shader in $requiredShaders) {
    $path = Join-Path $wpfOutput $shader
    if (Test-Path $path) {
        $info = Get-Item $path
        Write-Host "  [OK] $shader - $($info.LastWriteTime)" -ForegroundColor Green
    } else {
        Write-Host "  [MISSING] $shader" -ForegroundColor Red
        $missingShaders += $shader
    }
}

if ($missingShaders.Count -gt 0) {
    Write-Error "Missing shader files: $($missingShaders -join ', ')"
    exit 1
}

# Show output DLL timestamps
Write-Host "`nOutput files:" -ForegroundColor Cyan
Get-ChildItem "$wpfOutput\*.dll" -ErrorAction SilentlyContinue | 
    Where-Object { $_.Name -match "RayTraceVS" } |
    ForEach-Object { Write-Host "  $($_.Name) - $($_.LastWriteTime)" }

# Run if requested
if ($Run) {
    $exe = "$wpfOutput\RayTraceVS.WPF.exe"
    if (Test-Path $exe) {
        Write-Host "`nStarting application..." -ForegroundColor Yellow
        Start-Process $exe
    } else {
        Write-Warning "Executable not found: $exe"
    }
}
