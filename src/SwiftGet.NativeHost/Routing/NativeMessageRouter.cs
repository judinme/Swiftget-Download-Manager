using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Core;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Security;
using SwiftGet.Engine.Server;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.NativeHost.Routing;

public sealed class NativeMessageRouter
{
    private static readonly ConcurrentDictionary<string, (string DownloadId, DateTime Timestamp)> _correlationCache = new();
    private readonly ISwiftGetDatabase? _db;
    private readonly ICategoryManager? _categoryManager;
    private readonly LoopbackTokenManager _tokenManager;
    private readonly ISingleInstanceIpc? _ipc;
    private readonly SwiftGet.Core.IO.IFileNameResolver _fileNameResolver;

    public NativeMessageRouter(
        ISwiftGetDatabase? db = null,
        ICategoryManager? categoryManager = null,
        LoopbackTokenManager? tokenManager = null,
        ISingleInstanceIpc? ipc = null,
        SwiftGet.Core.IO.IFileNameResolver? fileNameResolver = null)
    {
        _db = db;
        _categoryManager = categoryManager;
        _tokenManager = tokenManager ?? new LoopbackTokenManager();
        _ipc = ipc;
        _fileNameResolver = fileNameResolver ?? new SwiftGet.Core.IO.FileNameResolver();
    }

    public static bool ValidateOrigin(string? origin, string[]? allowedOrigins = null)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        var clean = origin.Trim();

