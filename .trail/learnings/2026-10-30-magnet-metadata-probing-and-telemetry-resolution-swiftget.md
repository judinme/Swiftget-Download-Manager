---
title: Magnet metadata probing and telemetry resolution
author: SwiftGet
date: 2026-10-30
branch: master
id: l-27a857d8
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

MonoTorrentEngine is keyed by jobId (Guid N) while AddTorrentDialog was probing with a tempJobId that got removed after 8s, and MainWindow looked up stats by InfoHash rather than JobId. To support both, FindEntry in MonoTorrentEngine matches both JobId and InfoHash (hex), TrackerSafetyValidator skips blocking synchronous DNS resolves, and AddTorrentViewModel exposes Seeds/Peers telemetry during probing without premature job disposal.
