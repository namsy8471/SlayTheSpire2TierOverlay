<#
  러스트 앱(rust/crates/ov-app)을 이 PC에서 빌드해 GitHub Releases에 올립니다. 레포 맨 위의 release.cmd가 이 스크립트를 실행합니다.

    release.cmd 0.2.0 -AdvisorZip <zip> -DryRun   검사와 빌드·묶기까지 하고, 올리는 명령은 보여 주기만 합니다.
    release.cmd 0.2.0 -AdvisorZip <zip>           검사 → 테스트 → 빌드 → 묶기 → vpk pack → vpk upload(공개).
    -NotesFile notes.md   릴리스 노트 파일 (기본: artifacts\release-notes\v<버전>.md, 없으면 멈춤)
    -AdvisorSha256 <hex>  도우미 zip의 SHA-256 (기본: zip 옆의 <zip>.sha256 파일)
    -Yes                  확인 질문을 건너뜁니다.   -NoPause  끝나고 키 입력을 기다리지 않습니다.

  도우미 zip: 비공개 sts2-advisor.exe와 그 데이터(decide\*.args, cards\defect\..., macro\..., combat-value-*.json,
  lasting-cards.tsv)를 담은 zip입니다. 이 스크립트는 zip을 publish\advisor\에 그대로 풉니다. 앱은
  advisor\sts2-advisor.exe를 찾아 그 폴더를 --data로 넘깁니다.
  publish\: Sts2Overlay.exe, data\catalog\*.json(git이 추적하는 파일만), advisor\.

  사용자 경로가 exe에 남지 않도록 RUSTFLAGS에 --remap-path-prefix를 넣고, 빌드 뒤 exe 안에 사용자 폴더 문자열이
  있는지 검사합니다. Windows PowerShell 5.1과 PowerShell 7 모두에서 돕니다. 종료 코드: 0 성공, 1 실패.
#>
param(
    [Parameter(Position = 0)]
    [string]$Version,
    [string]$AdvisorZip,
    [string]$AdvisorSha256,
    [string]$NotesFile,
    [switch]$DryRun,
    [switch]$Yes,
    [switch]$NoPause
)

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$RepoUrl = 'https://github.com/namsy8471/SlayTheSpire2TierOverlay'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
$script:problems = @()
$script:token = $null

function Finish([int]$code) {
    if (-not $NoPause) { Write-Host ''; Read-Host '엔터를 누르면 창이 닫힙니다' | Out-Null }
    exit $code
}
function Fail([string]$message) {
    Write-Host ''
    Write-Host $message -ForegroundColor Red
    Write-Host '아무것도 올리지 않았습니다.' -ForegroundColor Red
    Finish 1
}
function Problem([string]$message) {
    if ($DryRun) { Write-Host "  [문제] $message" -ForegroundColor Red; $script:problems += $message }
    else { Fail $message }
}
# 외부 명령. 토큰은 화면에 가립니다. $Upload: -DryRun이면 보여 주기만 합니다 (빌드는 -DryRun에서도 실행).
function Invoke-Native([string]$exe, [string[]]$arguments, [switch]$AllowFail, [switch]$Upload) {
    $shown = @($arguments | ForEach-Object {
            if ($script:token -and $_ -eq $script:token) { '<token>' } elseif ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
        }) -join ' '
    Write-Host "> $exe $shown" -ForegroundColor DarkGray
    if ($DryRun -and $Upload) { return $true }
    & $exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFail) { return $false }
        Fail "명령이 실패했습니다 (종료 코드 $LASTEXITCODE): $exe $($arguments[0]) $($arguments[1])"
    }
    return $true
}

$SemVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*)?$'
function Compare-SemVer([string]$a, [string]$b) {
    $pa = $a.Split('-', 2); $pb = $b.Split('-', 2)
    $ca = $pa[0].Split('.'); $cb = $pb[0].Split('.')
    for ($i = 0; $i -lt 3; $i++) {
        $x = [long]$ca[$i]; $y = [long]$cb[$i]
        if ($x -ne $y) { if ($x -lt $y) { return -1 } else { return 1 } }
    }
    $hasA = $pa.Count -gt 1; $hasB = $pb.Count -gt 1
    if (-not $hasA -and -not $hasB) { return 0 }
    if (-not $hasA) { return 1 }
    if (-not $hasB) { return -1 }
    $ia = $pa[1].Split('.'); $ib = $pb[1].Split('.')
    for ($i = 0; $i -lt [Math]::Min($ia.Count, $ib.Count); $i++) {
        $na = $ia[$i] -match '^\d+$'; $nb = $ib[$i] -match '^\d+$'
        if ($na -and $nb) { $x = [long]$ia[$i]; $y = [long]$ib[$i]; if ($x -ne $y) { if ($x -lt $y) { return -1 } else { return 1 } } }
        elseif ($na) { return -1 } elseif ($nb) { return 1 }
        else { $c = [string]::CompareOrdinal($ia[$i], $ib[$i]); if ($c -ne 0) { if ($c -lt 0) { return -1 } else { return 1 } } }
    }
    if ($ia.Count -eq $ib.Count) { return 0 }
    if ($ia.Count -lt $ib.Count) { return -1 } else { return 1 }
}

# ---- 1. 버전과 git ----
if (-not $Version) { $Version = Read-Host '올릴 버전 (예: 0.2.0)' }
$Version = $Version.Trim()
if ($Version -match '^[vV]\d') { Fail "버전 앞의 v는 빼고 입력해 주세요: '$Version'" }
if ($Version -notmatch $SemVerPattern) { Fail "'$Version'은(는) SemVer 2 버전이 아닙니다 (예: 0.2.0)." }
$tag = "v$Version"
$isPre = $Version.Contains('-')
$mode = '러스트 앱을 이 PC에서 빌드해 공개'
if ($DryRun) { $mode += ' (시험 실행: 업로드 안 함)' }
Write-Host "버전      : $Version (태그 $tag)"
Write-Host "방식      : $mode"

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git을 찾지 못했습니다.' }
git fetch --quiet --tags origin
if ($LASTEXITCODE -ne 0) { Fail 'git fetch가 실패했습니다. 인터넷 연결과 GitHub 로그인을 확인해 주세요.' }
$branch = git rev-parse --abbrev-ref HEAD
$head = git rev-parse HEAD
Write-Host "브랜치    : $branch"
Write-Host "마지막 커밋: $(git log -1 --format='%h %s (%ci)')"
$dirty = @(git status --porcelain --untracked-files=no)
if ($dirty.Count -gt 0) { Problem '커밋 안 된 변경이 있습니다. 되돌리거나 커밋·push한 뒤 다시 실행해 주세요.' }
$upstream = git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
if ($LASTEXITCODE -ne 0 -or -not $upstream) { Problem "'$branch'에 upstream이 없습니다. main에서 실행해 주세요." }
else {
    $counts = (git rev-list --left-right --count "HEAD...$upstream") -split '\s+'
    if ([int]$counts[0] -gt 0) { Problem "push 안 한 커밋이 $($counts[0])개 있습니다." }
    if ([int]$counts[1] -gt 0) { Problem "받지 않은 커밋이 $($counts[1])개 있습니다. 'git pull --ff-only' 후 다시 실행해 주세요." }
}
git rev-parse -q --verify "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -eq 0) { Problem "태그 $tag 가 이미 로컬에 있습니다." }
if (git ls-remote --tags origin "refs/tags/$tag") { Problem "태그 $tag 가 이미 GitHub에 있습니다." }
$prevTag = $null
foreach ($t in @(git tag --list 'v*')) {
    $v = $t.Substring(1)
    if ($v -notmatch $SemVerPattern) { continue }
    if (-not $prevTag -or (Compare-SemVer $v $prevTag.Substring(1)) -gt 0) { $prevTag = $t }
}
if ($prevTag) {
    Write-Host "이전 태그 : $prevTag"
    if ((Compare-SemVer $Version $prevTag.Substring(1)) -le 0) { Problem "새 버전 $Version 이 이전 태그 $prevTag 보다 크지 않습니다." }
}

$cargoVersion = (Select-String -LiteralPath (Join-Path $repo 'rust\Cargo.toml') -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1).Matches.Groups[1].Value
if ($cargoVersion -ne $Version) { Problem "rust\Cargo.toml의 version($cargoVersion)이 $Version 과 다릅니다. 앱이 보여 주는 버전이 틀려집니다." }