        if (allowedOrigins != null && allowedOrigins.Length > 0)
        {
            foreach (var allowed in allowedOrigins)
            {
                if (string.Equals(clean, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // Standard WebExtension origins
        return clean.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("extension://", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<object> RouteMessageAsync(string jsonPayload, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(jsonPayload);
        var root = doc.RootElement;

        var action = string.Empty;
        if (root.TryGetProperty("action", out var actionProp))
        {
            action = actionProp.GetString() ?? string.Empty;
        }
        else if (root.TryGetProperty("type", out var typeProp))
        {
            action = typeProp.GetString() ?? string.Empty;
        }

        return action.ToLowerInvariant() switch
        {
            "ping" => new
            {
                action = "ping",
                status = "pong",
                version = "1.1.0",
                torrentSupported = true
            },

            "status" or "checkstatus" => new
            {
                action = "status",
                status = "running",
                version = "1.1.0",
                torrentSupported = true,
                authToken = _tokenManager.GetOrCreateToken()
            },

            "interceptdownload" => await HandleInterceptDownloadAsync(root, ct).ConfigureAwait(false),

            "addmagnet" or "add_magnet" => await HandleAddMagnetAsync(root, ct).ConfigureAwait(false),

            "addtorrentfile" or "add_torrent" => await HandleAddTorrentFileAsync(root, ct).ConfigureAwait(false),

            "shellexecute" or "openfile" => HandleShellExecute(root),

            _ => new
            {
                success = false,
                error = $"Unknown native messaging action: '{action}'"
            }
        };
    }

    private async Task<object> HandleAddMagnetAsync(JsonElement root, CancellationToken ct)
    {
        var uri = string.Empty;
        if (root.TryGetProperty("magnetUri", out var uriProp))
        {
            uri = uriProp.GetString() ?? string.Empty;
        }
        else if (root.TryGetProperty("url", out var urlProp))
        {
            uri = urlProp.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(uri))
        {
            return new
            {
                action = "addMagnet",
                success = false,
                message = "Missing 'magnetUri' or 'url' in request."
            };
        }

        if (!MagnetUriParser.TryParse(uri, out var parsed, out var parseError))
        {
            return new
            {
                action = "addMagnet",
                success = false,
                message = parseError ?? "Invalid magnet URI."
            };
        }

        var saveDir = root.TryGetProperty("saveDirectory", out var sdProp) ? sdProp.GetString() : null;
        var startImmediately = !root.TryGetProperty("startImmediately", out var siProp) || siProp.GetBoolean();
        var silent = root.TryGetProperty("silent", out var silentProp) && silentProp.GetBoolean();

        var jobId = Guid.NewGuid().ToString("N");
        var rawName = parsed!.DisplayName ?? $"Torrent_{parsed.InfoHash[..8]}";
        var name = FileNameSanitizer.Sanitize(rawName, $"Torrent_{parsed.InfoHash[..8]}");

        // 1. Attempt forwarding to running SwiftGet app instance via IPC
        var forwarded = false;
        if (_ipc != null)
        {
            forwarded = await _ipc.TryForwardToRunningInstanceAsync(new SingleInstanceMessage
            {
                Action = "AddTorrent",
                Argument = uri,
                SaveDirectory = saveDir,
                StartImmediately = startImmediately,
                Silent = silent
            }, timeoutMs: 1500, ct).ConfigureAwait(false);
        }
        else if (!IsTestEnvironment())
        {
            var ipc = new SingleInstanceIpc();
            forwarded = await ipc.TryForwardToRunningInstanceAsync(new SingleInstanceMessage
            {
                Action = "AddTorrent",
                Argument = uri,
                SaveDirectory = saveDir,
                StartImmediately = startImmediately,
                Silent = silent
            }, timeoutMs: 1500, ct).ConfigureAwait(false);
        }

        // 2. If app wasn't running, save directly to database
        if (!forwarded && _db != null)
        {
            var defaultSaveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!string.IsNullOrWhiteSpace(saveDir))
            {
                try
                {
                    var full = Path.GetFullPath(saveDir);
                    if (Directory.Exists(full) || Path.IsPathRooted(full))
                    {
                        defaultSaveDir = full;
                    }
                }
                catch { }
            }
            Directory.CreateDirectory(defaultSaveDir);
            var safeDestination = PathTraversalGuard.EnsureSafePath(defaultSaveDir, name, name);

            var download = new DownloadItem
            {
                Id = jobId,
                JobKind = JobKind.Torrent,
                InfoHash = parsed.InfoHash,
                Url = uri,
                FileName = name,
                DestinationPath = safeDestination,
                CategoryId = "Torrents",
                QueueId = "default",
                Status = startImmediately ? DownloadStatus.Downloading : DownloadStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow
            };
            await _db.SaveDownloadAsync(download, ct).ConfigureAwait(false);

            var torrent = new TorrentItem
            {
                Id = jobId,
                InfoHash = parsed.InfoHash,
                Name = name,
                MagnetUri = uri,
                CreatedAtUtc = DateTime.UtcNow
            };
            await _db.SaveTorrentAsync(torrent, ct).ConfigureAwait(false);
            TryLaunchSwiftGetApp(uri);
        }

        return new
        {
            action = "addMagnet",
            success = true,
            jobId,
            infoHash = parsed.InfoHash,
            name,
            forwardedToRunningInstance = forwarded,
            message = "Magnet link successfully enqueued to SwiftGet."
        };
    }

    private async Task<object> HandleAddTorrentFileAsync(JsonElement root, CancellationToken ct)
    {
        var urlOrPath = string.Empty;
        if (root.TryGetProperty("filePath", out var fpProp))
        {
            urlOrPath = fpProp.GetString() ?? string.Empty;
        }
        else if (root.TryGetProperty("url", out var urlProp))
        {
            urlOrPath = urlProp.GetString() ?? string.Empty;
        }

        byte[]? torrentBytes = null;
        if (root.TryGetProperty("fileBytesBase64", out var bytesProp))
        {
            var base64 = bytesProp.GetString();
            if (!string.IsNullOrWhiteSpace(base64))
            {
                if ((long)base64.Length * 3 / 4 > 10 * 1024 * 1024)
                {
                    return new { action = "addTorrentFile", success = false, message = "Payload exceeds maximum 10 MB limit." };
                }

                try
                {
                    var decoded = Convert.FromBase64String(base64);
                    if (decoded.Length > 10 * 1024 * 1024)
                    {
                        return new { action = "addTorrentFile", success = false, message = "Decoded torrent exceeds 10 MB limit." };
                    }
                    torrentBytes = decoded;
                }
                catch (Exception ex)
                {
                    return new { action = "addTorrentFile", success = false, message = $"Failed to parse base64: {ex.Message}" };
                }
            }
        }

        string infoHash = string.Empty;
        string name = "Torrent";

        if (torrentBytes != null && torrentBytes.Length > 0)
        {
            try
            {
                var torrent = MonoTorrent.Torrent.Load(torrentBytes);
                infoHash = torrent.InfoHashes.V1OrV2.ToHex();
                name = FileNameSanitizer.Sanitize(torrent.Name, "Torrent");
            }
            catch (Exception ex)
            {
                return new { action = "addTorrentFile", success = false, message = $"Failed to parse .torrent bytes: {ex.Message}" };
            }
        }
        else if (File.Exists(urlOrPath))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(urlOrPath, ct).ConfigureAwait(false);
                if (bytes.Length > 10 * 1024 * 1024)
                {
                    return new { action = "addTorrentFile", success = false, message = "Torrent file exceeds 10 MB limit." };
                }
                var torrent = MonoTorrent.Torrent.Load(bytes);
                infoHash = torrent.InfoHashes.V1OrV2.ToHex();
                name = FileNameSanitizer.Sanitize(torrent.Name, "Torrent");
                torrentBytes = bytes;
            }
            catch (Exception ex)
            {
                return new { action = "addTorrentFile", success = false, message = $"Failed to read .torrent file: {ex.Message}" };
            }
        }
        else if (!string.IsNullOrWhiteSpace(urlOrPath) && (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            // Fall back to standard download intercept so SwiftGet downloads the .torrent file
            return await HandleInterceptDownloadAsync(root, ct).ConfigureAwait(false);
        }
        else
        {
            return new { action = "addTorrentFile", success = false, message = "Missing valid .torrent fileBytesBase64, filePath, or url." };
        }

        var saveDir = root.TryGetProperty("saveDirectory", out var sdProp) ? sdProp.GetString() : null;
        var startImmediately = !root.TryGetProperty("startImmediately", out var siProp) || siProp.GetBoolean();
        var silent = root.TryGetProperty("silent", out var silentProp) && silentProp.GetBoolean();

        // 1. Prepare argument for IPC forwarder (write bytes to temp file if not already a local path)
        var forwardArgument = urlOrPath;
        if ((string.IsNullOrWhiteSpace(forwardArgument) || !File.Exists(forwardArgument)) && torrentBytes != null && torrentBytes.Length > 0)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"swiftget_{infoHash}.torrent");
                await File.WriteAllBytesAsync(tempPath, torrentBytes, ct).ConfigureAwait(false);
                forwardArgument = tempPath;
            }
            catch { }
        }

        // 2. Attempt forwarding to running SwiftGet app instance via IPC
        var forwarded = false;
        if (_ipc != null)
        {
            forwarded = await _ipc.TryForwardToRunningInstanceAsync(new SingleInstanceMessage
            {
                Action = "AddTorrent",
                Argument = forwardArgument,
                SaveDirectory = saveDir,
                StartImmediately = startImmediately,
                Silent = silent
            }, timeoutMs: 1500, ct).ConfigureAwait(false);
        }
        else if (!IsTestEnvironment())
        {
            var ipc = new SingleInstanceIpc();
            forwarded = await ipc.TryForwardToRunningInstanceAsync(new SingleInstanceMessage
            {
                Action = "AddTorrent",
                Argument = forwardArgument,
                SaveDirectory = saveDir,
                StartImmediately = startImmediately,
                Silent = silent
            }, timeoutMs: 1500, ct).ConfigureAwait(false);
        }

        var jobId = Guid.NewGuid().ToString("N");

        // 3. If app wasn't running, save directly to database
        if (!forwarded && _db != null)
        {
            var defaultSaveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!string.IsNullOrWhiteSpace(saveDir))
            {
                try
                {
                    var full = Path.GetFullPath(saveDir);
                    if (Directory.Exists(full) || Path.IsPathRooted(full))
                    {
                        defaultSaveDir = full;
                    }
                }
                catch { }
            }
            Directory.CreateDirectory(defaultSaveDir);
            var safeDestination = PathTraversalGuard.EnsureSafePath(defaultSaveDir, name, name);

            var download = new DownloadItem
            {
                Id = jobId,
                JobKind = JobKind.Torrent,
                InfoHash = infoHash,
                Url = urlOrPath,
                FileName = name,
                DestinationPath = safeDestination,
                CategoryId = "Torrents",
                QueueId = "default",
                Status = startImmediately ? DownloadStatus.Downloading : DownloadStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow
            };
            await _db.SaveDownloadAsync(download, ct).ConfigureAwait(false);

            var torrent = new TorrentItem
            {
                Id = jobId,
                InfoHash = infoHash,
                Name = name,
                TotalBytes = torrentBytes != null ? torrentBytes.Length : 0,
                CreatedAtUtc = DateTime.UtcNow
            };
            await _db.SaveTorrentAsync(torrent, ct).ConfigureAwait(false);
            TryLaunchSwiftGetApp(forwardArgument);
        }

