using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace SwiftGet.Engine.Tests.Harness;

public enum ProbeScenario
{
    StandardRange200,
    HeadReturns405,
    HeadReturns403,
    HeadWithoutContentLength,
    GetRange00Fallback,
    ChunkedNoLength,
    GzipCompressed,
    RedirectChainToFinal,
    RelativeRedirect,
    CookieAuthRequired,
    LargeFile5Gb,
    ZeroBytes,
    SizeChangesMidDownload,
    ContentLengthWrong,
    MalformedRange,
    ThrottledPerConnection,
    EarlyCloseTruncated,
    Returns200InsteadOf206OnRange,
    WrongContentRangeHeader,
    Burst429ThenSuccess,
    TrickyContentDispositionRfc5987,
    ExpiringLink,
    ChangingEtagMidDownload
}

public sealed class KestrelTestServer : IAsyncDisposable
{
    private readonly IHost _host;
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public ProbeScenario CurrentScenario { get; set; } = ProbeScenario.StandardRange200;

    public byte[] StandardPayload { get; }
    public string StandardEtag { get; set; } = "\"swiftget-etag-standard\"";
    public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow.AddDays(-1);

    public int PerConnectionBps { get; set; } = 64 * 1024;
    public int Burst429Count { get; set; } = 2;
    private int _remaining429Count = 2;
    public DateTimeOffset ExpireAtUtc { get; set; } = DateTimeOffset.UtcNow.AddSeconds(10);
    public int EarlyCloseByteLimit { get; set; } = 4096;

