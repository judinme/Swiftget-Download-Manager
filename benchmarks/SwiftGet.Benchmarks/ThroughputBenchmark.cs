using System.Diagnostics;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Coordinator;

namespace SwiftGet.Benchmarks;

[MemoryDiagnoser]
public class ThroughputBenchmark
{
    private string _tempDir = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;
    private SqliteDatabase _db = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // 32 MB payload
        _payload = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(_payload);

        _host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        var res = ctx.Response;
                        res.Headers.AcceptRanges = "bytes";
                        res.Headers.ETag = "\"bench-etag\"";

                        if (HttpMethods.IsHead(ctx.Request.Method))
                        {
                            res.ContentLength = _payload.Length;
                            res.StatusCode = StatusCodes.Status200OK;
                            return;
                        }

                        var rangeHeader = ctx.Request.Headers.Range.ToString();
                        if (rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = rangeHeader["bytes=".Length..].Split('-');
                            var start = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                            var end = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : _payload.Length - 1;
                            var len = end - start + 1;
                            res.StatusCode = StatusCodes.Status206PartialContent;
                            res.Headers["Content-Range"] = $"bytes {start}-{end}/{_payload.Length}";
                            res.ContentLength = len;
                            await res.Body.WriteAsync(_payload.AsMemory(start, len));
                            return;
                        }

                        res.StatusCode = StatusCodes.Status200OK;
                        res.ContentLength = _payload.Length;
                        await res.Body.WriteAsync(_payload);
                    });
                });
            })
            .Start();

        _client = _host.GetTestClient();
        _db = new SqliteDatabase(Path.Combine(_tempDir, "bench.db"));
        _db.InitializeAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        _db.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Benchmark]
    public async Task<long> Download32Mb_MultiSegment()
    {
        var dest = Path.Combine(_tempDir, $"bench_out_{Guid.NewGuid():N}.bin");
        var item = new DownloadItem
        {
            Url = "http://localhost/payload.bin",
            DestinationPath = dest,
            FileName = Path.GetFileName(dest)
        };

        var coordinator = new DownloadCoordinator(_client, _db);
        await coordinator.StartAsync(item, maxConnections: 8);

        var len = new FileInfo(dest).Length;
        try { File.Delete(dest); } catch { }
        return len;
    }
}
