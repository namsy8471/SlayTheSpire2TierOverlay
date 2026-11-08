<#
  새 버전을 GitHub Releases에 올립니다. 레포 맨 위의 release.cmd를 더블클릭하면 이 스크립트가 실행됩니다.

    release.cmd 0.2.0               검사 → 확인 → 태그 v0.2.0을 만들어 push. 나머지(테스트·패키지·업로드)는
                                    GitHub Actions(.github/workflows/release.yml)가 합니다.
    release.cmd 0.2.0 -DryRun       검사만 하고 실행할 명령을 보여 줍니다. 태그·push·업로드는 하지 않습니다.
    release.cmd 0.2.0 -Local        Actions 대신 이 PC에서 테스트·publish·vpk pack·vpk upload까지 합니다.
                                    태그는 vpk upload(--tag)가 GitHub에 만듭니다.
                                    토큰: $env:GITHUB_TOKEN, 없으면 'gh auth token' (화면에 찍지 않습니다).
    release.cmd 0.2.0 -Yes          확인 질문을 건너뜁니다 (메모장도 열지 않으므로 -NotesFile과 함께 쓰세요).
    release.cmd 0.2.0 -NoPause      끝나고 키 입력을 기다리지 않습니다 (자동화용).
    release.cmd 0.2.0 -NotesFile notes.md
                                    릴리스 노트를 이 파일에서 읽습니다 (초안을 만들지 않음).

  릴리스 노트: 이전 태그 이후 병합된 PR 제목으로 초안을 만들어 artifacts\release-notes\v<버전>.md에 쓰고
  메모장으로 엽니다. 플레이어가 읽을 글로 고쳐 저장하고 메모장을 닫으면 계속합니다. 노트가 비어 있으면 멈춥니다.
  노트는 주석 태그(git tag -a)의 메시지가 되고, release.yml이 그것을 Velopack 패키지(--releaseNotes)와
  GitHub Release 본문에 넣습니다. '<!--'로 시작하는 줄(안내문)은 빠집니다.

  버전은 SemVer 2 (예: 0.1.0, 0.2.0-beta.1). 앞에 v는 붙이지 않습니다.
  검사: 추적 중인 파일에 커밋 안 된 변경 없음, HEAD가 upstream과 같음(git fetch 후), 태그 v<버전>이 로컬·원격에 없음,
        이전 최신 태그보다 큰 버전.
  Windows PowerShell 5.1과 PowerShell 7 모두에서 돕니다. 종료 코드: 0 성공, 1 실패.
#>
param(
    [Parameter(Position = 0)]
    [string]$Version,
    [switch]$Local,
    [switch]$DryRun,
    [switch]$Yes,
    [switch]$NoPause,
    [string]$NotesFile
)

# 'Stop'은 쓰지 않습니다: Windows PowerShell 5.1에서는 git/dotnet이 stderr에 쓰는 진행 메시지까지 오류로 바뀝니다.
# 외부 명령은 $LASTEXITCODE로 확인합니다.
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$RepoUrl = 'https://github.com/namsy8471/SlayTheSpire2TierOverlay'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo

$script:problems = @()

function Finish([int]$code) {
    if (-not $NoPause) {
        Write-Host ''
        Read-Host '엔터를 누르면 창이 닫힙니다' | Out-Null
    }
    exit $code
}

function Fail([string]$message) {
    Write-Host ''
    Write-Host $message -ForegroundColor Red
    Write-Host '아무것도 올리지 않았습니다.' -ForegroundColor Red
    Finish 1
}

# 검사 실패: 보통은 바로 멈추고, -DryRun이면 모아 두었다가 명령까지 다 보여 준 뒤 실패로 끝냅니다.
function Problem([string]$message) {
    if ($DryRun) {
        Write-Host "  [문제] $message" -ForegroundColor Red
        $script:problems += $message
    }
    else {
        Fail $message
    }
}

