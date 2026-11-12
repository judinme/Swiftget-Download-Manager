---
title: Fix YouTube media download stalls, probe hangs, and intermediate .fXXX stream path corruption
date: 2026-11-12
files:
  - src/SwiftGet.Media/Sidecars/SidecarManager.cs
  - src/SwiftGet.Media/Recorder/StreamRecorder.cs
  - src/SwiftGet.Media/Extractor/YtDlpMediaExtractor.cs
  - src/SwiftGet.App/ViewModels/MediaDownloadViewModel.cs
  - src/SwiftGet.App/Views/MainWindow.xaml.cs
---

1. `SidecarManager.ExecuteSidecarAsync` must await `stdoutEof` and `stderrEof` (`e.Data == null`) with a lock around `StringBuilder` after `Process.WaitForExitAsync(ct)` and enforce an optional `inactivityTimeout` so stalled `yt-dlp` processes are terminated and retried.
2. `StreamRecorder` and `YtDlpMediaExtractor` must pass `--force-ipv4`, `--socket-timeout`, `--retries`, `--fragment-retries`, `--throttled-rate 100K`, `--continue`, and `--extractor-args youtube:player_client=default,-visionos`, and strip intermediate `.f<format_id>` suffixes (e.g. `.f137.mp4`, `.f251.webm`) before firing `DestinationDiscovered`.
3. Register `StreamRecorder` as `Transient` so concurrent media downloads never cross-talk events.
