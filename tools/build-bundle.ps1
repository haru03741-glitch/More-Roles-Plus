<#
.SYNOPSIS
  unity/MrpBundle を Unity 2022.3.44f1 (Among Us と同じ版) のバッチモードで開き、シェーダと素材を
  AssetBundle mrp_fx に焼いて Resources/Bundles/ に置く (csproj が DLL に埋め込む)。
.DESCRIPTION
  シェーダや素材を変えた時だけ手で実行する (dotnet build には組み込まない)。
  -Android: BuildTarget=Android で焼き mrp_fx.android.bundle として置く (Editor に Android Build Support が要る)。
#>
param(
    [string]$UnityExe = 'D:\Unity\Editor\2022.3.44f1\Editor\Unity.exe',
    [switch]$Android
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'unity\MrpBundle'
$buildDir = if ($Android) { 'Build\android' } else { 'Build' }
$suffix = if ($Android) { '.android.bundle' } else { '.bundle' }
$method = if ($Android) { 'MrpBundleBuilder.BuildAndroid' } else { 'MrpBundleBuilder.Build' }
$out = Join-Path $proj "$buildDir\mrp_fx"
$log = Join-Path $proj 'Logs\build-bundle.log'

if (-not (Test-Path $UnityExe)) { throw "Unity editor not found: $UnityExe" }
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null

$argList = @('-batchmode', '-nographics', '-quit', '-projectPath', ('"' + $proj + '"'), '-executeMethod', $method, '-logFile', ('"' + $log + '"'))
$p = Start-Process -FilePath $UnityExe -ArgumentList $argList -PassThru -Wait
if ($p.ExitCode -ne 0) {
    Select-String -Path $log -Pattern 'error CS|MrpBundleBuilder|Exception|Shader error' | Select-Object -First 10 | ForEach-Object { Write-Host $_.Line }
    throw "Unity exited with $($p.ExitCode) (log: $log)"
}
if (-not (Test-Path $out)) { throw "bundle not produced: $out" }

$dstDir = Join-Path $repo 'Resources\Bundles'
New-Item -ItemType Directory -Force $dstDir | Out-Null
$dst = Join-Path $dstDir "mrp_fx$suffix"
Copy-Item $out $dst -Force
Write-Host ("bundle: {0} ({1:N0} bytes)" -f $dst, (Get-Item $dst).Length)
Select-String -Path $log -Pattern 'MrpBundleBuilder:' | Select-Object -First 1 | ForEach-Object { Write-Host $_.Line }
