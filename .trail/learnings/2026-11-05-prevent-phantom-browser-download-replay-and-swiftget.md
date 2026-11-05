---
title: Prevent phantom browser download replay and cascading dialogs on startup
author: SwiftGet
date: 2026-11-05
branch: master
id: l-aece4d98
scope: repo
status: active
src: agent
touches:
  - extension/background.js
  - extension/content.js
  - extension/manifest.firefox.json
  - extension/manifest.json
  - installer/SwiftGet.Package/Package.wxs
  - installer/assets/app.ico
  - installer/winget/manifests/s/SwiftGet/SwiftGet/1.0.0/SwiftGet.SwiftGet.installer.yaml
  - scripts/package.ps1
  - src/SwiftGet.App/App.xaml.cs
  - src/SwiftGet.App/Assets/app.ico
---

On fresh install, reboot, or app launch, SwiftGet previously popped up multiple unwanted File Info dialogs for historical browser downloads (such as claude.ai/api markdown attachments, gradle.properties, and YouTube mix playlists). Root causes: (1) Chrome extension onCreated/onDeterminingFilename firing during session restore on historical items with past startTimes; (2) recursive re-download fail-safe calling chrome.downloads.download() when the native host timed out; (3) native host inserting unconfirmed placeholders directly into SQLite and spawning SwiftGet.App.exe per event when closed; (4) app restarting unconfirmed 0-byte Queued rows on boot. Fixed by introducing CaptureGatekeeper with a 15s TTL, startup event-time guard, 3s deduplication, scheme/ignore pattern filtering, and X-SwiftGet-Origin loop suppression. In the native host and extension, unconfirmed downloads are no longer saved or re-invoked if SwiftGet is closed. Startup migration safely purges stale unconfirmed placeholders.
