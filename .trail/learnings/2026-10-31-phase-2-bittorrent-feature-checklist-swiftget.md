---
title: Phase 2 BitTorrent Feature Checklist Implementation
author: SwiftGet
date: 2026-10-31
branch: master
id: l-37f098ab
scope: repo
status: active
src: agent
touches:
  - src/SwiftGet.Engine/Clipboard/ClipboardMonitor.cs
  - src/SwiftGet.Engine/Torrent/ITorrentEngine.cs
  - src/SwiftGet.Engine/Torrent/MonoTorrentEngine.cs
  - src/SwiftGet.Engine/Torrent/TorrentModels.cs
  - src/SwiftGet.App/ViewModels/AddTorrentViewModel.cs
  - src/SwiftGet.App/ViewModels/CreateTorrentViewModel.cs
  - src/SwiftGet.App/ViewModels/DownloadDetailsViewModel.cs
  - src/SwiftGet.App/ViewModels/TorrentDetailsViewModel.cs
  - src/SwiftGet.App/Views/AddTorrentDialog.xaml
  - src/SwiftGet.App/Views/CreateTorrentDialog.xaml
  - src/SwiftGet.App/Views/CreateTorrentDialog.xaml.cs
  - src/SwiftGet.App/Views/DownloadDetailsPanel.xaml
  - src/SwiftGet.App/Views/MainWindow.xaml
  - src/SwiftGet.App/Views/MainWindow.xaml.cs
  - src/SwiftGet.App/Views/TorrentDetailsDialog.xaml
---

Implemented the complete Phase 2 13-point BitTorrent Feature Checklist against MonoTorrent 3.0.2:
1. Magnet clipboard monitoring: Integrated Regex matching for magnet: and .torrent URIs with Windows clipboard events.
2. Pre-download file tree: Wired dynamic calculation of selected bytes and total size, updating disk space warnings live as items are checked.
3. Torrent controls: Added Force Announce, Move Storage Folder, and Remove with downloaded files to context menu and details drawer.
4. Live File priorities: Wired live priority changes (Skip/Low/Normal/High) to MonoTorrent manager.SetFilePriorityAsync.
5. Sequential downloading: Connected UI toggles directly to manager.SetSequentialDownloadAsync.
6. Rate limiting & Snail mode: Mapped status bar turtle mode button directly to SetGlobalRateLimitsAsync.
7. Creation: Built CreateTorrentDialog and CreateTorrentViewModel using TorrentCreator with piece size selection, tracker tier configuration, and private torrent flag.
8. MonoTorrent 3.0.2 does not support native blocklist/IP filtering without custom socket wrappers, which is documented and reported.