        return new
        {
            action = "addTorrentFile",
            success = true,
            jobId,
            infoHash,
            name,
            forwardedToRunningInstance = forwarded,
            message = "Torrent file successfully enqueued to SwiftGet."
        };
    }

    private async Task<object> HandleInterceptDownloadAsync(JsonElement root, CancellationToken ct)
    {
        var url = string.Empty;
        if (root.TryGetProperty("url", out var urlProp))
        {
            url = urlProp.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new { success = false, message = "Missing 'url' property in download request." };
        }

        string? correlationId = null;
        if (root.TryGetProperty("correlationId", out var corrProp) && !string.IsNullOrWhiteSpace(corrProp.GetString()))
        {
            correlationId = corrProp.GetString();
            var now = DateTime.UtcNow;
            foreach (var kvp in _correlationCache)
            {
                if ((now - kvp.Value.Timestamp).TotalSeconds > 30)
                {
                    _correlationCache.TryRemove(kvp.Key, out _);
                }
            }

            if (_correlationCache.TryGetValue(correlationId!, out var existing))
            {
                return new
                {
                    action = "interceptDownload",
                    success = true,
                    downloadId = existing.DownloadId,
                    deduplicated = true,
                    message = "Duplicate request suppressed by correlationId."
                };
            }
        }

        var rawFileName = root.TryGetProperty("fileName", out var fnProp) ? fnProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            rawFileName = _fileNameResolver.ResolveInitialCandidate(url);
        }

        var downloadId = Guid.NewGuid().ToString("N");
        if (correlationId != null)
        {
            _correlationCache[correlationId] = (downloadId, DateTime.UtcNow);
        }

        var fallbackFileName = "download_" + downloadId[..8] + ".bin";
        var fileName = _fileNameResolver.SanitizeFileName(rawFileName ?? string.Empty, fallbackFileName);

        bool isTorrent = url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
                         url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ||
                         fileName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        bool isMedia = UrlMediaDetector.IsStreamingMediaPlatformUrl(url);
        JobKind jobKind = isTorrent ? JobKind.Torrent : (isMedia ? JobKind.Media : JobKind.Http);

        var cookies = root.TryGetProperty("cookies", out var ckProp) ? ckProp.GetString() : null;
        var referrer = root.TryGetProperty("referrer", out var refProp) ? refProp.GetString() : null;
        var userAgent = root.TryGetProperty("userAgent", out var uaProp) ? uaProp.GetString() : null;

        string? destPath = null;
        if (_db != null && _categoryManager != null)
        {
            var category = _categoryManager.ResolveCategory(fileName);
            var categoryId = isTorrent ? "Torrents" : (isMedia ? "video" : category.Id);
            if (root.TryGetProperty("categoryId", out var catProp) && !string.IsNullOrWhiteSpace(catProp.GetString()))
            {
                var reqCatId = catProp.GetString()!;
                var categories = await _categoryManager.GetCategoriesAsync(ct).ConfigureAwait(false);
                var existingCat = categories.FirstOrDefault(c => string.Equals(c.Id, reqCatId, StringComparison.OrdinalIgnoreCase));
                if (existingCat != null)
                {
                    category = existingCat;
                    categoryId = existingCat.Id;
                }
            }

            var queueId = "default";
            if (root.TryGetProperty("queueId", out var qProp) && !string.IsNullOrWhiteSpace(qProp.GetString()))
            {
                var reqQueueId = qProp.GetString()!;
                var queues = await _db.GetAllQueuesAsync(ct).ConfigureAwait(false);
                if (queues.Any(q => string.Equals(q.Id, reqQueueId, StringComparison.OrdinalIgnoreCase)))
                {
                    queueId = reqQueueId;
                }
            }

            string candidateName = fileName;
            if (root.TryGetProperty("destinationPath", out var dpProp) && !string.IsNullOrWhiteSpace(dpProp.GetString()))
            {
                var rawDestPath = dpProp.GetString()!;
                var destFileName = _fileNameResolver.SanitizeFileName(rawDestPath);
                if (!string.IsNullOrWhiteSpace(destFileName))
                {
                    candidateName = _fileNameResolver.SanitizeFileName(destFileName, fileName);
                }
            }

            var targetDir = Environment.ExpandEnvironmentVariables(category.TargetDirectory);
            destPath = PathTraversalGuard.EnsureSafePath(targetDir, candidateName, fileName);

            var item = new DownloadItem
            {
                Id = downloadId,
                JobKind = jobKind,
                Url = url,
                FileName = fileName,
                DestinationPath = destPath,
                CategoryId = categoryId,
                QueueId = queueId,
                CookiesJson = cookies,
                Referrer = referrer,
                UserAgent = userAgent,
                Status = DownloadStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow
            };

            await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
        }

        // 1. Attempt forwarding to running SwiftGet app instance via IPC
        var forwarded = false;
        if (_ipc != null)
        {
            forwarded = await _ipc.TryForwardToRunningInstanceAsync(new SingleInstanceMessage
            {
                DownloadId = downloadId,
                Action = "AddDownload",
                Argument = url,
                FileName = fileName,
                SaveDirectory = destPath,
                StartImmediately = true,
                CorrelationId = correlationId,
                JobKind = jobKind.ToString(),
                Cookies = cookies,
                Referrer = referrer,
                UserAgent = userAgent
            }, timeoutMs: 1500, ct).ConfigureAwait(false);
        }
        else if (!IsTestEnvironment())
        {
            var ipc = new SingleInstanceIpc();
            forwarded = await ipc.TryForwardToRunningInstanceAsync(new SingleInstanceMessage
            {
                DownloadId = downloadId,
                Action = "AddDownload",
                Argument = url,
                FileName = fileName,
                SaveDirectory = destPath,
                StartImmediately = true,
                CorrelationId = correlationId,
                JobKind = jobKind.ToString(),
                Cookies = cookies,
                Referrer = referrer,
                UserAgent = userAgent
            }, timeoutMs: 1500, ct).ConfigureAwait(false);
        }

        // 2. If app wasn't running, launch SwiftGet.App.exe
        if (!forwarded)
        {
            TryLaunchSwiftGetApp(url);
        }

        return new
        {
            action = "interceptDownload",
            success = true,
            downloadId,
            fileName,
            forwardedToRunningInstance = forwarded,
            message = "Download successfully enqueued to SwiftGet."
        };
    }

    private object HandleShellExecute(JsonElement root)
    {
        var filePath = root.TryGetProperty("filePath", out var fpProp) ? fpProp.GetString() : string.Empty;
        var subAction = root.TryGetProperty("subAction", out var saProp) ? saProp.GetString() : "open";

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new { success = false, message = "Missing filePath property." };
        }

        try
        {
            var fullPath = filePath;
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Path.IsPathRooted(fullPath))
            {
                var cleanRelative = filePath.Replace("Downloads\\", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("Downloads/", string.Empty, StringComparison.OrdinalIgnoreCase);
                fullPath = Path.Combine(downloads, cleanRelative);
            }

            // Security Validation (SEC-01): ensure target resides within safe downloads or was downloaded by SwiftGet
            bool isAllowed = PathTraversalGuard.IsSafePath(downloads, fullPath);
            if (!isAllowed && _db != null)
            {
                var all = _db.GetAllDownloadsAsync().GetAwaiter().GetResult();
                isAllowed = all.Any(d => string.Equals(d.DestinationPath, fullPath, StringComparison.OrdinalIgnoreCase));
            }

            if (!isAllowed)
            {
                return new { success = false, message = "Access denied: Target file is outside allowed download directories." };
            }

            if (string.Equals(subAction, "openFolder", StringComparison.OrdinalIgnoreCase))
            {
                var dir = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && !IsTestEnvironment())
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
                return new { success = true, action = "openFolder", target = dir };
            }
            else
            {
                if (File.Exists(fullPath) && !IsTestEnvironment())
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = fullPath,
                        UseShellExecute = true
                    });
                }
                return new { success = true, action = "open", target = fullPath };
            }
        }
        catch (Exception ex)
        {
            return new { success = false, message = ex.Message };
        }
    }

    private static bool IsTestEnvironment()
    {
        return AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
               Environment.GetEnvironmentVariable("SWIFTGET_TEST_ENV") != null;
    }

    private static void TryLaunchSwiftGetApp(string? argument = null)
    {
        if (IsTestEnvironment())
        {
            return;
        }

        try
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "SwiftGet.App.exe"),
                Path.Combine(progFiles, "SwiftGet", "SwiftGet.App.exe"),
                Path.Combine(progFilesX86, "SwiftGet", "SwiftGet.App.exe"),
                Path.Combine(localApp, "Programs", "SwiftGet", "SwiftGet.App.exe"),
                Path.Combine(localApp, "SwiftGet", "SwiftGet.App.exe")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = candidate,
                        UseShellExecute = true
                    };
                    if (!string.IsNullOrWhiteSpace(argument))
                    {
                        psi.Arguments = $"\"{argument}\"";
                    }
                    System.Diagnostics.Process.Start(psi);
                    break;
                }
            }
        }
        catch
        {
            // Best-effort process launch
        }
    }
}
