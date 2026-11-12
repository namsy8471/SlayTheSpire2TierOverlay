<#
  전투 추천 도우미(combat advisor helper)의 컴파일된 파일 세 개를 오버레이 출력 폴더의 advisor\ 에 넣습니다.
  tools\build-exe.ps1, tools\release.ps1 -Local, .github\workflows\release.yml이 부릅니다.
  도우미의 소스는 이 레포에 없습니다. 파일을 어디서 가져오는지는 docs\DISTRIBUTION.md "Combat advisor helper"에 있습니다.

  가져오는 곳 (위에서부터 처음 있는 것):
    1. advisor-dist\ (레포 맨 위, git에 올라가지 않음): Sts2CombatAdvisor.exe, sts2_search.dll, search-tables.bin을
       직접 넣어 두는 폴더. exe가 있으면 씁니다. exe만 있고 나머지가 없으면 실패합니다.
    2. advisor.lock.json (레포 맨 위, 추적되는 파일, 기본은 없음):
         { "url": "https://…/advisor.zip", "sha256": "<64자리 hex>", "version": "<아무 글자>" }
       zip을 받아 SHA-256을 확인하고(다르면 실패) 세 파일을 꺼냅니다. url은 https://(또는 시험용 file://)만 받습니다.
    3. 둘 다 없으면 도우미 없이 빌드합니다. 한 줄 안내만 하고 실패하지 않습니다.

    advisor-package.ps1 -Destination publish\advisor                 1 → 2 → 3 (release.ps1 -Local)
    advisor-package.ps1 -Destination dist\advisor -NoLock            1 → 3     (build-exe.ps1)
    advisor-package.ps1 -Destination publish/advisor -LockOnly       2 → 3     (GitHub Actions: advisor-dist\는 없음)
    advisor-package.ps1 -Check [-NoLock|-LockOnly] [-Quiet]          파일을 바꾸지 않고 가져올 곳만 검사해 한 줄로 알림
                                                                     (빌드 전에 빨리 실패; -Quiet이면 실패할 때만 출력)
    advisor-package.ps1 -SelfTest                                    임시 폴더에서 이 스크립트를 시험 (네트워크 안 씀)

  -CacheDir <폴더>: 받은 zip을 해시 이름으로 보관하고 다음에 다시 씁니다 (해시가 맞을 때만).
  Windows PowerShell 5.1과 PowerShell 7(Linux 포함) 모두에서 돕니다. 종료 코드: 0 성공(도우미 없음 포함), 1 실패.
