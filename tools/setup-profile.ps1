<#
.SYNOPSIS
  Among Us のインストール先に、この mod 専用の BepInEx ツリー (BepInEx-MRP) を作る。
.DESCRIPTION
  既存の BepInEx/ から core / interop / unity-libs / config/BepInEx.cfg を複写する (元は読むだけで書き換えない)。
  plugins / patchers は複写しないので、他の mod とは混ざらない。何度実行してもよい。
  起動時にどちらのツリーを読むかは tools/launch.ps1 が doorstop の設定で切り替える。
.PARAMETER AmongUsPath
  ゲームのインストール先。
.PARAMETER Force
  既にあるファイルも上書きする (ゲーム更新で interop を作り直した後など)。
#>
param(
    [string]$AmongUsPath = "C:\Program Files\Epic Games\AmongUs",
    [switch]$Force
)
$ErrorActionPreference = "Stop"

$src = Join-Path $AmongUsPath "BepInEx"
$dst = Join-Path $AmongUsPath "BepInEx-MRP"
if (-not (Test-Path (Join-Path $src "core\BepInEx.Unity.IL2CPP.dll"))) { throw "BepInEx が見つからない: $src" }

foreach ($d in "core", "interop", "unity-libs") {
    $from = Join-Path $src $d
    if (-not (Test-Path $from)) { Write-Warning "skip (無い): $from"; continue }
    $to = Join-Path $dst $d
    New-Item -ItemType Directory -Force $to | Out-Null
    $n = 0
    Get-ChildItem $from -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($from.Length).TrimStart('\')
        $target = Join-Path $to $rel
        if ($Force -or -not (Test-Path $target)) {
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $_.FullName $target -Force
            $n++
        }
    }
    Write-Output "$d : $n files copied"
}

New-Item -ItemType Directory -Force (Join-Path $dst "plugins"), (Join-Path $dst "config") | Out-Null
$cfg = Join-Path $dst "config\BepInEx.cfg"
if ($Force -or -not (Test-Path $cfg)) { Copy-Item (Join-Path $src "config\BepInEx.cfg") $cfg -Force; Write-Output "config/BepInEx.cfg copied" }

Write-Output "OK profile ready: $dst"
