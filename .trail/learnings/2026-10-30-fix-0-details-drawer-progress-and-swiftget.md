---
title: "Fix 0% Details Drawer Progress and Magnet Initial State"
author: SwiftGet
date: 2026-10-30
branch: master
id: l-fb2ce7ed
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

Progress displaying 0% in bottom details drawer for small percentages was caused by MonoTorrent m.Progress reporting 0.0 until full pieces complete SHA-1 verification, plus integer/single-decimal rounding truncation in DownloadDetailsViewModel. Fixed by computing byte-ratio progress Math.Max(m.Progress, downloaded/total) and providing sub-0.1% two-decimal precision. Also fixed UI-thread dispatcher violations during AddTorrentDialog magnet probing and set initial state of unknown-size magnets to FetchingMetadata instead of Downloading.
