# Unified File Name Resolution & Extension Recovery (IDM Parity)

## Context
When downloading files with non-standard or obfuscated token URLs (such as `https://s1-filecr.xyz/d08df4022443f114?do=...&token=...`), the download showed the correct name `"Wondershare UniConverter 18.0.1.66 (x64) Multilingual [FileCR].zip"` in the initial `DownloadFileInfoDialog`, but once started, the download list item, General tab inspector, `DownloadCompleteDialog`, and final file on disk reverted to `"d08df4022443f114"` without an extension.

## Root Causes Identified
1. **Premature SQLite Persistence of Browser Captured Slugs**: The browser extension's `chrome.downloads.onCreated` dispatched the preliminary URL path segment (`d08df4022443f114`) to `NativeMessageRouter` before headers were received. `NativeMessageRouter` saved this placeholder directly into SQLite.
2. **Restrictive Update Guard in DownloadCoordinator**: `DownloadCoordinator` previously checked:
   `else if (!isResuming && item.FileName.StartsWith("download_") && item.FileName.EndsWith(".bin"))`
   Because `d08df4022443f114` did not start with `"download_"`, `DownloadCoordinator` refused to adopt the probe's `SuggestedFileName`.
3. **No GET-Response Inspection or Magic Byte Sniffing in Engine**: Active segment workers never captured live GET response headers, and downloaded chunks were never sniffed for magic byte signatures.
4. **Scattered `Path.GetFileName(uri.LocalPath)` Calls**: 20 different places in the UI, grabber, loopback server, and IPC handlers extracted the last URL segment directly, bypassing Content-Disposition and MIME tables.

## Architecture Solution (`IFileNameResolver`)
- **Single Source of Truth**: All URL and header file name resolutions now pass through `IFileNameResolver` (`FileNameResolver.cs`).
- **6-Level Strict Priority Order**:
  1. `UserEdited`: Explicitly edited or confirmed in the dialog (authoritative, user-locked).
  2. `BrowserCaptured`: Filename captured from the browser when valid and not a generic extensionless slug.
  3. `ContentDisposition`: RFC 5987 / RFC 6266 `filename*=UTF-8''...` with fallback to `filename="..."`, handling unquoted, malformed quotes, and Unicode (Tamil, Arabic, etc.).
  4. `FinalUrlPath`: Last path segment of the final redirected URL, percent-decoded.
  5. `QueryString`: Query-string hints (`?filename=`, `?file=`, `?name=`, `response-content-disposition=`).
  6. `Fallback`: Timestamped filename.
- **Extension Recovery**: `MimeTypeMap` maps Content-Type, and `MagicByteSniffer` sniffs the first 512/8192 bytes for signatures (zip, docx, rar, 7z, exe, pdf, iso, mp4, mp3, png, jpg, etc.).
- **Live UI Updates**: `DownloadCoordinator.FileNameUpdated` event notifies `MainWindow.xaml.cs` to update list items, drawer details, and categories in real time.
- **Safety**: Path traversal stripping, Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`), MAX_PATH truncation, Mark-of-the-Web ADS, and dangerous extension warning in `DownloadCompleteDialog`.
- **Architectural Guard**: Unit test `ArchitecturalGuard_NoUnauthorizedUrlPathSegmentNamingOutsideResolver` scans all `src/` C# files to guarantee `Path.GetFileName(uri.AbsolutePath/LocalPath)` is never reintroduced.