# 외부 명령 실행. 화면에는 토큰을 가린 명령줄을 보여 주고, -DryRun이면 보여 주기만 합니다.
function Invoke-Native([string]$exe, [string[]]$arguments, [switch]$AllowFail) {
    $shown = @($arguments | ForEach-Object {
            if ($script:token -and $_ -eq $script:token) { '<token>' }
            elseif ($_ -match '\s') { '"' + $_ + '"' }
            else { $_ }
        }) -join ' '
    Write-Host "> $exe $shown" -ForegroundColor DarkGray
    if ($DryRun) { return $true }
    & $exe @arguments | Out-Host   # 출력은 화면으로 (함수 반환값에 섞이지 않게)
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFail) { return $false }
        Fail "명령이 실패했습니다 (종료 코드 $LASTEXITCODE): $exe $($arguments[0]) $($arguments[1])"
    }
    return $true
}

# SemVer 2: MAJOR.MINOR.PATCH[-prerelease]. 빌드 메타데이터(+...)는 릴리스 버전에 쓰지 않습니다.
$SemVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*)?$'

# SemVer 우선순위 비교: a<b → -1, 같으면 0, a>b → 1 (둘 다 패턴을 통과한 문자열)
function Compare-SemVer([string]$a, [string]$b) {
    $pa = $a.Split('-', 2); $pb = $b.Split('-', 2)
    $ca = $pa[0].Split('.'); $cb = $pb[0].Split('.')
    for ($i = 0; $i -lt 3; $i++) {
        $x = [long]$ca[$i]; $y = [long]$cb[$i]
        if ($x -ne $y) { if ($x -lt $y) { return -1 } else { return 1 } }
    }
    $hasA = $pa.Count -gt 1; $hasB = $pb.Count -gt 1
    if (-not $hasA -and -not $hasB) { return 0 }
    if (-not $hasA) { return 1 }      # 1.0.0 > 1.0.0-beta
    if (-not $hasB) { return -1 }
    $ia = $pa[1].Split('.'); $ib = $pb[1].Split('.')
    $n = [Math]::Min($ia.Count, $ib.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $na = $ia[$i] -match '^\d+$'; $nb = $ib[$i] -match '^\d+$'
        if ($na -and $nb) {
            $x = [long]$ia[$i]; $y = [long]$ib[$i]
            if ($x -ne $y) { if ($x -lt $y) { return -1 } else { return 1 } }
        }
        elseif ($na) { return -1 }    # 숫자 식별자 < 문자 식별자
        elseif ($nb) { return 1 }
        else {
            $c = [string]::CompareOrdinal($ia[$i], $ib[$i])
            if ($c -ne 0) { if ($c -lt 0) { return -1 } else { return 1 } }
        }
    }
    if ($ia.Count -eq $ib.Count) { return 0 }
    if ($ia.Count -lt $ib.Count) { return -1 } else { return 1 }
}

if ($Local) { $mode = '이 PC에서 빌드·업로드 (-Local)' } else { $mode = '태그 push → GitHub Actions' }
if ($DryRun) { $mode += ', 시험 실행(-DryRun)' }
Write-Host "== STS2 티어 오버레이 릴리스: $mode ==" -ForegroundColor Cyan

# ---- 1. 버전 ----
if (-not $Version) {
    if ($NoPause -or -not [Environment]::UserInteractive) {
        Fail '버전을 지정해 주세요. 예: release.cmd 0.2.0'
    }
    $Version = Read-Host '올릴 버전 (예: 0.2.0, 0.2.0-beta.1)'
}
$Version = $Version.Trim()
if ($Version -match '^[vV]\d') {
    Fail "버전 앞의 v는 빼고 입력해 주세요: '$Version' → '$($Version.Substring(1))' (태그는 v가 자동으로 붙습니다)."
}
if ($Version -notmatch $SemVerPattern) {
    Fail "'$Version'은(는) SemVer 2 버전이 아닙니다. MAJOR.MINOR.PATCH 형식으로 입력해 주세요 (예: 0.2.0, 0.2.0-beta.1)."
}
$tag = "v$Version"
$isPre = $Version.Contains('-')
Write-Host "버전      : $Version (태그 $tag$(if ($isPre) { ', 사전 릴리스' }))"

# ---- 2. git 상태 ----
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git을 찾지 못했습니다.' }

Write-Host '원격 정보 받는 중 (git fetch --tags)...'
git fetch --quiet --tags origin
if ($LASTEXITCODE -ne 0) { Fail 'git fetch가 실패했습니다. 인터넷 연결과 GitHub 로그인을 확인해 주세요.' }

