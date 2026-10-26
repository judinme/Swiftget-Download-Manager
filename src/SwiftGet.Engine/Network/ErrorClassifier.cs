using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace SwiftGet.Engine.Network;

public static class ErrorClassifier
{
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static DownloadErrorCategory Classify(Exception ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;

        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
            {
                return ClassifyStatusCode(httpEx.StatusCode.Value, null, out retryAfter);
            }
        }

        if (ex is IOException ioEx)
        {
            if (ioEx.HResult == ErrorDiskFull || ioEx.HResult == ErrorHandleDiskFull)
            {
                return DownloadErrorCategory.DiskFull;
            }
        }

        if (ex is UnauthorizedAccessException)
        {
            return DownloadErrorCategory.Permission;
        }

        if (ex is AuthenticationException)
        {
            return DownloadErrorCategory.TlsFailure;
        }

        if (ex is SocketException || ex is TimeoutException)
        {
            return DownloadErrorCategory.Transient;
        }

        if (ex is TaskCanceledException && ex.InnerException is TimeoutException)
        {
            return DownloadErrorCategory.Transient;
        }

        return DownloadErrorCategory.Unrecoverable;
    }

    public static DownloadErrorCategory ClassifyResponse(HttpResponseMessage response, out TimeSpan? retryAfter)
    {
        return ClassifyStatusCode(response.StatusCode, response.Headers.RetryAfter?.ToString(), out retryAfter);
    }

    private static DownloadErrorCategory ClassifyStatusCode(HttpStatusCode statusCode, string? retryAfterHeader, out TimeSpan? retryAfter)
    {
        retryAfter = ParseRetryAfter(retryAfterHeader);

        return statusCode switch
        {
            HttpStatusCode.TooManyRequests => DownloadErrorCategory.RateLimited,
            HttpStatusCode.ServiceUnavailable when retryAfter.HasValue => DownloadErrorCategory.RateLimited,
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError => DownloadErrorCategory.Transient,
            HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired => DownloadErrorCategory.Authentication,
            HttpStatusCode.Forbidden or HttpStatusCode.Gone => DownloadErrorCategory.ExpiredUrl,
            HttpStatusCode.RequestedRangeNotSatisfiable => DownloadErrorCategory.RangeRejected,
            _ => DownloadErrorCategory.Unrecoverable
        };
    }

    public static TimeSpan? ParseRetryAfter(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;

        if (int.TryParse(headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 3600));
        }

        if (DateTimeOffset.TryParse(headerValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var diff = date - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }

        return null;
    }
}
