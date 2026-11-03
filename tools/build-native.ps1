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
# 声の圧縮 (libopus 1.5.2・C)。ソースはリポジトリの外 (リポジトリの一つ上の opus フォルダー、または MRP_OPUS) に置く。
# 無ければ公式の配布物を build/opus-src へ取ってくる。組む .c の一覧は tools/opus-sources.txt。
# x86 / ARM の専用命令は使わない (どの端末でも同じ C の道)
$opus = if ($env:MRP_OPUS) { $env:MRP_OPUS } else { Join-Path (Split-Path -Parent $root) 'opus' }
if (-not (Test-Path (Join-Path $opus 'include\opus.h'))) {
    $opus = Join-Path $root 'build\opus-src\opus-1.5.2'
    if (-not (Test-Path (Join-Path $opus 'include\opus.h'))) {
        $dl = Join-Path $root 'build\opus-src'
        New-Item -ItemType Directory -Force $dl | Out-Null
        $tgz = Join-Path $dl 'opus-1.5.2.tar.gz'
        Write-Host 'opus not found, downloading opus-1.5.2'
        Invoke-WebRequest 'https://downloads.xiph.org/releases/opus/opus-1.5.2.tar.gz' -OutFile $tgz -UseBasicParsing
        # Git Bash の GNU tar が先に見つかると C: を接続先と読んで失敗するので Windows の tar を使う
        $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (-not (Test-Path $tarExe)) { $tarExe = 'tar' }
        & $tarExe -xzf $tgz -C $dl
        if (-not (Test-Path (Join-Path $opus 'include\opus.h'))) { Write-Host 'ERR opus source not found'; exit 1 }
    }
}
$opusFiles = Get-Content (Join-Path $PSScriptRoot 'opus-sources.txt') | Where-Object { $_.Trim() } | ForEach-Object { (Join-Path $opus $_.Trim()) -replace '/', '\' }
$opusInc = @('include', 'celt', 'silk', 'silk\float', 'src') | ForEach-Object { Join-Path $opus $_ }
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
    # libopus は先に別の置き場へ .obj にする (ファイルが多くコマンドの長さを超えるので応答ファイルで渡す)
    $opusObj = Join-Path $dir 'obj\opus'
    New-Item -ItemType Directory -Force $opusObj | Out-Null
    $rsp = Join-Path $dir 'opus.rsp'
    $rspLines = @('/nologo', '/c', '/O2', '/MT', '/fp:precise', '/W0', '/DOPUS_BUILD', '/DUSE_ALLOCA', '/DHAVE_LRINTF', '/DHAVE_LRINT', "/Fo`"$opusObj\\`"") +
        ($opusInc | ForEach-Object { "/I`"$_`"" }) + ($opusFiles | ForEach-Object { "`"$_`"" })
    [System.IO.File]::WriteAllLines($rsp, $rspLines)
    cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cl @`"$rsp`" >`"$dir\opus.log`""
    if ($LASTEXITCODE -ne 0) { Get-Content "$dir\opus.log" | Select-String 'error' | Select-Object -First 10; Write-Host 'ERR PC opus build failed'; exit 1 }
    $opusObjs = ($opusFiles | ForEach-Object { "`"$opusObj\$([System.IO.Path]::GetFileNameWithoutExtension($_)).obj`"" })
    $linkRsp = Join-Path $dir 'link.rsp'
    [System.IO.File]::WriteAllLines($linkRsp, $opusObjs)
    $cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cl /nologo /utf-8 /LD /O2 /MT /std:c++20 /fp:precise /GR- /EHs-c- /W3 /I`"$opus\include`" /Fo`"$obj`" $srcList @`"$linkRsp`" /Fe`"$dir\MrpNative.dll`" /link /NOLOGO >`"$dir\build.log`""
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { Get-Content "$dir\build.log" | Select-String 'error' | Select-Object -First 10; Write-Host 'ERR PC native build failed'; $ok = $false }
    else { Write-Host "OK PC   $dir\MrpNative.dll" }
}

if (-not $SkipAndroid) {
    $sdk = 'D:\Android\Sdk'
    $lp = Join-Path $root 'Android\Launcher\local.properties'
    if (Test-Path $lp) { $m = Select-String -Path $lp -Pattern '^sdk\.dir=(.+)$'; if ($m) { $sdk = $m.Matches[0].Groups[1].Value } }
    $ndk = $null
    if (Test-Path "$sdk\ndk" -ErrorAction SilentlyContinue) { $ndk = Get-ChildItem "$sdk\ndk" -Directory | Sort-Object Name -Descending | Select-Object -First 1 }
    # SDK を入れていない PC では ANDROID_NDK_HOME (NDK を展開したフォルダー) でも組める
    if (-not $ndk -and $env:ANDROID_NDK_HOME -and (Test-Path $env:ANDROID_NDK_HOME)) { $ndk = Get-Item $env:ANDROID_NDK_HOME }
    if (-not $ndk) { Write-Host "ERR Android NDK not found under $sdk\ndk"; exit 1 }
    $cxx = Join-Path $ndk.FullName 'toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android24-clang++.cmd'
    $dir = Join-Path $out 'android-arm64'
    New-Item -ItemType Directory -Force $dir | Out-Null
    # libopus は C として先に .o にする (clang++ に .c を渡すと C++ として読んでしまう)
    $cc = Join-Path $ndk.FullName 'toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android24-clang.cmd'
    $opusObj = Join-Path $dir 'obj\opus'
    New-Item -ItemType Directory -Force $opusObj | Out-Null
    $opusOut = @()
    foreach ($f in $opusFiles) {
        $o = Join-Path $opusObj ([System.IO.Path]::GetFileNameWithoutExtension($f) + '.o')
        $ccArgs = @('-c', '-fPIC', '-O2', '-ffp-contract=off', '-fvisibility=hidden', '-w', '-DOPUS_BUILD', '-DUSE_ALLOCA', '-DHAVE_LRINTF', '-DHAVE_LRINT') +
            ($opusInc | ForEach-Object { "-I$_" }) + @('-o', $o, $f)
        & $cc @ccArgs 2>&1 | Select-Object -First 10
        if ($LASTEXITCODE -ne 0) { Write-Host "ERR Android opus build failed: $f"; exit 1 }
        $opusOut += $o
    }
    $clangArgs = @('-shared', '-fPIC', '-O2', '-std=c++20', '-ffp-contract=off', '-fno-exceptions', '-fno-rtti', '-fvisibility=hidden',
              '-nostdlib++', "-I$opus\include", '-Wl,--build-id=sha1', '-Wl,-z,max-page-size=16384', '-o', (Join-Path $dir 'libmrpnative.so')) + $files + $opusOut
    # ファイルが多くコマンドの長さを超えるので応答ファイルで渡す
    $andRsp = Join-Path $dir 'link.rsp'
    [System.IO.File]::WriteAllLines($andRsp, ($clangArgs | ForEach-Object { '"' + ($_ -replace '\\', '/') + '"' }))
    & $cxx "@$andRsp" 2>&1 | Select-Object -First 20
    if ($LASTEXITCODE -ne 0) { Write-Host 'ERR Android native build failed'; $ok = $false }
    else { Write-Host "OK And  $dir\libmrpnative.so" }
}

if ($ok) { Write-Host 'RESULT: OK' } else { Write-Host 'RESULT: FAIL'; exit 1 }
