---
title: resolve windows 10 msi error 1335 cab3 corruption
author: SwiftGet
date: 2026-11-01
branch: master
id: l-feb5718e
scope: repo
status: active
src: agent
touches:
  - extension/background.js
  - extension/manifest.firefox.json
  - extension/manifest.json
  - installer/SwiftGet.Package/Package.wxs
  - installer/winget/manifests/s/SwiftGet/SwiftGet/1.0.0/SwiftGet.SwiftGet.installer.yaml
  - scripts/package.ps1
  - src/SwiftGet.App/App.xaml.cs
  - src/SwiftGet.App/Controls/ConnectionTableControl.xaml
  - src/SwiftGet.App/Controls/SegmentedProgressBar.xaml.cs
  - src/SwiftGet.App/ViewModels/DownloadDialogViewModel.cs
---

Windows 10 Windows Installer failed with Error 1335 (cabinet file cab3.cab is corrupt) because WiX default MaximumUncompressedMediaSize of 200MB caused multiple embedded cabinet streams (#cab1.cab, #cab2.cab, #cab3.cab, #cab4.cab) when giant sidecars (222MB ffmpeg and 222MB unused ffprobe) were blindly copied into staging. In Windows 10, multi-cab embedded streams with single files over 200MB trigger 0xca00a009 in dpx.dll during decompression. Fix: package.ps1 selectively stages only essential sidecars (ffmpeg.exe and yt-dlp.exe, completely eliminating ffprobe bloat) and Package.wxs configures MaximumUncompressedMediaSize=1000 to package all 532 payload files into a single unified cab1.cab stream (DiskId 1), shrinking the MSI from 215MB to 138MB and installing cleanly on both Windows 10 and 11.
