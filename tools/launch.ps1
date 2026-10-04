<#
.SYNOPSIS
  Among Us を BepInEx-MRP ツリーで 1 回だけ起動する。
.DESCRIPTION
  doorstop_config.ini の target_assembly を BepInEx-MRP に向けて起動し、プリローダーが
  BepInEx-MRP/LogOutput.log を書き始めたのを確認したら、成否にかかわらず元の設定へ戻す。
  起動中の Among Us がある時は何もしない (他の用途で使っている最中の切り替えを防ぐ)。
  Epic 版は exe を直接起動できないので、ランチャーの URI 経由で起動する。
.PARAMETER AmongUsPath
  ゲームのインストール先。
.PARAMETER TimeoutSec
  プリローダーの起動を待つ上限秒。
#>
param(
    [string]$AmongUsPath = "C:\Program Files\Epic Games\AmongUs",
    [int]$TimeoutSec = 120
)
$ErrorActionPreference = "Stop"

$EpicUrl = "com.epicgames.launcher://apps/963137e4c29d4c79a81323b8fab03a40?action=launch&silent=true"
$ini = Join-Path $AmongUsPath "doorstop_config.ini"
$profileRoot = Join-Path $AmongUsPath "BepInEx-MRP"
$mrpTarget = "BepInEx-MRP\core\BepInEx.Unity.IL2CPP.dll"
$log = Join-Path $profileRoot "LogOutput.log"

if (Get-Process -Name "Among Us" -ErrorAction SilentlyContinue) { Write-Output "ERR Among Us が起動中なので切り替えない"; exit 1 }
if (-not (Test-Path (Join-Path $profileRoot "core\BepInEx.Unity.IL2CPP.dll"))) { Write-Output "ERR 先に tools/setup-profile.ps1 を実行する"; exit 1 }

$original = [IO.File]::ReadAllText($ini)
if ($original -notmatch '(?m)^target_assembly=(.*)$') { Write-Output "ERR target_assembly 行が見つからない"; exit 1 }
$originalTarget = $Matches[1].Trim()
if ($originalTarget -eq $mrpTarget) { Write-Output "ERR doorstop が既に BepInEx-MRP を向いている (前回の復元漏れ)。手で元に戻してから再実行する"; exit 1 }

$backup = "$ini.mrp-launch.bak"
[IO.File]::WriteAllText($backup, $original)
$before = if (Test-Path $log) { (Get-Item $log).LastWriteTimeUtc } else { [datetime]::MinValue }

try {
    $patched = [regex]::Replace($original, '(?m)^target_assembly=.*$', "target_assembly=$mrpTarget")
    [IO.File]::WriteAllText($ini, $patched)

    # ランチャーが寝ていると 1 回目の URI は食われるので、プロセスが現れないまま十分待った時だけ 2 回目を叩く。
    # 早く叩きすぎると 2 つ目の起動が 1 つ目を落とし、設定を戻した後の AU (= 別ツリー) だけが残る
    Start-Process $EpicUrl
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $retryAt = (Get-Date).AddSeconds(25)
    $retried = $false
    $started = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ((Test-Path $log) -and (Get-Item $log).LastWriteTimeUtc -gt $before) { $started = $true; break }
        if (-not $retried -and (Get-Date) -gt $retryAt -and -not (Get-Process -Name "Among Us" -ErrorAction SilentlyContinue)) {
            Start-Process $EpicUrl
            $retried = $true
        }
    }
}
finally {
    [IO.File]::WriteAllText($ini, $original)
    Remove-Item $backup -ErrorAction SilentlyContinue
}

# 起動したのが本当にこのツリーの AU か確かめる (別の起動が割り込むと他のツリーのログが進む)
$otherLog = Join-Path $AmongUsPath "BepInEx\LogOutput.log"
$otherBefore = if (Test-Path $otherLog) { (Get-Item $otherLog).LastWriteTimeUtc } else { [datetime]::MinValue }
if ($started) {
    Start-Sleep -Seconds 8
    $procs = @(Get-Process -Name "Among Us" -ErrorAction SilentlyContinue)
    $otherMoved = (Test-Path $otherLog) -and (Get-Item $otherLog).LastWriteTimeUtc -gt $otherBefore
    if ($procs.Count -ne 1 -or $otherMoved) {
        Write-Output "ERR 起動が競合した (AU プロセス数=$($procs.Count), 他ツリーのログ更新=$otherMoved)。AU を閉じてやり直す"
        exit 1
    }
}

if ($started) { Write-Output "OK launched with BepInEx-MRP (doorstop restored to $originalTarget)" }
else { Write-Output "ERR ${TimeoutSec}s 以内に BepInEx-MRP/LogOutput.log が更新されなかった (doorstop は元に戻した)"; exit 1 }
