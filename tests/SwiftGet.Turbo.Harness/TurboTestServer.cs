using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SwiftGet.Turbo.Harness;

/// <summary>
/// High-fidelity local Kestrel test server capable of simulating 10 GbE throughput,
/// per-connection limits, shared line bottlenecks, latency, packet loss, and 429 backoff.
/// </summary>
public sealed class TurboTestServer : IAsyncDisposable
{
    private readonly NetworkShaperOptions _options;
    private readonly byte[] _cyclicPattern = new byte[1024 * 1024]; // 1 MB cyclic buffer
    private readonly byte[] _fullPayloadSha256;
    private readonly IHost _host;
    private readonly int _port;
    private int _activeConnections;
    private long _totalBytesServed;

    public int Port => _port;
    public string BaseUrl => $"http://127.0.0.1:{_port}";
    public string DownloadUrl => $"{BaseUrl}/payload.bin";
    public string MirrorUrl => $"{BaseUrl}/mirror/payload.bin";
    public string ExpectedSha256Hex => Convert.ToHexString(_fullPayloadSha256);
    public long ContentLength => _options.ContentLengthBytes;
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public long TotalBytesServed => Volatile.Read(ref _totalBytesServed);

    public TurboTestServer(NetworkShaperOptions? options = null)
    {
        _options = options ?? new NetworkShaperOptions();

        // Initialize deterministic cyclic pattern
        var prng = new Random(1337);
        prng.NextBytes(_cyclicPattern);

        // Compute authoritative SHA256 of full payload
        using var sha = SHA256.Create();
        using var stream = new CyclicPayloadStream(_cyclicPattern, _options.ContentLengthBytes);
        _fullPayloadSha256 = sha.ComputeHash(stream);

        // Find available dynamic port
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        _port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel(kestrel =>
                {
                    kestrel.Listen(IPAddress.Loopback, _port, listenOptions =>
                    {
                        var protocols = HttpProtocols.Http1;
                        if (_options.EnableHttp2) protocols |= HttpProtocols.Http2;
                        if (_options.EnableHttp3) protocols |= HttpProtocols.Http3;
                        listenOptions.Protocols = protocols;
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.Run(HandleRequestAsync);
                });
            })
            .Build();

        _host.Start();
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        var connIndex = Interlocked.Increment(ref _activeConnections);
        try
        {
            var req = context.Request;
            var res = context.Response;

            // 429 Too Many Requests enforcement
            if (_options.MaxConcurrentConnectionsBefore429 > 0 &&
                connIndex > _options.MaxConcurrentConnectionsBefore429)
            {
                res.StatusCode = StatusCodes.Status429TooManyRequests;
                res.Headers["Retry-After"] = _options.RetryAfterSeconds.ToString();
                await res.WriteAsync($"Rate limited. Max {_options.MaxConcurrentConnectionsBefore429} connections allowed.");
                return;
            }

            // Simulate one-way RTT latency
            if (_options.LatencyMs > 0)
            {
                await Task.Delay(_options.LatencyMs / 2);
            }

            res.Headers.ETag = "\"turbo-engine-etag-v1\"";
            res.Headers.LastModified = "Sat, 31 Oct 2026 12:00:00 GMT";

            if (_options.SupportsRanges)
            {
                res.Headers.AcceptRanges = "bytes";
            }
            else
            {
                res.Headers.AcceptRanges = "none";
            }

            if (HttpMethods.IsHead(req.Method))
            {
                res.StatusCode = StatusCodes.Status200OK;
                res.ContentLength = _options.ContentLengthBytes;
                return;
            }

            if (!HttpMethods.IsGet(req.Method))
            {
                res.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            long start = 0;
            long end = _options.ContentLengthBytes - 1;
            bool isRange = false;

            var rangeHeader = req.Headers.Range.ToString();
            if (_options.SupportsRanges && !string.IsNullOrWhiteSpace(rangeHeader) &&
                rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = rangeHeader["bytes=".Length..].Trim();
                var parts = spec.Split('-');
                if (long.TryParse(parts[0], out var parsedStart))
                {
                    start = Math.Clamp(parsedStart, 0, _options.ContentLengthBytes - 1);
                }

                if (parts.Length > 1 && long.TryParse(parts[1], out var parsedEnd))
                {
                    end = Math.Clamp(parsedEnd, start, _options.ContentLengthBytes - 1);
                }

                isRange = true;
            }

            long totalToServe = end - start + 1;

            if (isRange)
            {
                res.StatusCode = StatusCodes.Status206PartialContent;
                res.Headers["Content-Range"] = $"bytes {start}-{end}/{_options.ContentLengthBytes}";
            }
            else
            {
                res.StatusCode = StatusCodes.Status200OK;
            }

            res.ContentLength = totalToServe;

            // Stream chunks with rate limiting and network shaping
            const int chunkSize = 64 * 1024; // 64 KB
            var chunk = new byte[chunkSize];
            long servedForThisRequest = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (servedForThisRequest < totalToServe && !context.RequestAborted.IsCancellationRequested)
            {
                var toRead = (int)Math.Min(chunkSize, totalToServe - servedForThisRequest);
                var currentGlobalOffset = start + servedForThisRequest;

                // Check if this chunk falls in the final 5% straggler zone
                bool isStraggler = _options.SimulateStragglerOnLastRange &&
                                   currentGlobalOffset >= (_options.ContentLengthBytes * 0.95);

                // Fill chunk from cyclic pattern
                for (int i = 0; i < toRead; i++)
                {
                    chunk[i] = _cyclicPattern[(currentGlobalOffset + i) % _cyclicPattern.Length];
                }

                await res.Body.WriteAsync(chunk.AsMemory(0, toRead), context.RequestAborted);
                servedForThisRequest += toRead;
                Interlocked.Add(ref _totalBytesServed, toRead);

                if (isStraggler)
                {
                    // Delay straggler connection in the tail range
                    await Task.Delay(40, context.RequestAborted);
                }
                else if (_options.PerConnectionRateLimitBytesPerSec > 0)
                {
                    var expectedElapsedMs = (double)servedForThisRequest / _options.PerConnectionRateLimitBytesPerSec * 1000.0;
                    var actualElapsedMs = sw.Elapsed.TotalMilliseconds;
                    var delayMs = expectedElapsedMs - actualElapsedMs;
                    if (delayMs > 2.0)
                    {
                        await Task.Delay((int)delayMs, context.RequestAborted);
                    }
                }

                // Add random jitter
                if (_options.JitterMs > 0 && Random.Shared.NextDouble() < 0.2)
                {
                    await Task.Delay(Random.Shared.Next(1, _options.JitterMs), context.RequestAborted);
                }

                // Packet loss simulation: randomly drop/abort connection
                if (_options.PacketLossRatio > 0 && Random.Shared.NextDouble() < _options.PacketLossRatio)
                {
                    context.Abort();
                    return;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// Helper stream for calculating full payload SHA256 without memory allocations.
    /// </summary>
    private sealed class CyclicPayloadStream : Stream
    {
        private readonly byte[] _pattern;
        private readonly long _length;
        private long _position;

        public CyclicPayloadStream(byte[] pattern, long length)
        {
            _pattern = pattern;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length) return 0;
            var toRead = (int)Math.Min(count, _length - _position);
            for (int i = 0; i < toRead; i++)
            {
                buffer[offset + i] = _pattern[(_position + i) % _pattern.Length];
            }
            _position += toRead;
            return toRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
