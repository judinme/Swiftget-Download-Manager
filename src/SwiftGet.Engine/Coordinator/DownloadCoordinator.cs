using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.IO;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Network;
using SwiftGet.Engine.Probing;
using SwiftGet.Engine.Segmentation;
using SwiftGet.Engine.Throttling;

namespace SwiftGet.Engine.Coordinator;

public class DownloadCoordinator
{
    private readonly HttpClient _httpClient;
    private readonly ISwiftGetDatabase _db;
    private readonly HttpProbeService _probeService;
    private readonly IFileNameResolver _fileNameResolver;
    private readonly ILogger<DownloadCoordinator> _logger;

    public bool ApplyMarkOfTheWeb { get; set; } = true;
    public bool UseTurboEngine { get; set; } = true;
    public TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public SwiftGet.Engine.Security.IAntivirusScanner? AntivirusScanner { get; set; }

    public event EventHandler<(string DownloadId, string NewFileName, string NewDestinationPath)>? FileNameUpdated;
    public event EventHandler<(string DownloadId, IReadOnlyList<SwiftGet.Engine.Turbo.ConnectionSnapshot> Connections)>? ConnectionsUpdated;

    public DownloadCoordinator(
        HttpClient httpClient,
        ISwiftGetDatabase db,
        HttpProbeService? probeService = null,
        IFileNameResolver? fileNameResolver = null,
        ILogger<DownloadCoordinator>? logger = null)
    {
        _httpClient = httpClient;
        _db = db;
        _fileNameResolver = fileNameResolver ?? new FileNameResolver();
        _probeService = probeService ?? new HttpProbeService(httpClient, _fileNameResolver);
        _logger = logger ?? NullLogger<DownloadCoordinator>.Instance;
    }

    public async Task StartAsync(
        DownloadItem item,
        int maxConnections = 8,
        long minSplitSize = DynamicSplitter.DefaultMinSplitSize,
        ITokenBucketLimiter? limiter = null,
        ILinkRefreshHook? linkRefreshHook = null,
        MirrorFailoverHandler? mirrorHandler = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            // 1. Initial State Persistence & Probing
            item.Status = DownloadStatus.Probing;
            await _db.SaveDownloadAsync(item, CancellationToken.None).ConfigureAwait(false);

            var isResuming = item.DownloadedBytes > 0 && item.Segments.Count > 0;
            var probeResult = await _probeService.ProbeAsync(
                item.FinalUrl.Length > 0 ? item.FinalUrl : item.Url,
                cookies: item.CookiesJson,
                userAgent: item.UserAgent,
                referrer: item.Referrer,
                ct: ct).ConfigureAwait(false);

            if (!probeResult.IsSuccess)
            {
                // Check if probe failed due to expired link (403/410)
                if (linkRefreshHook != null && (probeResult.StatusCode == 403 || probeResult.StatusCode == 410))
                {
                    _logger.LogInformation("Probe returned HTTP {Status}. Attempting expired link refresh for download {Id}.", probeResult.StatusCode, item.Id);
                    await _db.UpdateDownloadStatusAsync(item.Id, DownloadStatus.RefreshingLink, "Refreshing expired download link...", ct).ConfigureAwait(false);
                    var refreshedUrl = await linkRefreshHook.RefreshUrlAsync(item.Id, item.Url, ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(refreshedUrl))
                    {
                        item.FinalUrl = refreshedUrl;
                        await StartAsync(item, maxConnections, minSplitSize, limiter, null, mirrorHandler, progress, ct).ConfigureAwait(false);
                        return;
                    }
                }

                // Check mirror failover
                if (mirrorHandler != null)
                {
                    var nextMirror = await mirrorHandler.TryFailoverAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(nextMirror))
                    {
                        _logger.LogInformation("Failing over download {Id} to mirror {Mirror}", item.Id, nextMirror);
                        item.FinalUrl = nextMirror;
                        await StartAsync(item, maxConnections, minSplitSize, limiter, linkRefreshHook, mirrorHandler, progress, ct).ConfigureAwait(false);
                        return;
                    }
                }

                throw new InvalidOperationException($"Probe failed: {probeResult.ErrorMessage}");
            }