#>
param(
    [string]$Destination,
    [switch]$NoLock,
    [switch]$LockOnly,
    [switch]$Check,
    [switch]$Quiet,
    [switch]$SelfTest,
    [string]$CacheDir,
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1의 진행 표시줄은 다운로드를 몇 배 느리게 합니다
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# 함께 있어야 하는 파일. 첫 번째가 오버레이가 찾는 exe입니다 (AdvisorCommand.DefaultRelativePath).
$script:AdvisorFiles = @('Sts2CombatAdvisor.exe', 'sts2_search.dll', 'search-tables.bin')

function Format-Size([long]$bytes) { return ('{0:N1} MB' -f ($bytes / 1MB)) }

function Resolve-FullPath([string]$path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
}

# advisor.lock.json을 읽어 모양을 검사합니다. 반환: Url, Sha256(소문자), Version.
function Read-AdvisorLock([string]$path) {
    try {
        $json = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    }
    catch {
        throw "advisor.lock.json을 JSON으로 읽지 못했습니다: $($_.Exception.Message)"
    }
    $url = [string]$json.url
    $sha = [string]$json.sha256
    $version = [string]$json.version
    if ($url -notmatch '^(https|file)://') {
        throw "advisor.lock.json의 url은 https:// 주소여야 합니다 (지금: '$url')."
    }
    if ($sha -notmatch '^[0-9a-fA-F]{64}$') {
        throw "advisor.lock.json의 sha256은 64자리 16진수여야 합니다 (지금: '$sha')."
    }
    if (-not $version.Trim()) {
        throw 'advisor.lock.json에 version이 없습니다.'
    }
    return [pscustomobject]@{ Url = $url; Sha256 = $sha.ToLowerInvariant(); Version = $version.Trim() }
}

# 어디서 가져올지 정합니다 (파일은 바꾸지 않음). Kind: drop / lock / none.
function Get-AdvisorSource([string]$Root, [switch]$NoLock, [switch]$LockOnly) {
    $drop = Join-Path $Root 'advisor-dist'
    $lockPath = Join-Path $Root 'advisor.lock.json'
    if (-not $LockOnly -and (Test-Path -LiteralPath (Join-Path $drop $script:AdvisorFiles[0]) -PathType Leaf)) {
        $missing = @($script:AdvisorFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $drop $_) -PathType Leaf) })
        if ($missing.Count -gt 0) {
            throw "advisor-dist에 $($script:AdvisorFiles[0])는 있지만 $($missing -join ', ')이(가) 없습니다. 세 파일을 함께 넣거나 advisor-dist 폴더를 지워 주세요."
        }
        return [pscustomobject]@{ Kind = 'drop'; Dir = $drop; Lock = $null }
    }
    if (-not $NoLock -and (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        return [pscustomobject]@{ Kind = 'lock'; Dir = $null; Lock = (Read-AdvisorLock $lockPath) }
    }
    return [pscustomobject]@{ Kind = 'none'; Dir = $null; Lock = $null }
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# zip을 받아 SHA-256을 확인한 경로를 돌려줍니다. 다르면 받은 파일을 지우고 실패합니다.
function Get-VerifiedArchive($lock, [string]$cacheDir) {
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $zip = Join-Path $cacheDir "advisor-$($lock.Sha256).zip"
    if ((Test-Path -LiteralPath $zip) -and (Get-Sha256 $zip) -eq $lock.Sha256) { return $zip }
    $part = "$zip.part"
    if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
    $uri = [Uri]$lock.Url
    if ($uri.Scheme -eq 'file') {
        Copy-Item -LiteralPath $uri.LocalPath -Destination $part
    }
    else {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
        $attempt = 0
        while ($true) {
            $attempt++
            try {
                Invoke-WebRequest -Uri $lock.Url -OutFile $part -UseBasicParsing
                break
            }
            catch {
                if ($attempt -ge 3) { throw "도우미 zip을 받지 못했습니다 ($($lock.Url)): $($_.Exception.Message)" }
                Start-Sleep -Seconds (2 * $attempt)
            }
        }
    }
    $actual = Get-Sha256 $part
    if ($actual -ne $lock.Sha256) {
        Remove-Item -LiteralPath $part -Force
        throw "도우미 zip의 SHA-256이 advisor.lock.json과 다릅니다. 기대 $($lock.Sha256), 실제 $actual ($($lock.Url))."
    }
    Move-Item -LiteralPath $part -Destination $zip -Force
    return $zip
}

# zip을 임시 폴더에 풀고 세 파일이 있는 폴더를 돌려줍니다 (zip 맨 위든 하위 폴더 하나 안이든).
function Expand-AdvisorArchive([string]$zip, [string]$tempDir) {
    Expand-Archive -LiteralPath $zip -DestinationPath $tempDir -Force
    $exes = @(Get-ChildItem -LiteralPath $tempDir -Recurse -File | Where-Object { $_.Name -eq $script:AdvisorFiles[0] })
    if ($exes.Count -ne 1) { throw "도우미 zip 안에 $($script:AdvisorFiles[0])가 정확히 하나 있어야 합니다 (찾은 수: $($exes.Count))." }
    $dir = $exes[0].DirectoryName
    $missing = @($script:AdvisorFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $dir $_) -PathType Leaf) })
    if ($missing.Count -gt 0) { throw "도우미 zip에 $($missing -join ', ')이(가) 없습니다 ($($script:AdvisorFiles[0]) 옆에 있어야 함)." }
    return $dir
}

