using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Probing;
using SwiftGet.Engine.Tests.Harness;
using Xunit;

namespace SwiftGet.Engine.Tests.Probing;

public sealed class HttpProbeServiceMatrixTests : IAsyncLifetime
{
    private KestrelTestServer _server = null!;
    private HttpClient _httpClient = null!;
    private HttpProbeService _probeService = null!;

    public Task InitializeAsync()
    {
        _server = new KestrelTestServer();
        _httpClient = new HttpClient();
        _probeService = new HttpProbeService(_httpClient);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _httpClient.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Scenario_StandardRange200_ShouldProbeTotalBytesAndRanges()
    {
        _server.CurrentScenario = ProbeScenario.StandardRange200;
        var url = $"{_server.BaseUrl}/file.bin";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(_server.StandardPayload.Length);
        result.SupportsRanges.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario_HeadReturns405_ShouldFallbackToRange00()
    {
        _server.CurrentScenario = ProbeScenario.HeadReturns405;
        var url = $"{_server.BaseUrl}/file.bin";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(_server.StandardPayload.Length);
        result.SupportsRanges.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario_HeadReturns403_ShouldFallbackToRange00()
    {
        _server.CurrentScenario = ProbeScenario.HeadReturns403;
        var url = $"{_server.BaseUrl}/file.bin";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(_server.StandardPayload.Length);
        result.SupportsRanges.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario_HeadWithoutContentLength_ShouldFallbackToRange00()
    {
        _server.CurrentScenario = ProbeScenario.HeadWithoutContentLength;
        var url = $"{_server.BaseUrl}/file.bin";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(_server.StandardPayload.Length);
    }

    [Fact]
    public async Task Scenario_GetRange00Fallback_ShouldParseContentRange()
    {
        _server.CurrentScenario = ProbeScenario.GetRange00Fallback;
        var url = $"{_server.BaseUrl}/file.bin";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(_server.StandardPayload.Length);
        result.SupportsRanges.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario_ChunkedNoLength_ShouldReturnNegativeOneTotalBytes()
    {
        _server.CurrentScenario = ProbeScenario.ChunkedNoLength;
        var url = $"{_server.BaseUrl}/stream";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(-1);
    }

    [Fact]
    public async Task Scenario_GzipCompressed_ShouldHandleDecompression()
    {
        _server.CurrentScenario = ProbeScenario.GzipCompressed;
        var url = $"{_server.BaseUrl}/file.gz";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Scenario_RedirectChainToFinal_ShouldResolveFinalUrl()
    {
        _server.CurrentScenario = ProbeScenario.RedirectChainToFinal;
        var url = $"{_server.BaseUrl}/redirect1";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.FinalUrl.Should().EndWith("/final-target.bin");
        result.TotalBytes.Should().Be(_server.StandardPayload.Length);
    }

    [Fact]
    public async Task Scenario_RelativeRedirect_ShouldResolveFinalUrl()
    {
        _server.CurrentScenario = ProbeScenario.RelativeRedirect;
        var url = $"{_server.BaseUrl}/rel-start";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.FinalUrl.Should().EndWith("/final-target.bin");
    }

    [Fact]
    public async Task Scenario_CookieAuthRequired_WithCookie_ShouldSucceed()
    {
        _server.CurrentScenario = ProbeScenario.CookieAuthRequired;
        var url = $"{_server.BaseUrl}/protected";

        var resultWithCookie = await _probeService.ProbeAsync(url, cookies: "qa_session=valid_token_123");
        resultWithCookie.IsSuccess.Should().BeTrue();

        var resultWithoutCookie = await _probeService.ProbeAsync(url);
        resultWithoutCookie.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Scenario_LargeFile5Gb_ShouldSupportAbove4GbWithoutIntOverflow()
    {
        _server.CurrentScenario = ProbeScenario.LargeFile5Gb;
        var url = $"{_server.BaseUrl}/bigfile.iso";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        const long expected5Gb = 5_000_000_000L;
        result.TotalBytes.Should().Be(expected5Gb);
    }

    [Fact]
    public async Task Scenario_ZeroBytes_ShouldReturnZeroTotalBytes()
    {
        _server.CurrentScenario = ProbeScenario.ZeroBytes;
        var url = $"{_server.BaseUrl}/empty.bin";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(0);
    }

    [Fact]
    public async Task Scenario_ContentLengthWrong_ShouldProbeWhatServerReported()
    {
        _server.CurrentScenario = ProbeScenario.ContentLengthWrong;
        var url = $"{_server.BaseUrl}/wrong.bin";

        var result = await _probeService.ProbeAsync(url);

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(10 * 1024 * 1024);
    }
}