            // Verify file consistency on resume
            if (isResuming)
            {
                var etagMismatch = !string.IsNullOrWhiteSpace(item.ETag) && !string.IsNullOrWhiteSpace(probeResult.ETag) && !IsETagMatch(item.ETag, probeResult.ETag);
                var sizeMismatch = item.TotalBytes > 0 && probeResult.TotalBytes > 0 && item.TotalBytes != probeResult.TotalBytes;

                if (etagMismatch || sizeMismatch)
                {
                    _logger.LogWarning("Remote file has changed since initial download for {Url} (ETag mismatch: {EtagMismatch}, Size mismatch: {SizeMismatch}). Invalidate previous progress and restart from scratch.",
                        item.Url, etagMismatch, sizeMismatch);
                    item.DownloadedBytes = 0;
                    item.Segments.Clear();
                    await _db.SaveSegmentsAsync(item.Id, [], ct).ConfigureAwait(false);
                }
            }

            item.FinalUrl = probeResult.FinalUrl;
            item.TotalBytes = probeResult.TotalBytes;
            item.SupportsRanges = probeResult.SupportsRanges;
            item.ETag = probeResult.ETag;
            item.LastModified = probeResult.LastModified;
            item.ContentType = probeResult.ContentType;

            if (string.IsNullOrWhiteSpace(item.FileName))
            {
                item.FileName = probeResult.SuggestedFileName ?? "download.bin";
            }
            else if (!isResuming && !string.IsNullOrWhiteSpace(probeResult.SuggestedFileName))
            {
                var currentExt = _fileNameResolver.GetCompoundExtension(item.FileName);
                var isPlaceholder = item.FileName.StartsWith("download_", StringComparison.OrdinalIgnoreCase) && item.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                var isExtensionlessOrGeneric = string.IsNullOrEmpty(currentExt) || _fileNameResolver.IsGenericOrScriptExtension(currentExt);

                var suggestedExt = _fileNameResolver.GetCompoundExtension(probeResult.SuggestedFileName);
                var suggestedHasRealExt = !string.IsNullOrEmpty(suggestedExt) && !_fileNameResolver.IsGenericOrScriptExtension(suggestedExt);

                // Priority invariant: Only update filename from probe if the current name is a placeholder/generic AND the probe returned a real extension,
                // OR if a genuine Content-Disposition header with a real extension was returned. Never replace a rich name with an extensionless path-slug!
                if ((!item.IsFileNameUserLocked || isExtensionlessOrGeneric) &&
                    ((isPlaceholder && (suggestedHasRealExt || !string.IsNullOrWhiteSpace(probeResult.ContentDisposition))) ||
                     (isExtensionlessOrGeneric && suggestedHasRealExt) ||
                     (!string.IsNullOrWhiteSpace(probeResult.ContentDisposition) && suggestedHasRealExt && isExtensionlessOrGeneric)))
                {
                    item.FileName = probeResult.SuggestedFileName;
                    if (!string.IsNullOrWhiteSpace(item.DestinationPath))
                    {
                        var dir = Path.GetDirectoryName(item.DestinationPath) ?? Environment.CurrentDirectory;
                        item.DestinationPath = Path.Combine(dir, item.FileName);
                    }
                    FileNameUpdated?.Invoke(this, (item.Id, item.FileName, item.DestinationPath));
                }
            }

            if (string.IsNullOrWhiteSpace(item.DestinationPath))
            {
                item.DestinationPath = Path.Combine(Environment.CurrentDirectory, item.FileName);
            }

