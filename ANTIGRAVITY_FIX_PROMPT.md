# Antigravity Fix Prompt — SwiftGet Download Manager QA Remediation

> **Paste this entire file as the prompt to your Antigravity coding agent.**
> It is self-contained and references the QA artifacts in `qa-output/`.

---

## ROLE & CONTEXT

You are a senior C# / WinUI 3 / security engineer fixing **SwiftGet Download Manager** — a Windows download accelerator (C# .NET 8, WinUI 3, SQLite WAL, Manifest V3 extension, Native Messaging host, MonoTorrent 3.0.2, yt-dlp/ffmpeg sidecars, WiX MSI).

A read-only QA pass has already been completed. All findings are in `qa-output/` next to the project:

- `qa-output/bug-report.md` — executive summary, top 10 risks, findings by component, coverage matrix
- `qa-output/bugs.csv` — **22 bugs** (IDs `C-01`..`C-03`, `H-01`..`H-07`, `M-01`..`D-01`) with Severity, Evidence, File:line, Steps to Reproduce, Expected/Actual, Root Cause, Suggested Fix, Regression Test Idea
- `qa-output/fix-tasks.md` — **10 prioritized fix tasks** mapping bugs → files → acceptance criteria (your primary work list)
- `qa-output/repro/README.md` + `qa-output/repro/C-*.md`, `H-*.md` — exact repro steps & payloads
- `qa-output/testserver/server.mjs` + `README.md` — local switchable HTTP test server (16 behaviors: `normal`, `no-range`, `ignore-range-200`, `wrong-length`, `changing-etag`, `disconnect-mid`, `slow`, `redirect-chain`, `429-burst`, `500-burst`, `chunked-unknown`, `zero-byte`, `large`, etc.)
- `qa-output/new-tests/` — Node probes + live integration suite (`node --test` compatible)
- `qa-output/build-and-test.log` — what QA could/could not run (no .NET SDK on QA VM)

**Project root:** `D:\SwiftGet` (solution `SwiftGet.sln`)
**Source projects:** `src/SwiftGet.Core`, `src/SwiftGet.Engine`, `src/SwiftGet.Media`, `src/SwiftGet.NativeHost`, `src/SwiftGet.App`
**Tests:** `tests/SwiftGet.Engine.Tests`, `SwiftGet.Engine.PropertyTests`, `SwiftGet.FaultInjection.Tests`, `SwiftGet.App.Tests`, `tests/extension.test.mjs`
**Installer:** `installer/SwiftGet.Package/Package.wxs`

---

## MISSION

Fix **all 22 bugs** in `bugs.csv` in priority order as defined in `fix-tasks.md` (Tasks 1–10). Do not skip Critical/High bugs. Low bugs may be fixed opportunistically but do not block the release.

**Top 10 risks to eliminate first:**

1. `C-01` Loopback/NativeHost path injection (`LoopbackServer.cs:314`, `NativeMessageRouter.cs:339`)
2. `C-02` Media sidecar command injection (`YtDlpMediaExtractor.cs:36`, `StreamRecorder.cs:56`, `SidecarManager.cs:104`)
3. `C-03` Torrent `RemoveAsync` arbitrary delete (`MonoTorrentEngine.cs:190,363`)
4. `H-01` Tracker SSRF not enforced (`MonoTorrentEngine.cs:131,195`)
5. `H-02` `BatchPatternParser.CalculateRangeCount` OOM (`BatchPatternParser.cs:126`)
6. `H-03` Piece-count flood OOM (`MonoTorrentEngine.cs:176`)
7. `H-04` Token world-readable + unbounded body (`LoopbackTokenManager.cs:51`, `LoopbackServer.cs:239`)
8. `H-05` Extension intranet fetch oracle (`background.js:285`)
9. `H-06` CORS `Origin: null` (`LoopbackServer.cs:448`)
10. `H-07` Grabber query-strip (`SiteGrabberEngine.cs:216`)

---

## HARD CONSTRAINTS

