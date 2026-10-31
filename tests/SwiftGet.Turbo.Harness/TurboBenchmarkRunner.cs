using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Coordinator;

namespace SwiftGet.Turbo.Harness;

/// <summary>
/// Executes benchmarks against test servers and collects fine-grained telemetry
/// comparing the legacy baseline engine against the SwiftGet Turbo Engine.
/// </summary>
public static class TurboBenchmarkRunner
{
    public static Task<TurboBenchmarkMetrics> RunBaselineDownloadAsync(
        string scenarioName,
        TurboTestServer server,
        int requestedConnections = 8,
        double targetCapacityMBps = 0)
    {
        return RunDownloadInternalAsync(scenarioName, "Baseline (Current)", useTurbo: false, server, requestedConnections, targetCapacityMBps);
    }

    public static Task<TurboBenchmarkMetrics> RunTurboDownloadAsync(
        string scenarioName,
        TurboTestServer server,
        int requestedConnections = 16,
        double targetCapacityMBps = 0)
    {
        return RunDownloadInternalAsync(scenarioName, "SwiftGet Turbo Engine", useTurbo: true, server, requestedConnections, targetCapacityMBps);
    }

    private static async Task<TurboBenchmarkMetrics> RunDownloadInternalAsync(
        string scenarioName,
        string engineVariant,
        bool useTurbo,
        TurboTestServer server,
        int requestedConnections,
        double targetCapacityMBps)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"turbo_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "bench.db");
        var destPath = Path.Combine(tempDir, "downloaded.bin");

        try
        {
            await using var db = new SqliteDatabase(dbPath);
            await db.InitializeAsync();

            using var httpClient = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                EnableMultipleHttp2Connections = true
            });

            var coordinator = new DownloadCoordinator(httpClient, db)
            {
                UseTurboEngine = useTurbo,
                ProgressReportInterval = TimeSpan.FromMilliseconds(10)
            };

            var item = new DownloadItem
            {
                Url = server.DownloadUrl,
                DestinationPath = destPath,
                FileName = Path.GetFileName(destPath),
                TotalBytes = server.ContentLength
            };

            var startCpu = Process.GetCurrentProcess().TotalProcessorTime;
            var startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();

            var sw = Stopwatch.StartNew();
            double ttfbMs = 0;
            double rampUpTimeSec = 0;
            double timeAt95PercentSec = 0;
            long lastBytes = 0;

            var target90Throughput = targetCapacityMBps > 0 ? (targetCapacityMBps * 1024 * 1024 * 0.90) : 0;
            var ninetyPercentThreshold = server.ContentLength * 0.90;
            var ninetyFivePercentThreshold = server.ContentLength * 0.95;

            var progressReporter = new Progress<long>(current =>
            {
                if (ttfbMs == 0 && current > 0)
                {
                    ttfbMs = sw.Elapsed.TotalMilliseconds;
                }

                var elapsedSec = sw.Elapsed.TotalSeconds;
                if (elapsedSec > 0.05)
                {
                    var instantRate = (double)(current - lastBytes) / 0.05;
                    if (rampUpTimeSec == 0 && target90Throughput > 0 && instantRate >= target90Throughput)
                    {
                        rampUpTimeSec = elapsedSec;
                    }
                }
                lastBytes = current;

                if (timeAt95PercentSec == 0 && current >= ninetyFivePercentThreshold)
                {
                    timeAt95PercentSec = sw.Elapsed.TotalSeconds;
                }
            });

            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            await coordinator.StartAsync(
                item,
                maxConnections: requestedConnections,
                progress: progressReporter,
                ct: cts.Token);

            sw.Stop();
            cts.Cancel();

            var endCpu = Process.GetCurrentProcess().TotalProcessorTime;
            var endAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();

            var totalDuration = sw.Elapsed;
            var totalBytes = new FileInfo(destPath).Length;
            var steadyThroughput = totalDuration.TotalSeconds > 0
                ? (totalBytes / (1024.0 * 1024.0)) / totalDuration.TotalSeconds
                : 0;

            if (rampUpTimeSec == 0)
            {
                rampUpTimeSec = totalDuration.TotalSeconds * 0.90;
            }

            double tailTimeSec = timeAt95PercentSec > 0
                ? Math.Max(0, totalDuration.TotalSeconds - timeAt95PercentSec)
                : (totalDuration.TotalSeconds * 0.05);

            using var sha = SHA256.Create();
            using var fileStream = File.OpenRead(destPath);
            var computedHashHex = Convert.ToHexString(sha.ComputeHash(fileStream));
            var integrityMatch = string.Equals(computedHashHex, server.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase);

            var cpuUsagePercent = totalDuration.TotalMilliseconds > 0
                ? Math.Round(((endCpu - startCpu).TotalMilliseconds / (totalDuration.TotalMilliseconds * Environment.ProcessorCount)) * 100.0, 1)
                : 0.0;

            var allocatedMb = Math.Round((double)(endAllocatedBytes - startAllocatedBytes) / (1024.0 * 1024.0), 2);

            return new TurboBenchmarkMetrics
            {
                ScenarioName = scenarioName,
                EngineVariant = engineVariant,
                TotalBytesDownloaded = totalBytes,
                TotalDuration = totalDuration,
                TimeToFirstByteMs = ttfbMs,
                RampUpTimeTo90PercentSec = Math.Round(rampUpTimeSec, 2),
                SteadyStateThroughputMBps = Math.Round(steadyThroughput, 2),
                TargetCapacityMBps = targetCapacityMBps > 0 ? targetCapacityMBps : Math.Round(steadyThroughput, 2),
                TailTimeSeconds = Math.Round(tailTimeSec, 2),
                PeakConnections = requestedConnections,
                CpuPercent = cpuUsagePercent,
                AllocatedMemoryMB = allocatedMb,
                IntegrityVerified = integrityMatch,
                Sha256Hash = computedHashHex
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }
    }
}
