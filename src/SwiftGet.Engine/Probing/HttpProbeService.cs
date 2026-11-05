using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.IO;

namespace SwiftGet.Engine.Probing;

public class HttpProbeService
{
    private readonly HttpClient _httpClient;
    private readonly IFileNameResolver _fileNameResolver;
    private readonly ILogger<HttpProbeService> _logger;

    public HttpProbeService(
        HttpClient? httpClient = null,
        IFileNameResolver? fileNameResolver = null,
        ILogger<HttpProbeService>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });
        _fileNameResolver = fileNameResolver ?? new FileNameResolver();
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
                var result = ParseResponse(url, headResponse, isRangeResponse: false, initialBytes: null);

                // If Content-Length or Accept-Ranges was ambiguous, OR if Content-Disposition was missing
                // and the resolved filename has no extension or generic extension, verify with Range probe
                var ext = _fileNameResolver.GetCompoundExtension(result.SuggestedFileName ?? string.Empty);
                var isExtensionlessOrGeneric = string.IsNullOrEmpty(ext) || _fileNameResolver.IsGenericOrScriptExtension(ext);

                if (result.TotalBytes <= 0 || !result.SupportsRanges || (string.IsNullOrWhiteSpace(result.ContentDisposition) && isExtensionlessOrGeneric))
                {
                    var rangeProbeResult = await ProbeWithRangeAsync(url, customHeaders, cookies, userAgent, referrer, ct).ConfigureAwait(false);
                    if (rangeProbeResult.IsSuccess)
                    {
                        return rangeProbeResult;
                    }
                }
                return result;
            }

            // If HEAD was rejected (e.g. 405 Method Not Allowed, 501 Not Implemented, 403 Forbidden)
            _logger.LogInformation("HEAD request failed with status {StatusCode}. Falling back to GET Range probe.", headResponse.StatusCode);
            return await ProbeWithRangeAsync(url, customHeaders, cookies, userAgent, referrer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "HEAD probe failed with exception for {Url}. Falling back to Range probe.", url);
            try
            {
                return await ProbeWithRangeAsync(url, customHeaders, cookies, userAgent, referrer, ct).ConfigureAwait(false);
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

    private async Task<ProbeResult> ProbeWithRangeAsync(
        string url,
        IDictionary<string, string>? customHeaders,
        string? cookies,
        string? userAgent,
        string? referrer,
        CancellationToken ct)
    {
        // Try Range: bytes=0-511 first to capture magic bytes
        try
        {
            using var rangeRequest = CreateRequest(HttpMethod.Get, url, customHeaders, cookies, userAgent, referrer);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 511);

            using var response = await _httpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent)
            {
                var isRangeResponse = response.StatusCode == HttpStatusCode.PartialContent;
                byte[]? initialBytes = null;
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    var buffer = new byte[512];
                    var read = await stream.ReadAsync(buffer.AsMemory(0, 512), ct).ConfigureAwait(false);
                    if (read > 0)
                    {
                        initialBytes = new byte[read];
                        Array.Copy(buffer, initialBytes, read);
                    }
                }
                catch
                {
                    // Ignore body read failure on probe
                }

                return ParseResponse(url, response, isRangeResponse, initialBytes);
            }
        }
        catch
        {
            // Fall back to Range 0-0 below
        }

        // Fallback: Range 0-0
        using var singleByteRequest = CreateRequest(HttpMethod.Get, url, customHeaders, cookies, userAgent, referrer);
        singleByteRequest.Headers.Range = new RangeHeaderValue(0, 0);

        using var singleByteResponse = await _httpClient.SendAsync(singleByteRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!singleByteResponse.IsSuccessStatusCode && singleByteResponse.StatusCode != HttpStatusCode.PartialContent)
        {
            return new ProbeResult
            {
                OriginalUrl = url,
                FinalUrl = singleByteResponse.RequestMessage?.RequestUri?.ToString() ?? url,
                StatusCode = (int)singleByteResponse.StatusCode,
                IsSuccess = false,
                ErrorMessage = $"Server returned HTTP {(int)singleByteResponse.StatusCode} {singleByteResponse.ReasonPhrase}"
            };
        }

        return ParseResponse(url, singleByteResponse, isRangeResponse: singleByteResponse.StatusCode == HttpStatusCode.PartialContent, initialBytes: null);
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
        request.Headers.TryAddWithoutValidation("X-SwiftGet-Origin", "1");
        request.Headers.TryAddWithoutValidation("X-SwiftGet-Session-Token", SwiftGet.Engine.Integration.CaptureGatekeeper.SessionOriginToken);

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

    private ProbeResult ParseResponse(string originalUrl, HttpResponseMessage response, bool isRangeResponse, byte[]? initialBytes)
    {
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
        var headers = response.Headers;
        var contentHeaders = response.Content.Headers;

        // Check range support
        var acceptRanges = headers.AcceptRanges.ToString();
        var supportsRanges = isRangeResponse || acceptRanges.Contains("bytes", StringComparison.OrdinalIgnoreCase);

        // Server range deception detection
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

        // Extract raw Content-Disposition header safely
        string? rawCd = null;
        if (contentHeaders.TryGetValues("Content-Disposition", out var cdVals))
        {
            rawCd = string.Join("; ", cdVals);
        }
        else if (headers.TryGetValues("Content-Disposition", out var hcdVals))
        {
            rawCd = string.Join("; ", hcdVals);
        }
        else
        {
            rawCd = contentHeaders.ContentDisposition?.ToString();
        }

        // Unified filename resolution via IFileNameResolver
        var resolutionContext = new FileNameResolutionContext
        {
            OriginalUrl = originalUrl,
            FinalUrl = finalUrl,
            ContentDisposition = rawCd,
            ContentType = contentType,
            InitialBytes = initialBytes != null ? new ReadOnlyMemory<byte>(initialBytes) : null
        };

        var resolved = _fileNameResolver.Resolve(resolutionContext);

        return new ProbeResult
        {
            OriginalUrl = originalUrl,
            FinalUrl = finalUrl,
            TotalBytes = totalBytes,
            SupportsRanges = supportsRanges,
            ETag = etag,
            LastModified = lastModified,
            ContentType = contentType,
            ContentDisposition = rawCd,
            SuggestedFileName = resolved.FileName,
            InitialBytes = initialBytes,
            ExtensionRecovered = resolved.ExtensionRecovered,
            IsDangerous = resolved.IsDangerous,
            StatusCode = (int)response.StatusCode,
            IsSuccess = true
        };
    }
}