1. **Never edit `qa-output/`** — it is the QA evidence/harness. Work only in `src/`, `extension/`, `installer/`, `tests/`.
2. **Preserve existing behavior** except where buggy. Do not change public APIs without updating callers.
3. **Security fixes must be deny-by-default** (sanitize → reject → fallback to safe dir, not silent store).
4. **No `ProcessStartInfo.Arguments` string interpolation** after Task 2 — use `ArgumentList` everywhere.
5. **No `PATH` fallback** for sidecars after Task 2 — bundled `sidecars/` only or hash-verified.
6. **Run tests after each Task** — `dotnet test` (or at least the affected project) must stay green. Add regression tests for every C/High bug.
7. **No secrets in logs** — redact tokens/cookies/URLs with credentials (reuse `RedactionEngine`).

---

## EXECUTION PLAN — DO TASKS IN ORDER

Create a todo list for Tasks 1–10 and work sequentially. For each task:

1. Read the task section in `fix-tasks.md` + the corresponding row(s) in `bugs.csv` + the repro in `qa-output/repro/README.md`.
2. Open the exact `File:line` quoted in `bugs.csv` and confirm the bug.
3. Implement the **Suggested fix** (or a strictly equivalent safe fix).
4. Add/extend a regression test as described in `fix-tasks.md` → `Regression test` line.
5. Run `dotnet test` (or `dotnet test tests/<Affected>.Tests`) and `node --test qa-output/new-tests/*.test.mjs` where applicable; ensure green.
6. Mark task done before starting the next.

### Task 1 — Loopback / NativeHost path injection + body caps
**Bugs:** `C-01`, `H-01` (body)
**Files:** `src/SwiftGet.Engine/Server/LoopbackServer.cs:237-328`, `src/SwiftGet.NativeHost/Routing/NativeMessageRouter.cs:210-390`, `src/SwiftGet.Engine/IO/FileNameSanitizer.cs`, `src/SwiftGet.Engine/Security/PathTraversalGuard.cs`
**Fix:** Sanitize `fileName` via `FileNameSanitizer.Sanitize`; resolve `DestinationPath` only via `PathTraversalGuard.EnsureSafePath(category.TargetDirectory, fileName)` — reject absolute attacker-controlled `DestinationPath` (400) or treat as filename-only. Cap `Content-Length` / body at 1–2 MB before `ReadToEndAsync`; cap `FileBytesBase64` decoded size before `Torrent.Load` (413). Validate `QueueId`/`CategoryId`.
**Accept:** Traversal payload `../../pwned.bat` stays under category dir; 30 MB POST → 413 fast.
**Test:** `LoopbackServerSecurityTests.PathTraversal_FileNameRejected`

### Task 2 — Media sidecar command injection + planting
**Bugs:** `C-02`, `H-12`
**Files:** `src/SwiftGet.Media/Sidecars/SidecarManager.cs:93-163,177-207`, `src/SwiftGet.Media/Extractor/YtDlpMediaExtractor.cs:35-37`, `src/SwiftGet.Media/Recorder/StreamRecorder.cs:40-65,109-142`
**Fix:** New `ExecuteSidecarAsync(string, IEnumerable<string>, ...)` using `ProcessStartInfo.ArgumentList`; callers pass token lists (e.g. `["--dump-single-json","--no-warnings","--no-playlist", mediaUrl]`). Validate URLs (`Uri.TryCreate`, reject `"` / null byte). Remove `PATH` search from `FindBinary`; require `VerifyFileSha256` against allowlist before execute. Do not log full argv with secrets.
**Accept:** `"` / `--exec` in URL not parsed as extra args (stub sidecar echoes argv JSON); fake sidecar without valid hash not executed.
**Test:** `SidecarCommandInjectionTests.UrlWithQuotes_DoesNotEscapeArgs` (stub exe)

