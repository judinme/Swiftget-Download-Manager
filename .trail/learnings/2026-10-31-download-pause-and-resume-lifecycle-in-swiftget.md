---
title: Download Pause and Resume Lifecycle in Turbo Engine and Dialog
author: SwiftGet
date: 2026-10-31
branch: master
id: l-a9b57ce1
scope: repo
status: active
src: agent
touches:
  - installer/winget/manifests/s/SwiftGet/SwiftGet/1.0.0/SwiftGet.SwiftGet.installer.yaml
  - src/SwiftGet.App/App.xaml.cs
  - src/SwiftGet.App/Controls/SegmentedProgressBar.xaml.cs
  - src/SwiftGet.App/ViewModels/DownloadDialogViewModel.cs
  - src/SwiftGet.App/Views/DownloadCompleteDialog.xaml
  - src/SwiftGet.App/Views/DownloadProgressDialog.xaml
  - src/SwiftGet.App/Views/DownloadProgressDialog.xaml.cs
  - src/SwiftGet.App/Views/MainWindow.xaml.cs
  - src/SwiftGet.Engine/Turbo/DynamicRangeScheduler.cs
  - src/SwiftGet.Engine/Turbo/TurboDownloadScheduler.cs
---

Fixed download pause and cancel handling across SwiftGet. App: DownloadDialogViewModel.TogglePause previously only modified in-memory item status without signaling the engine or CancellationTokenSource, leaving active downloads running. Connected PauseHandler, ResumeHandler, and CancelHandler to MainWindow StopHandler/ResumeHandler, and updated button text to dynamically reflect Pause vs Resume. TurboDownloadScheduler: ExecuteAsync loop previously did not rethrow OperationCanceledException if cancelled during loop condition checks, causing cancellations to return as successful completion and trigger completion dialogs. Enforced cancellationToken.ThrowIfCancellationRequested() and restored segment checkpoints from database on resume.
