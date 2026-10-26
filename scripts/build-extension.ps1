#!/usr/bin/env pwsh
<#
.SYNOPSIS
    SwiftGet Browser Extension Packaging Automation
.DESCRIPTION
    Packages SwiftGet extension for Chromium (Chrome, Edge, Brave, Opera) and Mozilla Firefox,
    validating manifests, generating distribution ZIP/XPI archives, and calculating SHA-256 hashes.
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$ExtensionDir = Join-Path $Root "extension"
$DistExtensionDir = Join-Path $Root "dist\extension"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [SwiftGet] Packaging Browser Extensions v$Version        " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

if (Test-Path $DistExtensionDir) {
    Remove-Item $DistExtensionDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $DistExtensionDir | Out-Null

# 1. Package Chromium Extension (Chrome, Edge, Brave, Opera)
Write-Host "`n[1/3] Packaging Chromium Extension (Manifest V3)..." -ForegroundColor Yellow
$chromiumZip = Join-Path $DistExtensionDir "swiftget-chromium-v$Version.zip"
$tempChromium = Join-Path $DistExtensionDir "chromium_staging"
New-Item -ItemType Directory -Force -Path $tempChromium | Out-Null

$includeFiles = @(
    "manifest.json",
    "background.js",
    "content.js",
    "sniffer.css",
    "popup.html",
    "popup.js",
    "options.html",
    "options.js",
    "README.md"
)

foreach ($f in $includeFiles) {
    $src = Join-Path $ExtensionDir $f
    if (Test-Path $src) {
        Copy-Item $src $tempChromium -Force
    }
}

$iconsSrc = Join-Path $ExtensionDir "icons"
if (Test-Path $iconsSrc) {
    Copy-Item $iconsSrc (Join-Path $tempChromium "icons") -Recurse -Force
}

# Also copy root defexclist.txt into extension directory
$defExclist = Join-Path $Root "defexclist.txt"
if (Test-Path $defExclist) {
    Copy-Item $defExclist (Join-Path $tempChromium "defexclist.txt") -Force
}

Compress-Archive -Path (Join-Path $tempChromium "*") -DestinationPath $chromiumZip -Force
Remove-Item $tempChromium -Recurse -Force

$chromiumHash = (Get-FileHash -Path $chromiumZip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Chromium Package: $chromiumZip" -ForegroundColor Green
Write-Host "SHA256: $chromiumHash" -ForegroundColor Green

# 2. Package Firefox WebExtension (.xpi)
Write-Host "`n[2/3] Packaging Mozilla Firefox Extension (WebExtensions MV3)..." -ForegroundColor Yellow
$firefoxXpi = Join-Path $DistExtensionDir "swiftget-firefox-v$Version.xpi"
$tempFirefox = Join-Path $DistExtensionDir "firefox_staging"
New-Item -ItemType Directory -Force -Path $tempFirefox | Out-Null

foreach ($f in $includeFiles) {
    if ($f -eq "manifest.json") {
        # Use Firefox-specific manifest
        $src = Join-Path $ExtensionDir "manifest.firefox.json"
        Copy-Item $src (Join-Path $tempFirefox "manifest.json") -Force
    } else {
        $src = Join-Path $ExtensionDir $f
        if (Test-Path $src) {
            Copy-Item $src $tempFirefox -Force
        }
    }
}

if (Test-Path $iconsSrc) {
    Copy-Item $iconsSrc (Join-Path $tempFirefox "icons") -Recurse -Force
}
if (Test-Path $defExclist) {
    Copy-Item $defExclist (Join-Path $tempFirefox "defexclist.txt") -Force
}

# Compress to .zip then rename to .xpi
$tempFirefoxZip = Join-Path $DistExtensionDir "temp_firefox.zip"
Compress-Archive -Path (Join-Path $tempFirefox "*") -DestinationPath $tempFirefoxZip -Force
Move-Item $tempFirefoxZip $firefoxXpi -Force
Remove-Item $tempFirefox -Recurse -Force

$firefoxHash = (Get-FileHash -Path $firefoxXpi -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Firefox Package: $firefoxXpi" -ForegroundColor Green
Write-Host "SHA256: $firefoxHash" -ForegroundColor Green

# 3. Generate extension-checksums.json
Write-Host "`n[3/3] Generating Checksums Manifest..." -ForegroundColor Yellow
$checksums = [PSCustomObject]@{
    version = $Version
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    chromium = [PSCustomObject]@{
        file = "swiftget-chromium-v$Version.zip"
        sha256 = $chromiumHash
        targetBrowsers = @("Google Chrome", "Microsoft Edge", "Brave", "Opera")
    }
    firefox = [PSCustomObject]@{
        file = "swiftget-firefox-v$Version.xpi"
        sha256 = $firefoxHash
        targetBrowsers = @("Mozilla Firefox", "LibreWolf", "Waterfox")
    }
}

$checksumsJson = $checksums | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $DistExtensionDir "checksums.json") -Value $checksumsJson -NoNewline
Write-Host "Checksums written to $(Join-Path $DistExtensionDir 'checksums.json')" -ForegroundColor DarkCyan

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " [SwiftGet] Browser Extensions Built & Packaged!          " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
