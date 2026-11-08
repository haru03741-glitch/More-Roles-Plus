# インストーラーに埋め込む中身 (Among Us のフォルダにそのまま置く一式) を組む。-Zip を付けると手で入れる人向けの zip も作る。
#   powershell -File installer/make-payload.ps1 [-Dll <MoreRolesPlus.dll>] [-Zip <出力.zip>]
# 中身:
#   winhttp.dll / .doorstop_version / doorstop_config.ini   Unity Doorstop 4.5.1 (packaging/doorstop)
#   dotnet/                                                  BepInEx 6.0.0-be.735 公式の zip の物
#   BepInEx/core/                                            同上 + Il2CppInterop 1.5.3 (packaging/il2cppinterop。Runtime はここで tools/InteropWbPatch を通す)
#   BepInEx/patchers/MoreRolesPlus.BootAccel.dll
#   BepInEx/plugins/MoreRolesPlus.dll
#   BepInEx/config/BepInEx.cfg                               packaging/config
#   LICENSE.txt / THIRD-PARTY-NOTICES.md / licenses/
# interop/ と unity-libs/ は初回起動で BepInEx が作るので入れない。
param(
    [string]$Dll,
    [string]$Stage,
    [string]$Zip
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Dll) { $Dll = Join-Path $repo 'bin/Release/net6.0/MoreRolesPlus.dll' }
if (-not $Stage) { $Stage = Join-Path $PSScriptRoot 'MrpInstaller/obj/mrp-payload' }
function Fail([string]$msg) { Write-Output "[FAIL] $msg"; exit 1 }
function Machine([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($b, 0x3c)
    return [BitConverter]::ToUInt16($b, $pe + 4)
}
if (-not (Test-Path $Dll)) { Fail "MoreRolesPlus.dll が無い: $Dll (先に dotnet build MoreRolesPlus.csproj -c Release)" }

$bieUrl = 'https://builds.bepinex.dev/projects/bepinex_be/735/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.735%2B5fef357.zip'
$bieSha = 'BADEF8112853A00939A0DF6CA143BC0A4E3DC02BD4D21B873302731BFA0E4DF4'
# packaging/il2cppinterop/Il2CppInterop.Runtime.dll に InteropWbPatch を通した結果 (違えば道具か元の版が変わった)
$expectRuntimeMd5 = '29D1515F'

$cache = Join-Path $repo 'build/cache'
$work = Join-Path $repo 'build/payload-src'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
foreach ($d in $Stage, $work) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force -Path $Stage, $work | Out-Null

# 1. BepInEx be.735 (公式のビルド)
$bieZip = Join-Path $cache 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.735.zip'
if (-not (Test-Path $bieZip)) { Invoke-WebRequest -Uri $bieUrl -OutFile $bieZip -UseBasicParsing }
$sha = (Get-FileHash $bieZip -Algorithm SHA256).Hash
if ($sha -ne $bieSha) { Remove-Item $bieZip -Force; Fail "BepInEx の zip の SHA-256 が違う ($sha)" }
Expand-Archive -Path $bieZip -DestinationPath $work -Force

$bie = Join-Path $Stage 'BepInEx'
foreach ($d in 'core', 'patchers', 'plugins', 'config') { New-Item -ItemType Directory -Force -Path (Join-Path $bie $d) | Out-Null }
Copy-Item (Join-Path $work 'BepInEx/core/*') (Join-Path $bie 'core') -Recurse -Force
Copy-Item (Join-Path $work 'dotnet') (Join-Path $Stage 'dotnet') -Recurse -Force

# 2. Doorstop 4.5.1
foreach ($f in 'winhttp.dll', '.doorstop_version', 'doorstop_config.ini') {
    Copy-Item (Join-Path $repo "packaging/doorstop/$f") (Join-Path $Stage $f) -Force
}
$ini = Get-Content -Raw (Join-Path $Stage 'doorstop_config.ini')
if ($ini -notmatch '(?m)^target_assembly=BepInEx\\core\\BepInEx\.Unity\.IL2CPP\.dll\s*$') { Fail 'doorstop_config.ini の target_assembly が違う' }
if ($ini -notmatch '(?m)^boot_config_override=BepInEx\\config\\MoreRolesPlus\.boot\.config\s*$') { Fail 'doorstop_config.ini の boot_config_override が違う' }
if ($ini -match '(?m)^\s*GC_DISABLE_INCREMENTAL\s*=') { Fail 'doorstop_config.ini に GC_DISABLE_INCREMENTAL がある (incremental GC は mod が起動時に決める)' }

# 3. Il2CppInterop 1.5.3 (Runtime は参照の書き込みが GC の書き込みバリアを通るように書き換える)
foreach ($f in 'Il2CppInterop.Common.dll', 'Il2CppInterop.Common.xml', 'Il2CppInterop.Runtime.deps.json', 'Il2CppInterop.Runtime.xml') {
    Copy-Item (Join-Path $repo "packaging/il2cppinterop/$f") (Join-Path $bie "core/$f") -Force
}
$runtimeOut = Join-Path $bie 'core/Il2CppInterop.Runtime.dll'
Remove-Item $runtimeOut -Force -ErrorAction SilentlyContinue
& dotnet run --project (Join-Path $repo 'tools/InteropWbPatch') -c Release -v q -- (Join-Path $repo 'packaging/il2cppinterop/Il2CppInterop.Runtime.dll') $runtimeOut | Select-Object -Last 1
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $runtimeOut)) { Fail 'InteropWbPatch が失敗した' }
$md5 = (Get-FileHash $runtimeOut -Algorithm MD5).Hash
if (-not $md5.StartsWith($expectRuntimeMd5)) { Fail "書き換えた Il2CppInterop.Runtime.dll の md5 が違う ($md5)" }

