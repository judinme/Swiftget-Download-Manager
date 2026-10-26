using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Network;
using SwiftGet.Engine.Segmentation;
using Xunit;

namespace SwiftGet.Integration;

public class ServerConditionsResilienceTests
{
    [Fact]
    public void ErrorClassifier_ClassifiesRateLimitingWithIntegerRetryAfter()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "45");

        var category = ErrorClassifier.ClassifyResponse(response, out var retryAfter);

        category.Should().Be(DownloadErrorCategory.RateLimited);
        retryAfter.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, DownloadErrorCategory.Transient)]
    [InlineData(HttpStatusCode.BadGateway, DownloadErrorCategory.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DownloadErrorCategory.Transient)]
    [InlineData(HttpStatusCode.GatewayTimeout, DownloadErrorCategory.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, DownloadErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, DownloadErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, DownloadErrorCategory.ExpiredUrl)]
    [InlineData(HttpStatusCode.Gone, DownloadErrorCategory.ExpiredUrl)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable, DownloadErrorCategory.RangeRejected)]
    public void ErrorClassifier_ClassifiesHttpStatusCodesAccurately(HttpStatusCode code, DownloadErrorCategory expectedCategory)
    {
        using var response = new HttpResponseMessage(code);
        var category = ErrorClassifier.ClassifyResponse(response, out _);

        category.Should().Be(expectedCategory);
    }

    [Fact]
    public void ErrorClassifier_ClassifiesExceptionsAccurately()
    {
        // 1. SocketException
        var sockEx = new SocketException((int)SocketError.ConnectionReset);
        ErrorClassifier.Classify(sockEx, out _).Should().Be(DownloadErrorCategory.Transient);

        // 2. TimeoutException
        var timeoutEx = new TimeoutException("Operation timed out");
        ErrorClassifier.Classify(timeoutEx, out _).Should().Be(DownloadErrorCategory.Transient);

        // 3. UnauthorizedAccessException
        var authEx = new UnauthorizedAccessException("Access denied to folder");
        ErrorClassifier.Classify(authEx, out _).Should().Be(DownloadErrorCategory.Permission);

        // 4. AuthenticationException (TLS)
        var tlsEx = new AuthenticationException("SSL Handshake failed");
        ErrorClassifier.Classify(tlsEx, out _).Should().Be(DownloadErrorCategory.TlsFailure);

        // 5. Disk Full IOException
        var diskFullEx = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        ErrorClassifier.Classify(diskFullEx, out _).Should().Be(DownloadErrorCategory.DiskFull);
    }

    [Fact]
    public void DynamicSplitter_PartitionsSegmentsWithExactContiguousBoundaries()
    {
        const long totalSize = 100_000_000; // 100 MB
        const int connections = 8;
        const string downloadId = "test-dl-1";

        var segments = DynamicSplitter.CreateInitialSegments(downloadId, totalSize, connections);

        segments.Should().HaveCount(8);
        segments[0].StartOffset.Should().Be(0);
        segments[^1].EndOffset.Should().Be(totalSize - 1);

        for (int i = 0; i < segments.Count - 1; i++)
        {
            segments[i + 1].StartOffset.Should().Be(segments[i].EndOffset + 1, "segments must be contiguous without gaps or overlaps");
        }
    }

    [Fact]
    public void DynamicSplitter_HandlesUnknownOrSingleConnectionGracefully()
    {
        // Unknown size (-1)
        var unknownSegments = DynamicSplitter.CreateInitialSegments("test-unknown", -1, 4);
        unknownSegments.Should().HaveCount(1);
        unknownSegments[0].StartOffset.Should().Be(0);
        unknownSegments[0].EndOffset.Should().Be(0);

        // 1 connection requested
        var singleSegments = DynamicSplitter.CreateInitialSegments("test-single", 5000, 1);
        singleSegments.Should().HaveCount(1);
        singleSegments[0].StartOffset.Should().Be(0);
        singleSegments[0].EndOffset.Should().Be(4999);
    }

    [Fact]
    public void DynamicSplitter_SplitsLaggingSegmentInHalf()
    {
        var segments = new List<DownloadSegment>
        {
            new()
            {
                DownloadId = "dl-1",
                SegmentIndex = 0,
                StartOffset = 0,
                CurrentOffset = 10_000_000,
                EndOffset = 20_000_000, // 10 MB remaining
                Status = SegmentStatus.Downloading
            }
        };

        var success = DynamicSplitter.TrySplitLargestSegment(
            segments,
            minSplitSize: 2 * 1024 * 1024,
            newSegmentIndex: 1,
            out var newSegment);

        success.Should().BeTrue();
        newSegment.Should().NotBeNull();
        newSegment!.SegmentIndex.Should().Be(1);
        newSegment.StartOffset.Should().Be(15_000_000);
        newSegment.EndOffset.Should().Be(20_000_000);
        segments[0].EndOffset.Should().Be(14_999_999);
    }
}
