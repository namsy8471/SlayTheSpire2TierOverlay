<#
  src/Sts2Overlay.App/Assets/icon.svg(아이콘 원본)로 src/Sts2Overlay.App/Assets/Sts2Overlay.ico를 만듭니다.
  아이콘은 직접 그린 것입니다 (빨간 카드 + 첨탑 + 금색 "S" 동전 배지). 게임 그림은 쓰지 않습니다.
  "S" 글자는 Liberation Serif Bold 글리프를 경로로 바꾼 것입니다 (SIL OFL, 포함해도 됨).

    powershell -File tools/make-icon.ps1        (PowerShell 7도 됩니다)

  1. Microsoft Edge(헤드리스)로 SVG를 1024x1024 투명 PNG로 렌더링합니다.
  2. 그 큰 그림을 크기마다 고품질로 줄여 16/20/24/32/40/48/64/256 프레임을 만듭니다.
     24px 이하는 동전 배지와 "S"를 1.4배로 키운 변형을 씁니다. 원본 비율 그대로면 16px에서 S가 뭉개지기 때문입니다.
     작은 크기도 큰 그림에서 줄여야 가장자리가 부드럽습니다.
  3. 프레임을 PNG로 압축해 .ico 하나에 담습니다 (Windows Vista 이상은 PNG 프레임을 읽습니다).
  -PreviewDir을 주면 크기별 PNG도 그 폴더에 저장합니다 (16/32px 확인용).
#>
param(
    [string]$Svg = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Sts2Overlay.App/Assets/icon.svg'),
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Sts2Overlay.App/Assets/Sts2Overlay.ico'),
    [string]$PreviewDir
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge(msedge.exe)를 찾지 못했습니다.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('sts2-make-icon-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # 1. SVG → 1024px PNG. 페이지 여백 없이 SVG만 꽉 차게, 배경은 투명.
    function Render-Svg([string]$svgPath, [string]$name) {
        $svgUri = ([Uri](Resolve-Path -LiteralPath $svgPath).Path).AbsoluteUri
        $html = Join-Path $work "$name.html"
        Set-Content -LiteralPath $html -Encoding UTF8 -Value @"
<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}
img{display:block;width:1024px;height:1024px}</style></head><body><img src="$svgUri"></body></html>
"@
        $png = Join-Path $work "$name-1024.png"
        $edgeArgs = @('--headless', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000',
            "--user-data-dir=$work\edge", "--screenshot=$png", '--window-size=1024,1024', ([Uri]$html).AbsoluteUri)
        $p = Start-Process -FilePath $edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'Edge 렌더링이 60초 안에 끝나지 않았습니다.' }
        for ($i = 0; $i -lt 20 -and -not (Test-Path -LiteralPath $png); $i++) { Start-Sleep -Milliseconds 250 }
        if (-not (Test-Path -LiteralPath $png)) { throw 'Edge가 PNG를 만들지 못했습니다.' }
        $img = [System.Drawing.Image]::FromFile($png)
        if ($img.Width -ne 1024 -or $img.Height -ne 1024) { throw "렌더링 크기가 1024x1024가 아닙니다: $($img.Width)x$($img.Height)" }
        return $img
    }

    # 작은 크기용 변형: 동전 배지(원 2개 + S 경로, 맨 끝 요소들)를 1.4배로 키우고 타일 안쪽으로 옮깁니다.
    $text = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Svg).Path)
    $coin = $text.IndexOf('<circle cx="184"')
    $end = $text.LastIndexOf('</svg>')
    if ($coin -lt 0 -or $end -lt $coin) { throw 'icon.svg에서 동전 배지(<circle cx="184")를 찾지 못했습니다.' }
    $smallSvg = Join-Path $work 'icon-small.svg'
    [IO.File]::WriteAllText($smallSvg, $text.Substring(0, $coin) +
        '<g transform="translate(172 86) scale(1.4) translate(-184 -76)">' + $text.Substring($coin, $end - $coin) + '</g></svg>')

    $src = Render-Svg $Svg 'icon'
    $srcSmall = Render-Svg $smallSvg 'icon-small'

    # 2. 크기별 프레임.
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $frames = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.CompositingQuality = 'HighQuality'
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.PixelOffsetMode = 'HighQuality'
        $g.SmoothingMode = 'AntiAlias'
        $from = if ($s -le 24) { $srcSmall } else { $src }
        $g.DrawImage($from, (New-Object System.Drawing.Rectangle 0, 0, $s, $s))
        $g.Dispose()
        if ($PreviewDir) {
            New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null
            $bmp.Save((Join-Path $PreviewDir "icon-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $frames += , $ms.ToArray()
    }
    $src.Dispose(); $srcSmall.Dispose()

    # 3. .ico 파일 쓰기.
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
    $fs = [System.IO.File]::Create($Out)
    $bw = New-Object System.IO.BinaryWriter $fs
    # ICONDIR: reserved, type(1 = icon), count
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }  # 0은 256을 뜻합니다
        # ICONDIRENTRY: width, height, colours, reserved, planes, bpp, bytes, offset
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $bw.Write([byte[]]$f) }
    $bw.Dispose()
    Write-Host ("wrote {0} ({1:N0} bytes, sizes {2})" -f $Out, (Get-Item $Out).Length, ($sizes -join '/'))
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
