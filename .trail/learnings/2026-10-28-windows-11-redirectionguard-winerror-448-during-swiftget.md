---
title: Windows 11 RedirectionGuard WinError 448 during yt-dlp PATH probing
author: SwiftGet
date: 2026-10-28
branch: master
id: l-ec8e0c9b
scope: repo
status: active
src: agent
touches:
  - extension/background.js
  - extension/content.js
  - extension/popup.html
  - extension/popup.js
  - installer/README.md
  - installer/SwiftGet.Package/Package.wxs
  - installer/assets/com.swiftget.downloader.chrome.json
  - installer/assets/com.swiftget.native.json
  - installer/winget/manifests/s/SwiftGet/SwiftGet/1.0.0/SwiftGet.SwiftGet.installer.yaml
  - scripts/package.ps1
---

When SwiftGet.App.exe runs elevated (e.g. launched from perMachine MSI ExitDialog CustomAction), Windows 11 Build 26300 RedirectionGuard (Microsoft-Windows-Security-Mitigations/KernelMode Event ID 32) blocks yt-dlp.exe with WinError 448 (ERROR_UNTRUSTED_MOUNT_POINT) if os.environ['PATH'] contains any user-owned junction/symlink directory (such as %LOCALAPPDATA%\Programs\Cua\cua-driver\bin or WinGet\Links) while yt-dlp searches PATH for deno.exe/ffmpeg.exe. Fix: SidecarManager.BuildSafePathEnvironment strips reparse points, WindowsApps, and WinGet\Links from PATH; ExecuteSidecarAsync passes --ignore-config, --no-config-locations, --no-plugin-dirs, --ffmpeg-location, and --js-runtimes node (or --no-js-runtimes); Package.wxs launches SwiftGet.App.exe via explorer.exe so it starts de-elevated.