            await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);

            // 2. Allocation
            await _db.UpdateDownloadStatusAsync(item.Id, DownloadStatus.Allocating, null, ct).ConfigureAwait(false);
            item.Status = DownloadStatus.Allocating;

            await using var writer = new SparseFileWriter(item.DestinationPath);
            await writer.PreallocateAsync(item.TotalBytes, ct).ConfigureAwait(false);

            // 3. Bandwidth limiter resolution
            using var localLimiter = item.SpeedLimitBytesPerSec > 0 && limiter == null
                ? new TokenBucketLimiter(item.SpeedLimitBytesPerSec)
                : null;
            var effectiveLimiter = limiter ?? localLimiter;

            // 4. Downloading via SegmentScheduler
            await _db.UpdateDownloadStatusAsync(item.Id, DownloadStatus.Downloading, null, ct).ConfigureAwait(false);
            item.Status = DownloadStatus.Downloading;

            string? liveGetResolvedName = null;

            using SwiftGet.Engine.Turbo.IDownloadScheduler scheduler = UseTurboEngine
                ? new SwiftGet.Engine.Turbo.TurboDownloadScheduler(
                    _httpClient,
                    item,
                    writer,
                    _db,
                    concurrencyController: new SwiftGet.Engine.Turbo.AdaptiveConcurrencyController(
                        initialConnections: Math.Min(6, item.SupportsRanges ? maxConnections : 1),
                        maxConnections: item.SupportsRanges ? maxConnections : 1),
                    limiter: effectiveLimiter,
                    logger: _logger)
                : new SegmentScheduler(
                    _httpClient,
                    item,
                    writer,
                    _db,
                    maxConnections: item.SupportsRanges ? maxConnections : 1,
                    minSplitSize: minSplitSize,
                    limiter: effectiveLimiter,
                    logger: _logger);

            scheduler.OnFirstChunkRead = (resp, sample) =>
            {
                try
                {
                        string? rawCd = null;
                        if (resp.Content.Headers.TryGetValues("Content-Disposition", out var cdVals))
                        {
                            rawCd = string.Join("; ", cdVals);
                        }
                        else if (resp.Headers.TryGetValues("Content-Disposition", out var hcdVals))
                        {
                            rawCd = string.Join("; ", hcdVals);
                        }

                        var contentType = resp.Content.Headers.ContentType?.MediaType;
                        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? item.FinalUrl;

                        var resolved = _fileNameResolver.Resolve(new FileNameResolutionContext
                        {
                            OriginalUrl = item.Url,
                            FinalUrl = finalUrl,
                            ContentDisposition = rawCd,
                            ContentType = contentType,
                            InitialBytes = sample.Length > 0 ? new ReadOnlyMemory<byte>(sample) : null
                        });

                        var currentExt = _fileNameResolver.GetCompoundExtension(item.FileName);
                        var isCurrentGeneric = string.IsNullOrEmpty(currentExt) || _fileNameResolver.IsGenericOrScriptExtension(currentExt);
                        var isNewReal = !string.IsNullOrEmpty(resolved.Extension) && !_fileNameResolver.IsGenericOrScriptExtension(resolved.Extension);

                        // Only accept live GET re-resolution if:
                        // 1. Name is NOT locked by the user.
                        // 2. The resolution came from a genuine Content-Disposition header with a valid filename, OR fixed a generic/extensionless filename with a real extension.
                        // 3. Crucially: NEVER replace a name that has a valid extension with an extensionless or generic name!
                        if ((!item.IsFileNameUserLocked || isCurrentGeneric) &&
                            resolved.Source == FileNameSource.ContentDisposition &&
                            !string.IsNullOrWhiteSpace(resolved.FileName) &&
                            (isCurrentGeneric || isNewReal) &&
                            !(string.IsNullOrEmpty(resolved.Extension) && !string.IsNullOrEmpty(currentExt)))
                        {
                            liveGetResolvedName = resolved.FileName;
                        }
                    }
                    catch
                    {
                        // Non-fatal inspection
                    }
                };

            using (var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task? progressTask = null;
                if (progress != null)
                {
                    progressTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!progressCts.IsCancellationRequested)
                            {
                                item.DownloadedBytes = scheduler.DownloadedBytes;
                                progress.Report(item.DownloadedBytes);
                                var conns = scheduler.GetConnectionSnapshots();
                                ConnectionsUpdated?.Invoke(this, (item.Id, conns));
                                await Task.Delay(ProgressReportInterval, progressCts.Token).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Reporting loop stopped
                        }
                    }, CancellationToken.None);
                }

                try
                {
                    await scheduler.ExecuteAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    await progressCts.CancelAsync().ConfigureAwait(false);
                    if (progressTask != null)
                    {
                        await progressTask.ConfigureAwait(false);
                    }

                    item.DownloadedBytes = scheduler.DownloadedBytes;
                    progress?.Report(item.DownloadedBytes);
                }
            }

            await writer.CloseAsync().ConfigureAwait(false);

            // Extension recovery & Atomic rename on completion
            try
            {
                if (File.Exists(item.DestinationPath))
                {
                    var currentExt = _fileNameResolver.GetCompoundExtension(item.FileName);
                    var isCurrentGeneric = string.IsNullOrEmpty(currentExt) || _fileNameResolver.IsGenericOrScriptExtension(currentExt);

                    string targetFileName = item.FileName;

                    if ((!item.IsFileNameUserLocked || isCurrentGeneric) && !string.IsNullOrWhiteSpace(liveGetResolvedName) && !liveGetResolvedName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        var resolvedExt = _fileNameResolver.GetCompoundExtension(liveGetResolvedName);
                        var resolvedHasRealExt = !string.IsNullOrEmpty(resolvedExt) && !_fileNameResolver.IsGenericOrScriptExtension(resolvedExt);
                        if (isCurrentGeneric || resolvedHasRealExt)
                        {
                            targetFileName = liveGetResolvedName;
                        }
                    }
                    else if (isCurrentGeneric)
                    {
                        byte[] sample = new byte[8192];
                        int readBytes = 0;
                        try
                        {
                            using var fs = new FileStream(item.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            readBytes = fs.Read(sample, 0, sample.Length);
                        }
                        catch { }

                        var recoveredExt = _fileNameResolver.RecoverExtension(item.FileName, item.ContentType, sample.AsSpan(0, readBytes));
                        if (!string.IsNullOrEmpty(recoveredExt) && !recoveredExt.Equals(currentExt, StringComparison.OrdinalIgnoreCase))
                        {
                            var basePart = string.IsNullOrEmpty(currentExt) ? item.FileName : item.FileName[..^currentExt.Length];
                            targetFileName = basePart + recoveredExt;
                        }
                    }

                    if (!targetFileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = Path.GetDirectoryName(item.DestinationPath) ?? Environment.CurrentDirectory;
                        var uniqueDest = _fileNameResolver.ResolveUniquePath(dir, targetFileName, overwrite: false);
                        var finalName = Path.GetFileName(uniqueDest);

                        if (!uniqueDest.Equals(item.DestinationPath, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Move(item.DestinationPath, uniqueDest, overwrite: false);
                            item.FileName = finalName;
                            item.DestinationPath = uniqueDest;
                            await _db.SaveDownloadAsync(item, CancellationToken.None).ConfigureAwait(false);
                            FileNameUpdated?.Invoke(this, (item.Id, item.FileName, item.DestinationPath));
                        }
                    }
                }
            }
            catch (Exception renameEx)
            {
                _logger.LogWarning(renameEx, "Atomic filename extension rename skipped for download {Id}", item.Id);
            }

            if (item.TotalBytes <= 0)
            {
                var actualLen = item.DownloadedBytes > 0
                    ? item.DownloadedBytes
                    : (File.Exists(item.DestinationPath) ? new FileInfo(item.DestinationPath).Length : 0);
                if (actualLen > 0)
                {
                    item.TotalBytes = actualLen;
                    item.DownloadedBytes = actualLen;
                }
            }
            else if (item.DownloadedBytes < item.TotalBytes && File.Exists(item.DestinationPath))
            {
                var diskLen = new FileInfo(item.DestinationPath).Length;
                if (diskLen >= item.TotalBytes)
                {
                    item.DownloadedBytes = item.TotalBytes;
                }
            }

            // 5. Verifying & Completion
            await _db.UpdateDownloadStatusAsync(item.Id, DownloadStatus.Verifying, null, ct).ConfigureAwait(false);
            item.Status = DownloadStatus.Verifying;

            if (!string.IsNullOrWhiteSpace(item.ChecksumExpected) && item.ChecksumType != ChecksumType.None)
            {
                var actual = await ComputeChecksumAsync(item.DestinationPath, item.ChecksumType, ct).ConfigureAwait(false);
                item.ChecksumActual = actual;

                if (!actual.Equals(item.ChecksumExpected.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Checksum mismatch! Expected: {item.ChecksumExpected}, Actual: {actual}");
                }
            }

            // Post-download Security: Mark-of-the-Web
            if (ApplyMarkOfTheWeb && !string.IsNullOrWhiteSpace(item.DestinationPath) && File.Exists(item.DestinationPath))
            {
                await SwiftGet.Engine.Security.MarkOfTheWeb.ApplyZoneIdentifierAsync(
                    item.DestinationPath,
                    item.FinalUrl.Length > 0 ? item.FinalUrl : item.Url,
                    logger: _logger,
                    ct: ct).ConfigureAwait(false);
            }

            // Post-download Security: Antivirus scan
            if (AntivirusScanner != null && AntivirusScanner.IsEnabled && !string.IsNullOrWhiteSpace(item.DestinationPath) && File.Exists(item.DestinationPath))
            {
                var scanResult = await AntivirusScanner.ScanFileAsync(item.DestinationPath, ct).ConfigureAwait(false);
                if (scanResult.ThreatDetected)
                {
                    _logger.LogWarning("Antivirus threat detected in completed download {Id}: {Path}", item.Id, item.DestinationPath);
                }
            }

            item.Status = DownloadStatus.Completed;
            item.ErrorMessage = null;
            item.CompletedAtUtc = DateTime.UtcNow;
            await _db.SaveDownloadAsync(item, CancellationToken.None).ConfigureAwait(false);
            await _db.UpdateDownloadStatusAsync(item.Id, DownloadStatus.Completed, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Status = DownloadStatus.Paused;
            await _db.UpdateDownloadStatusAsync(item.Id, DownloadStatus.Paused, "Download paused by user or queue.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Check if link expired mid-flight and can be refreshed
            var category = ErrorClassifier.Classify(ex, out _);
            if (category == DownloadErrorCategory.ExpiredUrl && linkRefreshHook != null)
            {
                _logger.LogInformation("Download encountered expired link mid-flight. Attempting refresh for {Id}.", item.Id);
                await _db.UpdateDownloadStatusAsync(item.Id, DownloadStatus.RefreshingLink, "Refreshing expired download link...", ct).ConfigureAwait(false);
                var newUrl = await linkRefreshHook.RefreshUrlAsync(item.Id, item.FinalUrl, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(newUrl))
                {
                    item.FinalUrl = newUrl;
                    await StartAsync(item, maxConnections, minSplitSize, limiter, null, mirrorHandler, progress, ct).ConfigureAwait(false);
                    return;
                }
            }

            // Check if mirror failover is available
            if (mirrorHandler != null)
            {
                var nextMirror = await mirrorHandler.TryFailoverAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(nextMirror))
                {
                    _logger.LogInformation("Failing over download {Id} to mirror {Mirror}", item.Id, nextMirror);
                    item.FinalUrl = nextMirror;
                    await StartAsync(item, maxConnections, minSplitSize, limiter, linkRefreshHook, mirrorHandler, progress, ct).ConfigureAwait(false);
                    return;
                }
            }

            item.Status = DownloadStatus.Failed;
            item.ErrorMessage = ex.Message;
            await _db.UpdateDownloadStatusAsync(item.Id, DownloadStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 is supported for legacy file integrity verification per download manager spec")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SHA1 is supported for legacy file integrity verification per download manager spec")]
    public static async Task<string> ComputeChecksumAsync(string filePath, ChecksumType type, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
        using HashAlgorithm hasher = type switch
        {
            ChecksumType.Md5 => MD5.Create(),
            ChecksumType.Sha1 => SHA1.Create(),
            ChecksumType.Sha256 => SHA256.Create(),
            _ => throw new NotSupportedException($"Unsupported checksum type: {type}")
        };

        var hashBytes = await hasher.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool IsETagMatch(string oldTag, string newTag)
    {
        var cleanOld = oldTag.Trim();
        var cleanNew = newTag.Trim();

        if (cleanOld.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            cleanOld = cleanOld[2..].Trim();
        }
        if (cleanNew.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            cleanNew = cleanNew[2..].Trim();
        }

        cleanOld = cleanOld.Trim('"');
        cleanNew = cleanNew.Trim('"');

        return string.Equals(cleanOld, cleanNew, StringComparison.Ordinal);
    }
}
