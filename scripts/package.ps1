#!/usr/bin/env pwsh
<#
.SYNOPSIS
    SwiftGet Release Packaging Automation
.DESCRIPTION
    Publishes self-contained binaries, builds the WiX MSI installer, computes SHA-256 hashes,
    generates auto-updater manifest, and updates Winget package manifests.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# Prioritise local .NET SDK
if (Test-Path "C:\Users\SPARK\AppData\Local\Microsoft\dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "C:\Users\SPARK\AppData\Local\Microsoft\dotnet"
    $env:PATH = "C:\Users\SPARK\AppData\Local\Microsoft\dotnet;$env:PATH"
}

$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Dist = Join-Path $Root "dist"
if (Test-Path $Dist) {
    Remove-Item $Dist -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [SwiftGet] Packaging Release Artifacts v$Version         " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Publish SwiftGet.NativeHost
Write-Host "`n[1/5] Publishing SwiftGet.NativeHost (win-x64)..." -ForegroundColor Yellow
$nativeHostProj = Join-Path $Root "src\SwiftGet.NativeHost\SwiftGet.NativeHost.csproj"
$nativeHostTemp = Join-Path $Dist "nativehost_temp"
if (Test-Path $nativeHostTemp) { Remove-Item $nativeHostTemp -Recurse -Force }
dotnet publish $nativeHostProj -c $Configuration -r win-x64 --self-contained -o $nativeHostTemp
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish SwiftGet.NativeHost."
    exit $LASTEXITCODE
}

# 2. Publish SwiftGet.App and assemble staging directory
Write-Host "`n[2/5] Publishing SwiftGet.App (win-x64, self-contained)..." -ForegroundColor Yellow
$appProj = Join-Path $Root "src\SwiftGet.App\SwiftGet.App.csproj"
$stagingDir = Join-Path $Dist "staging"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
dotnet publish $appProj -c $Configuration -r win-x64 --self-contained -o $stagingDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish SwiftGet.App."
    exit $LASTEXITCODE
}

# Merge NativeHost into Staging directory
Copy-Item (Join-Path $nativeHostTemp "*") $stagingDir -Recurse -Force
Remove-Item $nativeHostTemp -Recurse -Force

# Copy native messaging host manifest into staging directory
$assetsDir = Join-Path $Root "installer\assets"
Copy-Item (Join-Path $assetsDir "*.json") $stagingDir -Force

# Copy IDM domain exclusion list into staging directory
Copy-Item (Join-Path $Root "defexclist.txt") $stagingDir -Force

# 3. Build Browser Extensions (Chromium & Firefox)
Write-Host "`n[3/5] Packaging Browser Extensions..." -ForegroundColor Yellow
$extScript = Join-Path $PSScriptRoot "build-extension.ps1"
if (Test-Path $extScript) {
    & $extScript -Version $Version
}

# Stage extension files and distribution archives into the installer staging directory
$stagingExtDir = Join-Path $stagingDir "extension"
New-Item -ItemType Directory -Force -Path $stagingExtDir | Out-Null
$includeExtFiles = @("manifest.json", "manifest.firefox.json", "background.js", "content.js", "sniffer.css", "popup.html", "popup.js", "options.html", "options.js", "README.md")
foreach ($ef in $includeExtFiles) {
    $src = Join-Path $Root "extension\$ef"
    if (Test-Path $src) { Copy-Item $src $stagingExtDir -Force }
}
if (Test-Path (Join-Path $Root "extension\icons")) {
    Copy-Item (Join-Path $Root "extension\icons") (Join-Path $stagingExtDir "icons") -Recurse -Force
}
$distExtDir = Join-Path $Dist "extension"
if (Test-Path $distExtDir) {
    $packagesDir = Join-Path $stagingDir "packages"
    New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null
    Copy-Item (Join-Path $distExtDir "*") $packagesDir -Recurse -Force
}

# 4. Build WiX MSI Package
Write-Host "`n[4/5] Compiling WiX v4/v5 MSI Installer..." -ForegroundColor Yellow
$genAssetsScript = Join-Path $PSScriptRoot "generate-installer-assets.ps1"
if (Test-Path $genAssetsScript) {
    & $genAssetsScript
}
$wxsFile = Join-Path $Root "installer\SwiftGet.Package\Package.wxs"
$msiOut = Join-Path $Dist "SwiftGet-$Version-x64.msi"

wix build $wxsFile -arch x64 -ext WixToolset.UI.wixext -d StagingDir="$stagingDir" -d AssetsDir="$assetsDir" -d Version="$Version" -out $msiOut
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $msiOut)) {
    Write-Error "Failed to build WiX MSI installer."
    exit 1
}

# 5. Compute Checksum & Update Manifests
Write-Host "`n[5/5] Computing SHA-256 Checksums & Generating Manifests..." -ForegroundColor Yellow
$hashBytes = (Get-FileHash -Path $msiOut -Algorithm SHA256).Hash.ToLowerInvariant()
$fileSize = (Get-Item $msiOut).Length
$fileSizeMb = [math]::Round($fileSize / (1024 * 1024), 2)

Write-Host "MSI Package: $msiOut" -ForegroundColor Green
Write-Host "Size: $fileSize bytes ($fileSizeMb MB)" -ForegroundColor Green
Write-Host "SHA256: $hashBytes" -ForegroundColor Green

# Update Winget manifest with real SHA-256 hash
$wingetInstallerFile = Join-Path $Root "installer\winget\manifests\s\SwiftGet\SwiftGet\$Version\SwiftGet.SwiftGet.installer.yaml"
if (Test-Path $wingetInstallerFile) {
    $wingetContent = Get-Content $wingetInstallerFile -Raw
    $wingetContent = $wingetContent -replace "InstallerSha256:\s*[0-9a-fA-F]+", "InstallerSha256: $hashBytes"
    Set-Content -Path $wingetInstallerFile -Value $wingetContent -NoNewline
    Write-Host "Updated Winget installer manifest with SHA-256 hash." -ForegroundColor DarkCyan
}

# Generate Auto-Updater Manifest (updates.json)
$updateManifest = [PSCustomObject]@{
    version = $Version
    releaseDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    releaseNotes = "SwiftGet v$Version Release - Full IDM Parity with Multi-Segment Acceleration and BitTorrent Support"
    downloadUrl = "https://github.com/swiftget/swiftget/releases/download/v$Version/SwiftGet-$Version-x64.msi"
    sha256Checksum = $hashBytes
    minSupportedVersion = "1.0.0"
    mandatoryUpdate = $false
    installerArguments = "/passive"
}

$updateJson = $updateManifest | ConvertTo-Json -Depth 4
$updateFile = Join-Path $Dist "updates.json"
Set-Content -Path $updateFile -Value $updateJson -NoNewline
Write-Host "Auto-updater manifest written to $updateFile" -ForegroundColor DarkCyan

$sw.Stop()
Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " [SwiftGet] Release Packaging Complete in $($sw.Elapsed.TotalSeconds.ToString("0.00"))s! " -ForegroundColor Green
Write-Host " Artifacts stored in: $Dist" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
