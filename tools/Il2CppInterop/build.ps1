# packaging/il2cppinterop/ を公開のソースから作る。
#   Pietrodjaowjao/Il2CppInterop@f1627d60 (LGPL-3.0) に tools/Il2CppInterop/il2cppinterop.patch を当て、手元の SDK でビルドする。
#   Runtime の書き込みバリアの書き換え (tools/InteropWbPatch) は installer/make-payload.ps1 が後で掛ける。
# 使い方: pwsh -NoProfile -File tools/Il2CppInterop/build.ps1 [-Out packaging/il2cppinterop]
param([string] $Out)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Out) { $Out = Join-Path $repo 'packaging/il2cppinterop' }
$interopRepo = 'https://github.com/Pietrodjaowjao/Il2CppInterop.git'
$interopCommit = 'f1627d60efc218e4861bacc1239267f96f32ee2c'
$files = @('Il2CppInterop.Common.dll', 'Il2CppInterop.Common.xml', 'Il2CppInterop.Runtime.dll', 'Il2CppInterop.Runtime.xml', 'Il2CppInterop.Runtime.deps.json')

$work = Join-Path ([IO.Path]::GetTempPath()) "mrp-il2cppinterop-$([Guid]::NewGuid().ToString('N'))"
try {
    # パッチは LF なので、ソースはリポジトリの改行のまま取る
    $src = Join-Path $work 'src'
    git init -q $src
    git -C $src config core.autocrlf false
    git -C $src fetch -q --depth 1 $interopRepo $interopCommit
    if ($LASTEXITCODE -ne 0) { throw "Il2CppInterop の取得に失敗 ($LASTEXITCODE)" }
    git -C $src checkout -q FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw "Il2CppInterop の checkout に失敗 ($LASTEXITCODE)" }
    git -C $src apply (Join-Path $PSScriptRoot 'il2cppinterop.patch')
    if ($LASTEXITCODE -ne 0) { throw "il2cppinterop.patch を当てられない ($LASTEXITCODE)" }
    # このリポジトリの global.json で SDK を選ぶ (Il2CppInterop 側の global.json は SDK 8 を指す)
    Push-Location $repo
    try {
        dotnet build (Join-Path $src 'Il2CppInterop.Runtime/Il2CppInterop.Runtime.csproj') -c Release -v:q -nologo -p:GeneratePackageOnBuild=false -p:ContinuousIntegrationBuild=true -p:SourceRevisionId=f1627d60.mrp
        if ($LASTEXITCODE -ne 0) { throw "Il2CppInterop のビルドに失敗 ($LASTEXITCODE)" }
    }
    finally { Pop-Location }

    $built = Join-Path $src 'bin/Il2CppInterop.Runtime/net6.0'
    New-Item -ItemType Directory -Force $Out | Out-Null
    foreach ($f in $files) { Copy-Item (Join-Path $built $f) (Join-Path $Out $f) -Force }
    Write-Host "built: $Out"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
