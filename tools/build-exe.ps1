<#
  원격의 최신 코드를 받아(git pull --ff-only) 오버레이 exe를 다시 만들어 dist\Sts2Overlay.exe에 넣습니다.
  레포 맨 위의 build-exe.cmd를 더블클릭하면 이 스크립트가 실행됩니다.

    build-exe.cmd              최신 코드 받기 → 빌드 → exe 교체. 오버레이가 켜져 있으면 다시 켤지 묻습니다.
                               받기에 실패하면(오프라인, 로컬 변경 등) 경고만 띄우고 지금 폴더의 코드로 빌드합니다.
    build-exe.cmd -NoPull      원격에서 받지 않고 지금 폴더의 코드 그대로 빌드합니다.
    build-exe.cmd -Restart     묻지 않고 켜져 있는 오버레이를 새 exe로 다시 켭니다.
    build-exe.cmd -NoRestart   켜져 있는 오버레이를 건드리지 않습니다 (다음에 켤 때 새 exe).
    build-exe.cmd -NoPause     끝나고 키 입력을 기다리지 않습니다 (자동화용, 재시작도 안 함).

  켜져 있는 exe도 이름은 바꿀 수 있어서, 이전 exe를 Sts2Overlay.prev.exe로 옮기고 새 exe를 넣습니다.
  빌드가 실패하면 dist는 그대로 둡니다. Windows PowerShell 5.1과 PowerShell 7 모두에서 돕니다.

  전투 추천 도우미: 레포 맨 위 advisor-dist\에 Sts2CombatAdvisor.exe가 있으면 세 파일을 dist\advisor\에 넣습니다
  (tools\advisor-package.ps1). 없으면 한 줄 안내만 하고 도우미 없이 빌드합니다. exe만 있고 dll이나 테이블이 없으면 실패합니다.
  종료 코드: 0 성공, 1 실패.
#>
param(
    [switch]$NoPull,
    [switch]$Restart,
    [switch]$NoRestart,
    [switch]$NoPause
)

# 'Stop'은 쓰지 않습니다: Windows PowerShell 5.1에서는 git/dotnet이 stderr에 쓰는 진행 메시지까지 오류로 바뀝니다.
# 외부 명령은 $LASTEXITCODE로 확인합니다.
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
$dist = Join-Path $repo 'dist'
$exe = Join-Path $dist 'Sts2Overlay.exe'
$stage = Join-Path $repo 'artifacts\exe-build'
$advisorScript = Join-Path $PSScriptRoot 'advisor-package.ps1'

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
    Write-Host 'dist\Sts2Overlay.exe는 바꾸지 않았습니다.' -ForegroundColor Red
    Finish 1
}

# advisor-dist\의 도우미 세 파일을 dist\advisor\에 넣고 한 줄로 알립니다 (없으면 도우미 없이). 실패하면 $false.
function Update-DistAdvisor {
    & $advisorScript -Destination (Join-Path $dist 'advisor') -NoLock
    if ($LASTEXITCODE -ne 0) { return $false }
    if (Test-Path -LiteralPath (Join-Path $dist 'advisor\Sts2CombatAdvisor.exe')) {
        Copy-Item -LiteralPath (Join-Path $repo 'data\advisor\combat-value-R01.json') -Destination (Join-Path $dist 'advisor') -Force
    }
    return $true
}

Write-Host '== STS2 오버레이 exe 다시 만들기 ==' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail '.NET SDK(dotnet)를 찾지 못했습니다. https://dotnet.microsoft.com/download 에서 .NET 10 SDK를 설치해 주세요.'
}

# 빌드(1~3분) 전에 확인: advisor-dist\에 exe만 있고 dll이나 테이블이 없으면 바로 멈춥니다.
& $advisorScript -Check -NoLock -Quiet
if ($LASTEXITCODE -ne 0) { Fail 'advisor-dist\ 폴더를 고친 뒤 다시 실행해 주세요.' }

# ---- 1. 어떤 코드로 빌드하는지 보여 주기 (git이 없으면 건너뜀) ----
$hasGit = [bool](Get-Command git -ErrorAction SilentlyContinue)
if ($hasGit) {
    # 추적 중인 원격 브랜치(upstream)가 있을 때만 받습니다. 없으면 조용히 건너뜁니다.
    $upstream = git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
    if (-not $NoPull -and $LASTEXITCODE -eq 0 -and $upstream) {
        Write-Host "최신 코드 받는 중 ($upstream, git pull --ff-only)..."
        git pull --ff-only
        if ($LASTEXITCODE -ne 0) {
            # 빌드를 멈추지 않고 경고만 합니다: 오프라인이거나 로컬 변경이 있어도 exe는 만들 수 있어야 하니까요.
            Write-Host '최신 코드를 받지 못했습니다 (git pull 실패). 지금 폴더의 코드로 빌드합니다.' -ForegroundColor Yellow
        }
    }
    $branch = git rev-parse --abbrev-ref HEAD
    $commit = git log -1 --format='%h %s (%ci)'
    Write-Host "브랜치    : $branch"
    Write-Host "마지막 커밋: $commit"
    $dirty = git status --porcelain --untracked-files=no
    if ($dirty) {
        Write-Host '커밋 안 된 변경이 있습니다. 그 변경도 함께 빌드됩니다.' -ForegroundColor Yellow
    }
}

