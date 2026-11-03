# MRP のリリースに載せる物を作る (公開はしない)。
#   1. src/Plugin.cs の Version を上げる (例: "0.0.2")
#   2. powershell -File installer/make-release.ps1
#   3. 表示される release/vX.Y.Z の 3 つを、GitHub の Releases でタグ vX.Y.Z として公開する
# インストーラーはタグの版と見比べて、古ければ自分 → MRP の順に更新を勧める
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$version = [regex]::Match((Get-Content "$repo\src\Plugin.cs" -Raw), 'const string Version = "([0-9.]+)"').Groups[1].Value
if (-not $version) { throw "src/Plugin.cs に Version が見つからない" }

# 配る DLL は公開しているソースだけで組む
dotnet build "$repo\MoreRolesPlus.csproj" -c Release -p:MrpPublicBuild=true
if ($LASTEXITCODE) { throw "MRP のビルドに失敗" }
$out = "$repo\release\v$version"
New-Item -ItemType Directory -Force $out | Out-Null
# インストーラーに埋め込む中身 (BepInEx 一式 + MRP) と、手で入れる人向けの同じ中身の zip
& powershell -NoProfile -ExecutionPolicy Bypass -File "$repo\installer\make-payload.ps1" -Zip "$out\MoreRolesPlus-v$version.zip"
if ($LASTEXITCODE) { throw "インストーラーの中身を組めない" }
dotnet build "$repo\installer\MrpInstaller\MrpInstaller.csproj" -c Release
if ($LASTEXITCODE) { throw "インストーラーのビルドに失敗" }
dotnet build "$repo\server\MrpServer\MrpServer.csproj" -c Release
if ($LASTEXITCODE) { throw "サーバー用プラグインのビルドに失敗" }

Copy-Item "$repo\bin\Release\net6.0\MoreRolesPlus.dll" $out -Force
Copy-Item "$repo\installer\MrpInstaller\bin\Release\MoreRolesPlusInstaller.exe" $out -Force
# インストール式: 同じ exe を Setup の名前で置く (名前を見て、このPCへ入れる動きになる)
Copy-Item "$repo\installer\MrpInstaller\bin\Release\MoreRolesPlusInstaller.exe" "$out\MoreRolesPlusSetup.exe" -Force
# サーバー用 (Impostor の plugins へ置く物。リリースには添付しない)
New-Item -ItemType Directory -Force "$out\server" | Out-Null
Copy-Item "$repo\server\MrpServer\bin\Release\net8.0\MrpServer.dll" "$out\server" -Force

Write-Host ""
Write-Host "できました: $out" -ForegroundColor Green
Get-ChildItem $out -File | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ""
Write-Host "GitHub の Releases → Draft a new release で、タグを v$version にして上の 4 つを添付し、公開してください。"
Write-Host "server\MrpServer.dll は MRP サーバー (Impostor) の plugins に置く物です (リリースには付けない)。"
Write-Host "(gh を使うなら: gh release create v$version `"$out\MoreRolesPlus.dll`" `"$out\MoreRolesPlusInstaller.exe`" `"$out\MoreRolesPlusSetup.exe`" `"$out\MoreRolesPlus-v$version.zip`" --title `"v$version`" --notes `"更新内容`")"
