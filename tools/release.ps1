<#
.SYNOPSIS
  配布用の MoreRolesPlus.dll を組み、GitHub のリリース (タグ v<版>) に添付する。
.DESCRIPTION
  ゲーム内の更新ボタンは GitHub のリリースの一番新しい物 (プレリリースも含む) に付いた MoreRolesPlus.dll を落とす。
  配布用の DLL には床マスク (Resources/FloorMasks) と開発者の名簿 (src/Dev/DevUsers.Local.cs) を埋め込むが、
  どちらもリポジトリに入れていないので CI では組めない。この手元のスクリプトで組んで添付する。
  手順: 版の一致 (src/Plugin.cs の Version) → HEAD が origin/main に入っているか → 床マスク 5 枚と名簿 →
  tools/check-gates.ps1 (Release + Android + 照合) → タグを作って push → リリースを作って DLL を添付。
.PARAMETER Version
  出す版 (例 0.1.0)。src/Plugin.cs の Version と同じでないと止まる。
.PARAMETER Prerelease
  プレリリースとして出す (更新ボタンはプレリリースも拾う)。
.PARAMETER NotesFile
  リリースの説明文のファイル。無ければ前のタグからのコミットの件名を並べる。
.PARAMETER DryRun
  確かめと組み立てだけをして、タグもリリースも作らない。
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Prerelease,
    [string]$NotesFile,
    [switch]$DryRun
)
# git は進捗を標準エラーへ書く (5.1 の Stop だとそこで止まる) ので、外のコマンドは終了コードで判定する
$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"

function Fail([string]$msg) { Write-Output "[FAIL] $msg"; exit 1 }

Push-Location $repo
try {
    # 1. 版の一致 (更新ボタンはタグの版と Plugin.Version を比べる)
    $m = [regex]::Match((Get-Content -Raw 'src/Plugin.cs'), 'const string Version = "([^"]+)"')
    if (-not $m.Success) { Fail 'src/Plugin.cs に Version が見つからない' }
    if ($m.Groups[1].Value -ne $Version) { Fail "src/Plugin.cs の Version は $($m.Groups[1].Value) (出す版は $Version)" }

    # 2. 出すコミットが公開済みで、同じタグがまだ無い
    git fetch -q origin
    if ($LASTEXITCODE -ne 0) { Fail 'git fetch に失敗' }
    git merge-base --is-ancestor HEAD origin/main
    if ($LASTEXITCODE -ne 0) { Fail 'HEAD が origin/main に入っていない (先に push する)' }
    if (git tag --list $tag) { Fail "タグ $tag はもうある" }
    # DLL に入る物 (src・Resources・native・csproj) にコミットしていない変更があると、タグの中身と配る DLL が食い違う
    $dirty = @(git status --porcelain -- src Resources native MoreRolesPlus.csproj)
    if ($dirty.Count -gt 0) {
        $dirty | Select-Object -First 10 | ForEach-Object { Write-Output "   $_" }
        if (-not $DryRun) { Fail 'DLL に入る物にコミットしていない変更がある' }
        Write-Output '[DRY RUN] コミットしていない変更があるが続ける'
    }

    # 3. リポジトリに入れていない埋め込み物
    $masks = @(Get-ChildItem 'Resources/FloorMasks/*.bin' -ErrorAction SilentlyContinue).Count
    if ($masks -lt 5) { Fail "床マスクが $masks 枚 (5 マップ分要る・tools/make-floor-mask.py)" }
    if (-not (Test-Path 'src/Dev/DevUsers.Local.cs')) { Fail 'src/Dev/DevUsers.Local.cs が無い' }

    # 4. 決定的ゲート (Release を組む・Android も)
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'check-gates.ps1') | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { Fail 'check-gates が通らない' }

    $outDir = Join-Path $repo 'build/release'
    New-Item -ItemType Directory -Force -Path $outDir -ErrorAction Stop | Out-Null
    $dll = Join-Path $outDir 'MoreRolesPlus.dll'
    Copy-Item 'bin/Release/net6.0/MoreRolesPlus.dll' $dll -Force -ErrorAction Stop
    $hash = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLower()
    Write-Output "DLL: $dll ($((Get-Item $dll).Length) B, sha256 $hash)"

    if (-not $NotesFile) {
        $prev = git describe --tags --abbrev=0 2>$null
        $range = if ($LASTEXITCODE -eq 0 -and $prev) { "$prev..HEAD" } else { 'HEAD' }
        $NotesFile = Join-Path $outDir 'notes.md'
        $lines = @(git log --no-merges --format='- %s' $range | Select-Object -First 200)
        Set-Content -Path $NotesFile -Value ($lines + '' + "SHA-256 (MoreRolesPlus.dll): $hash") -Encoding utf8
    }

    if ($DryRun) { Write-Output "[DRY RUN] $tag は作っていない (説明文 = $NotesFile)"; exit 0 }

    # 5. タグとリリース
    git tag -a $tag -m "More Roles Plus $tag"
    if ($LASTEXITCODE -ne 0) { Fail 'タグを作れない' }
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { Fail 'タグを push できない' }
    $relArgs = @('release', 'create', $tag, $dll, '--title', "More Roles Plus $tag", '--notes-file', $NotesFile)
    if ($Prerelease) { $relArgs += '--prerelease' }
    & gh @relArgs
    if ($LASTEXITCODE -ne 0) { Fail 'リリースを作れない (タグは push 済み)' }
    Write-Output "[ OK ] $tag"
}
finally { Pop-Location }
