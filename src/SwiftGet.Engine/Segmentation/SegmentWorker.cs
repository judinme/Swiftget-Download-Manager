using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Network;
using SwiftGet.Engine.Throttling;

namespace SwiftGet.Engine.Segmentation;

public class SegmentWorker
{
    private const int BufferSize = 64 * 1024; // 64 KB chunks
    private readonly HttpClient _httpClient;
    private readonly DownloadSegment _segment;
    private readonly DownloadItem _item;
    private readonly ChannelWriter<SegmentChunk> _channelWriter;
    private readonly ITokenBucketLimiter? _limiter;
    private readonly ResiliencePipeline<HttpResponseMessage> _resiliencePipeline;
    private readonly ILogger _logger;
    private bool _firstChunkReported;

    public Action<HttpResponseMessage, byte[]>? OnFirstChunkRead { get; set; }

    public DownloadSegment Segment => _segment;

    public SegmentWorker(
        HttpClient httpClient,
        DownloadSegment segment,
        DownloadItem item,
        ChannelWriter<SegmentChunk> channelWriter,
        ITokenBucketLimiter? limiter = null,
        ResiliencePipeline<HttpResponseMessage>? resiliencePipeline = null,
        ILogger? logger = null)
    {
        _httpClient = httpClient;
        _segment = segment;
        _item = item;
        _channelWriter = channelWriter;
        _limiter = limiter;
        _resiliencePipeline = resiliencePipeline ?? ResiliencePolicies.CreateHttpRetryPipeline();
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_segment.IsComplete)
        {
            _segment.Status = SegmentStatus.Completed;
            return;
        }

        _segment.Status = SegmentStatus.Downloading;
        _segment.UpdatedAtUtc = DateTime.UtcNow;

        using var response = await _resiliencePipeline.ExecuteAsync(async state =>
        {
            using var req = CreateRequest();
            return await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, state).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (_segment.CurrentOffset >= _segment.EndOffset)
            {
                _segment.Status = SegmentStatus.Completed;
                return;
            }
            throw new HttpRequestException($"Range not satisfiable for segment {_segment.SegmentIndex} ({_segment.CurrentOffset}-{_segment.EndOffset})");
        }

        if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
        {
            _segment.Status = SegmentStatus.Failed;
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        // Detect server ignoring ranges: if 200 OK received when sub-range was requested or returned length exceeds sub-range
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var isSubRange = _segment.StartOffset > 0 || _segment.CurrentOffset > 0 ||
                (_item.TotalBytes > 0 && _segment.EndOffset < _item.TotalBytes - 1);
            var isOversized = response.Content.Headers.ContentLength.HasValue &&
                response.Content.Headers.ContentLength.Value > (_segment.EndOffset - _segment.CurrentOffset + 1);

            if (isSubRange || isOversized)
            {
                _segment.Status = SegmentStatus.Failed;
                throw new InvalidOperationException("Server ignored Range header and returned full content (200 OK). Single-stream degradation required.");
            }
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        while (_segment.CurrentOffset <= _segment.EndOffset)
        {
            ct.ThrowIfCancellationRequested();

            var maxToRead = (int)Math.Min(BufferSize, _segment.EndOffset - _segment.CurrentOffset + 1);
            if (maxToRead <= 0) break;

            var buffer = ArrayPool<byte>.Shared.Rent(maxToRead);
            var bytesRead = 0;

            try
            {
                bytesRead = await stream.ReadAsync(buffer.AsMemory(0, maxToRead), ct).ConfigureAwait(false);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            if (bytesRead <= 0)
            {
                // EOF reached unexpectedly before EndOffset
                ArrayPool<byte>.Shared.Return(buffer);
                break;
            }

            if (_segment.StartOffset == 0 && !_firstChunkReported)
            {
                _firstChunkReported = true;
                try
                {
                    var sampleLen = Math.Min(bytesRead, 1024);
                    var sample = new byte[sampleLen];
                    Array.Copy(buffer, sample, sampleLen);
                    OnFirstChunkRead?.Invoke(response, sample);
                }
                catch
                {
                    // Non-fatal if header inspection callback fails
                }
            }

            // Apply bandwidth limiter throttling if configured
            if (_limiter != null)
            {
                await _limiter.ConsumeAsync(bytesRead, ct).ConfigureAwait(false);
            }

            var chunk = new SegmentChunk(_segment.Id, _segment.CurrentOffset, bytesRead, buffer);
            await _channelWriter.WriteAsync(chunk, ct).ConfigureAwait(false);

            _segment.CurrentOffset += bytesRead;
            _segment.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (_segment.CurrentOffset > _segment.EndOffset)
        {
            _segment.Status = SegmentStatus.Completed;
        }
    }

    private HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _item.FinalUrl);

        if (_item.SupportsRanges && _segment.EndOffset >= _segment.CurrentOffset)
        {
            request.Headers.Range = new RangeHeaderValue(_segment.CurrentOffset, _segment.EndOffset);

            if (!string.IsNullOrWhiteSpace(_item.ETag))
            {
                var etag = _item.ETag.Trim();
                if (!etag.StartsWith('"') && !etag.StartsWith("W/\"", StringComparison.OrdinalIgnoreCase))
                {
                    etag = $"\"{etag}\"";
                }
                request.Headers.TryAddWithoutValidation("If-Range", etag);
            }
            else if (!string.IsNullOrWhiteSpace(_item.LastModified))
            {
                request.Headers.TryAddWithoutValidation("If-Range", _item.LastModified);
            }
        }

        if (!string.IsNullOrWhiteSpace(_item.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", _item.UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(_item.Referrer))
        {
            request.Headers.TryAddWithoutValidation("Referer", _item.Referrer);
        }

        if (!string.IsNullOrWhiteSpace(_item.CookiesJson))
        {
            request.Headers.TryAddWithoutValidation("Cookie", _item.CookiesJson);
        }

        return request;
    }
}
