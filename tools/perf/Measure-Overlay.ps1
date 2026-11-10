<#
.SYNOPSIS
  Samples CPU, GPU and memory use of the game and the overlay from the outside, once a second.

.DESCRIPTION
  The overlay's own "perf:" lines in app.log tell how long its stages take. This script answers the other half:
  how much of the machine the overlay uses, and whether the game needs more CPU/GPU while the overlay is on.

  Run it twice during the same kind of play (for example two minutes on the map and in a shop):
    1. with the overlay running            .\Measure-Overlay.ps1 -Label on
    2. with the overlay closed             .\Measure-Overlay.ps1 -Label off
  and compare the two summaries. The machine load line shows whether other heavy jobs distorted a run.

  Numbers:
    cpu%   process CPU time / wall time, in percent of ONE core (so 200 = two full cores)
    gpu%   sum of the process's "GPU Engine" utilisation over its 3D engines (what Task Manager shows)
    vram   dedicated GPU memory of the process, MB
    ws     working set, MB
    load   whole machine CPU utility, percent of all cores (Windows counts turbo boost, so it can read a little over 100)

  It does not measure the game's frame rate: Windows has no counter for that. The overlay logs the frame rate it
  sees through Windows.Graphics.Capture in its "perf: summary" line; for an overlay-off baseline use PresentMon.

.EXAMPLE
  .\Measure-Overlay.ps1 -Seconds 120 -Label on
#>
[CmdletBinding()]
param(
    [int]$Seconds = 120,
    [string]$Label = 'run',
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'Sts2Overlay\perf'),
    [string[]]$Processes = @('SlayTheSpire2', 'Sts2Overlay')
)

$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$csv = Join-Path $OutDir "measure_${Label}_$stamp.csv"

# Counter names are English on every Windows language for these two sets.
$counters = @(
    '\Processor Information(_Total)\% Processor Utility',
    '\GPU Engine(*engtype_3D)\Utilization Percentage',
    '\GPU Process Memory(*)\Dedicated Usage'
)

function Get-Targets {
    $found = @{}
    foreach ($name in $Processes) {
        # Several processes can share a name (a crash handler, a second instance): take the one using the most memory.
        $p = Get-Process -Name $name -ErrorAction SilentlyContinue | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
        if ($p) { $found[$name] = $p }
    }
    $found
}

$targets = Get-Targets
if ($targets.Count -eq 0) { Write-Warning "None of [$($Processes -join ', ')] is running; only the machine load will be recorded." }
foreach ($name in $Processes) {
    if (-not $targets.ContainsKey($name)) { Write-Host "note: $name is not running" }
}

$header = @('t', 'load') + ($Processes | ForEach-Object { "${_}_cpu", "${_}_gpu", "${_}_vram", "${_}_ws" })
Set-Content -Path $csv -Value ($header -join ',') -Encoding utf8

$lastCpu = @{}
foreach ($name in $targets.Keys) { $lastCpu[$name] = $targets[$name].TotalProcessorTime }
$lastAt = [Diagnostics.Stopwatch]::GetTimestamp()
$start = Get-Date
$rows = [Collections.Generic.List[object]]::new()
$sw = [Diagnostics.Stopwatch]::StartNew()
$n = 0

