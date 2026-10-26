using FluentAssertions;
using SwiftGet.Engine.Probing;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class HttpProbeServiceTests : IAsyncLifetime
{
    private TestDownloadServer? _server;

    public Task InitializeAsync()
    {
        _server = new TestDownloadServer(payloadSize: 65536);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProbeAsync_StandardHead_ShouldExtractAllMetadata()
    {
        var probe = new HttpProbeService(_server!.Client);
        var result = await probe.ProbeAsync("http://localhost/test_payload.bin");

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(65536);
        result.SupportsRanges.Should().BeTrue();
        result.SuggestedFileName.Should().Be("test_payload.bin");
        result.ETag.Should().Be("test-etag-v1");
    }
}
