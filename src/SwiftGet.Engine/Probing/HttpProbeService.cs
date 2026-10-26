using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Engine.IO;

namespace SwiftGet.Engine.Probing;

public class HttpProbeService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpProbeService> _logger;

    public HttpProbeService(HttpClient? httpClient = null, ILogger<HttpProbeService>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });
        _logger = logger ?? NullLogger<HttpProbeService>.Instance;
    }

    public async Task<ProbeResult> ProbeAsync(
        string url,
        IDictionary<string, string>? customHeaders = null,
        string? cookies = null,
        string? userAgent = null,
        string? referrer = null,
        CancellationToken ct = default)
    {
        try
        {
            // 1. Attempt HEAD request
            using var headRequest = CreateRequest(HttpMethod.Head, url, customHeaders, cookies, userAgent, referrer);
            using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (headResponse.IsSuccessStatusCode)
            {
                var result = ParseResponse(url, headResponse, isRangeResponse: false);
                // If Content-Length or Accept-Ranges was ambiguous, verify with Range 0-0
                if (result.TotalBytes <= 0 || !result.SupportsRanges)
                {
                    var rangeProbeResult = await ProbeWithRange0Async(url, customHeaders, cookies, userAgent, referrer, ct).ConfigureAwait(false);
                    if (rangeProbeResult.IsSuccess)
                    {
                        return rangeProbeResult;
                    }
                }
                return result;
            }

            // If HEAD was rejected (e.g. 405 Method Not Allowed, 501 Not Implemented, 403 Forbidden)
            _logger.LogInformation("HEAD request failed with status {StatusCode}. Falling back to GET Range: bytes=0-0 probe.", headResponse.StatusCode);
            return await ProbeWithRange0Async(url, customHeaders, cookies, userAgent, referrer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "HEAD probe failed with exception for {Url}. Falling back to Range probe.", url);
            try
            {
                return await ProbeWithRange0Async(url, customHeaders, cookies, userAgent, referrer, ct).ConfigureAwait(false);
            }
            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
            {
                _logger.LogError(fallbackEx, "All probe strategies failed for {Url}", url);
                return new ProbeResult
                {
                    OriginalUrl = url,
                    FinalUrl = url,
                    IsSuccess = false,
                    ErrorMessage = fallbackEx.Message
                };
            }
        }
    }

    private async Task<ProbeResult> ProbeWithRange0Async(
        string url,
        IDictionary<string, string>? customHeaders,
        string? cookies,
        string? userAgent,
        string? referrer,
        CancellationToken ct)
    {
        using var rangeRequest = CreateRequest(HttpMethod.Get, url, customHeaders, cookies, userAgent, referrer);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await _httpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            return new ProbeResult
            {
                OriginalUrl = url,
                FinalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url,
                StatusCode = (int)response.StatusCode,
                IsSuccess = false,
                ErrorMessage = $"Server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }

        var isRangeResponse = response.StatusCode == HttpStatusCode.PartialContent;
        return ParseResponse(url, response, isRangeResponse);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        IDictionary<string, string>? customHeaders,
        string? cookies,
        string? userAgent,
        string? referrer)
    {
        var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 SwiftGet/1.0");
        }

        if (!string.IsNullOrWhiteSpace(referrer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referrer);
        }

        if (!string.IsNullOrWhiteSpace(cookies))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        if (customHeaders != null)
        {
            foreach (var (k, v) in customHeaders)
            {
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        return request;
    }

    private static ProbeResult ParseResponse(string originalUrl, HttpResponseMessage response, bool isRangeResponse)
    {
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
        var headers = response.Headers;
        var contentHeaders = response.Content.Headers;

        // Check range support
        var acceptRanges = headers.AcceptRanges.ToString();
        var supportsRanges = isRangeResponse || acceptRanges.Contains("bytes", StringComparison.OrdinalIgnoreCase);

        // Server range deception detection: if we asked for Range: bytes=0-0 and got 200 OK, server ignored range!
        if (!isRangeResponse && response.RequestMessage?.Headers.Range != null && response.StatusCode == HttpStatusCode.OK)
        {
            supportsRanges = false;
        }

        // Determine total bytes
        long totalBytes = -1;
        if (isRangeResponse && contentHeaders.ContentRange != null && contentHeaders.ContentRange.HasLength)
        {
            totalBytes = contentHeaders.ContentRange.Length!.Value;
        }
        else if (contentHeaders.ContentLength.HasValue)
        {
            totalBytes = contentHeaders.ContentLength.Value;
        }

        // ETag & LastModified
        var etag = headers.ETag?.Tag?.Trim('"');
        var lastModified = contentHeaders.LastModified?.ToString("R");
        var contentType = contentHeaders.ContentType?.MediaType;

        // Suggested file name extraction
        var suggestedFileName = ExtractFileName(contentHeaders.ContentDisposition, finalUrl, contentType);

        return new ProbeResult
        {
            OriginalUrl = originalUrl,
            FinalUrl = finalUrl,
            TotalBytes = totalBytes,
            SupportsRanges = supportsRanges,
            ETag = etag,
            LastModified = lastModified,
            ContentType = contentType,
            SuggestedFileName = suggestedFileName,
            StatusCode = (int)response.StatusCode,
            IsSuccess = true
        };
    }

    private static string ExtractFileName(ContentDispositionHeaderValue? disposition, string finalUrl, string? contentType = null)
    {
        string? rawName = null;

        if (disposition != null)
        {
            if (!string.IsNullOrWhiteSpace(disposition.FileNameStar))
            {
                rawName = disposition.FileNameStar;
            }
            else if (!string.IsNullOrWhiteSpace(disposition.FileName))
            {
                rawName = disposition.FileName.Trim('"');
            }
        }

        if (string.IsNullOrWhiteSpace(rawName))
        {
            try
            {
                var uri = new Uri(finalUrl);
                var path = uri.AbsolutePath.TrimEnd('/');
                var segment = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    rawName = Uri.UnescapeDataString(segment);
                }
            }
            catch
            {
                // Fallback handled below
            }
        }

        var sanitized = FileNameSanitizer.Sanitize(rawName ?? "download.bin");
        if ((Path.GetExtension(sanitized).Length == 0 || sanitized.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(contentType))
        {
            var ext = GetExtensionFromContentType(contentType);
            if (!string.IsNullOrEmpty(ext))
            {
                if (sanitized.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    sanitized = Path.ChangeExtension(sanitized, ext);
                }
                else
                {
                    sanitized += ext;
                }
            }
        }
        return sanitized;
    }

    private static string? GetExtensionFromContentType(string contentType)
    {
        return contentType.ToLowerInvariant().Trim() switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" or "video/mkv" => ".mkv",
            "video/quicktime" => ".mov",
            "video/x-msvideo" => ".avi",
            "video/x-ms-wmv" => ".wmv",
            "video/mp2t" => ".ts",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/aac" => ".aac",
            "audio/ogg" or "application/ogg" => ".ogg",
            "application/zip" or "application/x-zip-compressed" => ".zip",
            "application/x-7z-compressed" => ".7z",
            "application/x-rar-compressed" or "application/vnd.rar" => ".rar",
            "application/x-tar" => ".tar",
            "application/gzip" or "application/x-gzip" => ".gz",
            "application/pdf" => ".pdf",
            "application/x-msdownload" => ".exe",
            "application/x-iso9660-image" => ".iso",
            _ => null
        };
    }
}