$branch = git rev-parse --abbrev-ref HEAD
$head = git rev-parse HEAD
Write-Host "브랜치    : $branch"
Write-Host "마지막 커밋: $(git log -1 --format='%h %s (%ci)')"
if ($branch -ne 'main') {
    Write-Host "  참고: main이 아닌 '$branch' 브랜치에서 릴리스합니다." -ForegroundColor Yellow
}

# 추적 중인 파일의 커밋 안 된 변경 (새 파일(untracked)은 빌드에 영향이 없다고 보고 넘어갑니다)
$dirty = @(git status --porcelain --untracked-files=no)
if ($dirty.Count -gt 0) {
    Write-Host '커밋 안 된 변경:' -ForegroundColor Yellow
    $dirty | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    Problem '커밋 안 된 변경이 있습니다. 커밋해서 push하거나 되돌린 뒤 다시 실행해 주세요 (릴리스는 push된 커밋으로만 만듭니다).'
}

# HEAD == upstream: 올릴 커밋이 GitHub에 그대로 있어야 Actions/vpk가 같은 코드를 빌드합니다.
$upstream = git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
if ($LASTEXITCODE -ne 0 -or -not $upstream) {
    Problem "'$branch' 브랜치에 연결된 원격 브랜치(upstream)가 없습니다. 먼저 'git push -u origin $branch'로 올려 주세요."
}
else {
    $counts = (git rev-list --left-right --count "HEAD...$upstream") -split '\s+'
    $ahead = [int]$counts[0]; $behind = [int]$counts[1]
    Write-Host "upstream  : $upstream (안 올린 커밋 ${ahead}개, 안 받은 커밋 ${behind}개)"
    if ($ahead -gt 0) { Problem "push 안 한 커밋이 ${ahead}개 있습니다. 'git push'로 올린 뒤 다시 실행해 주세요." }
    if ($behind -gt 0) { Problem "원격에 받지 않은 커밋이 ${behind}개 있습니다. 'git pull --ff-only'로 받은 뒤 다시 실행해 주세요." }
}

# 태그 중복: 로컬과 origin 모두
git rev-parse -q --verify "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -eq 0) { Problem "태그 $tag 가 이미 로컬에 있습니다. 다른 버전을 고르세요." }
$remoteTag = git ls-remote --tags origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) { Problem 'origin의 태그를 확인하지 못했습니다 (git ls-remote 실패).' }
elseif ($remoteTag) { Problem "태그 $tag 가 이미 GitHub(origin)에 있습니다. 다른 버전을 고르세요." }

# 이전 최신 태그 (SemVer 형식만, SemVer 순서로)
$prevTag = $null
foreach ($t in @(git tag --list 'v*')) {
    $v = $t.Substring(1)
    if ($v -notmatch $SemVerPattern) { continue }
    if (-not $prevTag -or (Compare-SemVer $v $prevTag.Substring(1)) -gt 0) { $prevTag = $t }
}
if ($prevTag) {
    Write-Host "이전 태그 : $prevTag ($(git log -1 --format='%h %ci' $prevTag))"
    if ((Compare-SemVer $Version $prevTag.Substring(1)) -le 0) {
        Problem "새 버전 $Version 이 이전 태그 $prevTag 보다 크지 않습니다. 자동 업데이트는 더 큰 버전만 받습니다."
    }
}
else {
    Write-Host '이전 태그 : 없음 (첫 릴리스)'
}

# ---- 3. -Local 준비: dotnet, 토큰 ----
$script:token = $null
if ($Local) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Problem '.NET SDK(dotnet)를 찾지 못했습니다. .NET 10 SDK를 설치해 주세요.'
    }
    if ($env:GITHUB_TOKEN) {
        $script:token = $env:GITHUB_TOKEN.Trim()
        Write-Host '토큰      : $env:GITHUB_TOKEN 사용'
    }
    elseif (Get-Command gh -ErrorAction SilentlyContinue) {
        $t = gh auth token 2>$null
        if ($LASTEXITCODE -eq 0 -and $t) {
            $script:token = ([string]$t).Trim()
            Write-Host "토큰      : 'gh auth token' 사용"
        }
    }
    if (-not $script:token) {
        Problem "GitHub 토큰이 없습니다. 'gh auth login'으로 로그인하거나 GITHUB_TOKEN 환경 변수를 설정해 주세요."
        $script:token = '<token>'
    }
}

