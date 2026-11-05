---
title: fix progress dialog access violation media dialog popup and multi download stalls
author: SwiftGet
date: 2026-11-05
branch: master
id: l-c93f18a2
scope: repo
status: active
src: agent
touches:
  - src/SwiftGet.Core/Models/AppSettings.cs
  - src/SwiftGet.Core/Models/Enums.cs
  - src/SwiftGet.Engine/Turbo/DynamicRangeScheduler.cs
  - src/SwiftGet.Engine/Turbo/TurboDownloadScheduler.cs
  - src/SwiftGet.Engine/Turbo/TurboSocketFactory.cs
  - src/SwiftGet.Engine/Segmentation/SegmentWorker.cs
  - src/SwiftGet.Media/Sidecars/SidecarManager.cs
  - src/SwiftGet.App/Views/DownloadProgressDialog.xaml.cs
  - src/SwiftGet.App/Views/MainWindow.xaml.cs
  - src/SwiftGet.App/ViewModels/DownloadItemViewModel.cs
  - src/SwiftGet.App/ViewModels/MainViewModel.cs
  - src/SwiftGet.App/ViewModels/OptionsViewModel.cs
  - src/SwiftGet.App/Views/OptionsDialog.xaml
---

Closing DownloadProgressDialog threw AccessViolationException (0xc0000005) in coreclr.dll/Microsoft.ui.xaml.dll because compiled {x:Bind} bindings remained subscribed to DownloadItemViewModel.PropertyChanged after window destruction, attempting to set ProgressBar.Value on released COM peers. Fix: DownloadProgressDialog.Closed explicitly calls Bindings.StopTracking(), sets _isClosed flag, and unhooks all ViewModel and Coordinator events. YouTube/media downloads now skip the small progress window by default (ShowMediaProgressDialog toggle in Settings), rendering live progress solely in the list row and details panel. Multi-download stalls were solved by: 1) DynamicRangeScheduler.ReleaseWork returning stalled/faulted ranges to Pending so peers can acquire them; 2) 30s read watchdog on stream.ReadAsync; 3) Concurrency limiter (MaxConcurrentDownloads default 3, max 10) with automatic FIFO queue processing; 4) yt-dlp --no-cache-dir isolation eliminating multi-process SQLite cache lock contention; 5) ProcessTreeTerminator killing child processes cleanly on cancellation.
