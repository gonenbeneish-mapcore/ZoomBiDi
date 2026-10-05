# Generates assets\ZoomBiDi.ico (blue) and assets\ZoomBiDi-paused.ico (grey): a Zoom-style lit blue "ball" with "Aא".
# Each size is drawn natively (not downscaled) so the tray sizes stay crisp. Also writes a preview PNG.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\assets'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

# Per-pixel "lit ball" background, modelled on measurements of the Zoom Workplace icon:
#  * outline: superellipse |x|^2.4 + |y|^2.4 = 1 filling the tile, anti-aliased analytically;
#  * light: a soft glow hugging the edge, strongest around 1-2 o'clock, fading ~30/256 px inward;
#  * shade: darkening that deepens toward the bottom-left (the underside of the ball).
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class BallRenderer {
  public static Bitmap Render(int s, Color light, Color baseColor, Color shade) {
    const double N = 2.4;
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    var px = new byte[s * s * 4];
    double half = s / 2.0, r = s / 2.0 - 0.25;
    double dirX = -0.41, dirY = 0.91;              // toward the shaded side (down, a bit left)
    for (int y = 0; y < s; y++)
    for (int x = 0; x < s; x++) {
      double nx = (x + 0.5 - half) / r, ny = (y + 0.5 - half) / r;
      double rho = Math.Pow(Math.Pow(Math.Abs(nx), N) + Math.Pow(Math.Abs(ny), N), 1.0 / N);
      double coverage = Math.Max(0, Math.Min(1, (1 - rho) * r + 0.5));
      if (coverage <= 0) continue;

      double edge = Math.Max(0, (1 - rho) * 128);   // distance to the outline, in 256-px units
      double ang = Math.Atan2(-ny, nx) * 180 / Math.PI; // 0 = right, 90 = top
      double a = (ang - 60) / 40;
      double glow = 0.72 * Math.Exp(-a * a) * Math.Exp(-edge / 38);

      double t = Math.Max(0, nx * dirX + ny * dirY);
      double dark = Math.Min(1, Math.Pow(t, 1.6));

      double R = baseColor.R + (shade.R - baseColor.R) * dark, G = baseColor.G + (shade.G - baseColor.G) * dark, B = baseColor.B + (shade.B - baseColor.B) * dark;
      R += (light.R - R) * glow; G += (light.G - G) * glow; B += (light.B - B) * glow;

      int i = (y * s + x) * 4;
      px[i] = (byte)Math.Round(B); px[i + 1] = (byte)Math.Round(G); px[i + 2] = (byte)Math.Round(R);
      px[i + 3] = (byte)Math.Round(255 * coverage);
    }
    Marshal.Copy(px, 0, data.Scan0, px.Length);
    bmp.UnlockBits(data);
    return bmp;
  }
}
"@

# $top = highlight colour, $mid = body colour, $bottom = shade colour.
function New-IconBitmap([int]$s, [System.Drawing.Color]$top, [System.Drawing.Color]$mid, [System.Drawing.Color]$bottom) {
  $bmp = [BallRenderer]::Render($s, $top, $mid, $bottom)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.PixelOffsetMode = 'HighQuality'
  $g.CompositingQuality = 'HighQuality'

  # "Aא" as a path so it can be centred on its real ink bounds. Small sizes get relatively larger letters.
  $ratio = if ($s -le 20) { 0.60 } elseif ($s -le 32) { 0.56 } else { 0.46 }
  $family = New-Object System.Drawing.FontFamily 'Segoe UI Semibold'
  $text = New-Object System.Drawing.Drawing2D.GraphicsPath
  $fmt = [System.Drawing.StringFormat]::GenericTypographic
  $text.AddString([string]'A' + [char]0x05D0, $family, 0, [float]($s * $ratio), (New-Object System.Drawing.PointF 0, 0), $fmt)
  $b = $text.GetBounds()
  $maxW = if ($s -le 32) { $s * 0.74 } else { $s * 0.66 }
  if ($b.Width -gt $maxW) {
    $scale = New-Object System.Drawing.Drawing2D.Matrix
    $scale.Scale([float]($maxW / $b.Width), [float]($maxW / $b.Width))
    $text.Transform($scale)
    $b = $text.GetBounds()
  }
  $move = New-Object System.Drawing.Drawing2D.Matrix
  $move.Translate([float](($s - $b.Width) / 2 - $b.X), [float](($s - $b.Height) / 2 - $b.Y))
  $text.Transform($move)
  $g.FillPath([System.Drawing.Brushes]::White, $text)

  $g.Dispose()
  return $bmp
}

function Write-Ico([string]$file, [System.Drawing.Color]$top, [System.Drawing.Color]$mid, [System.Drawing.Color]$bottom) {
  $pngs = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s $top $mid $bottom
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , @($s, $ms.ToArray())
  }
  $fs = [System.IO.File]::Create($file)
  $w = New-Object System.IO.BinaryWriter $fs
  $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
  $offset = 6 + 16 * $pngs.Count
  foreach ($p in $pngs) {
    $dim = if ($p[0] -ge 256) { 0 } else { $p[0] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$p[1].Length); $w.Write([uint32]$offset)
    $offset += $p[1].Length
  }
  foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
  $w.Dispose()
}

$blueTop = [System.Drawing.Color]::FromArgb(0x55, 0x93, 0xF0)
$blueMid = [System.Drawing.Color]::FromArgb(0x0B, 0x5C, 0xFF)
$blueBottom = [System.Drawing.Color]::FromArgb(0x08, 0x44, 0xBE)
$greyTop = [System.Drawing.Color]::FromArgb(0xC2, 0xC7, 0xCE)
$greyMid = [System.Drawing.Color]::FromArgb(0x8A, 0x91, 0x9B)
$greyBottom = [System.Drawing.Color]::FromArgb(0x5F, 0x66, 0x70)

Write-Ico (Join-Path $OutDir 'ZoomBiDi.ico') $blueTop $blueMid $blueBottom
Write-Ico (Join-Path $OutDir 'ZoomBiDi-paused.ico') $greyTop $greyMid $greyBottom

# Preview strip: every size, blue and grey, on light and dark backgrounds.
$pw = 0; foreach ($s in $sizes) { $pw += $s + 16 }
$preview = New-Object System.Drawing.Bitmap ($pw + 16), (2 * (256 + 32) * 2)
$pg = [System.Drawing.Graphics]::FromImage($preview)
$rows = @(
  @([System.Drawing.Color]::FromArgb(245, 245, 245), $blueTop, $blueMid, $blueBottom),
  @([System.Drawing.Color]::FromArgb(32, 32, 32), $blueTop, $blueMid, $blueBottom),
  @([System.Drawing.Color]::FromArgb(245, 245, 245), $greyTop, $greyMid, $greyBottom),
  @([System.Drawing.Color]::FromArgb(32, 32, 32), $greyTop, $greyMid, $greyBottom)
)
$y = 0
foreach ($r in $rows) {
  $pg.FillRectangle((New-Object System.Drawing.SolidBrush $r[0]), 0, $y, $preview.Width, 288)
  $x = 16
  foreach ($s in $sizes) { $bmp = New-IconBitmap $s $r[1] $r[2] $r[3]; $pg.DrawImage($bmp, $x, $y + 16 + (256 - $s)); $bmp.Dispose(); $x += $s + 16 }
  $y += 288
}
$pg.Dispose()
$preview.Save((Join-Path $OutDir 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Get-ChildItem $OutDir | Select-Object Name, Length