# ---- 3-b. 릴리스 노트 ----
# BOM 없는 UTF-8로 씁니다: Windows PowerShell 5.1의 Out-File -Encoding UTF8은 BOM을 붙여 태그 메시지 첫 줄이 깨집니다.
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$notesDir = Join-Path $repo 'artifacts\release-notes'
New-Item -ItemType Directory -Force -Path $notesDir | Out-Null
$notesPath = $null

# 이전 태그 이후 first-parent 히스토리: PR 병합은 "Merge pull request #N from …" + 본문 첫 줄(PR 제목).
function New-NotesDraft {
    if ($prevTag) { $range = "$prevTag..HEAD" } else { $range = 'HEAD' }
    $lines = @()
    $log = (git log --first-parent --format='%s%x1f%b%x1e' $range) -join "`n"
    foreach ($entry in ($log -split [char]0x1e)) {
        $parts = $entry.Trim() -split [char]0x1f, 2
        if (-not $parts[0]) { continue }
        if ($parts[0] -match '^Merge pull request #(\d+)') {
            $title = @(($parts[1] -split "`n") | Where-Object { $_.Trim() }) | Select-Object -First 1
            if (-not $title) { $title = $parts[0] }
            $lines += "- $($title.Trim()) (#$($Matches[1]))"
        }
        elseif ($parts[0] -notmatch '^Merge ') {
            $lines += "- $($parts[0].Trim())"
        }
    }
    if ($lines.Count -eq 0) { $lines = @('- ') }
    $since = if ($prevTag) { "$prevTag 이후" } else { '처음부터' }
    return @(
        "<!-- STS2 티어 오버레이 $tag 릴리스 노트. 플레이어가 읽는 글입니다: 한 줄에 변경 하나, 쉬운 말로. -->"
        "<!-- 아래 목록은 $since 병합된 PR 제목입니다. 섹션으로 나눠 다듬고, 개발용 변경(CI, 테스트 등)은 지우세요. -->"
        "<!-- '<!--'로 시작하는 줄은 저장 후 빠집니다. 다 고쳤으면 저장하고 메모장을 닫으세요. -->"
        ''
        '## 바뀐 점'
    ) + $lines + @(
        ''
        '## 설치 / 업데이트'
        '- 처음 설치: 아래 `namsy8471.Sts2Overlay-win-Setup.exe`를 받아 실행하세요.'
        '- 이미 설치했다면 자동으로 업데이트됩니다. 업데이트는 오버레이를 종료할 때 적용됩니다.'
    )
}

