# SwiftGet Native Messaging Host Unregistration Script
$ErrorActionPreference = "SilentlyContinue"

$regKeys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.swiftget.downloader",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.swiftget.downloader",
    "HKCU:\Software\Mozilla\NativeMessagingHosts\com.swiftget.downloader"
)

foreach ($key in $regKeys) {
    if (Test-Path $key) {
        Remove-Item -Path $key -Recurse -Force
        Write-Host "Unregistered Native Messaging Host: $key"
    }
}

Write-Host "SwiftGet Native Messaging Host successfully unregistered."
