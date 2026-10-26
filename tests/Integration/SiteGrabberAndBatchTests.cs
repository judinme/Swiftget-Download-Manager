using System.Net;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Batch;
using SwiftGet.Engine.Grabber;
using Xunit;

namespace SwiftGet.Integration;

public class SiteGrabberAndBatchTests
{
    [Fact]
    public void BatchPatternParser_ExpandsNumericRange_Accurately()
    {
        const string template = "https://example.com/item_[1-5].zip";
        var isValid = BatchPatternParser.IsValidPattern(template, out var error);

        isValid.Should().BeTrue();
        error.Should().BeNull();

        var urls = BatchPatternParser.GenerateUrls(template);
        urls.Should().HaveCount(5);
        urls[0].Should().Be("https://example.com/item_1.zip");
        urls[4].Should().Be("https://example.com/item_5.zip");
    }

    [Fact]
    public void BatchPatternParser_ExpandsLeadingZeroFormat_PreservesDigits()
    {
        const string template = "https://example.com/part_[008-012].iso";
        var urls = BatchPatternParser.GenerateUrls(template);

        urls.Should().Equal(
            "https://example.com/part_008.iso",
            "https://example.com/part_009.iso",
            "https://example.com/part_010.iso",
            "https://example.com/part_011.iso",
            "https://example.com/part_012.iso"
        );
    }

    [Fact]
    public void BatchPatternParser_ExpandsAlphabeticRange_Accurately()
    {
        const string template = "https://mirror.org/archive_[a-d].tar";
        var urls = BatchPatternParser.GenerateUrls(template);

        urls.Should().Equal(
            "https://mirror.org/archive_a.tar",
            "https://mirror.org/archive_b.tar",
            "https://mirror.org/archive_c.tar",
            "https://mirror.org/archive_d.tar"
        );
    }

    [Fact]
    public void BatchPatternParser_RejectsExcessiveUrlCount()
    {
        const string template = "https://example.com/[1-10000].bin";
        var isValid = BatchPatternParser.IsValidPattern(template, out var error);

        isValid.Should().BeFalse();
        error.Should().Contain("exceeding limit");
    }

    [Fact]
    public async Task SiteGrabber_ExtractsLinksAndRespectsDepthLimit()
    {
        // Mock HttpMessageHandler simulating a multi-page site hierarchy
        var handler = new MockSiteHttpHandler();
        using var client = new HttpClient(handler);
        var crawler = new SiteGrabberEngine(client);

        var project = new GrabberProject
        {
            Id = "crawl-proj-1",
            Name = "Test Crawl",
            StartUrl = "https://mocksite.local/index.html",
            MaxDepth = 1, // Only root and direct children
            FilePreset = FileFilterPreset.Custom,
            CustomExtensions = ".zip,.pdf"
        };

        var files = await crawler.CrawlAsync(project);

        files.Should().NotBeNull();
        // Should find document.pdf and data.zip from level 0/1, but not deep_ignored.zip from level 2
        files.Select(f => f.Url).Should().Contain("https://mocksite.local/files/document.pdf");
        files.Select(f => f.Url).Should().Contain("https://mocksite.local/files/data.zip");
        files.Select(f => f.Url).Should().NotContain("https://mocksite.local/files/deep_ignored.zip");
    }

    private sealed class MockSiteHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url == "https://mocksite.local/index.html")
            {
                var html = @"
                    <html>
                    <body>
                        <a href=""https://mocksite.local/files/document.pdf"">Download PDF</a>
                        <a href=""https://mocksite.local/files/data.zip"">Download ZIP</a>
                        <a href=""https://mocksite.local/files/image.jpg"">Image</a>
                        <a href=""https://mocksite.local/subpage.html"">Subpage</a>
                    </body>
                    </html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
                });
            }

            if (url == "https://mocksite.local/subpage.html")
            {
                var html = @"
                    <html>
                    <body>
                        <a href=""https://mocksite.local/deep_page.html"">Deep Page</a>
                    </body>
                    </html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
                });
            }

            if (url == "https://mocksite.local/deep_page.html")
            {
                var html = @"
                    <html>
                    <body>
                        <a href=""https://mocksite.local/files/deep_ignored.zip"">Deep File</a>
                    </body>
                    </html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
