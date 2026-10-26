# SwiftGet Native Messaging Host Registration Script
param(
    [string]$HostExePath = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($HostExePath)) {
    $HostExePath = Join-Path $repoRoot "src\SwiftGet.NativeHost\bin\Release\net8.0\SwiftGet.NativeHost.exe"
}

if (-not (Test-Path $HostExePath)) {
    Write-Host "Host binary not found at $HostExePath. Building SwiftGet.NativeHost in Release mode..."
    dotnet build (Join-Path $repoRoot "src\SwiftGet.NativeHost\SwiftGet.NativeHost.csproj") -c Release
}

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$targetDir = Join-Path $localAppData "SwiftGet\native-host"
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

$chromeManifestSource = Join-Path $repoRoot "src\SwiftGet.NativeHost\manifests\com.swiftget.downloader.chrome.json"
$firefoxManifestSource = Join-Path $repoRoot "src\SwiftGet.NativeHost\manifests\com.swiftget.downloader.firefox.json"

$chromeManifestTarget = Join-Path $targetDir "com.swiftget.downloader.chrome.json"
$firefoxManifestTarget = Join-Path $targetDir "com.swiftget.downloader.firefox.json"

# Update path to HostExePath inside the target manifest JSON
$chromeJson = Get-Content $chromeManifestSource -Raw | ConvertFrom-Json
$chromeJson.path = $HostExePath
$chromeJson | ConvertTo-Json -Depth 5 | Set-Content $chromeManifestTarget -Encoding utf8

$firefoxJson = Get-Content $firefoxManifestSource -Raw | ConvertFrom-Json
$firefoxJson.path = $HostExePath
$firefoxJson | ConvertTo-Json -Depth 5 | Set-Content $firefoxManifestTarget -Encoding utf8

$regKeys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.swiftget.downloader",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.swiftget.downloader",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.swiftget.downloader"
)

foreach ($key in $regKeys) {
    if (-not (Test-Path $key)) {
        New-Item -Path $key -Force | Out-Null
    }
    Set-ItemProperty -Path $key -Name "(Default)" -Value $chromeManifestTarget
    Write-Host "Registered Chrome/Edge Native Messaging Host: $key -> $chromeManifestTarget"
}

$firefoxKey = "HKCU:\Software\Mozilla\NativeMessagingHosts\com.swiftget.downloader"
if (-not (Test-Path $firefoxKey)) {
    New-Item -Path $firefoxKey -Force | Out-Null
}
Set-ItemProperty -Path $firefoxKey -Name "(Default)" -Value $firefoxManifestTarget
Write-Host "Registered Firefox Native Messaging Host: $firefoxKey -> $firefoxManifestTarget"

Write-Host "SwiftGet Native Messaging Host successfully registered!"