if ($NotesFile) {
    if (-not (Test-Path -LiteralPath $NotesFile)) { Fail "릴리스 노트 파일이 없습니다: $NotesFile" }
    $notesPath = (Resolve-Path -LiteralPath $NotesFile).Path
    Write-Host "노트      : $notesPath"
}
else {
    $notesPath = Join-Path $notesDir "$tag.md"
    if (Test-Path -LiteralPath $notesPath) {
        Write-Host "노트      : $notesPath (전에 쓴 초안을 이어서 씁니다)"
    }
    else {
        [System.IO.File]::WriteAllText($notesPath, ((New-NotesDraft) -join "`r`n") + "`r`n", $Utf8NoBom)
        Write-Host "노트      : $notesPath (병합된 PR로 초안을 만들었습니다)"
    }
    if (-not $DryRun -and -not $Yes -and -not $NoPause -and [Environment]::UserInteractive) {
        Write-Host '메모장에서 릴리스 노트를 고쳐 저장하고 닫으면 계속합니다...' -ForegroundColor Cyan
        Start-Process -FilePath 'notepad.exe' -ArgumentList "`"$notesPath`"" -Wait
    }
}

# 안내문(<!-- 줄)을 빼고 앞뒤 빈 줄을 정리합니다.
# 빈 노트 판정: 제목(#), 빈 줄, 빈 목록 항목("- "), 그리고 "설치 / 업데이트" 섹션(초안에 늘 들어 있는 안내)을 빼고
# 남는 줄이 없으면 무엇이 바뀌었는지 안 적은 것입니다.
$noteLines = @([System.IO.File]::ReadAllLines($notesPath, [System.Text.Encoding]::UTF8) |
        Where-Object { $_.TrimStart() -notlike '<!--*' } | ForEach-Object { $_.TrimEnd() })
$notesText = ($noteLines -join "`n").Trim()
$content = 0
$inInstall = $false
foreach ($line in $noteLines) {
    $t = $line.Trim()
    if ($t -match '^#+\s*(.*)$') { $inInstall = $Matches[1] -match '설치|업데이트|Install'; continue }
    if (-not $t -or $t -match '^[-*]\s*$' -or $t -match '^-{3,}$' -or $inInstall) { continue }
    $content++
}
if ($content -eq 0) {
    Problem "릴리스 노트가 비어 있습니다 ($notesPath). 무엇이 바뀌었는지 적은 뒤 다시 실행해 주세요."
    $notesText = '(비어 있음)'
}
$cleanNotes = Join-Path $notesDir "$tag.tag-message.md"
[System.IO.File]::WriteAllText($cleanNotes, $notesText + "`n", $Utf8NoBom)
Write-Host ''
Write-Host "---- 릴리스 노트 ($tag) ----" -ForegroundColor Cyan
Write-Host $notesText
Write-Host '----------------------------' -ForegroundColor Cyan

# ---- 4. 확인 ----
Write-Host ''
if ($DryRun) {
    Write-Host '시험 실행입니다. 아래 명령을 보여 주기만 하고 실행하지 않습니다.' -ForegroundColor Cyan
}
elseif (-not $Yes) {
    if ($Local) { $what = "이 PC에서 $tag 를 빌드해 GitHub Releases에 공개합니다" }
    else { $what = "태그 $tag 를 커밋 $($head.Substring(0, 7))에 붙여 push합니다 (Actions가 빌드·공개)" }
    $answer = Read-Host "$what. 계속할까요? [y/N]"
    if ($answer -notmatch '^[yY]') {
        Write-Host '취소했습니다. 아무것도 바꾸지 않았습니다.'
        Finish 1
    }
}

# ---- 5-a. 기본: 태그 만들어 push ----
if (-not $Local) {
    # 노트가 태그 메시지입니다. --cleanup=verbatim: 기본 정리는 '#'로 시작하는 줄(마크다운 제목)을 주석으로 보고 지웁니다.
    Invoke-Native git @('tag', '-a', $tag, '-F', $cleanNotes, '--cleanup=verbatim') | Out-Null
    $pushed = Invoke-Native git @('push', 'origin', $tag) -AllowFail
    if (-not $pushed) {
        # 다음 시도가 "태그가 이미 있음"에 막히지 않도록 로컬 태그를 지웁니다.
        git tag -d $tag | Out-Null
        Fail "태그 push가 실패했습니다. 로컬 태그 $tag 는 지웠습니다. GitHub 로그인/권한을 확인해 주세요."
    }
    if ($script:problems.Count -gt 0) {
        Write-Host ''
        Write-Host "검사에서 문제 $($script:problems.Count)개가 있었습니다. 고친 뒤 -DryRun 없이 다시 실행해 주세요." -ForegroundColor Red
        Finish 1
    }
    Write-Host ''
    if ($DryRun) {
        Write-Host '시험 실행 끝: 검사 통과. -DryRun을 빼면 위 명령을 실행합니다.' -ForegroundColor Green
    }
    else {
        Write-Host "태그 $tag 를 올렸습니다. GitHub Actions가 테스트·패키지·업로드를 합니다 (몇 분 걸립니다)." -ForegroundColor Green
    }
    Write-Host "진행 상황 : $RepoUrl/actions"
    Write-Host "릴리스    : $RepoUrl/releases"
    Finish 0
}

# ---- 5-b. -Local: release.yml과 같은 명령을 이 PC에서 ----
$started = Get-Date
$preArgs = @()
if ($isPre) { $preArgs = @('--pre') }

foreach ($dir in @('publish', 'releases')) {
    Write-Host "> 이전 출력 지우기: $dir\" -ForegroundColor DarkGray
    if (-not $DryRun -and (Test-Path -LiteralPath $dir)) { Remove-Item -LiteralPath $dir -Recurse -Force }
}
foreach ($p in @('tests/Sts2Overlay.Core.Tests', 'tests/Sts2Overlay.GameState.Tests', 'tests/Sts2Overlay.Resources.Tests',
        'tests/Sts2Overlay.Vision.Tests', 'tests/Sts2Overlay.App.Tests')) {
    Invoke-Native dotnet @('test', $p, '-c', 'Release', '--nologo') | Out-Null
}
Invoke-Native dotnet @('publish', 'src/Sts2Overlay.App', '-c', 'Release', '-r', 'win-x64', '--self-contained',
    '-o', 'publish', "-p:Version=$Version", '--nologo') | Out-Null
Invoke-Native dotnet @('tool', 'restore') | Out-Null

# 이전 릴리스(델타용). 첫 릴리스면 받을 게 없으니 실패해도 계속합니다.
$got = Invoke-Native dotnet (@('vpk', 'download', 'github', '--repoUrl', $RepoUrl, '--outputDir', 'releases',
        '--token', $script:token) + $preArgs) -AllowFail
if (-not $got) { Write-Host '이전 릴리스를 받지 못했습니다 (첫 릴리스?). 델타 없이 패키지를 만듭니다.' -ForegroundColor Yellow }

Invoke-Native dotnet @('vpk', 'pack', '--packId', 'namsy8471.Sts2Overlay', '--packVersion', $Version, '--packDir', 'publish',
    '--mainExe', 'Sts2Overlay.exe', '--packTitle', 'STS2 티어 오버레이', '--packAuthors', 'namsy8471',
    '--icon', 'src/Sts2Overlay.App/Assets/Sts2Overlay.ico', '--runtime', 'win-x64', '--releaseNotes', $cleanNotes,
    '--outputDir', 'releases') | Out-Null

# --targetCommitish: 태그가 아직 없으므로 GitHub가 기본 브랜치 끝이 아니라 이 커밋(push됨을 위에서 확인)에 태그를 만들게 합니다.
Invoke-Native dotnet (@('vpk', 'upload', 'github', '--repoUrl', $RepoUrl, '--outputDir', 'releases', '--publish',
        '--tag', $tag, '--releaseName', "STS2 티어 오버레이 $tag", '--targetCommitish', $head,
        '--token', $script:token) + $preArgs) | Out-Null
# vpk upload에는 본문 옵션이 없으므로 GitHub Release 본문은 gh로 노트를 넣습니다.
if (Get-Command gh -ErrorAction SilentlyContinue) {
    $edited = Invoke-Native gh @('release', 'edit', $tag, '--repo', $RepoUrl, '--notes-file', $cleanNotes) -AllowFail
    if (-not $edited) { Write-Host "릴리스 본문을 넣지 못했습니다. 'gh release edit $tag --notes-file $cleanNotes'로 넣어 주세요." -ForegroundColor Yellow }
}
else {
    Write-Host "gh가 없어 릴리스 본문을 넣지 못했습니다. GitHub에서 $tag 릴리스 본문에 $cleanNotes 내용을 붙여 넣어 주세요." -ForegroundColor Yellow
}
Invoke-Native git @('fetch', '--quiet', '--tags', 'origin') -AllowFail | Out-Null

Write-Host ''
if ($script:problems.Count -gt 0) {
    Write-Host "검사에서 문제 $($script:problems.Count)개가 있었습니다. 고친 뒤 -DryRun 없이 다시 실행해 주세요." -ForegroundColor Red
    Finish 1
}
if ($DryRun) {
    Write-Host '시험 실행 끝: 검사 통과. -DryRun을 빼면 위 명령을 실행합니다.' -ForegroundColor Green
}
else {
    $seconds = [int]((Get-Date) - $started).TotalSeconds
    Write-Host "릴리스 $tag 를 공개했습니다 ($seconds 초)." -ForegroundColor Green
}
Write-Host "릴리스    : $RepoUrl/releases/tag/$tag"
Write-Host "Actions   : $RepoUrl/actions"
Finish 0
