---
title: Filename Resolution Priority Lock and Live Connection Telemetry
author: SwiftGet
date: 2026-10-31
branch: master
id: l-bffc7a25
scope: repo
status: active
src: agent
touches:
  - SwiftGet.sln
  - src/SwiftGet.App/Views/DownloadCompleteDialog.xaml.cs
  - src/SwiftGet.App/Views/MainWindow.xaml.cs
  - src/SwiftGet.Engine/Coordinator/DownloadCoordinator.cs
  - src/SwiftGet.Engine/Segmentation/SegmentScheduler.cs
---

Filename resolution invariant: When a user confirms a filename in DownloadFileInfoDialog, it is locked (IsFileNameUserLocked = true) and cannot be overwritten by engine probing or lower-priority URL segment fallbacks. OnFirstChunkRead and completion rename only re-resolve if Content-Disposition provides a real extension and does not strip existing valid extensions. Live connections from IDownloadScheduler.GetConnectionSnapshots are wired to DownloadCoordinator.ConnectionsUpdated and sync to DownloadItemViewModel.Segments for IDM-parity connection grid and segmented progress bar.
