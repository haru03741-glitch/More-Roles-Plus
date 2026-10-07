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
  Android のビルドと照合を飛ばす (Android の参照先が無い環境用)。このときだけ結果に (ANDROID SKIPPED) が付く。
  付けずに Android の参照先が無い場合は不合格になる (確認していないものを合格と出さない)。
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
        # Android を確認していないのに合格と出すと、スマホ版で落ちる変更がそのまま通る。飛ばすのは -SkipAndroid を明示した時だけ
        Write-Output '== Android: local.props の AndroidBepInExPath が無い/不正 (-SkipAndroid なしでは不合格)'
        Write-Output '   [FAIL] Android の参照先を直すか、Android を確認できない環境なら -SkipAndroid を付ける'
        $fail++
    }
    else {
        if (-not $LibUnity) { $LibUnity = Join-Path (Split-Path -Parent $bep) 'apk-extract\lib\arm64-v8a\libunity.so' }

        # 2. Android
        Step 'Android build' { dotnet build $proj -c Android -v:q -nologo }

        # 3. 照合
        if (-not (Test-Path $LibUnity)) {
            Write-Output "== Android ICall audit: libunity.so が無い ($LibUnity)"
            Write-Output '   [FAIL] -LibUnity で渡すか、Android を確認できない環境なら -SkipAndroid を付ける'
            $fail++
        }
        else {
            $baseline = Join-Path $env:TEMP 'mrp-icall-empty-baseline.txt'
            Set-Content -Path $baseline -Value '' -NoNewline
            $dll = Join-Path $repo 'bin\Android\net10.0\MoreRolesPlus.dll'
            Step 'Android ICall audit' { dotnet run --project (Join-Path $PSScriptRoot 'AndroidIcallAudit') -c Release -v:q -- audit --mod $dll --interop (Join-Path $bep 'interop') --libunity $LibUnity --baseline $baseline }
        }
    }
}

# 4. il2cpp 配列の長さは (long) 明示 (net10 では int が nint のポインタ受けコンストラクタに解決されて壊れ配列になる)
Write-Output '== il2cpp array length cast'
$bad = Get-ChildItem (Join-Path $repo 'src') -Recurse -Filter *.cs |
    Select-String -Pattern 'new\s+Il2Cpp(Struct|Reference)Array<[^>]+>\s*\((?!\s*\(long\))[^)]|new\s+Il2CppStringArray\s*\((?!\s*\(long\))[^)]'
if ($bad) { $bad | ForEach-Object { Write-Output "   $($_.Path):$($_.LineNumber): $($_.Line.Trim())" }; Write-Output '   [FAIL] 長さに (long) を付ける'; $fail++ }
else { Write-Output '   [ OK ]' }

# 5. 場面に付いた本編の物は Vanilla から読む (X.Instance は無い時に空の X を作り、その更新が毎フレーム例外を出す)
Write-Output '== scene singleton access'
$bad = Get-ChildItem (Join-Path $repo 'src') -Recurse -Filter *.cs |
    Where-Object { $_.Name -ne 'Vanilla.cs' } |
    Select-String -Pattern '\b(HudManager|GameStartManager|LobbyInfoPane|StoreMenu)\.Instance\b'
if ($bad) { $bad | ForEach-Object { Write-Output "   $($_.Path):$($_.LineNumber): $($_.Line.Trim())" }; Write-Output '   [FAIL] Vanilla.Hud / StartManager / LobbyInfo / Store を使う'; $fail++ }
else { Write-Output '   [ OK ]' }

if ($fail -gt 0) { Write-Output "RESULT: FAIL ($fail)"; exit 1 }
if ($SkipAndroid) { Write-Output 'RESULT: OK (ANDROID SKIPPED)' } else { Write-Output 'RESULT: OK' }
