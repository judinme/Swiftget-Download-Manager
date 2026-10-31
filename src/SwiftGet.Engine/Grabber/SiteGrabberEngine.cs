using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftGet.Engine.Grabber;

public sealed class GrabberProgressEventArgs : EventArgs
{
    public int PagesVisited { get; }
    public int FilesFound { get; }
    public string CurrentUrl { get; }

    public GrabberProgressEventArgs(int pagesVisited, int filesFound, string currentUrl)
    {
        PagesVisited = pagesVisited;
        FilesFound = filesFound;
        CurrentUrl = currentUrl;
    }
}

public interface IWebCrawler
{
    event EventHandler<GrabberProgressEventArgs>? ProgressUpdated;
    event EventHandler<GrabberFileItem>? FileDiscovered;

    Task<IReadOnlyList<GrabberFileItem>> CrawlAsync(GrabberProject project, CancellationToken ct = default);
}

public sealed partial class SiteGrabberEngine : IWebCrawler
{
    public const int MaxPagesLimit = 500;
    private readonly HttpClient _httpClient;
    private readonly SwiftGet.Core.IO.IFileNameResolver _fileNameResolver;

    [GeneratedRegex(@"(?:href|src)\s*=\s*[""']([^""'#\s]+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".ico", ".tiff"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".m4v", ".ts"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".epub", ".csv", ".rtf"
    };

    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".tgz"
    };

    public event EventHandler<GrabberProgressEventArgs>? ProgressUpdated;
    public event EventHandler<GrabberFileItem>? FileDiscovered;

    public SiteGrabberEngine(HttpClient httpClient, SwiftGet.Core.IO.IFileNameResolver? fileNameResolver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _fileNameResolver = fileNameResolver ?? new SwiftGet.Core.IO.FileNameResolver();
    }

    public async Task<IReadOnlyList<GrabberFileItem>> CrawlAsync(GrabberProject project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Uri.TryCreate(project.StartUrl, UriKind.Absolute, out var startUri))
        {
            throw new ArgumentException($"Invalid starting URL: {project.StartUrl}", nameof(project));
        }

        var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enqueuedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveredFileUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveredFiles = new List<GrabberFileItem>();

        var queue = new Queue<(Uri Uri, int Depth)>();
        queue.Enqueue((startUri, 0));
        enqueuedPages.Add(NormalizeUrl(startUri));

        var allowedExtensions = ResolveAllowedExtensions(project);

