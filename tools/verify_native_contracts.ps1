param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswherePath -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual C++ tools were not found.' }
$devCmdPath = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'

# Import the compiler environment without opening a helper console.
$setupCommand = 'call "{0}" -no_logo -arch=x64 >nul && set' -f $devCmdPath
$compilerEnvironment = & $env:ComSpec /d /c $setupCommand
if ($LASTEXITCODE -ne 0) { throw 'Failed to initialize the compiler environment.' }
foreach ($entry in $compilerEnvironment) {
    if ($entry -match '^([^=]+)=(.*)$') {
        [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
    }
}

$outputDirectory = Join-Path $repoRoot 'obj\native-contract-tests'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$testSource = Join-Path $PSScriptRoot 'native_contract_tests.cpp'
$testObject = Join-Path $outputDirectory 'native_contract_tests.obj'
$testExecutable = Join-Path $outputDirectory 'native_contract_tests.exe'
# C4324 is expected for the explicitly 256-byte-aligned constant buffer.
& cl.exe /nologo /EHsc /std:c++17 /W4 /WX /wd4324 "/Fo$testObject" "/Fe$testExecutable" $testSource
if ($LASTEXITCODE -ne 0) { throw 'Native contract test compilation failed.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Native contract tests failed.' }

$cacheTestSource = Join-Path $PSScriptRoot 'shader_cache_tests.cpp'
$cacheSource = Join-Path $repoRoot 'src\RayTraceVS.DXEngine\ShaderCache.cpp'
$cacheTestExecutable = Join-Path $outputDirectory 'shader_cache_tests.exe'
Push-Location $outputDirectory
try {
    # Compile the production cache against a tiny fixture shader, without GPU work.
    & cl.exe /nologo /EHsc /std:c++17 /utf-8 /W3 /wd4101 "/Fe$cacheTestExecutable" $cacheTestSource $cacheSource /link d3dcompiler.lib
    if ($LASTEXITCODE -ne 0) { throw 'Shader cache test compilation failed.' }
    & $cacheTestExecutable $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Shader cache tests failed.' }
}
finally {
    Pop-Location
}