    public KestrelTestServer()
    {
        Port = GetAvailablePort();

        StandardPayload = new byte[1024 * 512]; // 512 KB
        Random.Shared.NextBytes(StandardPayload);

        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseKestrel(kestrel =>
                {
                    kestrel.Listen(IPAddress.Loopback, Port);
                });

                webHost.Configure(app =>
                {
                    app.Run(async context =>
                    {
                        await HandleRequestAsync(context);
                    });
                });
            });

        _host = builder.Start();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        var req = context.Request;
        var res = context.Response;

        res.Headers["Server"] = "SwiftGet-Kestrel-QA";
        res.Headers["Last-Modified"] = LastModified.ToString("R");
        res.Headers["ETag"] = StandardEtag;

        switch (CurrentScenario)
        {
            case ProbeScenario.HeadReturns405:
                if (HttpMethods.IsHead(req.Method))
                {
                    res.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }
                break;

            case ProbeScenario.HeadReturns403:
                if (HttpMethods.IsHead(req.Method))
                {
                    res.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                break;

            case ProbeScenario.HeadWithoutContentLength:
                if (HttpMethods.IsHead(req.Method))
                {
                    res.Headers.AcceptRanges = "bytes";
                    res.StatusCode = StatusCodes.Status200OK;
                    return;
                }
                break;

            case ProbeScenario.GetRange00Fallback:
                if (HttpMethods.IsHead(req.Method))
                {
                    res.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }
                var rangeHdr = req.Headers.Range.ToString();
                if (rangeHdr == "bytes=0-0")
                {
                    res.StatusCode = StatusCodes.Status206PartialContent;
                    res.Headers.AcceptRanges = "bytes";
                    res.Headers["Content-Range"] = $"bytes 0-0/{StandardPayload.Length}";
                    res.ContentLength = 1;
                    await res.Body.WriteAsync(StandardPayload.AsMemory(0, 1));
                    return;
                }
                break;

            case ProbeScenario.ChunkedNoLength:
                res.Headers.ContentType = "application/octet-stream";
                res.StatusCode = StatusCodes.Status200OK;
                // No ContentLength header, chunked transfer
                await res.Body.WriteAsync(StandardPayload.AsMemory(0, 1024));
                await res.Body.FlushAsync();
                await Task.Delay(20);
                await res.Body.WriteAsync(StandardPayload.AsMemory(1024, StandardPayload.Length - 1024));
                return;

            case ProbeScenario.GzipCompressed:
                res.Headers["Content-Encoding"] = "gzip";
                res.Headers.ContentType = "text/plain";
                byte[] compressedBytes;
                using (var ms = new MemoryStream())
                {
                    using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                    {
                        await gz.WriteAsync(StandardPayload);
                    }
                    compressedBytes = ms.ToArray();
                }
                res.ContentLength = compressedBytes.Length;
                res.StatusCode = StatusCodes.Status200OK;
                if (!HttpMethods.IsHead(req.Method))
                {
                    await res.Body.WriteAsync(compressedBytes);
                }
                return;

            case ProbeScenario.RedirectChainToFinal:
                if (req.Path == "/redirect1")
                {
                    res.Redirect($"{BaseUrl}/redirect2", permanent: false);
                    return;
                }
                if (req.Path == "/redirect2")
                {
                    res.Redirect($"{BaseUrl}/final-target.bin", permanent: false);
                    return;
                }
                break;

            case ProbeScenario.RelativeRedirect:
                if (req.Path == "/rel-start")
                {
                    res.Redirect("/final-target.bin", permanent: false);
                    return;
                }
                break;

            case ProbeScenario.CookieAuthRequired:
                var cookie = req.Cookies["qa_session"];
                if (string.IsNullOrEmpty(cookie) || cookie != "valid_token_123")
                {
                    res.StatusCode = StatusCodes.Status403Forbidden;
                    await res.WriteAsync("Forbidden: Missing cookie");
                    return;
                }
                break;

            case ProbeScenario.LargeFile5Gb:
                const long fiveGb = 5_000_000_000L;
                res.Headers.AcceptRanges = "bytes";
                if (HttpMethods.IsHead(req.Method))
                {
                    res.ContentLength = fiveGb;
                    res.StatusCode = StatusCodes.Status200OK;
                    return;
                }
                var range5g = req.Headers.Range.ToString();
                if (range5g == "bytes=0-0")
                {
                    res.StatusCode = StatusCodes.Status206PartialContent;
                    res.Headers["Content-Range"] = $"bytes 0-0/{fiveGb}";
                    res.ContentLength = 1;
                    await res.Body.WriteAsync(new byte[] { 0x42 });
                    return;
                }
                res.ContentLength = fiveGb;
                res.StatusCode = StatusCodes.Status200OK;
                return;

            case ProbeScenario.ZeroBytes:
                res.Headers.AcceptRanges = "bytes";
                res.ContentLength = 0;
                res.StatusCode = StatusCodes.Status200OK;
                return;

            case ProbeScenario.ContentLengthWrong:
                // Claims 10 MB, but real payload is 512 KB
                res.Headers.AcceptRanges = "bytes";
                res.ContentLength = 10 * 1024 * 1024;
                res.StatusCode = StatusCodes.Status200OK;
                if (!HttpMethods.IsHead(req.Method))
                {
                    await res.Body.WriteAsync(StandardPayload);
                }
                return;

            case ProbeScenario.SizeChangesMidDownload:
                // Alter ETag and size on second call
                res.Headers["ETag"] = "\"swiftget-etag-altered\"";
                res.ContentLength = StandardPayload.Length + 4096;
                res.StatusCode = StatusCodes.Status200OK;
                return;

            case ProbeScenario.Burst429ThenSuccess:
                if (_remaining429Count > 0)
                {
                    _remaining429Count--;
                    res.StatusCode = StatusCodes.Status429TooManyRequests;
                    res.Headers["Retry-After"] = "1";
                    await res.WriteAsync("Too many requests; back off.");
                    return;
                }
                break;

            case ProbeScenario.ExpiringLink:
                if (DateTimeOffset.UtcNow > ExpireAtUtc)
                {
                    res.StatusCode = StatusCodes.Status410Gone;
                    await res.WriteAsync("Link has expired.");
                    return;
                }
                break;

            case ProbeScenario.TrickyContentDispositionRfc5987:
                res.Headers.AcceptRanges = "bytes";
                res.Headers["Content-Disposition"] = "attachment; filename=\"fallback.bin\"; filename*=UTF-8''%E4%BC%81%E4%B8%9A%E7%BA%A7%E6%96%87%E4%BB%B6.bin";
                res.ContentLength = StandardPayload.Length;
                res.StatusCode = StatusCodes.Status200OK;
                if (!HttpMethods.IsHead(req.Method))
                {
                    await res.Body.WriteAsync(StandardPayload);
                }
                return;

            case ProbeScenario.EarlyCloseTruncated:
                res.Headers.AcceptRanges = "bytes";
                res.StatusCode = StatusCodes.Status206PartialContent;
                res.Headers["Content-Range"] = $"bytes 0-{StandardPayload.Length - 1}/{StandardPayload.Length}";
                res.ContentLength = StandardPayload.Length;
                // Intentionally write only EarlyCloseByteLimit bytes and abort
                var sendCount = Math.Min(EarlyCloseByteLimit, StandardPayload.Length);
                await res.Body.WriteAsync(StandardPayload.AsMemory(0, sendCount));
                await res.Body.FlushAsync();
                context.Abort();
                return;

            case ProbeScenario.Returns200InsteadOf206OnRange:
                // Ignores Range header completely, sends 200 OK with full file
                res.StatusCode = StatusCodes.Status200OK;
                res.ContentLength = StandardPayload.Length;
                if (!HttpMethods.IsHead(req.Method))
                {
                    await res.Body.WriteAsync(StandardPayload);
                }
                return;

            case ProbeScenario.WrongContentRangeHeader:
                res.StatusCode = StatusCodes.Status206PartialContent;
                // Corrupt content-range that doesn't match requested range
                res.Headers["Content-Range"] = "bytes 0-10/9999999";
                res.ContentLength = 11;
                await res.Body.WriteAsync(StandardPayload.AsMemory(0, 11));
                return;

            case ProbeScenario.ChangingEtagMidDownload:
                var clientEtag = req.Headers.IfRange.ToString();
                if (!string.IsNullOrWhiteSpace(clientEtag) && !clientEtag.Contains("altered"))
                {
                    // Server file was updated mid-download
                    res.Headers["ETag"] = "\"swiftget-etag-altered\"";
                    res.StatusCode = StatusCodes.Status200OK; // If-Range failed, return full new file
                    res.ContentLength = StandardPayload.Length;
                    await res.Body.WriteAsync(StandardPayload);
                    return;
                }
                break;
        }

        // Standard Handler
        res.Headers.AcceptRanges = "bytes";
        res.Headers["Content-Disposition"] = "attachment; filename=\"final-target.bin\"";

        if (HttpMethods.IsHead(req.Method))
        {
            res.ContentLength = StandardPayload.Length;
            res.StatusCode = StatusCodes.Status200OK;
            return;
        }

        var range = req.Headers.Range.ToString();
        if (string.IsNullOrWhiteSpace(range) || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            res.ContentLength = StandardPayload.Length;
            res.StatusCode = StatusCodes.Status200OK;
            await res.Body.WriteAsync(StandardPayload);
            return;
        }

        var spec = range["bytes=".Length..];
        var parts = spec.Split('-');
        var start = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var end = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
            ? long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
            : StandardPayload.Length - 1;

        if (start >= StandardPayload.Length || end >= StandardPayload.Length || start > end)
        {
            res.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            res.Headers["Content-Range"] = $"bytes */{StandardPayload.Length}";
            return;
        }

        var length = (int)(end - start + 1);
        res.StatusCode = StatusCodes.Status206PartialContent;
        res.Headers["Content-Range"] = $"bytes {start}-{end}/{StandardPayload.Length}";
        res.ContentLength = length;

        if (CurrentScenario == ProbeScenario.ThrottledPerConnection && PerConnectionBps > 0)
        {
            var chunkLen = Math.Min(4096, length);
            var delayMs = (int)Math.Max(10, (chunkLen * 1000.0) / PerConnectionBps);
            var offset = (int)start;
            var remaining = length;
            while (remaining > 0)
            {
                var toWrite = Math.Min(chunkLen, remaining);
                await res.Body.WriteAsync(StandardPayload.AsMemory(offset, toWrite));
                await res.Body.FlushAsync();
                offset += toWrite;
                remaining -= toWrite;
                if (remaining > 0)
                {
                    await Task.Delay(delayMs);
                }
            }
        }
        else
        {
            await res.Body.WriteAsync(StandardPayload.AsMemory((int)start, length));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}
