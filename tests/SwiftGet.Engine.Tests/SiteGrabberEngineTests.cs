using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Engine.Grabber;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class SiteGrabberEngineTests
{
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Content, string ContentType)> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void Map(string url, string content, string contentType = "text/html")
        {
            _responses[url] = (content, contentType);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (_responses.TryGetValue(url, out var val))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(val.Content, Encoding.UTF8, val.ContentType)
                };
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task CrawlAsync_WithPicturesPreset_ShouldDiscoverImagesAndFilterDocuments()
    {
        var handler = new MockHttpHandler();
        handler.Map("https://example.com/index.html", @"
            <html>
                <body>
                    <a href=""https://example.com/gallery.html"">Gallery</a>
                    <a href=""https://example.com/docs/manual.pdf"">PDF Manual</a>
                    <img src=""https://example.com/images/banner.jpg"" />
                </body>
            </html>
        ");
        handler.Map("https://example.com/gallery.html", @"
            <html>
                <body>
                    <img src=""https://example.com/images/photo1.png"" />
                    <img src=""https://example.com/images/photo2.webp"" />
                    <a href=""https://example.com/archive.zip"">Archive</a>
                </body>
            </html>
        ");

        using var client = new HttpClient(handler);
        var grabber = new SiteGrabberEngine(client);

        var project = new GrabberProject
        {
            StartUrl = "https://example.com/index.html",
            MaxDepth = 2,
            DomainScope = DomainScope.SameDomain,
            FilePreset = FileFilterPreset.Pictures
        };

        var files = await grabber.CrawlAsync(project);

        files.Should().HaveCount(3);
        files.Select(f => f.FileName).Should().BeEquivalentTo(["banner.jpg", "photo1.png", "photo2.webp"]);
    }

    [Fact]
    public async Task CrawlAsync_WithCyclicLinks_ShouldTerminateWithoutInfiniteLoop()
    {
        var handler = new MockHttpHandler();
        handler.Map("https://example.com/pageA.html", @"
            <html><body><a href=""https://example.com/pageB.html"">Go to B</a><img src=""imgA.jpg""/></body></html>
        ");
        handler.Map("https://example.com/pageB.html", @"
            <html><body><a href=""https://example.com/pageA.html"">Go back to A</a><img src=""imgB.jpg""/></body></html>
        ");

        using var client = new HttpClient(handler);
        var grabber = new SiteGrabberEngine(client);

        var project = new GrabberProject
        {
            StartUrl = "https://example.com/pageA.html",
            MaxDepth = 4,
            DomainScope = DomainScope.SameDomain,
            FilePreset = FileFilterPreset.Pictures
        };

        var files = await grabber.CrawlAsync(project);
        files.Should().HaveCount(2);
        files.Select(f => f.FileName).Should().BeEquivalentTo(["imgA.jpg", "imgB.jpg"]);
    }

    [Fact]
    public async Task CrawlAsync_WithSameDomainScope_ShouldIgnoreExternalDomainPages()
    {
        var handler = new MockHttpHandler();
        handler.Map("https://example.com/index.html", @"
            <html><body><a href=""https://external.org/page.html"">External</a><img src=""local.png""/></body></html>
        ");
        handler.Map("https://external.org/page.html", @"
            <html><body><img src=""external.png""/></body></html>
        ");

        using var client = new HttpClient(handler);
        var grabber = new SiteGrabberEngine(client);

        var project = new GrabberProject
        {
            StartUrl = "https://example.com/index.html",
            MaxDepth = 3,
            DomainScope = DomainScope.SameDomain,
            FilePreset = FileFilterPreset.Pictures
        };

        var files = await grabber.CrawlAsync(project);
        files.Should().HaveCount(1);
        files[0].FileName.Should().Be("local.png");
    }

    [Fact]
    public async Task CrawlAsync_WithCustomExtensions_ShouldFilterCorrectly()
    {
        var handler = new MockHttpHandler();
        handler.Map("https://example.com/downloads.html", @"
            <html><body>
                <a href=""file1.iso"">ISO Image</a>
                <a href=""file2.dmg"">Apple DMG</a>
                <a href=""file3.zip"">Zip Archive</a>
            </body></html>
        ");

        using var client = new HttpClient(handler);
        var grabber = new SiteGrabberEngine(client);

        var project = new GrabberProject
        {
            StartUrl = "https://example.com/downloads.html",
            MaxDepth = 1,
            FilePreset = FileFilterPreset.Custom,
            CustomExtensions = ".iso, .dmg"
        };

        var files = await grabber.CrawlAsync(project);
        files.Should().HaveCount(2);
        files.Select(f => f.FileName).Should().BeEquivalentTo(["file1.iso", "file2.dmg"]);
    }

    [Fact]
    public async Task QueryPaginatedPages_AllVisited()
    {
        var handler = new MockHttpHandler();
        handler.Map("https://example.com/items?page=1", @"
            <html><body>
                <a href=""https://example.com/items?page=2"">Next</a>
                <a href=""https://example.com/archive.zip?token=a"">Download Token A</a>
            </body></html>
        ");
        handler.Map("https://example.com/items?page=2", @"
            <html><body>
                <a href=""https://example.com/archive.zip?token=b"">Download Token B</a>
            </body></html>
        ");

        using var client = new HttpClient(handler);
        var grabber = new SiteGrabberEngine(client);

        var visitedUrls = new List<string>();
        grabber.ProgressUpdated += (s, e) =>
        {
            if (e.CurrentUrl.StartsWith("https://example.com/items?page=", StringComparison.OrdinalIgnoreCase))
            {
                visitedUrls.Add(e.CurrentUrl);
            }
        };

        var project = new GrabberProject
        {
            StartUrl = "https://example.com/items?page=1",
            MaxDepth = 3,
            DomainScope = DomainScope.SameDomain,
            FilePreset = FileFilterPreset.Compressed
        };

        var files = await grabber.CrawlAsync(project);

        // Both paginated pages must be visited (not skipped as duplicate)
        visitedUrls.Should().Contain("https://example.com/items?page=1");
        visitedUrls.Should().Contain("https://example.com/items?page=2");

        // Both file variants with distinct query tokens must be captured
        files.Should().HaveCount(2);
        files.Select(f => f.Url).Should().BeEquivalentTo([
            "https://example.com/archive.zip?token=a",
            "https://example.com/archive.zip?token=b"
        ]);
    }
}
