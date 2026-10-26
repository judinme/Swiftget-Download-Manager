using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace SwiftGet.Engine.Tests;

public class TestDownloadServer : IAsyncDisposable
{
    private readonly IHost _host;
    public byte[] Payload { get; }
    public string Sha256Hash { get; }
    public string ETag { get; } = "\"test-etag-v1\"";
    public HttpClient Client { get; }

    public TestDownloadServer(int payloadSize = 1024 * 1024)
    {
        Payload = new byte[payloadSize];
        Random.Shared.NextBytes(Payload);

        Sha256Hash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.Configure(app =>
                {
                    app.Run(async context =>
                    {
                        var req = context.Request;
                        var res = context.Response;

                        res.Headers.AcceptRanges = "bytes";
                        res.Headers.ETag = ETag;
                        res.Headers["Content-Disposition"] = "attachment; filename=\"test_payload.bin\"";

                        if (HttpMethods.IsHead(req.Method))
                        {
                            res.ContentLength = Payload.Length;
                            res.StatusCode = StatusCodes.Status200OK;
                            return;
                        }

                        if (!HttpMethods.IsGet(req.Method))
                        {
                            res.StatusCode = StatusCodes.Status405MethodNotAllowed;
                            return;
                        }

                        // Check Range header
                        var rangeHeader = req.Headers.Range.ToString();
                        if (string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            res.ContentLength = Payload.Length;
                            res.StatusCode = StatusCodes.Status200OK;
                            await res.Body.WriteAsync(Payload);
                            return;
                        }

                        // Parse range
                        var rangeSpec = rangeHeader["bytes=".Length..];
                        var parts = rangeSpec.Split('-');
                        var start = long.Parse(parts[0], CultureInfo.InvariantCulture);
                        var end = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                            ? long.Parse(parts[1], CultureInfo.InvariantCulture)
                            : Payload.Length - 1;

                        if (start >= Payload.Length || end >= Payload.Length || start > end)
                        {
                            res.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                            res.Headers["Content-Range"] = $"bytes */{Payload.Length}";
                            return;
                        }

                        var length = (int)(end - start + 1);
                        res.StatusCode = StatusCodes.Status206PartialContent;
                        res.Headers["Content-Range"] = $"bytes {start}-{end}/{Payload.Length}";
                        res.ContentLength = length;

                        await res.Body.WriteAsync(Payload.AsMemory((int)start, length));
                    });
                });
            });

        _host = builder.Start();
        Client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}