# 4. mod 本体と起動の高速化
& dotnet build (Join-Path $repo 'patcher/MoreRolesPlus.BootAccel.csproj') -c Release -v:q -nologo -p:MrpNoDeploy=true | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { Fail 'BootAccel のビルドに失敗' }
Copy-Item (Join-Path $repo 'patcher/bin/Release/MoreRolesPlus.BootAccel.dll') (Join-Path $bie 'patchers') -Force
Copy-Item $Dll (Join-Path $bie 'plugins/MoreRolesPlus.dll') -Force
Copy-Item (Join-Path $repo 'packaging/config/BepInEx.cfg') (Join-Path $bie 'config') -Force

# 5. ライセンス
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $Stage 'LICENSE.txt') -Force
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $Stage -Force
Copy-Item (Join-Path $repo 'licenses') (Join-Path $Stage 'licenses') -Recurse -Force

# 6. ビット数 (ゲームと違うと doorstop が読まれず mod が黙って動かない)
foreach ($native in 'winhttp.dll', 'dotnet/coreclr.dll', 'BepInEx/core/dobby.dll') {
    $m = Machine (Join-Path $Stage $native)
    if ($m -ne 0x8664) { Fail ("{0} が x64 でない (machine=0x{1:X4})" -f $native, $m) }
}
$count = (Get-ChildItem $Stage -Recurse -File -Force).Count
Write-Output "[ OK ] payload: $Stage ($count files)"

if ($Zip) {
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    # Windows PowerShell 5.1 の Compress-Archive は区切りが \ の zip を作るので、.NET の ZipFile で / 区切りにする
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::Open($Zip, 'Create')
    try {
        $root = (Resolve-Path $Stage).Path.TrimEnd('\') + '\'
        foreach ($file in Get-ChildItem $Stage -Recurse -File -Force) {
            $name = $file.FullName.Substring($root.Length).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $file.FullName, $name, 'Optimal') | Out-Null
        }
    }
    finally { $z.Dispose() }
    Write-Output ("[ OK ] zip: {0} ({1:N1} MB)" -f $Zip, ((Get-Item $Zip).Length / 1MB))
}
