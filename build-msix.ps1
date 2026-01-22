# RayTraceVS MSIX Package Build Script
# Usage: .\build-msix.ps1 [-Configuration Release|Debug] [-CreateCertificate]

param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$CreateCertificate
)

$ErrorActionPreference = "Stop"
$SolutionDir = $PSScriptRoot
$PackageProjectDir = "$SolutionDir\src\RayTraceVS.Package"

Write-Host "=== RayTraceVS MSIX Package Builder ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host ""

# Check for required tools
Write-Host "Checking build environment..." -ForegroundColor Yellow

# Check for MSBuild
$msbuildPath = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    $vsPath = & $vsWhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vsPath) {
        $msbuildPath = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
    }
}

if (-not $msbuildPath -or -not (Test-Path $msbuildPath)) {
    Write-Host "ERROR: MSBuild not found. Please install Visual Studio 2022 with the following workloads:" -ForegroundColor Red
    Write-Host "  - .NET desktop development"
    Write-Host "  - Desktop development with C++"
    Write-Host "  - Windows application development (for MSIX packaging)"
    exit 1
}

Write-Host "MSBuild found: $msbuildPath" -ForegroundColor Green

# Create self-signed certificate for development if requested
if ($CreateCertificate) {
    Write-Host ""
    Write-Host "Creating self-signed certificate for development..." -ForegroundColor Yellow
    
    $certSubject = "CN=RayTraceVS"
    $existingCert = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $certSubject }
    
    if ($existingCert) {
        Write-Host "Certificate already exists: $($existingCert.Thumbprint)" -ForegroundColor Green
    } else {
        try {
            $cert = New-SelfSignedCertificate -Type Custom -Subject $certSubject `
                -KeyUsage DigitalSignature `
                -FriendlyName "RayTraceVS Development" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
            
            Write-Host "Certificate created: $($cert.Thumbprint)" -ForegroundColor Green
            Write-Host "To install MSIX packages signed with this certificate, you need to install the certificate to the Trusted Root store." -ForegroundColor Yellow
            
            # Export certificate for users
            $certPath = "$SolutionDir\RayTraceVS_Dev.cer"
            Export-Certificate -Cert $cert -FilePath $certPath
            Write-Host "Certificate exported to: $certPath" -ForegroundColor Green
        } catch {
            Write-Host "WARNING: Failed to create certificate. You may need to run as Administrator." -ForegroundColor Yellow
            Write-Host $_.Exception.Message
        }
    }
}

# Restore NuGet packages
Write-Host ""
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
& dotnet restore "$SolutionDir\RayTraceVS.sln"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: NuGet restore failed" -ForegroundColor Red
    exit 1
}

# Build the solution
Write-Host ""
Write-Host "Building solution ($Configuration|x64)..." -ForegroundColor Yellow
& $msbuildPath "$SolutionDir\RayTraceVS.sln" /p:Configuration=$Configuration /p:Platform=x64 /t:Build /m /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Build completed successfully!" -ForegroundColor Green

# Check if we can create MSIX package
$wapProjPath = "$PackageProjectDir\RayTraceVS.Package.wapproj"
if (Test-Path $wapProjPath) {
    Write-Host ""
    Write-Host "Creating MSIX package..." -ForegroundColor Yellow
    
    # Build the packaging project
    & $msbuildPath $wapProjPath /p:Configuration=$Configuration /p:Platform=x64 /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false /m /verbosity:minimal
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "=== MSIX Package Created Successfully! ===" -ForegroundColor Green
        
        # Find the output package
        $packageDir = "$PackageProjectDir\AppPackages"
        if (Test-Path $packageDir) {
            $msixFiles = Get-ChildItem -Path $packageDir -Recurse -Filter "*.msix"
            if ($msixFiles) {
                Write-Host "Package location:" -ForegroundColor Cyan
                foreach ($file in $msixFiles) {
                    Write-Host "  $($file.FullName)"
                }
            }
        }
    } else {
        Write-Host ""
        Write-Host "WARNING: MSIX packaging failed. The application was built successfully, but the MSIX package could not be created." -ForegroundColor Yellow
        Write-Host "You may need to:"
        Write-Host "  1. Install the 'Windows application development' workload in Visual Studio"
        Write-Host "  2. Create and install a code signing certificate"
        Write-Host "  3. Build from Visual Studio IDE"
    }
} else {
    Write-Host ""
    Write-Host "WARNING: Packaging project not found at $wapProjPath" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Build output location:" -ForegroundColor Cyan
Write-Host "  $SolutionDir\src\RayTraceVS.WPF\bin\x64\$Configuration\net8.0-windows\"
