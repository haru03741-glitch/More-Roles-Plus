# C++ の部品の記録を食わせ直す道具 (replay.cpp) を PC (x64 の exe) と Android (arm64 の実行ファイル) に組む。
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/native-replay/build.ps1 [-SkipPc] [-SkipAndroid]
# 出力 = build/native-replay/win-x64/replay.exe と build/native-replay/android-arm64/replay。
# 部品は本番と同じ式で組む (縮約を切る・最適化も同じ) ので、本番の .dll/.so と同じ結果になる
#   -Out <dir> = 出力先 (同時に別の組み立てを回す時に分ける)
param([switch]$SkipPc, [switch]$SkipAndroid, [string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$src = Join-Path $root 'native\src'
$out = if ($Out) { $Out } else { Join-Path $root 'build\native-replay' }
$files = @(Get-ChildItem $src -Filter *.cpp | ForEach-Object { $_.FullName }) + (Join-Path $PSScriptRoot 'replay.cpp')
$ok = $true

if (-not $SkipPc) {
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { Write-Host 'ERR MSVC (C++ build tools) not found'; exit 1 }
    $dir = Join-Path $out 'win-x64'
    New-Item -ItemType Directory -Force (Join-Path $dir 'obj') | Out-Null
    $obj = (Join-Path $dir 'obj') + '/'
    $srcList = ($files | ForEach-Object { "`"$_`"" }) -join ' '
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    $cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cl /nologo /utf-8 /DMRP_REPLAY /O2 /MT /std:c++20 /fp:precise /GR- /EHs-c- /W3 /Fo`"$obj`" $srcList /Fe`"$dir\replay.exe`" /link /NOLOGO >`"$dir\build.log`""
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { Get-Content "$dir\build.log" | Select-String 'error' | Select-Object -First 10; Write-Host 'ERR PC replay build failed'; $ok = $false }
    else { Write-Host "OK PC   $dir\replay.exe" }
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
    $clangArgs = @('-DMRP_REPLAY', '-fPIE', '-pie', '-O2', '-std=c++20', '-ffp-contract=off', '-fno-exceptions', '-fno-rtti',
              '-nostdlib++', '-o', (Join-Path $dir 'replay')) + $files
    & $cxx @clangArgs 2>&1 | Select-Object -First 20
    if ($LASTEXITCODE -ne 0) { Write-Host 'ERR Android replay build failed'; $ok = $false }
    else { Write-Host "OK And  $dir\replay" }
}

if ($ok) { Write-Host 'RESULT: OK' } else { Write-Host 'RESULT: FAIL'; exit 1 }
