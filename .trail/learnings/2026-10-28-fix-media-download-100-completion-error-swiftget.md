---
title: "Fix media download 100% completion Error and browser extension handoff bugs"
author: SwiftGet
date: 2026-10-28
branch: master
id: l-296d850a
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
  - src/SwiftGet.App/App.xaml.cs
---

Media downloads reaching 100% showed Error because yt-dlp without --merge-output-format mp4 and --ffmpeg-location merged DASH video (.mp4) + audio (.webm) into <destinationPath>.mkv instead of <destinationPath>, causing File.Exists(dbItem.DestinationPath) verification to fail. Also fixed UpdateProgress clobbering ProgressPercentage when total is 0, ReloadDownloadsFromDatabaseAsync detaching active downloads and resetting ProgressPercentage to 0, duplicate/orphan DB rows and missing session handoff (cookies/referrer/userAgent) on browser extension interception, broad DEFAULT_BLACKLIST domains (github.com, microsoft.com, google.com, apple.com) blocking downloads, and MonoTorrentEngine throwing on duplicate magnet/torrent registration.
