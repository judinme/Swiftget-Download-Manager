#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates high-resolution WiX MSI installer bitmaps (dialog.bmp and banner.bmp).
.DESCRIPTION
    Creates standard two-tone WiX UI bitmaps conforming to Windows Installer specifications:
    - dialog.bmp (493x312): Left 164px branded sidebar with gradient, app icon, and typography;
      right 329px pure white (#FFFFFF) to guarantee high-contrast legibility and zero-artifact
      checkbox rendering in ExitDialog.
    - banner.bmp (493x58): Left 430px pure white (#FFFFFF) for high-contrast title text;
      right side featuring the SwiftGet app icon and subtle bottom border.
#>
param(
    [string]$AssetsDir = "$PSScriptRoot\..\installer\assets",
    [string]$AppIconPng = "$PSScriptRoot\..\src\SwiftGet.App\Assets\app.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $AppIconPng)) {
    throw "Icon file not found: $AppIconPng"
}

$icon = [System.Drawing.Bitmap]::FromFile((Resolve-Path $AppIconPng))

try {
    # -------------------------------------------------------------
    # 1. Generate dialog.bmp (493 x 312)
    # -------------------------------------------------------------
    $dialogBmp = New-Object System.Drawing.Bitmap 493, 312, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($dialogBmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    # Clear whole canvas with pure white (#FFFFFF)
    $g.Clear([System.Drawing.Color]::White)

    # Branded Sidebar (x: 0..164, y: 0..312)
    $sidebarRect = New-Object System.Drawing.Rectangle 0, 0, 164, 312
    $gradBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $sidebarRect,
        [System.Drawing.Color]::FromArgb(15, 23, 42),   # #0f172a (deep slate navy)
        [System.Drawing.Color]::FromArgb(30, 58, 138),  # #1e3a8a (deep royal blue)
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical
    )
    $g.FillRectangle($gradBrush, $sidebarRect)
    $gradBrush.Dispose()

    # Subtle diagonal ambient accents
    $accentPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(25, 255, 255, 255), 1)
    $g.DrawLine($accentPen, 0, 70, 164, 170)
    $g.DrawLine($accentPen, 0, 130, 164, 230)
    $accentPen.Dispose()

    # Center 100x100 SwiftGet Icon in sidebar (center x = 82)
    $iconX = [int]((164 - 100) / 2) # 32
    $iconY = 46
    $g.DrawImage($icon, $iconX, $iconY, 100, 100)

    # Branding typography in sidebar
    $titleFont = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Point)
    $titleBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center

    $g.DrawString('SwiftGet', $titleFont, $titleBrush, 82, 156, $sf)

    $subFont = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    $subBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(147, 197, 253)) # #93c5fd
    $g.DrawString('Download Manager', $subFont, $subBrush, 82, 184, $sf)

    $tagFont = New-Object System.Drawing.Font('Segoe UI', 7.5, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    $tagBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(148, 163, 184)) # #94a3b8
    $g.DrawString('IDM-Class Acceleration', $tagFont, $tagBrush, 82, 206, $sf)

    # Clean 1px border separating sidebar and white content area
    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(226, 232, 240), 1)
    $g.DrawLine($borderPen, 163, 0, 163, 312)
    $borderPen.Dispose()

    $titleFont.Dispose(); $titleBrush.Dispose(); $subFont.Dispose(); $subBrush.Dispose()
    $tagFont.Dispose(); $tagBrush.Dispose(); $sf.Dispose()
    $g.Dispose()

    $dialogBmpOut = Join-Path $AssetsDir 'dialog.bmp'
    $dialogBmp.Save($dialogBmpOut, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $dialogBmp.Dispose()
    Write-Host "Generated: $dialogBmpOut" -ForegroundColor Green

    # -------------------------------------------------------------
    # 2. Generate banner.bmp (493 x 58)
    # -------------------------------------------------------------
    $bannerBmp = New-Object System.Drawing.Bitmap 493, 58, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $gb = [System.Drawing.Graphics]::FromImage($bannerBmp)
    $gb.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $gb.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gb.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Clear whole banner with pure white (#FFFFFF)
    $gb.Clear([System.Drawing.Color]::White)

    # Draw SwiftGet icon on far right (46x46 at x=438, y=6)
    $gb.DrawImage($icon, 438, 6, 46, 46)

    # Bottom 1px separator line
    $bBorderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(226, 232, 240), 1)
    $gb.DrawLine($bBorderPen, 0, 57, 493, 57)
    $bBorderPen.Dispose()

    $gb.Dispose()

    $bannerBmpOut = Join-Path $AssetsDir 'banner.bmp'
    $bannerBmp.Save($bannerBmpOut, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bannerBmp.Dispose()
    Write-Host "Generated: $bannerBmpOut" -ForegroundColor Green
}
finally {
    $icon.Dispose()
}
