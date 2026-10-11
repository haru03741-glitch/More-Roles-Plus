# C++ の部品 (native/src) を PC (x64 の dll) と Android (arm64 の .so) に組む。
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-native.ps1 [-SkipPc] [-SkipAndroid]
# 出力 = build/native/win-x64/MrpNative.dll と build/native/android-arm64/libmrpnative.so。
# 小数の式は C# と同じ結果にするため縮約 (FMA) を切る (MSVC の /fp:precise は x64 で縮約しない・clang は -ffp-contract=off)
param([switch]$SkipPc, [switch]$SkipAndroid)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'native\src'
$out = Join-Path $root 'build\native'
$files = Get-ChildItem $src -Filter *.cpp | ForEach-Object { $_.FullName }
$ok = $true

if (-not $SkipPc) {
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { Write-Host 'ERR MSVC (C++ build tools) not found'; exit 1 }
    $dir = Join-Path $out 'win-x64'
    New-Item -ItemType Directory -Force $dir | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $dir 'obj') | Out-Null
    $obj = (Join-Path $dir 'obj') + '/'
    $srcList = ($files | ForEach-Object { "`"$_`"" }) -join ' '
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    $cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cl /nologo /utf-8 /LD /O2 /MT /std:c++20 /fp:precise /GR- /EHs-c- /W3 /Fo`"$obj`" $srcList /Fe`"$dir\MrpNative.dll`" /link /NOLOGO >`"$dir\build.log`""
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { Get-Content "$dir\build.log" | Select-String 'error' | Select-Object -First 10; Write-Host 'ERR PC native build failed'; $ok = $false }
    else { Write-Host "OK PC   $dir\MrpNative.dll" }
}

if (-not $SkipAndroid) {
    $sdk = 'D:\Android\Sdk'
    $lp = Join-Path $root 'Android\Launcher\local.properties'
    if (Test-Path $lp) { $m = Select-String -Path $lp -Pattern '^sdk\.dir=(.+)$'; if ($m) { $sdk = $m.Matches[0].Groups[1].Value } }
    $ndk = Get-ChildItem (Join-Path $sdk 'ndk') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $ndk) { Write-Host "ERR Android NDK not found under $sdk\ndk"; exit 1 }
    $cxx = Join-Path $ndk.FullName 'toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android24-clang++.cmd'
    $dir = Join-Path $out 'android-arm64'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $clangArgs = @('-shared', '-fPIC', '-O2', '-std=c++20', '-ffp-contract=off', '-fno-exceptions', '-fno-rtti', '-fvisibility=hidden',
              '-nostdlib++', '-Wl,--build-id=sha1', '-Wl,-z,max-page-size=16384', '-o', (Join-Path $dir 'libmrpnative.so')) + $files
    & $cxx @clangArgs 2>&1 | Select-Object -First 20
    if ($LASTEXITCODE -ne 0) { Write-Host 'ERR Android native build failed'; $ok = $false }
    else { Write-Host "OK And  $dir\libmrpnative.so" }
}

if ($ok) { Write-Host 'RESULT: OK' } else { Write-Host 'RESULT: FAIL'; exit 1 }
