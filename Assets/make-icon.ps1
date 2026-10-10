# AppIcon.xaml(벡터) → app.ico (16~256px, PNG 압축 항목) 와 미리 보기 PNG 를 만든다.
# 사용: powershell -ExecutionPolicy Bypass -File Assets\make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$xaml = [IO.File]::ReadAllText((Join-Path $dir 'AppIcon.xaml'))
$dict = [Windows.Markup.XamlReader]::Parse($xaml)
$img = $dict['IconApp']

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.DrawImage($img, (New-Object Windows.Rect 0, 0, $s, $s))
    $dc.Close()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    $pngs += , $ms.ToArray()
    if ($s -eq 256) { [IO.File]::WriteAllBytes((Join-Path $dir 'app-256.png'), $ms.ToArray()) }
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
"app.ico: $($sizes -join ', ')px"
