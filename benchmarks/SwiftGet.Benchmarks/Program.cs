using System.Diagnostics;
using BenchmarkDotNet.Running;

namespace SwiftGet.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--throughput")
        {
            BenchmarkRunner.Run<ThroughputBenchmark>();
        }
        else if (args.Length > 0 && args[0] == "--torrent")
        {
            BenchmarkRunner.Run<TorrentThroughputBenchmark>();
        }
        else if (args.Length > 0 && args[0] == "--magnet")
        {
            BenchmarkRunner.Run<MagnetParserBenchmark>();
        }
        else if (args.Length > 0 && args[0] == "--smoke")
        {
            RunSmokeSuite();
        }
        else if (args.Length > 0)
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
        else
        {
            Console.WriteLine("SwiftGet Benchmark Suite ready.");
            Console.WriteLine("Options: --smoke, --throughput, --torrent, --magnet, or BenchmarkDotNet filters (e.g. --filter *).");
        }
    }

    private static void RunSmokeSuite()
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine(" [SwiftGet] Performance Benchmark Smoke Suite             ");
        Console.WriteLine("==========================================================");

        // 1. HTTP Throughput Smoke
        try
        {
            var httpBench = new ThroughputBenchmark();
            httpBench.Setup();
            var swHttp = Stopwatch.StartNew();
            var httpBytes = httpBench.Download32Mb_MultiSegment().GetAwaiter().GetResult();
            swHttp.Stop();
            httpBench.Cleanup();

            var httpMb = httpBytes / (1024.0 * 1024.0);
            var httpMbPerSec = httpMb / swHttp.Elapsed.TotalSeconds;
            var httpMbps = httpMbPerSec * 8.0;
            Console.WriteLine($"[HTTP Smoke] Downloaded {httpBytes:N0} bytes (32 MB) in {swHttp.Elapsed.TotalMilliseconds:F1}ms => {httpMbPerSec:F1} MB/s ({httpMbps:F1} Mbps)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HTTP Smoke] Failed: {ex.Message}");
        }

        // 2. Torrent Loopback Swarm Throughput Smoke
        try
        {
            var torrentBench = new TorrentThroughputBenchmark();
            var result = torrentBench.RunSmoke();
            Console.WriteLine($"[Torrent Smoke] Downloaded {result.Bytes:N0} bytes (16 MB) in {result.ElapsedSeconds * 1000.0:F1}ms => {result.MbPerSec:F1} MB/s ({result.Mbps:F1} Mbps)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Torrent Smoke] Failed: {ex.Message}");
        }

        // 3. Magnet Parser Latency Smoke
        try
        {
            var parserBench = new MagnetParserBenchmark();
            // Warm-up
            for (int i = 0; i < 1000; i++)
            {
                parserBench.ParseStandardHex();
                parserBench.ParseComplexMultiTracker();
            }

            const int iterations = 50000;
            var swMagnet = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                parserBench.ParseComplexMultiTracker();
            }
            swMagnet.Stop();

            var totalUs = swMagnet.Elapsed.TotalMicroseconds;
            var usPerOp = totalUs / iterations;
            Console.WriteLine($"[Magnet Parser Smoke] {iterations:N0} complex parses in {swMagnet.Elapsed.TotalMilliseconds:F1}ms => {usPerOp:F2} µs/op");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Magnet Parser Smoke] Failed: {ex.Message}");
        }

        Console.WriteLine("==========================================================");
    }
}