Write-Host "Measuring for $Seconds s -> $csv"
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    # One Get-Counter call takes its own ~1 s sample, which is also the loop's pace.
    $samples = @()
    try { $samples = (Get-Counter -Counter $counters -ErrorAction SilentlyContinue).CounterSamples } catch { }
    $now = [Diagnostics.Stopwatch]::GetTimestamp()
    $wall = ($now - $lastAt) / [Diagnostics.Stopwatch]::Frequency
    $lastAt = $now

    $load = ($samples | Where-Object { $_.Path -like '*processor utility' } | Select-Object -First 1).CookedValue
    $row = [ordered]@{ t = [math]::Round($sw.Elapsed.TotalSeconds, 1); load = [math]::Round([double]$load, 1) }

    # A process that started or restarted after the script began is picked up here.
    $current = Get-Targets
    foreach ($name in $Processes) {
        $cpu = $null; $gpu = $null; $vram = $null; $ws = $null
        $p = $current[$name]
        if ($p) {
            $p.Refresh()
            $total = $p.TotalProcessorTime
            if ($lastCpu.ContainsKey($name) -and $targets.ContainsKey($name) -and $targets[$name].Id -eq $p.Id -and $wall -gt 0) {
                $cpu = [math]::Round(($total - $lastCpu[$name]).TotalSeconds / $wall * 100, 1)
            }
            $lastCpu[$name] = $total
            $targets[$name] = $p
            $tag = "pid_$($p.Id)_"
            $gpu = [math]::Round((($samples | Where-Object { $_.Path -like '*gpu engine*' -and $_.InstanceName -like "$tag*" } | Measure-Object CookedValue -Sum).Sum), 1)
            $vram = [math]::Round((($samples | Where-Object { $_.Path -like '*gpu process memory*' -and $_.InstanceName -like "$tag*" } | Measure-Object CookedValue -Sum).Sum) / 1MB, 0)
            $ws = [math]::Round($p.WorkingSet64 / 1MB, 0)
        }
        $row["${name}_cpu"] = $cpu; $row["${name}_gpu"] = $gpu; $row["${name}_vram"] = $vram; $row["${name}_ws"] = $ws
    }
    $rows.Add([pscustomobject]$row)
    Add-Content -Path $csv -Value (($row.Values | ForEach-Object { if ($null -eq $_) { '' } else { ([double]$_).ToString($inv) } }) -join ',')

    $n++
    $done = [math]::Min($sw.Elapsed.TotalSeconds, $Seconds)
    $eta = $start.AddSeconds($Seconds).ToString('HH:mm:ss')
    $line = "[{0,3:0}/{1} s {2,3:0}% ETA {3}] load {4,5:0.0}%" -f $done, $Seconds, ($done / $Seconds * 100), $eta, $row.load
    foreach ($name in $Processes) {
        if ($null -ne $row["${name}_ws"]) { $line += "  {0}: cpu {1}% gpu {2}%" -f $name, $row["${name}_cpu"], $row["${name}_gpu"] }
    }
    Write-Host $line
}

function Stat($values) {
    $v = @($values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    [pscustomobject]@{
        n   = $v.Count
        avg = [math]::Round(($v | Measure-Object -Average).Average, 1)
        p95 = $v[[math]::Min($v.Count - 1, [int][math]::Floor(($v.Count - 1) * 0.95))]
        max = $v[-1]
    }
}

$summary = [Collections.Generic.List[string]]::new()
$summary.Add("label $Label, $n samples over $([math]::Round($sw.Elapsed.TotalSeconds)) s, started $($start.ToString('yyyy-MM-dd HH:mm:ss'))")
$s = Stat ($rows | ForEach-Object { $_.load })
if ($s) { $summary.Add(("machine load   avg {0}%  p95 {1}%  max {2}%" -f $s.avg, $s.p95, $s.max)) }
foreach ($name in $Processes) {
    foreach ($m in 'cpu', 'gpu', 'vram', 'ws') {
        $s = Stat ($rows | ForEach-Object { $_."${name}_$m" })
        if ($s) {
            $unit = if ($m -in 'cpu', 'gpu') { '%' } else { ' MB' }
            $summary.Add(("{0,-14} {1,-4} avg {2}{5}  p95 {3}{5}  max {4}{5}" -f $name, $m, $s.avg, $s.p95, $s.max, $unit))
        }
    }
}
$txt = [IO.Path]::ChangeExtension($csv, '.txt')
Set-Content -Path $txt -Value $summary -Encoding utf8
Write-Host ''
$summary | ForEach-Object { Write-Host $_ }
Write-Host "saved: $csv"