# 세 파일만 복사합니다 (.pdb, runtimeconfig 같은 나머지는 넣지 않음). 같은 파일은 건너뜁니다.
# 실행 중인 도우미의 파일은 지우거나 덮어쓸 수 없지만 이름은 바꿀 수 있어, 옆으로 치우고(.old-…) 새 파일을 넣습니다.
# 반환: 바꾼 파일 수.
function Copy-AdvisorFiles([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Get-ChildItem -LiteralPath $to -File -Filter '*.old-*' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    $changed = 0
    foreach ($name in $script:AdvisorFiles) {
        $src = Join-Path $from $name
        $dst = Join-Path $to $name
        if (Test-Path -LiteralPath $dst) {
            if ((Get-Item -LiteralPath $dst).Length -eq (Get-Item -LiteralPath $src).Length -and (Get-Sha256 $dst) -eq (Get-Sha256 $src)) { continue }
            try {
                Remove-Item -LiteralPath $dst -Force
            }
            catch {
                try {
                    Move-Item -LiteralPath $dst -Destination ('{0}.old-{1:yyyyMMddHHmmss}' -f $dst, (Get-Date)) -Force
                }
                catch {
                    throw "$dst 를 바꾸지 못했습니다 (도우미가 실행 중?). 오버레이를 끄고 다시 실행해 주세요."
                }
            }
        }
        Copy-Item -LiteralPath $src -Destination $dst
        $changed++
    }
    return $changed
}

function Get-AdvisorSize([string]$dir) {
    $total = 0L
    foreach ($name in $script:AdvisorFiles) { $total += (Get-Item -LiteralPath (Join-Path $dir $name)).Length }
    return $total
}

# 가져와서 넣고, 화면에 낼 한 줄을 돌려줍니다.
function Install-Advisor($source, [string]$destination, [string]$cacheDir, [switch]$NoLock, [switch]$LockOnly) {
    if ($source.Kind -eq 'none') {
        if ($LockOnly) { $why = 'advisor.lock.json이 없어' }
        elseif ($NoLock) { $why = 'advisor-dist\Sts2CombatAdvisor.exe가 없어' }
        else { $why = 'advisor-dist\Sts2CombatAdvisor.exe도 advisor.lock.json도 없어' }
        $line = "전투 추천 도우미 없음: $why 이 빌드에는 넣지 않았습니다."
        if (Test-Path -LiteralPath (Join-Path $destination $script:AdvisorFiles[0])) {
            $line += " ($destination 에 있던 이전 파일은 그대로 둡니다.)"
        }
        return $line
    }
    if ($source.Kind -eq 'drop') {
        $changed = Copy-AdvisorFiles $source.Dir $destination
        $from = 'advisor-dist'
    }
    else {
        $ownCache = -not $cacheDir
        if ($ownCache) { $cacheDir = Join-Path ([System.IO.Path]::GetTempPath()) ('sts2-advisor-' + [Guid]::NewGuid().ToString('N')) }
        $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('sts2-advisor-x-' + [Guid]::NewGuid().ToString('N'))
        try {
            $zip = Get-VerifiedArchive $source.Lock $cacheDir
            $changed = Copy-AdvisorFiles (Expand-AdvisorArchive $zip $temp) $destination
        }
        finally {
            Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
            if ($ownCache) { Remove-Item -LiteralPath $cacheDir -Recurse -Force -ErrorAction SilentlyContinue }
        }
        $from = "advisor.lock.json 버전 $($source.Lock.Version), SHA-256 확인"
    }
    return ('전투 추천 도우미 포함: {0} (파일 {1}개, {2}, {3}; 바뀐 파일 {4}개)' -f $destination, $script:AdvisorFiles.Count,
        (Format-Size (Get-AdvisorSize $destination)), $from, $changed)
}

# ---- -SelfTest: 임시 폴더에 가짜 파일을 만들어 위 함수들을 시험합니다 (file:// zip, 네트워크 없음) ----
function Invoke-SelfTest {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('sts2-advisor-selftest-' + [Guid]::NewGuid().ToString('N'))
    $script:failed = 0
    $script:passed = 0
    function Assert-True([bool]$condition, [string]$what) {
        if ($condition) { $script:passed++ } else { $script:failed++; Write-Host "  FAIL: $what" -ForegroundColor Red }
    }
    function Assert-Throws([scriptblock]$block, [string]$part, [string]$what) {
        try { & $block; $script:failed++; Write-Host "  FAIL (no error): $what" -ForegroundColor Red }
        catch {
            if ($_.Exception.Message -like "*$part*") { $script:passed++ }
            else { $script:failed++; Write-Host "  FAIL: $what -> $($_.Exception.Message)" -ForegroundColor Red }
        }
    }
    function New-FakeFiles([string]$dir, [string[]]$names) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        foreach ($n in $names) { [System.IO.File]::WriteAllText((Join-Path $dir $n), "made-up $n " + [Guid]::NewGuid()) }
    }
    function Write-Lock([string]$repo, [string]$url, [string]$sha, [string]$version) {
        $json = '{ "url": "' + $url.Replace('\', '\\') + '", "sha256": "' + $sha + '", "version": "' + $version + '" }'
        [System.IO.File]::WriteAllText((Join-Path $repo 'advisor.lock.json'), $json)
    }
    # 절대 경로를 file:// 주소로 바꿉니다. [Uri]$path 캐스트는 쓰지 않습니다: Linux에서는 '/tmp/…'를 상대 주소로 읽어
    # AbsoluteUri가 예외를 내고, PowerShell은 속성 예외를 삼켜 $null(빈 url)을 돌려줍니다.
    function ConvertTo-FileUrl([string]$path) {
        return (New-Object System.Uri -ArgumentList $path, ([System.UriKind]::Absolute)).AbsoluteUri
    }
    try {
        $repo = Join-Path $root 'repo'
        New-Item -ItemType Directory -Force -Path $repo | Out-Null
        $out = Join-Path $root 'out-advisor'

        # 아무것도 없음 → none, 폴더를 만들지 않음
        $s = Get-AdvisorSource $repo
        Assert-True ($s.Kind -eq 'none') 'nothing → none'
        $line = Install-Advisor $s $out ''
        Assert-True ($line -like '전투 추천 도우미 없음*') 'none line'
        Assert-True (-not (Test-Path -LiteralPath $out)) 'none creates nothing'

        # advisor-dist: exe만 → 실패
        $drop = Join-Path $repo 'advisor-dist'
        New-FakeFiles $drop @('Sts2CombatAdvisor.exe')
        Assert-Throws { Get-AdvisorSource $repo } 'sts2_search.dll' 'exe without dll and tables'

        # advisor-dist: 세 파일 + pdb → 세 파일만 복사
        New-FakeFiles $drop @('sts2_search.dll', 'search-tables.bin', 'Extra.pdb')
        $s = Get-AdvisorSource $repo
        Assert-True ($s.Kind -eq 'drop') 'drop found'
        $line = Install-Advisor $s $out ''
        Assert-True ($line -like '*바뀐 파일 3개*') "drop copies 3 ($line)"
        Assert-True (@(Get-ChildItem -LiteralPath $out -File).Count -eq 3) 'only the three files'
        Assert-True ((Get-Sha256 (Join-Path $out 'sts2_search.dll')) -eq (Get-Sha256 (Join-Path $drop 'sts2_search.dll'))) 'dll copied as is'
        $line = Install-Advisor $s $out ''
        Assert-True ($line -like '*바뀐 파일 0개*') 'same files are skipped'

        # zip (하위 폴더 안에 세 파일 + pdb)
        $pkg = Join-Path $root 'pkg'
        New-FakeFiles (Join-Path $pkg 'advisor') @('Sts2CombatAdvisor.exe', 'sts2_search.dll', 'search-tables.bin', 'Helper.pdb')
        $zip = Join-Path $root 'advisor-made-up.zip'
        Compress-Archive -Path (Join-Path $pkg 'advisor') -DestinationPath $zip
        $sha = Get-Sha256 $zip
        $url = ConvertTo-FileUrl $zip
        Assert-True ($url -like 'file:///*') "zip path becomes a file:// url ($url)"

        # lock 모양 검사
        Write-Lock $repo 'http://example.invalid/a.zip' $sha '1'
        Assert-Throws { Get-AdvisorSource $repo -LockOnly } 'https' 'http url refused'
        Write-Lock $repo $url 'abc' '1'
        Assert-Throws { Get-AdvisorSource $repo -LockOnly } 'sha256' 'short hash refused'
        Write-Lock $repo $url $sha ''
        Assert-Throws { Get-AdvisorSource $repo -LockOnly } 'version' 'missing version refused'

        # -NoLock은 lock을 보지 않고, -LockOnly는 advisor-dist를 보지 않음
        Write-Lock $repo $url $sha 'made-up-1'
        Assert-True ((Get-AdvisorSource $repo).Kind -eq 'drop') 'drop wins over lock'
        Remove-Item -LiteralPath $drop -Recurse -Force
        Assert-True ((Get-AdvisorSource $repo -NoLock).Kind -eq 'none') '-NoLock ignores the lock'
        $s = Get-AdvisorSource $repo -LockOnly
        Assert-True ($s.Kind -eq 'lock' -and $s.Lock.Version -eq 'made-up-1') 'lock found'

        # 맞는 해시 → 세 파일만, 캐시에 남음
        $out2 = Join-Path $root 'out2-advisor'
        $cache = Join-Path $root 'cache'
        $line = Install-Advisor $s $out2 $cache
        Assert-True ($line -like '*SHA-256 확인*바뀐 파일 3개*') "lock installs ($line)"
        Assert-True (@(Get-ChildItem -LiteralPath $out2 -File).Count -eq 3) 'lock: only the three files'
        Assert-True ((Get-Sha256 (Join-Path $out2 'search-tables.bin')) -eq (Get-Sha256 (Join-Path (Join-Path $pkg 'advisor') 'search-tables.bin'))) 'lock: tables extracted as is'

        # 캐시가 있으면 원본 없이도 됨
        Remove-Item -LiteralPath $zip -Force
        $out3 = Join-Path $root 'out3-advisor'
        $line = Install-Advisor $s $out3 $cache
        Assert-True ($line -like '*바뀐 파일 3개*') 'lock from the cache'

        # 다른 해시 → 실패, 아무것도 넣지 않음
        Compress-Archive -Path (Join-Path $pkg 'advisor') -DestinationPath $zip
        Write-Lock $repo $url ('0' * 64) 'made-up-2'
        $s = Get-AdvisorSource $repo -LockOnly
        $out4 = Join-Path $root 'out4-advisor'
        Assert-Throws { Install-Advisor $s $out4 (Join-Path $root 'cache2') } 'SHA-256' 'hash mismatch fails'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $out4 'Sts2CombatAdvisor.exe'))) 'hash mismatch installs nothing'
        Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $root 'cache2') -File).Count -eq 0) 'hash mismatch keeps no download'

        # zip에 dll이 빠짐 → 실패
        Remove-Item -LiteralPath (Join-Path (Join-Path $pkg 'advisor') 'sts2_search.dll') -Force
        $bad = Join-Path $root 'bad.zip'
        Compress-Archive -Path (Join-Path $pkg 'advisor') -DestinationPath $bad
        Write-Lock $repo (ConvertTo-FileUrl $bad) (Get-Sha256 $bad) 'made-up-3'
        Assert-Throws { Install-Advisor (Get-AdvisorSource $repo -LockOnly) (Join-Path $root 'out5-advisor') '' } 'sts2_search.dll' 'zip without the dll fails'
    }
    finally {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
    $color = 'Green'
    if ($script:failed -gt 0) { $color = 'Red' }
    Write-Host ("advisor-package self-test: {0} passed, {1} failed" -f $script:passed, $script:failed) -ForegroundColor $color
    return ($script:failed -eq 0)
}

