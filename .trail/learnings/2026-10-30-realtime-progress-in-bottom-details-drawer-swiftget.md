---
title: Realtime Progress in Bottom Details Drawer
author: SwiftGet
date: 2026-10-30
branch: master
id: l-84071090
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

Details drawer progress freezing at 0% was caused by querying SQLite where in-flight byte counts are throttled and not yet flushed, plus CancellationTokenSource churn cancelling snapshot fetches on high-frequency progress ticks. Resolution separates real-time in-memory telemetry (applied immediately via ApplyLiveMetrics on UI thread without disk I/O) from heavy snapshot capture (trackers, connections, piece grid throttled to 2Hz) and overlays live progress onto the DB record.