### Task 3 — Torrent arbitrary delete + path harden
**Bugs:** `C-03`
**Files:** `src/SwiftGet.Engine/Torrent/MonoTorrentEngine.cs:188-215,344-380`, `src/SwiftGet.Engine/Torrent/TorrentPathSanitizer.cs:73-85`
**Fix:** Keep sanitized mapping (do not discard `_ = Sanitize(...)`); on `RemoveAsync` derive delete path via `GetSafeFullPath(saveDir, sanitizedPath)` with try/catch — skip if containment fails. Sanitize `Torrent.Name` for multi-file dir. Fix `GetSafeFullPath` to require trailing separator on `baseFullPath` before prefix check (mirror `PathTraversalGuard`).
**Accept:** Traversal torrent `../../../Temp/sentinel.txt` → sentinel outside `SaveDirectory` survives remove.
**Test:** `TorrentPathTraversalTests.RemoveAsync_DoesNotDeleteOutsideSaveDir`

### Task 4 — Enforce tracker SSRF filter
**Bugs:** `H-01`
**Files:** `src/SwiftGet.Engine/Torrent/MonoTorrentEngine.cs:119-165,167-260`, `src/SwiftGet.Engine/Torrent/TrackerSafetyValidator.cs`
**Fix:** Build filtered announce list; construct filtered `MagnetLink` / filtered `Torrent.AnnounceUrls` before `AddAsync`. If zero safe trackers remain, add via DHT/PEX with warning (do not fail). Respect `AllowLocalTrackers`.
**Accept:** `127.0.0.1/192.168.x/169.254.169.254` trackers absent from `TrackerManager.Tiers` when disallowed.
**Test:** `MonoTorrentEngineTrackerFilterTests.LocalTrackers_FilteredWhenDisallowed`

### Task 5 — Batch OOM + Grabber query-strip
**Bugs:** `H-02`, `H-05`
**Files:** `src/SwiftGet.Engine/Batch/BatchPatternParser.cs:124-197`, `src/SwiftGet.Engine/Grabber/SiteGrabberEngine.cs:216-250`
**Fix (Batch):** `CalculateRangeCount` → arithmetic `(end-start)/abs(step)+1` without `ExpandRange`; guard generation incrementally against `MaxGeneratedUrls`. Fix (Grabber): `NormalizeUrl` → `AbsolutePath + Query` (or `AbsoluteUri` minus `Fragment`) so `?page=` variants are distinct.
**Accept:** `[1-999999]` rejected in <50 ms; paginated crawl visits all pages; `file.zip?token=a` vs `b` both found.
**Test:** `BatchPatternParserTests.LargeRange_RejectedFast`, `SiteGrabberEngineTests.QueryPaginatedPages_AllVisited`

### Task 6 — Token at rest + request limits + CORS
**Bugs:** `H-04`, `H-06`, `H-09`
**Files:** `src/SwiftGet.Engine/Server/LoopbackTokenManager.cs:26-79`, `src/SwiftGet.Engine/Server/LoopbackServer.cs:119-168,237-385,422-465`
**Fix:** Token file ACL = current user only (`FileSecurity` / `File.SetAccessControl`) or DPAPI; `ValidateToken` fixed-time without early length leak (hash/padded compare). `Content-Length` / `MaxBodyLength` gate before `ReadToEndAsync` (dedup Task 1). `IsAllowedOrigin("null")` → `false`; do not send `Access-Control-Allow-Origin:*` for `Origin: null`; only allow `chrome-extension://`, `moz-extension://`, loopback.
**Accept:** Token ACL user-only; wrong-length token not distinguishable; `file://` fetch with `Origin: null` blocked.
**Test:** `LoopbackTokenManagerTests.TokenFile_IsUserOnlyAce`, `LoopbackServerCorsTests.OriginNull_Rejected`

### Task 7 — Updater feed authenticity + downgrade gate
**Bugs:** `H-11`
**Files:** `src/SwiftGet.Engine/Updating/UpdateService.cs:34-172`, `src/SwiftGet.Engine/Updating/UpdateManifest.cs`, `installer/SwiftGet.Package/Package.wxs:8-12`
**Fix:** `UpdateManifest.MinSupportedVersion` — reject `latestVersion <= current` or `latestVersion < minSupported`. Require `https` + signature (RSA/ECDSA over canonical JSON) or at least pinned hash list before `UpdateAvailable`. Fix `LaunchInstaller` to use `ArgumentList`/proper quoting for spaces; `MajorUpgrade` DowngradeErrorMessage for same-version downgrade; keep SHA256 verify before launch.
**Accept:** Older/replayed manifest not offered; tampered MSI not launched; `Program Files` path with spaces launches.
**Test:** `UpdateServiceTests.OldManifest_NotTreatedAsUpdate`

