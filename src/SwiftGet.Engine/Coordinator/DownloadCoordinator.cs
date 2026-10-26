using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<DownloadCoordinator> _logger;

    public bool ApplyMarkOfTheWeb { get; set; } = true;
    public SwiftGet.Engine.Security.IAntivirusScanner? AntivirusScanner { get; set; }

    public DownloadCoordinator(
        HttpClient httpClient,
        ISwiftGetDatabase db,
        HttpProbeService? probeService = null,
        ILogger<DownloadCoordinator>? logger = null)
    {
        _httpClient = httpClient;
        _db = db;
        _probeService = probeService ?? new HttpProbeService(httpClient);
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

            using var scheduler = new SegmentScheduler(
                _httpClient,
                item,
                writer,
                _db,
                maxConnections: item.SupportsRanges ? maxConnections : 1,
                minSplitSize: minSplitSize,
                limiter: effectiveLimiter,
                logger: _logger);

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
                                await Task.Delay(500, progressCts.Token).ConfigureAwait(false);
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
