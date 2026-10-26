# SwiftGet IDM Parity Checklist

| Feature | Category | Target Milestone | Status | Implementation Reference |
|---|---|---|---|---|
| Dynamic multi-segmentation (up to 32 conns) | Engine | M1 | Completed | `SegmentScheduler.cs`, `DownloadCoordinator.cs` |
| Dynamic midpoint splitting of largest segment | Engine | M1 | Completed | `SegmentScheduler.SplitSegment()` |
| Direct offset sparse writing (no temp-file merge) | Engine | M1 | Completed | `SparseFileWriter.cs` |
| SQLite WAL checkpoint persistence & crash resilience | Engine | M1 | Completed | `SqliteDatabase.cs` (WAL mode, transactions) |
| If-Range cache validation & partial resume | Engine | M1 | Completed | `HttpProbeService.cs`, `DownloadCoordinator.cs` |
| HTTP/1.1 & HTTP/2 with SocketsHttpHandler | Engine | M2 | Completed | `SocketsHttpHandler` with Brotli/Gzip decompression |
| Basic, Digest, NTLM/Negotiate, Cookies, Custom Headers | Engine | M2 | Completed | `DownloadCoordinator.cs`, `SiteCredentialManager.cs` |
| Token-bucket global & per-download speed limiter | Engine | M2 | Completed | `TokenBucketLimiter.cs` |
| Expired-link refresh protocol hook | Engine | M2 | Completed | `DownloadCoordinator.cs` |
| Mirror & fallback URL automatic failover | Engine | M2 | Completed | `DownloadCoordinator.cs` |
| Main window with IDM-parity menus & toolbar | Desktop UI | M3 | Completed | `MainWindow.xaml`, `MainWindow.xaml.cs` |
| Virtualized download list (10,000+ rows @ 60fps) | Desktop UI | M3 | Completed | WinUI 3 `ItemsRepeater` + compiled `x:Bind` |
| Segmented download progress dialog with live details grid | Desktop UI | M3 | Completed | `DownloadProgressDialog.xaml`, `SegmentProgressViewModel.cs` |
| Categories with file extension rules & default folders | Desktop UI | M4 | Completed | `CategoryManager.cs`, `CategoryItemViewModel.cs` |
| Multi-queue management & calendar scheduler | Desktop UI | M4 | Completed | `QueueManager.cs`, `DownloadScheduler.cs`, `SchedulerDialog.xaml` |
| After-completion actions (shutdown, sleep, command) | Desktop UI | M4 | Completed | `AfterCompletionService.cs` |
| Mark-of-the-Web (Zone.Identifier) & Antivirus hook | Desktop UI | M4 | Completed | `MarkOfTheWeb.cs`, `AntivirusScanner.cs` |
| System tray minimization, notifications & audio cues | Desktop UI | M4 | Completed | `SystemTrayManager.cs`, `AudioNotificationService.cs` |
| Command-line interface (`/d`, `/s`, `/p`, `/f`, `/q`, `/h`, `/n`, `/a`) | Desktop UI | M4 | Completed | `CommandLineParser.cs`, `Program.cs` |
| Batch URL generator (`[001-100]`) | Advanced | M5 | Completed | `BatchPatternParser.cs`, `BatchDownloadDialog.xaml` |
| Recursive Site Grabber with depth & file filters | Advanced | M5 | Completed | `SiteGrabberEngine.cs`, `GrabberWizardDialog.xaml` |
| Clipboard monitor & floating Drop Box widget | Advanced | M5 | Completed | `ClipboardMonitor.cs`, `DropBoxWindow.xaml` |
| JSON Import/Export of downloads and settings | Advanced | M5 | Completed | `ExportService.cs`, `ImportService.cs` |
| Native Messaging Host (stdio 32-bit LE framing) | Integration | M6 | Completed | `SwiftGet.NativeHost`, `NativeMessageRouter.cs` |
| Loopback authenticated REST & WebSocket API | Integration | M6 | Completed | `LoopbackServer.cs` (:52384), `LoopbackTokenManager.cs` |
| Browser Extension MV3 (Chrome, Edge, Brave, Opera) | Integration | M6 | Completed | `extension/manifest.json`, `background.js` |
| Browser Extension Firefox build | Integration | M6 | Completed | `extension/manifest.firefox.json`, `build-extension.ps1` |
| Deep session handoff (cookies, referrer, headers) | Integration | M6 | Completed | `background.js` -> `NativeMessageRouter.cs` |
| Alt-key bypass switch for browser download | Integration | M6 | Completed | `content.js` + `background.js` (`altKeyHeldTabs`) |
| Insert-key force switch for download capture | Integration | M6 | Completed | `content.js` + `background.js` (`insertKeyHeldTabs`) |
| Domain exclusion list (`defexclist.txt`) | Integration | M6 | Completed | `defexclist.txt`, `SiteExclusionService.cs` |
| yt-dlp & ffmpeg managed sidecar integration | Media | M7 | Completed | `SidecarManager.cs`, `YtDlpMediaExtractor.cs` |
| Video & audio sniffer with floating overlay panel | Media | M7 | Completed | `content.js`, `sniffer.css` (4-stage IDM flow) |
| Quality/format picker & HLS/DASH media recording | Media | M7 | Completed | `StreamRecorder.cs`, `MediaDownloadDialog.xaml` |
| DRM detection with non-circumvention notice | Media | M7 | Completed | `YtDlpMediaExtractor.cs` (`_has_drm` check) |
| High-DPI, dark/light themes, keyboard accessibility | Accessibility | M8 | Completed | WinUI 3 ThemeResource styles, high-contrast assets |
| WiX v4/v5 MSI installer (per-user & per-machine) | Packaging | M9 | Completed | `installer/SwiftGet.Package/Package.wxs` |
| Auto-updater client & winget manifest | Packaging | M9 | Completed | `UpdateService.cs`, `installer/winget/` |
| BitTorrent & Magnet integration (BEP 9, PeX, DHT) | Torrent | T1-T5 | Completed | `MonoTorrentEngine.cs`, `TorrentDetailsDialog.xaml` |
