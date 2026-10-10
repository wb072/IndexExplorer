# 색인 탐색기 app.ico 만들기
#  - 64px 이상: AppIcon.xaml 의 부드러운 벡터(IconApp)
#  - 48px 이하: 16칸 격자 디자인을 그 크기의 "정수 픽셀"에 맞춰 다시 계산해 그린다
#    (그냥 줄이거나 키우면 가장자리가 픽셀 사이에 걸쳐 흐려진다. 작업 표시줄 24px 등)
# 사용: powershell -NoProfile -STA -ExecutionPolicy Bypass -File Assets\make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dict = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $dir 'AppIcon.xaml')))
$big = $dict['IconApp']

function Brush([string]$hex) { $b = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }

# 16칸 격자 디자인 (AppIcon.xaml 의 IconAppSmall 과 같은 배치)
#   [색, x1, y1, x2, y2]  — 칸 단위
$parts = @(
    @('#BDB5F4', 13, 5, 15, 7),     # 색인 탭 1
    @('#A196EE', 13, 8, 15, 10),    # 색인 탭 2
    @('#8579E3', 13, 11, 15, 13),   # 색인 탭 3
    @('#4C42AE', 1, 4, 13, 13),     # 폴더 뒷면
    @('#4C42AE', 1, 3, 6, 4),       # 폴더 귀
    @('#7D74E0', 1, 5, 13, 14),     # 폴더 앞면
    @('#8E87EA', 1, 5, 13, 6),      # 앞면 위 밝은 띠
    @('#FFFFFF', 3, 7, 8, 8),       # 목차 줄
    @('#E6E3FC', 3, 9, 10, 10),
    @('#CFCAF7', 3, 11, 7, 12)
)

function SnappedIcon([int]$s) {
    $u = $s / 16.0
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    foreach ($p in $parts) {
        # 반올림은 늘 같은 방향(0.5 → 위)으로 해야 간격이 고르다 ([math]::Round 는 짝수 쪽으로 반올림)
        $x1 = [math]::Floor($p[1] * $u + 0.5); $y1 = [math]::Floor($p[2] * $u + 0.5)
        $x2 = [math]::Floor($p[3] * $u + 0.5); $y2 = [math]::Floor($p[4] * $u + 0.5)
        if ($y2 - $y1 -lt 1) { $y2 = $y1 + 1 }   # 1칸짜리 줄은 최소 1px
        if ($x2 - $x1 -lt 1) { $x2 = $x1 + 1 }
        $dc.DrawRectangle((Brush $p[0]), $null, (New-Object Windows.Rect $x1, $y1, ($x2 - $x1), ($y2 - $y1)))
    }
    $dc.Close()
    $dv
}

function BigIcon([int]$s) {
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.DrawImage($big, (New-Object Windows.Rect 0, 0, $s, $s))
    $dc.Close()
    $dv
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $dv = if ($s -le 48) { SnappedIcon $s } else { BigIcon $s }
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    $pngs += , $ms.ToArray()
}

# ICO 파일: 헤더 + 항목 표 + PNG 데이터
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $dir 'app.ico'), $out.ToArray())
"app.ico: $($sizes -join ', ')px (48px 이하는 픽셀 정렬)"
