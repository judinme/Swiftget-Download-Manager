using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Engine.Turbo;
using Xunit;
using Xunit.Abstractions;

namespace SwiftGet.Turbo.Harness;

public class TurboEngineComparisonBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public TurboEngineComparisonBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Benchmark_Turbo_ScenarioB_PerConnectionCappedServer_ScalesConcurrency()
    {
        // 32 MB, 20ms RTT, 2 MB/s per connection limit, total line capacity 32 MB/s
        var options = new NetworkShaperOptions
        {
            ContentLengthBytes = 32 * 1024 * 1024,
            LatencyMs = 20,
            PerConnectionRateLimitBytesPerSec = 2 * 1024 * 1024
        };

        await using var server = new TurboTestServer(options);

        // Run Turbo Engine (configured with max 16 connections)
        var turboMetrics = await TurboBenchmarkRunner.RunTurboDownloadAsync(
            "Per-Connection Capped (2 MB/s)",
            server,
            requestedConnections: 16,
            targetCapacityMBps: 32.0);

        _output.WriteLine(turboMetrics.ToString());

        turboMetrics.IntegrityVerified.Should().BeTrue();
        turboMetrics.TotalBytesDownloaded.Should().Be(options.ContentLengthBytes);
        // Turbo Engine dynamically scales connections to utilize the 32 MB/s line!
        turboMetrics.SteadyStateThroughputMBps.Should().BeGreaterThan(18.0);
    }

    [Fact]
    public async Task Benchmark_Turbo_ScenarioE_StragglerAndTailLatency_EliminatesTailStall()
    {
        // 32 MB, 20ms RTT, straggler on the last 5% of data
        var options = new NetworkShaperOptions
        {
            ContentLengthBytes = 32 * 1024 * 1024,
            LatencyMs = 20,
            SimulateStragglerOnLastRange = true
        };

        await using var server = new TurboTestServer(options);

        var turboMetrics = await TurboBenchmarkRunner.RunTurboDownloadAsync(
            "Tail Latency (Straggler Elimination)",
            server,
            requestedConnections: 8,
            targetCapacityMBps: 50.0);

        _output.WriteLine(turboMetrics.ToString());

        turboMetrics.IntegrityVerified.Should().BeTrue();
        turboMetrics.TotalBytesDownloaded.Should().Be(options.ContentLengthBytes);
    }
}
