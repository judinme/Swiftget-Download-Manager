# SwiftGet Installer & Browser Integration Infrastructure

## Packaging Formats
- **WiX v4/v5 MSI installer** (`SwiftGet.Package`, outputting `dist/SwiftGet-1.0.0-x64.msi`)
- **Winget manifests** (`installer/winget/manifests/s/SwiftGet/SwiftGet/1.0.0`)
- **Per-machine deployment** (`Scope="perMachine"`) with automatic elevation and clean uninstallation

## Browser Integration Architecture
SwiftGet communicates with browser extensions using the **Native Messaging Protocol** via standard I/O (`SwiftGet.NativeHost.exe`).

### 1. Native Messaging Host Registration
Because the MSI installs per-machine to `C:\Program Files\SwiftGet`, native messaging host manifests are registered under `HKLM` so all interactive users on the system have access:
- **Google Chrome**: `HKLM\Software\Google\Chrome\NativeMessagingHosts\com.swiftget.downloader`
- **Microsoft Edge**: `HKLM\Software\Microsoft\Edge\NativeMessagingHosts\com.swiftget.downloader`
- **Brave Browser**: `HKLM\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.swiftget.downloader`
- **Opera Stable**: `HKLM\Software\Opera Software\Opera Stable\NativeMessagingHosts\com.swiftget.downloader`
- **Opera GX**: `HKLM\Software\Opera Software\Opera GX Stable\NativeMessagingHosts\com.swiftget.downloader`
- **Mozilla Firefox**: `HKLM\Software\Mozilla\NativeMessagingHosts\com.swiftget.downloader`
*(Backward-compatible `com.swiftget.native` aliases and `HKCU` fallback registrations are also preserved).*

### 2. Browser Extension Deployment
Modern browsers block silent installation of unpacked/self-hosted extensions on consumer unmanaged machines. Sideloading store-hosted extensions or enterprise policy force-installs without store publishing triggers "Managed by your organization" banners.
- **Consumer Flow**: The MSI registers native hosts and stages unpacked extension files to `%LOCALAPPDATA%\SwiftGet\extension`.
- **Options -> Browsers**: SwiftGet provides an IDM-style Browsers management tab in Options allowing users to open the extension management page (`chrome://extensions`, `edge://extensions`, `about:debugging#/runtime/this-firefox`) or the official store page with one click.
- **Store Keys**: External extension keys (`HKLM\Software\[Vendor]\Extensions\albogoggaijdbgdldghgfcjakpdbmmfm`) are pre-configured to activate automatically upon Chrome Web Store / Edge Add-ons publication.