# ---- main ----
if ($SelfTest) {
    if (Invoke-SelfTest) { exit 0 } else { exit 1 }
}
try {
    $source = Get-AdvisorSource (Resolve-FullPath $RepoRoot) -NoLock:$NoLock -LockOnly:$LockOnly
    if ($Check) {
        if (-not $Quiet) {
            switch ($source.Kind) {
                'drop' { Write-Host ('전투 추천 도우미: advisor-dist의 세 파일을 넣습니다 ({0}).' -f (Format-Size (Get-AdvisorSize $source.Dir))) }
                'lock' { Write-Host "전투 추천 도우미: advisor.lock.json 버전 $($source.Lock.Version)을 받아 SHA-256을 확인하고 넣습니다." }
                default { Write-Host '전투 추천 도우미: 넣을 파일이 없습니다 (advisor-dist도 advisor.lock.json도 없음). 도우미 없이 빌드합니다.' }
            }
        }
        exit 0
    }
    if (-not $Destination) { throw '-Destination(도우미를 넣을 폴더)을 지정해 주세요.' }
    if ($CacheDir) { $CacheDir = Resolve-FullPath $CacheDir }
    $line = Install-Advisor $source (Resolve-FullPath $Destination) $CacheDir -NoLock:$NoLock -LockOnly:$LockOnly
    if ($source.Kind -eq 'none') { Write-Host $line } else { Write-Host $line -ForegroundColor Green }
    exit 0
}
catch {
    Write-Host "전투 추천 도우미: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