        while (queue.Count > 0 && visitedPages.Count < MaxPagesLimit)
        {
            ct.ThrowIfCancellationRequested();

            var (currentUri, currentDepth) = queue.Dequeue();
            var normalizedUrl = NormalizeUrl(currentUri);

            if (visitedPages.Contains(normalizedUrl))
            {
                continue;
            }

            visitedPages.Add(normalizedUrl);
            ProgressUpdated?.Invoke(this, new GrabberProgressEventArgs(visitedPages.Count, discoveredFiles.Count, currentUri.AbsoluteUri));

            string htmlContent;
            try
            {
                using var response = await _httpClient.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                // If the URL itself is a media file rather than HTML, capture it
                if (mediaType != null && !mediaType.Contains("html") && !mediaType.Contains("xml") && !mediaType.Contains("text"))
                {
                    TryAddDiscoveredFile(currentUri, currentDepth, currentUri.AbsoluteUri, allowedExtensions, discoveredFileUrls, discoveredFiles);
                    continue;
                }

                htmlContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore transient page fetch errors and continue crawling remaining pages
                continue;
            }

            var matches = LinkRegex().Matches(htmlContent);
            foreach (Match match in matches)
            {
                ct.ThrowIfCancellationRequested();

                var rawLink = match.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(rawLink) || rawLink.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || rawLink.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Uri.TryCreate(currentUri, rawLink, out var absoluteUri) || (absoluteUri.Scheme != "http" && absoluteUri.Scheme != "https"))
                {
                    continue;
                }

                // Check if this link is a file matching filter
                if (TryAddDiscoveredFile(absoluteUri, currentDepth + 1, currentUri.AbsoluteUri, allowedExtensions, discoveredFileUrls, discoveredFiles))
                {
                    // It matched a target file filter
                }

                // Check if link is an HTML page to crawl deeper
                if (currentDepth + 1 <= project.MaxDepth && MatchesDomainScope(absoluteUri, startUri, project.DomainScope))
                {
                    var ext = Path.GetExtension(absoluteUri.AbsolutePath);
                    var isHtmlCandidate = string.IsNullOrEmpty(ext) || ext.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                                          ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".php", StringComparison.OrdinalIgnoreCase) ||
                                          ext.Equals(".asp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".aspx", StringComparison.OrdinalIgnoreCase);

                    var childNormalized = NormalizeUrl(absoluteUri);
                    if (isHtmlCandidate && !visitedPages.Contains(childNormalized) && enqueuedPages.Add(childNormalized))
                    {
                        queue.Enqueue((absoluteUri, currentDepth + 1));
                    }
                }
            }
        }

        ProgressUpdated?.Invoke(this, new GrabberProgressEventArgs(visitedPages.Count, discoveredFiles.Count, "Completed"));
        return discoveredFiles;
    }

    private bool TryAddDiscoveredFile(
        Uri uri,
        int depth,
        string sourcePageUrl,
        HashSet<string>? allowedExtensions,
        HashSet<string> discoveredFileUrls,
        List<GrabberFileItem> discoveredFiles)
    {
        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        if (allowedExtensions != null && !allowedExtensions.Contains(ext))
        {
            return false;
        }

        var normalized = NormalizeUrl(uri);
        if (discoveredFileUrls.Contains(normalized))
        {
            return false;
        }

        discoveredFileUrls.Add(normalized);
        var fileName = _fileNameResolver.ResolveInitialCandidate(uri.AbsoluteUri);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file" + ext;
        }

        var item = new GrabberFileItem
        {
            Url = uri.AbsoluteUri,
            FileName = fileName,
            Extension = ext,
            Depth = depth,
            SourcePageUrl = sourcePageUrl,
            IsSelected = true
        };

        discoveredFiles.Add(item);
        FileDiscovered?.Invoke(this, item);
        return true;
    }

    private static HashSet<string>? ResolveAllowedExtensions(GrabberProject project)
    {
        return project.FilePreset switch
        {
            FileFilterPreset.Pictures => PictureExtensions,
            FileFilterPreset.Audio => AudioExtensions,
            FileFilterPreset.Video => VideoExtensions,
            FileFilterPreset.Documents => DocumentExtensions,
            FileFilterPreset.Compressed => CompressedExtensions,
            FileFilterPreset.Custom => ParseCustomExtensions(project.CustomExtensions),
            _ => null // All files allowed
        };
    }

    private static HashSet<string> ParseCustomExtensions(string custom)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(custom)) return set;

        var tokens = custom.Split([' ', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            var clean = t.Trim();
            if (!clean.StartsWith('.')) clean = "." + clean;
            set.Add(clean);
        }
        return set;
    }

    private static bool MatchesDomainScope(Uri targetUri, Uri startUri, DomainScope scope)
    {
        return scope switch
        {
            DomainScope.SameDomain => string.Equals(targetUri.Host, startUri.Host, StringComparison.OrdinalIgnoreCase),
            DomainScope.Subdomains => targetUri.Host.EndsWith(GetRootDomain(startUri.Host), StringComparison.OrdinalIgnoreCase),
            DomainScope.AnyDomain => true,
            _ => true
        };
    }

    private static string GetRootDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        return string.Join('.', parts[^2..]);
    }

    private static string NormalizeUrl(Uri uri)
    {
        // Preserve scheme, host, port, path, and query parameters; discard fragment (#)
        return uri.GetLeftPart(UriPartial.Query);
    }
}
