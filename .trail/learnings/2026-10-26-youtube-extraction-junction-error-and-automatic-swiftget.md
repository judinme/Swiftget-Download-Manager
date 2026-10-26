---
title: YouTube extraction junction error and automatic retry
author: SwiftGet
date: 2026-10-26
branch: master
id: l-9b40ced4
scope: repo
status: active
src: agent
touches:
  - src/SwiftGet.Media/Sidecars/SidecarManager.cs
  - src/SwiftGet.App/ViewModels/MediaDownloadViewModel.cs
---

yt-dlp searches user profile folders for config unless --no-config-locations is passed, which triggers WinError 448 on junctioned/redirected folders. Additionally, SidecarManager.FormatSidecarError was replacing the original failing path with SaveDirectory on double-formatting during probe, and MediaDownloadViewModel.FixPathAutomatically failed to clear error state or re-probe.
