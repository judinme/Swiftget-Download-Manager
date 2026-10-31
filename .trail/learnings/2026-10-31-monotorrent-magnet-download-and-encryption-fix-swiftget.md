# MonoTorrent Magnet Download, Encryption Mode, and Telemetry Crash Fix

## Context
When downloading public torrents/magnets (e.g., 1TamilMV release `xt=urn:btih:732904e88eafabcb2e85d4800d941d97e...`), Free Download Manager (FDM) achieved 2.84 MB/s while SwiftGet showed 0 B/s with 0 seeds and 0 peers, and the bottom drawer froze at 0% / Unknown size.

## Root Causes Identified
1. **Production `torrent_settings.json` Pollution from Unit Tests**:
   `OptionsViewModelTests.BitTorrentOptions_ShouldInitializeFromTorrentEngineAndSave` mutated `vm.TorrentEncryptionModeIndex = 1` (`RequireEncryption`) and called `vm.SaveAllOptionsAsync()`. Because `TorrentSettingsStore.Save(opts)` wrote directly to `%LOCALAPPDATA%\SwiftGet\torrent_settings.json`, production MonoTorrent initialized with `AllowedEncryption = [RC4Header, RC4Full]` (Plaintext strictly disallowed). Modern public swarms connect with plaintext handshakes, causing MonoTorrent to immediately reject 100% of swarm peers and seeds. The test also saved `EnableIpFilter: true` with a dummy file and enabled rate limit schedules.
2. **`ArgumentOutOfRangeException` in `MonoTorrentEngine.GetTrackers`**:
   Before a tracker announce finishes, MonoTorrent defaults `tracker.TimeSinceLastAnnounce` to `TimeSpan.MaxValue`. Computing `DateTime.UtcNow - tracker.TimeSinceLastAnnounce` threw `ArgumentOutOfRangeException: The added or subtracted value results in an un-representable DateTime.`. This exception bubbled up every 500 ms when `DownloadDetailsSnapshotService.CaptureSnapshotAsync` ran, crashing the timer tick and preventing the bottom details drawer from updating.
3. **`TorrentManager.StartAsync()` Omission upon Magnet Metadata Resolution**:
   In `AddMagnetAsync`, once `WaitForMetadataAsync` completed in the background, file priorities were not applied to `manager.Files` and `manager.StartAsync()` was not called if it was in metadata mode.
4. **`KeyNotFoundException` in `StartAsync` / `StopAsync` / `PauseAsync`**:
   `MonoTorrentEngine.GetEntryOrThrow` threw unhandled `KeyNotFoundException` when invoked on a job not yet in `_entries` memory cache, which when fired in `ViewModel.StopHandler` became an unobserved Task exception that crashed the process on finalization.
5. **Legacy `PreferEncryption` Boolean Setter Overwrite**:
   `TorrentEngineOptions.PreferEncryption` setter previously overwrote `EncryptionMode` back to `PreferEncryption` whenever set, colliding with explicit enum selections.

## Changes Made
- Added `TorrentSettingsStore.CustomSettingsPath` so tests specify isolated temporary files without polluting production user configuration.
- Guarded `tracker.TimeSinceLastAnnounce` and `tracker.UpdateInterval` against `TimeSpan.MaxValue` with `< TimeSpan.FromDays(365)` bounds checks in `MonoTorrentEngine.GetTrackers`.
- In `MonoTorrentEngine.AddMagnetAsync`, applied `options.SelectedFileIndices` and `options.FilePriorities` upon metadata arrival and ensured `manager.StartAsync()` is called immediately if `options.StartImmediately` is set.
- Added `manager.PeerConnected` and `manager.PeerDisconnected` handlers in `WireManagerEvents` and connected peers fallback in `GetPeers(jobId)` and `GetJobStats(jobId)`.
- Updated `StartAsync`, `StopAsync`, `PauseAsync`, and `ForceRecheckAsync` in `MonoTorrentEngine` to log warnings instead of throwing `KeyNotFoundException`.
- Updated `MainWindow.xaml.cs` `ResumeHandler` to restore unregistered torrents from database before starting.
- Reset `%LOCALAPPDATA%\SwiftGet\torrent_settings.json` to default configuration (`EncryptionMode = PreferEncryption`, `ListenPort = 52138`, `EnableIpFilter = false`, `EnableRateLimitSchedule = false`).
- Recompiled and re-packaged WiX MSI installer at `dist/SwiftGet-1.0.0-x64.msi`. All 524 tests pass.