# ---- 2. 도구, 토큰, 도우미 zip, 노트 ----
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { Problem 'cargo(Rust)를 찾지 못했습니다. rustup을 설치해 주세요.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Problem 'dotnet을 찾지 못했습니다 (vpk 도구에 필요).' }
if ($env:GITHUB_TOKEN) { $script:token = $env:GITHUB_TOKEN.Trim() }
elseif (Get-Command gh -ErrorAction SilentlyContinue) {
    $t = gh auth token 2>$null
    if ($LASTEXITCODE -eq 0 -and $t) { $script:token = ([string]$t).Trim() }
}
if (-not $script:token) { Problem "GitHub 토큰이 없습니다. 'gh auth login' 후 다시 실행해 주세요."; $script:token = '<token>' }

if (-not $AdvisorZip) { $AdvisorZip = Join-Path $repo "artifacts\advisor-$Version.zip" }
if (-not (Test-Path -LiteralPath $AdvisorZip)) { Fail "도우미 zip이 없습니다: $AdvisorZip (-AdvisorZip으로 경로를 주세요)." }
$AdvisorZip = (Resolve-Path -LiteralPath $AdvisorZip).Path
if (-not $AdvisorSha256) {
    $shaFile = "$AdvisorZip.sha256"
    if (Test-Path -LiteralPath $shaFile) { $AdvisorSha256 = ((Get-Content -LiteralPath $shaFile -Raw).Trim() -split '\s+')[0] }
}
$actualSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $AdvisorZip).Hash.ToLowerInvariant()
if (-not $AdvisorSha256) { Problem "도우미 zip의 SHA-256을 확인할 값이 없습니다 (-AdvisorSha256 또는 $AdvisorZip.sha256). 실제 값: $actualSha" }
elseif ($AdvisorSha256.ToLowerInvariant() -ne $actualSha) { Problem "도우미 zip의 SHA-256이 다릅니다. 기대 $AdvisorSha256, 실제 $actualSha" }
Write-Host "도우미 zip: $AdvisorZip (SHA-256 $($actualSha.Substring(0, 16))...)"

if (-not $NotesFile) { $NotesFile = Join-Path $repo "artifacts\release-notes\$tag.md" }
if (-not (Test-Path -LiteralPath $NotesFile)) { Fail "릴리스 노트 파일이 없습니다: $NotesFile" }
$NotesFile = (Resolve-Path -LiteralPath $NotesFile).Path
$noteLines = @([System.IO.File]::ReadAllLines($NotesFile, [System.Text.Encoding]::UTF8) | Where-Object { $_.TrimStart() -notlike '<!--*' } | ForEach-Object { $_.TrimEnd() })
$notesText = ($noteLines -join "`n").Trim()
if (-not $notesText) { Problem "릴리스 노트가 비어 있습니다: $NotesFile" }
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
New-Item -ItemType Directory -Force -Path (Join-Path $repo 'artifacts\release-notes') | Out-Null
$cleanNotes = Join-Path $repo "artifacts\release-notes\$tag.clean.md"
[System.IO.File]::WriteAllText($cleanNotes, $notesText + "`n", $Utf8NoBom)
Write-Host "노트      : $NotesFile"

if (-not $DryRun -and -not $Yes) {
    Write-Host ''
    Write-Host "이 PC에서 $tag 를 빌드해 GitHub Releases에 공개합니다. 설치된 모든 사용자가 자동 업데이트로 받습니다." -ForegroundColor Cyan
    if ((Read-Host '계속할까요? (y/N)') -notmatch '^[yY]') { Fail '취소했습니다.' }
}

# ---- 3. 테스트와 빌드 (-DryRun에서도 실행) ----
$started = Get-Date
foreach ($dir in @('publish', 'releases')) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}
# 사용자 경로(레포 위치, 사용자 폴더)가 exe의 패닉 메시지 등에 남지 않게 바꿉니다.
$cargoHome = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
$env:RUSTFLAGS = "--remap-path-prefix=$repo=src --remap-path-prefix=$cargoHome=cargo --remap-path-prefix=$($env:USERPROFILE)=home"
$rust = Join-Path $repo 'rust'
Push-Location -LiteralPath $rust
Invoke-Native cargo @('test', '--workspace', '--release') | Out-Null
Invoke-Native cargo @('build', '--release', '-p', 'ov-app') | Out-Null
Pop-Location

