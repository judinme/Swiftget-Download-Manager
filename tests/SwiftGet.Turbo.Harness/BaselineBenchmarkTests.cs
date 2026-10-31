using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SwiftGet.Turbo.Harness;

public class BaselineBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public BaselineBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Benchmark_ScenarioA_LocalHighSpeedLink_Unconstrained()
    {
        // 64 MB, 1ms RTT, unlimited server & link
        var options = new NetworkShaperOptions
        {
            ContentLengthBytes = 64 * 1024 * 1024,
            LatencyMs = 1,
            PerConnectionRateLimitBytesPerSec = 0,
            PerIpRateLimitBytesPerSec = 0
        };

        await using var server = new TurboTestServer(options);
        var metrics = await TurboBenchmarkRunner.RunBaselineDownloadAsync(
            "Local Link (Unconstrained)",
            server,
            requestedConnections: 8,
            targetCapacityMBps: 100.0);

        _output.WriteLine(metrics.ToString());
        metrics.IntegrityVerified.Should().BeTrue();
        metrics.TotalBytesDownloaded.Should().Be(options.ContentLengthBytes);
    }

    [Fact]
    public async Task Benchmark_ScenarioB_PerConnectionCappedServer()
    {
        // 32 MB, 20ms RTT, 2 MB/s per connection limit, total line capacity 32 MB/s
        var options = new NetworkShaperOptions
        {
            ContentLengthBytes = 32 * 1024 * 1024,
            LatencyMs = 20,
            PerConnectionRateLimitBytesPerSec = 2 * 1024 * 1024 // 2 MB/s per connection
        };

        await using var server = new TurboTestServer(options);
        var metrics = await TurboBenchmarkRunner.RunBaselineDownloadAsync(
            "Per-Connection Capped (2 MB/s)",
            server,
            requestedConnections: 8, // With 8 conns, max achievable is 16 MB/s on a 32 MB/s line!
            targetCapacityMBps: 32.0);

        _output.WriteLine(metrics.ToString());
        metrics.IntegrityVerified.Should().BeTrue();
        // Baseline with 8 connections only reaches ~50% of the 32 MB/s link!
        metrics.SteadyStateThroughputMBps.Should().BeLessThanOrEqualTo(18.0);
    }

    [Fact]
    public async Task Benchmark_ScenarioC_SharedBottleneck_CongestedLink()
    {
        // 32 MB, 30ms RTT, 12 MB/s aggregate bottleneck
        var options = new NetworkShaperOptions
        {
            ContentLengthBytes = 32 * 1024 * 1024,
            LatencyMs = 30,
            PerConnectionRateLimitBytesPerSec = 12 * 1024 * 1024 // Shared line limit
        };

        await using var server = new TurboTestServer(options);
        var metrics = await TurboBenchmarkRunner.RunBaselineDownloadAsync(
            "Shared Link Bottleneck (12 MB/s)",
            server,
            requestedConnections: 8,
            targetCapacityMBps: 12.0);

        _output.WriteLine(metrics.ToString());
        metrics.IntegrityVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Benchmark_ScenarioD_HighLatencyLink_WAN()
    {
        // 32 MB, 150ms RTT, 40 MB/s link
        var options = new NetworkShaperOptions
        {
            ContentLengthBytes = 32 * 1024 * 1024,
            LatencyMs = 150
        };

        await using var server = new TurboTestServer(options);
        var metrics = await TurboBenchmarkRunner.RunBaselineDownloadAsync(
            "High Latency WAN (150ms RTT)",
            server,
            requestedConnections: 8,
            targetCapacityMBps: 40.0);

        _output.WriteLine(metrics.ToString());
        metrics.IntegrityVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Benchmark_ScenarioE_StragglerAndTailLatency()
    {
        // 32 MB, 20ms RTT, straggler on the last 5% of data
        var options = new NetworkShaperOptions
        {
            ContentLengthBytes = 32 * 1024 * 1024,
            LatencyMs = 20,
            SimulateStragglerOnLastRange = true
        };

        await using var server = new TurboTestServer(options);
        var metrics = await TurboBenchmarkRunner.RunBaselineDownloadAsync(
            "Tail Latency (Straggler Connection)",
            server,
            requestedConnections: 8,
            targetCapacityMBps: 50.0);

        _output.WriteLine(metrics.ToString());
        metrics.IntegrityVerified.Should().BeTrue();
        // Tail time should show significant degradation
        metrics.TailTimePercent.Should().BeGreaterThan(10.0);
    }
}
