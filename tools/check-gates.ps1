<#
.SYNOPSIS
  完成前の決定的ゲート: Release ビルド (ゲームへは配置しない) → Android ビルド → Android の libunity に無い API の照合。
.DESCRIPTION
  Android 版はゲーム純正の削られた libunity で動くため、無い API (ICall) を呼ぶとその瞬間に例外になる
  (読み込み時には何も起きない)。tools/AndroidIcallAudit で、mod から到達できる ICall のうち libunity に
  登録されていないものを列挙する。0 件で合格。
  Android の参照先 (端末ローダーが生成した core / interop) は local.props の AndroidBepInExPath。
  libunity.so は -LibUnity で渡すか、AndroidBepInExPath の隣の apk-extract\lib\arm64-v8a\libunity.so。
.PARAMETER SkipAndroid
  Android のビルドと照合を飛ばす (Android の参照先が無い環境用)。
#>
param(
    [string]$LibUnity,
    [switch]$SkipAndroid
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'MoreRolesPlus.csproj'
$fail = 0

function Step([string]$name, [scriptblock]$body) {
    Write-Output "== $name"
    $out = & $body 2>&1
    $code = $LASTEXITCODE
    $out | Select-String -Pattern 'error|エラー|FAIL|NEW |OK -|ビルドに成功|Build succeeded' | Select-Object -First 20 | ForEach-Object { Write-Output "   $($_.Line.Trim())" }
    if ($code -ne 0) { Write-Output "   [FAIL] exit $code"; $script:fail++ } else { Write-Output "   [ OK ]" }
}

# 1. Release (ゲームへの配置はしない: 配置先を存在しないパスにする)
Step 'Release build' { dotnet build $proj -c Release -v:q -nologo "-p:AmongUsPath=$repo\__no_deploy__" }

if (-not $SkipAndroid) {
    $localProps = Join-Path $repo 'local.props'
    $bep = $null
    if (Test-Path $localProps) {
        $m = [regex]::Match((Get-Content -Raw $localProps), '<AndroidBepInExPath>([^<]+)</AndroidBepInExPath>')
        if ($m.Success) { $bep = $m.Groups[1].Value.Trim() }
    }
    if (-not $bep -or -not (Test-Path (Join-Path $bep 'interop'))) {
        Write-Output '== Android: local.props の AndroidBepInExPath が無い/不正 → 飛ばす ([WARN])'
    }
    else {
        if (-not $LibUnity) { $LibUnity = Join-Path (Split-Path -Parent $bep) 'apk-extract\lib\arm64-v8a\libunity.so' }

        # 2. Android
        Step 'Android build' { dotnet build $proj -c Android -v:q -nologo }

        # 3. 照合
        if (-not (Test-Path $LibUnity)) {
            Write-Output "== Android ICall audit: libunity.so が無い ($LibUnity) → 飛ばす ([WARN])"
        }
        else {
            $baseline = Join-Path $env:TEMP 'mrp-icall-empty-baseline.txt'
            Set-Content -Path $baseline -Value '' -NoNewline
            $dll = Join-Path $repo 'bin\Android\net10.0\MoreRolesPlus.dll'
            Step 'Android ICall audit' { dotnet run --project (Join-Path $PSScriptRoot 'AndroidIcallAudit') -c Release -v:q -- audit --mod $dll --interop (Join-Path $bep 'interop') --libunity $LibUnity --baseline $baseline }
        }
    }
}

if ($fail -gt 0) { Write-Output "RESULT: FAIL ($fail)"; exit 1 }
Write-Output 'RESULT: OK'
