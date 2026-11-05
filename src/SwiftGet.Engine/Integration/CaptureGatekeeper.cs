using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftGet.Engine.Integration;

public enum CaptureMode
{
    AskMe = 0,
    AutoStart = 1,
    Off = 2
}

public sealed class BrowserCaptureSettings
{
    public bool CaptureEnabled { get; set; } = true;
    public CaptureMode Mode { get; set; } = CaptureMode.AskMe;
    public int MinFileSizeKb { get; set; } = 256;

    public List<string> IgnorePatterns { get; set; } =
    [
        "claude.ai/api",
        "*/api/*",
        "chrome-extension://",
        "moz-extension://",
        "localhost",
        "127.0.0.1",
        "*.appinstaller",
        "gradle.properties",
        "google.com",
        "accounts.google.com",
        "apis.google.com",
        "gstatic.com",
        "login.live.com",
        "login.microsoftonline.com"
    ];

    public List<string> AllowedExtensions { get; set; } =
    [
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".tgz",
        ".exe", ".msi", ".apk", ".appx", ".msix", ".ipa", ".dmg", ".pkg", ".deb", ".rpm",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".epub",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".opus", ".bin"
    ];
}

public sealed record CaptureLogEntry
{
    public string EventId { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Url { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public int TabId { get; init; } = -1;
    public string Initiator { get; init; } = string.Empty;
    public bool Captured { get; init; }
    public string Reason { get; init; } = string.Empty;

    [JsonIgnore]
    public string DisplayLine => $"[{TimestampUtc.ToLocalTime():HH:mm:ss}] {(Captured ? "CAPTURED" : "IGNORED")} | {Reason} | {(string.IsNullOrWhiteSpace(FileName) ? Url : $"{FileName} ({Url})")}";
}

public sealed record CaptureGateResult(bool Allowed, string Reason, string EventId);

/// <summary>
/// Enforces strict TTL, event-time guard, idempotency/deduplication, ignore-list, MIME/size rules,
/// and loop-prevention across NativeMessageRouter, LoopbackServer, and App UI.
/// </summary>
public sealed class CaptureGatekeeper
{
    public const int DefaultTtlSeconds = 15;
    public const int MaxDedupeEntries = 200;
    public const int SameUrlDedupeSeconds = 3;
    public const int MaxLogEntries = 100;
    public const string OriginHeaderName = "X-SwiftGet-Origin";

    public static string SessionOriginToken { get; } = Guid.NewGuid().ToString("N");
    public static DateTime ProcessStartupUtc { get; set; } = DateTime.UtcNow;

    private readonly object _sync = new();
    private readonly LinkedList<string> _eventIdOrder = new();
    private readonly HashSet<string> _eventIdSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recentUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeOrPendingUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CaptureLogEntry> _decisionLog = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string? CustomSettingsDirectory { get; set; }

    private static string GetDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CustomSettingsDirectory))
        {
            return CustomSettingsDirectory;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet");
    }

    public static string GetSettingsFilePath() => Path.Combine(GetDirectory(), "browser_capture_settings.json");
    public static string GetCaptureLogFilePath() => Path.Combine(GetDirectory(), "capture_decisions.json");

    private BrowserCaptureSettings? _cachedSettings;

