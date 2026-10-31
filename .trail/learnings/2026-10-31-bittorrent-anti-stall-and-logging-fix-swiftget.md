# Learning: BitTorrent Mid-Download Stalling and Structured Torrent Logging Fix

**Date**: 2026-10-31  
**Target**: SwiftGet.Engine.Torrent & SwiftGet.App  
**Problem**:
1. Torrents were downloading at high speed, reaching over 1 GB (e.g. 39.5%), and then suddenly dropping to 18 B/s (only TCP keepalives) and stalling indefinitely without resuming or finding new peers.
2. The log file `c:\Users\SPARK\AppData\Local\SwiftGet\logs\swiftget-20261031.log` did not contain any internal BitTorrent logs (DHT discovery, tracker announces, peer connections, disconnect reasons).

**Root Causes**:
1. **Engine and Torrent Settings Bottleneck**:
   - `MaximumConnections` per torrent was hardcoded to 50 in `AddMagnetAsync` and `AddTorrentFileAsync`, with total engine connections set to 150.
   - `MaximumHalfOpenConnections` was left at the MonoTorrent default of 8.
   - `UploadSlots` was left at default 8. Because of BitTorrent's tit-for-tat mechanism, peers choke leechers who don't upload to them.
   - `DiskCacheBytes` was at MonoTorrent's default 5 MB, causing write buffer saturation and TCP socket read throttling at multi-megabyte download speeds.
   - `StaleRequestTimeout` was 40 seconds. When initial peers ran out of pieces or choked SwiftGet, all 50 slots stayed occupied by idle/choking peers sending only 18 B/s keepalives. MonoTorrent's `ConnectionManager.TryConnect` refused to dial new peers (`ConnectedPeers.Count >= 50`).
2. **Tracker Re-Announce Interval**:
   - Trackers enforce 30-minute re-announce intervals (`min interval = 1800s`). When the initial peer pool dried up, MonoTorrent did not actively re-announce to DHT or trackers.
3. **DI Logger Registration**:
   - In `App.xaml.cs`, `new MonoTorrentEngine()` was called directly, instantiating `MonoTorrentEngine` with `NullLogger`. Serilog never received any log messages from `MonoTorrentEngine` or `MonoTorrentLoggerBridge`.

**Solution**:
1. **Engine Settings Tuning**:
   - `MaximumConnections`: Set to 500 globally and 200 per torrent.
   - `MaximumHalfOpenConnections`: Increased to 60 for fast parallel handshakes.
   - `DiskCacheBytes`: Increased to 64 MB (eliminates disk-write backpressure).
   - `UploadSlots`: Increased to 20 for reciprocal tit-for-tat unchoking.
   - `StaleRequestTimeout`: Lowered to 20 seconds.
   - Expanded default public trackers list with high-availability trackers.
2. **Anti-Stall Swarm Watchdog**:
   - Added a 10-second periodic watchdog in `MonoTorrentEngine`.
   - If download speed is under 100 KB/s or open connections < 25, actively announces to DHT and trackers to replenish peers.
   - If a torrent is actively downloading but stalled at < 1 KB/s for 40+ seconds, recycles the socket connection pool via `PauseAsync()` and `StartAsync()` to cleanly drop choked/hung sockets and immediately reconnect to fresh unchoked peers.
3. **Structured Logging Wire-up**:
   - In `App.xaml.cs`, registered `services.AddSingleton<ITorrentEngine, MonoTorrentEngine>();` so Microsoft DI injects `ILogger<MonoTorrentEngine>` wired to Serilog.
   - Verified that `swiftget-YYYYMMDD.log` and `torrent-YYYYMMDD.log` now capture full structured engine telemetry.