### Task 8 — Sidecar discovery tightening (dedup Task 2 planting part)
**Bugs:** `H-12`
**Files:** `src/SwiftGet.Media/Sidecars/SidecarManager.cs:165-208`
**Fix:** Enforce Task 2's PATH removal + hash allowlist as the dedicated check.

### Task 9 — Queue / scheduler correctness
**Bugs:** `H-13`, `H-15`
**Files:** `src/SwiftGet.Engine/Queues/QueueManager.cs:42-120`, `src/SwiftGet.Engine/Automation/AfterCompletionService.cs`, `src/SwiftGet.Engine/Scheduling/DownloadScheduler.cs`
**Fix:** `_runningQueues` atomic (`TryAdd` + lock); propagate `CancellationToken` to coordinator workers; `StopQueueAsync` cancels CTS + awaits. After-completion `shutdown/hibernate` → confirmation dialog + 30 s countdown, propagate token, allowlist/sanitize `CustomActionCommand`, ignore import-borne commands unless user-confirmed. Scheduler: handle sleep/wake + DST (re-evaluate on resume, use `UtcNow` + TZ conversion).
**Accept:** Concurrent `StartQueueAsync("default")` → one worker; cancel stops downloads; shutdown gated.
**Test:** Concurrent queue start + cancel coverage.

### Task 10 — Engine/threading polish + docs
**Bugs:** `H-14`, `M-01`, `M-02`, `I-01`, `D-01`
**Files:** `src/SwiftGet.Engine/Segmentation/SegmentScheduler.cs`, `src/SwiftGet.Engine/IO/SparseFileWriter.cs`, `src/SwiftGet.Engine/Throttling/TokenBucketLimiter.cs`, `src/SwiftGet.Engine/Coordinator/DownloadCoordinator.cs`, `installer/SwiftGet.Package/Package.wxs`, `docs/*.md`
**Fix:** `Segments` async/lock + clone-before-mutate; `SparseFileWriter` don't `SetLength` on resume truncate; `TokenBucketLimiter` fix `Delay` outside lock bookkeeping + document burst; installer scope fix; materialize `graft/` into `docs/` so links are not machine-local.

---

## HOW TO VERIFY (run after each task, and fully at the end)

```powershell
# .NET suite (must be green)
dotnet test --configuration Release

# Node harness (already green on QA VM; keep green)
node qa-output/testserver/server.mjs 8765 &
node --test qa-output/new-tests/*.test.mjs

# Manual repros (see qa-output/repro/README.md C1..H6)
# - C-01: curl POST with traversal fileName + token → assert under category dir
# - C-02: media URL with quotes/--exec → stub sidecar argv JSON shows no escape
# - C-03: traversal torrent remove → sentinel outside SaveDirectory survives
```

---

## DEFINITION OF DONE

- [ ] Tasks 1–7 fully done with regression tests and green `dotnet test`.
- [ ] Tasks 8–10 done (or explicitly deferred with reason).
- [ ] `bugs.csv` Critical/High rows have a corresponding passing test.
- [ ] No new `TreatWarningsAsErrors` violations; no secrets in logs.
- [ ] `qa-output/` untouched (verify `git status` shows no changes under it).

---

## NOTES FOR THE AGENT

- Quote the exact `File:line` from `bugs.csv` in commit messages (e.g. `fix(C-01): sanitize LoopbackServer:314`).
- If a fix requires a public API change, update all call sites (`LoopbackServer`, `NativeMessageRouter`, `SidecarManager`, `DownloadCoordinator`).
- If you disagree with a finding, do not silently skip it — add a `WONTFIX` note in the PR description citing the `bugs.csv` row and evidence.
- When done, output a table: `ID | Status | Test | Commit`.

