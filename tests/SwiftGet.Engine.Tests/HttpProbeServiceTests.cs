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

    [Fact]
    public async Task ProbeAsync_WhenHeadReturns405_FallsBackToRange0AndSucceeds()
    {
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.Method == HttpMethod.Head)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.MethodNotAllowed));
            }

            if (req.Method == HttpMethod.Get && req.Headers.Range != null)
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent);
                resp.Content = new ByteArrayContent(new byte[] { 0x42 });
                resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 0, 1048576);
                resp.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                {
                    FileName = "fallback_video.mp4"
                };
                return Task.FromResult(resp);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest));
        });

        using var client = new HttpClient(handler);
        var probe = new HttpProbeService(client);
        var result = await probe.ProbeAsync("https://cdn.example.com/fallback_video.mp4");

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(1048576);
        result.SupportsRanges.Should().BeTrue();
        result.SuggestedFileName.Should().Be("fallback_video.mp4");
    }

    [Fact]
    public async Task ProbeAsync_WhenServerReturnsNoContentLength_ShouldReportUnknownSize()
    {
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            resp.Content = new StringContent("streaming live content without length");
            resp.Content.Headers.ContentLength = null; // chunked/stream
            return Task.FromResult(resp);
        });

        using var client = new HttpClient(handler);
        var probe = new HttpProbeService(client);
        var result = await probe.ProbeAsync("https://live.example.com/stream");

        result.IsSuccess.Should().BeTrue();
        result.TotalBytes.Should().Be(-1);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