# ---- 2. 임시 폴더로 publish (실행 중인 dist exe와 겹치지 않게) ----
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
Write-Host ''
Write-Host '빌드 중... (처음에는 1~3분 걸릴 수 있습니다)'
$started = Get-Date
# --self-contained false: 이 PC에는 빌드에 쓰는 .NET 10 SDK(런타임 포함)가 있으므로 .NET을 exe에 넣지 않습니다 (205 MB → 약 40 MB).
dotnet publish src/Sts2Overlay.App -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None `
    -o $stage --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail '빌드가 실패했습니다. 위의 오류 메시지를 확인해 주세요.' }
$newExe = Join-Path $stage 'Sts2Overlay.exe'
if (-not (Test-Path -LiteralPath $newExe)) { Fail "빌드는 끝났지만 $newExe 가 없습니다." }

# 새 exe가 지금 exe와 바이트까지 같으면 아무것도 바꾸지 않습니다 (켜져 있는 오버레이도 그대로).
# 그대로 교체하면 Sts2Overlay.prev.exe(진짜 이전 버전 백업)가 같은 내용의 파일로 덮여 버리기 때문입니다.
# 크기를 먼저 비교하고(싸다), 같을 때만 SHA256을 계산합니다(파일 전체를 읽어야 해서 비싸다).
if (Test-Path -LiteralPath $exe) {
    $oldInfo = Get-Item -LiteralPath $exe
    $newInfo = Get-Item -LiteralPath $newExe
    if ($oldInfo.Length -eq $newInfo.Length -and
        (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $newExe -Algorithm SHA256).Hash) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ''
        Write-Host '바뀐 내용이 없습니다. dist\Sts2Overlay.exe를 그대로 둡니다.' -ForegroundColor Green
        # 도우미는 exe와 따로 바뀔 수 있습니다 (advisor-dist\에 새 파일을 넣은 경우).
        if (-not (Update-DistAdvisor)) { Finish 1 }
        Finish 0
    }
}

# ---- 3. 켜져 있는 오버레이 처리 ----
$running = @(Get-Process -Name 'Sts2Overlay' -ErrorAction SilentlyContinue)
$relaunch = $false
if ($running.Count -gt 0 -and -not $NoRestart) {
    if ($Restart) {
        $relaunch = $true
    }
    elseif (-not $NoPause) {
        $answer = Read-Host '오버레이가 켜져 있습니다. 끄고 새 버전으로 다시 켤까요? [Y/n]'
        $relaunch = ($answer -eq '' -or $answer -match '^[yY]')
    }
}
if ($relaunch) {
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

# ---- 4. exe 바꿔 끼우기 ----
New-Item -ItemType Directory -Force -Path $dist | Out-Null
# 예전 백업 정리. 아직 켜져 있는 파일은 지워지지 않으므로 오류는 무시합니다.
Get-ChildItem -LiteralPath $dist -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'Sts2Overlay.*.exe' -and $_.Name -ne 'Sts2Overlay.exe' } |
    Remove-Item -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $exe) {
    $backup = Join-Path $dist 'Sts2Overlay.prev.exe'
    if (Test-Path -LiteralPath $backup) {
        # 이전 백업이 아직 실행 중이라 못 지운 경우
        $backup = Join-Path $dist ('Sts2Overlay.prev-{0:yyyyMMdd-HHmmss}.exe' -f (Get-Date))
    }
    # 실행 중인 exe도 이름 바꾸기는 됩니다 (Windows는 실행 중 파일의 삭제·덮어쓰기만 막음).
    Move-Item -LiteralPath $exe -Destination $backup -Force
    if (Test-Path -LiteralPath $exe) { Fail '기존 exe를 옮기지 못했습니다. 오버레이를 끄고 다시 실행해 주세요.' }
}
# exe 말고 다른 파일이 생기는 경우(앞으로 네이티브 DLL 등)도 함께 옮깁니다.
# 단, XML 문서 주석 파일(*.xml)과 디버그 심볼(*.pdb)은 실행에 필요 없으므로 옮기지 않습니다.
Get-ChildItem -LiteralPath $stage -File |
    Where-Object { $_.Extension -notin @('.xml', '.pdb') } |
    ForEach-Object {
    Move-Item -LiteralPath $_.FullName -Destination (Join-Path $dist $_.Name) -Force
}
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

$info = Get-Item -LiteralPath $exe
$seconds = [int]((Get-Date) - $started).TotalSeconds
Write-Host ''
Write-Host ('완료: {0} ({1:N0} MB, {2}초)' -f $info.FullName, ($info.Length / 1MB), $seconds) -ForegroundColor Green
Write-Host '바탕화면 "STS2 오버레이" 바로가기는 이 exe를 엽니다.'
# 오버레이를 끈 뒤(다시 켜기 전)에 넣어야 실행 중인 도우미 파일과 겹치지 않습니다.
$advisorOk = Update-DistAdvisor

if ($relaunch) {
    Start-Process -FilePath $exe -WorkingDirectory $dist
    Write-Host '새 버전으로 오버레이를 다시 켰습니다.'
}
elseif ($running.Count -gt 0) {
    Write-Host '켜져 있는 오버레이는 이전 버전입니다. 껐다 켜면 새 버전이 실행됩니다.' -ForegroundColor Yellow
}
if (-not $advisorOk) {
    Write-Host '오버레이 exe는 바꿨지만 전투 추천 도우미를 넣지 못했습니다 (위 메시지).' -ForegroundColor Red
    Finish 1
}
Finish 0