$exe = Join-Path $rust 'target\release\Sts2Overlay.exe'
if (-not (Test-Path -LiteralPath $exe)) { Fail "빌드한 exe가 없습니다: $exe" }
# 사용자 이름이 exe 안에 남았는지 검사 (ASCII와 UTF-16 둘 다).
$bytes = [System.IO.File]::ReadAllBytes($exe)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
$wide = [System.Text.Encoding]::Unicode.GetString($bytes)
foreach ($needle in @($env:USERPROFILE, $repo) | Where-Object { $_ }) {
    if ($ascii.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $wide.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Fail "exe 안에 이 PC의 경로가 남아 있습니다 ($needle). 올리지 않습니다."
    }
}

# ---- 4. publish\ 묶기 ----
$publish = Join-Path $repo 'publish'
New-Item -ItemType Directory -Force -Path (Join-Path $publish 'data\catalog') | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $publish 'Sts2Overlay.exe')
$catalog = @(git ls-files 'data/catalog/*.json')
if ($catalog.Count -eq 0) { Fail 'data/catalog에 추적되는 파일이 없습니다.' }
foreach ($f in $catalog) { Copy-Item -LiteralPath (Join-Path $repo $f) -Destination (Join-Path $publish 'data\catalog') }
$advisorDir = Join-Path $publish 'advisor'
Expand-Archive -LiteralPath $AdvisorZip -DestinationPath $advisorDir -Force
foreach ($need in @('sts2-advisor.exe', 'decide', 'combat-value-R01.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $advisorDir $need))) { Fail "도우미 zip에 $need 가 없습니다." }
}
$files = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
$mb = [Math]::Round((($files | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
Write-Host "publish\  : 파일 $($files.Count)개, $mb MB (catalog $($catalog.Count)개)"

# ---- 5. Velopack ----
$preArgs = @()
if ($isPre) { $preArgs = @('--pre') }
Invoke-Native dotnet @('tool', 'restore') | Out-Null
$got = Invoke-Native dotnet (@('vpk', 'download', 'github', '--repoUrl', $RepoUrl, '--outputDir', 'releases', '--token', $script:token) + $preArgs) -AllowFail
if (-not $got) { Write-Host '이전 릴리스를 받지 못했습니다. 델타 없이 패키지를 만듭니다.' -ForegroundColor Yellow }
# --framework 없음: 러스트 앱은 .NET이 필요 없습니다. --packTitle을 빼면 바로가기 이름이 바뀝니다.
Invoke-Native dotnet @('vpk', 'pack', '--packId', 'namsy8471.Sts2Overlay', '--packVersion', $Version, '--packDir', 'publish',
    '--mainExe', 'Sts2Overlay.exe', '--packTitle', 'STS2 티어 오버레이', '--packAuthors', 'namsy8471',
    '--icon', 'rust/crates/ov-app/assets/Sts2Overlay.ico', '--runtime', 'win-x64',
    '--releaseNotes', $cleanNotes, '--outputDir', 'releases') | Out-Null
Invoke-Native dotnet (@('vpk', 'upload', 'github', '--repoUrl', $RepoUrl, '--outputDir', 'releases', '--publish',
        '--tag', $tag, '--releaseName', "STS2 티어 오버레이 $tag", '--targetCommitish', $head,
        '--token', $script:token) + $preArgs) -Upload | Out-Null
if (Get-Command gh -ErrorAction SilentlyContinue) {
    $edited = Invoke-Native gh @('release', 'edit', $tag, '--repo', $RepoUrl, '--notes-file', $cleanNotes) -AllowFail -Upload
    if (-not $edited) { Write-Host "릴리스 본문을 넣지 못했습니다. 'gh release edit $tag --notes-file $cleanNotes'로 넣어 주세요." -ForegroundColor Yellow }
}
Invoke-Native git @('fetch', '--quiet', '--tags', 'origin') -AllowFail -Upload | Out-Null

Write-Host ''
$seconds = [int]((Get-Date) - $started).TotalSeconds
if ($script:problems.Count -gt 0) {
    Write-Host "검사에서 문제 $($script:problems.Count)개가 있었습니다. 고친 뒤 다시 실행해 주세요." -ForegroundColor Red
    Finish 1
}
if ($DryRun) { Write-Host "시험 실행 끝 ($seconds 초): releases\ 에 패키지가 있습니다. -DryRun을 빼면 공개합니다." -ForegroundColor Green }
else { Write-Host "릴리스 $tag 를 공개했습니다 ($seconds 초): $RepoUrl/releases/tag/$tag" -ForegroundColor Green }
Finish 0