    public BrowserCaptureSettings LoadSettings()
    {
        lock (_sync)
        {
            try
            {
                var path = GetSettingsFilePath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<BrowserCaptureSettings>(json, JsonOptions);
                    if (loaded != null)
                    {
                        _cachedSettings = loaded;
                        return loaded;
                    }
                }
            }
            catch
            {
                // Fallback to cached or defaults
            }

            return _cachedSettings ??= new BrowserCaptureSettings();
        }
    }

    public void SaveSettings(BrowserCaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            _cachedSettings = settings;
            try
            {
                var path = GetSettingsFilePath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // Ignore write errors in sandboxed tests
            }
        }
    }

    public void AddIgnoredSite(string urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return;
        string host = urlOrHost.Trim();
        if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host.ToLowerInvariant();
        }

        var settings = LoadSettings();
        if (!settings.IgnorePatterns.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            settings.IgnorePatterns.Add(host);
            SaveSettings(settings);
        }
    }

    public void MarkUrlActive(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_sync)
        {
            _activeOrPendingUrls.Add(NormalizeUrl(url));
        }
    }

    public void UnmarkUrlActive(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_sync)
        {
            _activeOrPendingUrls.Remove(NormalizeUrl(url));
        }
    }

    public bool IsUrlActive(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        lock (_sync)
        {
            return _activeOrPendingUrls.Contains(NormalizeUrl(url));
        }
    }

    public void ClearState()
    {
        lock (_sync)
        {
            _eventIdOrder.Clear();
            _eventIdSet.Clear();
            _recentUrls.Clear();
            _activeOrPendingUrls.Clear();
            _decisionLog.Clear();
        }

        try
        {
            var logPath = GetCaptureLogFilePath();
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
        catch
        {
            // Ignore
        }
    }

    public IReadOnlyList<CaptureLogEntry> GetRecentDecisions()
    {
        lock (_sync)
        {
            if (_decisionLog.Count == 0)
            {
                LoadDecisionLogFromDiskUnsafe();
            }
            return _decisionLog.ToList();
        }
    }

    public void RecordDecision(
        string? eventId,
        string? url,
        string? fileName,
        string source,
        int tabId,
        string? initiator,
        bool captured,
        string reason)
    {
        var entry = new CaptureLogEntry
        {
            EventId = string.IsNullOrWhiteSpace(eventId) ? Guid.NewGuid().ToString("N") : eventId!,
            TimestampUtc = DateTime.UtcNow,
            Url = SanitizeUrlForLog(url),
            FileName = fileName ?? string.Empty,
            Source = source,
            TabId = tabId,
            Initiator = initiator ?? string.Empty,
            Captured = captured,
            Reason = reason
        };

        lock (_sync)
        {
            if (_decisionLog.Count == 0)
            {
                LoadDecisionLogFromDiskUnsafe();
            }

            _decisionLog.AddFirst(entry);
            while (_decisionLog.Count > MaxLogEntries)
            {
                _decisionLog.RemoveLast();
            }

            SaveDecisionLogToDiskUnsafe();
        }
    }

    private void LoadDecisionLogFromDiskUnsafe()
    {
        try
        {
            var path = GetCaptureLogFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<CaptureLogEntry>>(json, JsonOptions);
                if (list != null)
                {
                    foreach (var item in list.Take(MaxLogEntries))
                    {
                        _decisionLog.AddLast(item);
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
    }

    private void SaveDecisionLogToDiskUnsafe()
    {
        try
        {
            var path = GetCaptureLogFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(_decisionLog.ToList(), JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore
        }
    }

    /// <summary>
    /// Evaluates an incoming capture event against all Phase 2 rules:
    /// A. Master toggle & CaptureMode
    /// B. TTL (15s) and Startup Event-Time Guard
    /// C. Idempotency (eventId ring buffer + 3s same-URL dedupe + active download check)
    /// D. Stricter capture rules (URL scheme, ignore patterns, MIME/size filter, userGesture for media)
    /// E. Loop prevention (X-SwiftGet-Origin header)
    /// </summary>
    public CaptureGateResult EvaluateCapture(
        string? url,
        string? fileName = null,
        string? eventId = null,
        DateTime? createdAtUtc = null,
        string source = "browser",
        int tabId = -1,
        string? initiator = null,
        bool userGesture = true,
        long? fileSizeBytes = null,
        string? mimeType = null,
        IDictionary<string, string>? headers = null,
        bool enforceStartupTimeGuard = false,
        bool isExplicitUserAction = false,
        DateTime? nowUtcOverride = null)
    {
        var nowUtc = nowUtcOverride ?? DateTime.UtcNow;
        var resolvedEventId = string.IsNullOrWhiteSpace(eventId) ? Guid.NewGuid().ToString("N") : eventId!.Trim();
        var settings = LoadSettings();

        // 0. Master Toggle & CaptureMode check
        if (!isExplicitUserAction && (!settings.CaptureEnabled || settings.Mode == CaptureMode.Off))
        {
            return Reject(resolvedEventId, url, fileName, source, tabId, initiator, "Browser capture is disabled in settings");
        }

        // 1. Validate URL & Scheme
        if (string.IsNullOrWhiteSpace(url))
        {
            return Reject(resolvedEventId, url, fileName, source, tabId, initiator, "Empty URL");
        }

        var trimmedUrl = url.Trim();
        if (trimmedUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            trimmedUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmedUrl.StartsWith("chrome-extension:", StringComparison.OrdinalIgnoreCase) ||
            trimmedUrl.StartsWith("moz-extension:", StringComparison.OrdinalIgnoreCase) ||
            trimmedUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            trimmedUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, $"Non-network URL scheme rejected ({trimmedUrl.Split(':')[0]}:)");
        }

        if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && !trimmedUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)))
        {
            return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, "Unsupported URI scheme");
        }

        // 2. Loop prevention header check (Requirement E)
        if (headers != null)
        {
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, OriginHeaderName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(kv.Value))
                {
                    return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, "Loop prevention: request originated from SwiftGet (X-SwiftGet-Origin)");
                }
            }
        }

        // 3. TTL & Event-Time Guard (Requirement A & C)
        if (createdAtUtc.HasValue)
        {
            var eventUtc = createdAtUtc.Value.Kind == DateTimeKind.Utc
                ? createdAtUtc.Value
                : createdAtUtc.Value.ToUniversalTime();

            var ageSeconds = (nowUtc - eventUtc).TotalSeconds;
            if (ageSeconds > DefaultTtlSeconds)
            {
                return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, $"Expired capture TTL ({ageSeconds:F1}s > {DefaultTtlSeconds}s)");
            }

            if (enforceStartupTimeGuard && eventUtc < ProcessStartupUtc.AddSeconds(-2))
            {
                return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, $"Event predates app startup ({eventUtc:O} < {ProcessStartupUtc:O})");
            }
        }

        // 4. Ignore-list & API endpoint checks (Requirement D)
        if (!isExplicitUserAction)
        {
            if (uri.IsLoopback ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, "Localhost/loopback URL ignored");
            }

            // Check /api/ path segment unless explicitly triggered by context menu / toolbar
            if (uri.AbsolutePath.Contains("/api/", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, "API endpoint path (/api/) ignored");
            }

            // Check configurable ignore patterns against URL, host, initiator, and filename
            foreach (var pattern in settings.IgnorePatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (MatchesPattern(trimmedUrl, uri.Host, fileName, initiator, pattern))
                {
                    return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, $"Matched ignore rule '{pattern}'");
                }
            }

            // Reject non-binary MIME types with unknown or small size
            if (!string.IsNullOrWhiteSpace(mimeType))
            {
                var cleanMime = mimeType.Split(';')[0].Trim().ToLowerInvariant();
                bool isTextOrJson = cleanMime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                                    cleanMime == "application/json" ||
                                    cleanMime == "application/ld+json" ||
                                    cleanMime == "application/javascript" ||
                                    cleanMime == "application/x-javascript" ||
                                    cleanMime == "application/xml" ||
                                    cleanMime == "application/xhtml+xml";

                if (isTextOrJson)
                {
                    long minBytes = Math.Max(1, settings.MinFileSizeKb) * 1024L;
                    if (!fileSizeBytes.HasValue || fileSizeBytes.Value <= 0 || fileSizeBytes.Value < minBytes)
                    {
                        return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, $"Non-binary MIME type '{cleanMime}' with size {fileSizeBytes ?? -1}B rejected");
                    }
                }
            }

            // Reject known small files below MinFileSizeKb threshold (when size is known)
            if (fileSizeBytes.HasValue && fileSizeBytes.Value > 0)
            {
                long minBytes = Math.Max(0, settings.MinFileSizeKb) * 1024L;
                if (minBytes > 0 && fileSizeBytes.Value < minBytes)
                {
                    return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, $"File size ({fileSizeBytes.Value}B) below threshold ({minBytes}B)");
                }
            }

            // Media (YouTube, etc.) strict userGesture rule
            bool isMediaSite = uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                               uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
            if (isMediaSite && !userGesture)
            {
                return Reject(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, "Media capture requires explicit user gesture");
            }
        }

        // 5. Idempotency & Deduplication (Requirement B & E)
        var normalizedUrl = NormalizeUrl(trimmedUrl);
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(eventId) && _eventIdSet.Contains(resolvedEventId))
            {
                return RejectUnsafe(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, $"Duplicate eventId '{resolvedEventId}'");
            }

            if (!isExplicitUserAction)
            {
                if (_activeOrPendingUrls.Contains(normalizedUrl))
                {
                    return RejectUnsafe(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, "URL is already active or awaiting confirmation");
                }

                if (_recentUrls.TryGetValue(normalizedUrl, out var lastSeenUtc))
                {
                    var delta = (nowUtc - lastSeenUtc).TotalSeconds;
                    if (delta >= 0 && delta < SameUrlDedupeSeconds)
                    {
                        return RejectUnsafe(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, $"Duplicate URL within {SameUrlDedupeSeconds}s window ({delta:F1}s)");
                    }
                }
            }

            // Record in ring buffer
            _eventIdSet.Add(resolvedEventId);
            _eventIdOrder.AddLast(resolvedEventId);
            while (_eventIdOrder.Count > MaxDedupeEntries)
            {
                var oldest = _eventIdOrder.First!.Value;
                _eventIdOrder.RemoveFirst();
                _eventIdSet.Remove(oldest);
            }

            _recentUrls[normalizedUrl] = nowUtc;
            if (_recentUrls.Count > MaxDedupeEntries * 2)
            {
                var cutoff = nowUtc.AddSeconds(-30);
                var expiredKeys = _recentUrls.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
                foreach (var k in expiredKeys)
                {
                    _recentUrls.Remove(k);
                }
            }
        }

        RecordDecision(resolvedEventId, trimmedUrl, fileName, source, tabId, initiator, captured: true, reason: "Passed all capture guards");
        return new CaptureGateResult(true, "Allowed", resolvedEventId);
    }

    public static string SanitizeMediaUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains("youtube.com") && !host.Contains("youtu.be"))
        {
            return url;
        }

        // Strip automatic YouTube Radio/Mix playlist parameters (list=RD..., start_radio=..., index=...)
        if (string.IsNullOrEmpty(uri.Query)) return url;

        var queryParams = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToList();

        bool hasMixList = queryParams.Any(kv =>
            string.Equals(kv[0], "list", StringComparison.OrdinalIgnoreCase) &&
            kv.Length > 1 &&
            kv[1].StartsWith("RD", StringComparison.OrdinalIgnoreCase));

        bool hasWatchVideoId = queryParams.Any(kv => string.Equals(kv[0], "v", StringComparison.OrdinalIgnoreCase));

        if (hasMixList || hasWatchVideoId)
        {
            var filtered = queryParams.Where(kv =>
            {
                var key = kv[0];
                if (string.Equals(key, "list", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "start_radio", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "index", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "pp", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }).Select(kv => kv.Length > 1 ? $"{kv[0]}={kv[1]}" : kv[0]).ToList();

            var builder = new UriBuilder(uri)
            {
                Query = filtered.Count > 0 ? string.Join("&", filtered) : string.Empty
            };
            return builder.Uri.ToString();
        }

        return url;
    }

    private static bool MatchesPattern(string url, string host, string? fileName, string? initiator, string pattern)
    {
        var p = pattern.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(p)) return false;

        var lowerUrl = url.ToLowerInvariant();
        var lowerHost = host.ToLowerInvariant();
        var lowerFile = (fileName ?? string.Empty).ToLowerInvariant();
        var lowerInitiator = (initiator ?? string.Empty).ToLowerInvariant();

        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = p[1..]; // e.g. ".appinstaller"
            if (lowerFile.EndsWith(suffix, StringComparison.Ordinal) ||
                lowerHost.EndsWith(suffix, StringComparison.Ordinal) ||
                lowerUrl.Contains(suffix + "?", StringComparison.Ordinal) ||
                lowerUrl.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (p.Contains('*'))
        {
            var parts = p.Split('*', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts.All(part => lowerUrl.Contains(part, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        if (lowerHost.Equals(p, StringComparison.Ordinal) || lowerHost.EndsWith("." + p, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(lowerFile) && lowerFile.Equals(p, StringComparison.Ordinal))
        {
            return true;
        }

        if (lowerUrl.Contains(p, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(lowerInitiator) && lowerInitiator.Contains(p, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeUrl(string url)
    {
        return SanitizeMediaUrl(url.Trim());
    }

    private static string SanitizeUrlForLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        // Strip sensitive query tokens if any while keeping path and video id
        if (string.IsNullOrEmpty(uri.Query)) return url;
        if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return SanitizeMediaUrl(url);
        }
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
    }

    private CaptureGateResult Reject(
        string eventId,
        string? url,
        string? fileName,
        string source,
        int tabId,
        string? initiator,
        string reason)
    {
        RecordDecision(eventId, url, fileName, source, tabId, initiator, captured: false, reason: reason);
        return new CaptureGateResult(false, reason, eventId);
    }

    private CaptureGateResult RejectUnsafe(
        string eventId,
        string? url,
        string? fileName,
        string source,
        int tabId,
        string? initiator,
        string reason)
    {
        var entry = new CaptureLogEntry
        {
            EventId = eventId,
            TimestampUtc = DateTime.UtcNow,
            Url = SanitizeUrlForLog(url),
            FileName = fileName ?? string.Empty,
            Source = source,
            TabId = tabId,
            Initiator = initiator ?? string.Empty,
            Captured = false,
            Reason = reason
        };
        _decisionLog.AddFirst(entry);
        while (_decisionLog.Count > MaxLogEntries)
        {
            _decisionLog.RemoveLast();
        }
        SaveDecisionLogToDiskUnsafe();
        return new CaptureGateResult(false, reason, eventId);
    }
}
