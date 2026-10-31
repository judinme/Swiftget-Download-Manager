using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core;
using SwiftGet.Core.Models;
using SwiftGet.Core.IO;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Security;
using SwiftGet.Engine.Torrent;
using System.Linq;

namespace SwiftGet.Engine.Server;

public sealed class LoopbackServer : IAsyncDisposable
{
    public const int DefaultPort = 52384;
    private HttpListener _listener;
    private readonly LoopbackTokenManager _tokenManager;
    private readonly ISwiftGetDatabase? _db;
    private readonly ICategoryManager? _categoryManager;
    private readonly IFileNameResolver _fileNameResolver;
    private readonly ILogger<LoopbackServer> _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public int Port { get; private set; }
    public bool IsRunning => _listener.IsListening;
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public event EventHandler<InterceptDownloadRequest>? DownloadIntercepted;

    public LoopbackServer(
        LoopbackTokenManager tokenManager,
        ISwiftGetDatabase? db = null,
        ICategoryManager? categoryManager = null,
        IFileNameResolver? fileNameResolver = null,
        int port = DefaultPort,
        ILogger<LoopbackServer>? logger = null)
    {
        _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
        _db = db;
        _categoryManager = categoryManager;
        _fileNameResolver = fileNameResolver ?? new SwiftGet.Core.IO.FileNameResolver();
        _logger = logger ?? NullLogger<LoopbackServer>.Instance;
        Port = port;

        _listener = new HttpListener();
    }

    public void Start()
    {
        if (_listener.IsListening) return;

        if (Port <= 0)
        {
            Port = GetRandomAvailablePort();
        }

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

        try
        {
            _listener.Start();
        }
        catch (Exception)
        {
            // If port is busy or failed, recreate fresh listener on a new dynamic port
            try { _listener.Close(); } catch { }
            _listener = new HttpListener();
            Port = GetRandomAvailablePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }

        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        _logger.LogInformation("SwiftGet Loopback Server listening on {BaseUrl}", BaseUrl);
    }

    public async Task StopAsync()
    {
        if (!_listener.IsListening) return;

        try
        {
            _cts?.Cancel();
            _listener.Stop();
            if (_listenTask != null)
            {
                await _listenTask.ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore shutdown exceptions
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => ProcessRequestAsync(context, ct), ct);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in loopback listener loop");
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        // 1. Enforce that connection originates strictly from local loopback interface
        if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address))
        {
            _logger.LogWarning("Rejected non-loopback connection attempt from {RemoteAddress}", request.RemoteEndPoint.Address);
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            await WriteJsonResponseAsync(response, new { error = "Forbidden: Non-loopback requests are not allowed." }, HttpStatusCode.Forbidden, ct).ConfigureAwait(false);
            return;
        }

        // 2. Enforce Host header validation (DNS Rebinding protection)
        var hostHeader = request.UserHostName ?? request.Headers["Host"] ?? string.Empty;
        if (!IsAllowedHost(hostHeader))
        {
            _logger.LogWarning("Rejected request with untrusted Host header '{Host}' from {RemoteAddress}", hostHeader, request.RemoteEndPoint.Address);
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            await WriteJsonResponseAsync(response, new { error = "Forbidden: Invalid or untrusted Host header." }, HttpStatusCode.Forbidden, ct).ConfigureAwait(false);
            return;
        }

        // 3. Apply CORS headers with origin restriction (reject arbitrary web pages)
        var origin = request.Headers["Origin"];
        if (!string.IsNullOrWhiteSpace(origin))
        {
            if (IsAllowedOrigin(origin))
            {
                response.Headers.Add("Access-Control-Allow-Origin", origin);
            }
            else
            {
                _logger.LogWarning("Rejected request from untrusted CORS origin '{Origin}'", origin);
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                await WriteJsonResponseAsync(response, new { error = "Forbidden: Untrusted origin." }, HttpStatusCode.Forbidden, ct).ConfigureAwait(false);
                return;
            }
        }

        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "X-SwiftGet-Token, Content-Type, Accept");

        if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.Close();
            return;
        }

        var path = request.Url?.AbsolutePath.ToLowerInvariant() ?? string.Empty;

        try
        {
            // 1. Health check (no auth required)
            if (path == "/api/v1/health" && request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var health = new ServerHealthResponse
                {
                    Status = "ok",
                    Version = "1.1.0",
                    TorrentSupported = true,
                    ActiveDownloads = 0
                };
                await WriteJsonResponseAsync(response, health, HttpStatusCode.OK, ct).ConfigureAwait(false);
                return;
            }

            // 2. Loopback Auth Token endpoint (for pairing with local browser extension)
            if (path == "/api/v1/auth/token" && request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var token = _tokenManager.GetOrCreateToken();
                await WriteJsonResponseAsync(response, new { authToken = token }, HttpStatusCode.OK, ct).ConfigureAwait(false);
                return;
            }

            // 3. Validate token for all other endpoints (or allow trusted browser extension origins)
            var tokenHeader = request.Headers["X-SwiftGet-Token"];
            var originHeader = request.Headers["Origin"];
            bool isExtensionOrigin = !string.IsNullOrWhiteSpace(originHeader) &&
                (originHeader.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                 originHeader.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase));

            if (!_tokenManager.ValidateToken(tokenHeader) && !isExtensionOrigin)
            {
                _logger.LogWarning("Unauthorized loopback request from {RemoteEndpoint} for {Path}", request.RemoteEndPoint, path);
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await WriteJsonResponseAsync(response, new { error = "Unauthorized: Invalid or missing X-SwiftGet-Token" }, HttpStatusCode.Unauthorized, ct).ConfigureAwait(false);
                return;
            }

            if (request.ContentLength64 > MaxRequestBodyBytes)
            {
                response.StatusCode = 413;
                await WriteJsonResponseAsync(response, new { error = "Payload too large. Maximum body size is 2 MB." }, (HttpStatusCode)413, ct).ConfigureAwait(false);
                return;
            }

            // 3. Dispatch routes
            if ((path == "/api/v1/downloads" || path == "/api/v1/torrents") && request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCreateDownloadAsync(request, response, ct).ConfigureAwait(false);
            }
            else if (path == "/api/v1/batch" && request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCreateBatchAsync(request, response, ct).ConfigureAwait(false);
            }
            else if (path == "/api/v1/downloads" && request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await HandleListDownloadsAsync(response, ct).ConfigureAwait(false);
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteJsonResponseAsync(response, new { error = "Not found" }, HttpStatusCode.NotFound, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing loopback request {Path}", path);
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await WriteJsonResponseAsync(response, new { error = ex.Message }, HttpStatusCode.InternalServerError, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch { }
        }
    }

    public const int MaxRequestBodyBytes = 2 * 1024 * 1024; // 2 MB
    public const int MaxTorrentBytes = 10 * 1024 * 1024; // 10 MB

    private static async Task<(string? Body, bool TooLarge)> ReadBoundedBodyAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
            {
                return (null, true);
            }
            ms.Write(buffer, 0, read);
        }
        return (Encoding.UTF8.GetString(ms.ToArray()), false);
    }

    private async Task HandleCreateDownloadAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var (body, tooLarge) = await ReadBoundedBodyAsync(request.InputStream, MaxRequestBodyBytes, ct).ConfigureAwait(false);
        if (tooLarge || body == null)
        {
            response.StatusCode = 413;
            await WriteJsonResponseAsync(response, new InterceptDownloadResponse { Success = false, Message = "Payload exceeds maximum allowed size (2 MB)." }, (HttpStatusCode)413, ct).ConfigureAwait(false);
            return;
        }

        InterceptDownloadRequest? interceptReq;
        try
        {
            interceptReq = JsonSerializer.Deserialize<InterceptDownloadRequest>(body, JsonOptions);
        }
        catch
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonResponseAsync(response, new InterceptDownloadResponse { Success = false, Message = "Invalid request payload." }, HttpStatusCode.BadRequest, ct).ConfigureAwait(false);
            return;
        }

        if (interceptReq == null)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonResponseAsync(response, new InterceptDownloadResponse { Success = false, Message = "Invalid request payload." }, HttpStatusCode.BadRequest, ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(interceptReq.Url) && !string.IsNullOrWhiteSpace(interceptReq.MagnetUri))
        {
            interceptReq.Url = interceptReq.MagnetUri;
        }

        string? infoHash = null;
        var rawFileName = interceptReq.FileName;
        bool isTorrent = false;
        byte[]? torrentBytes = null;

        if (!string.IsNullOrWhiteSpace(interceptReq.FileBytesBase64))
        {
            if ((long)interceptReq.FileBytesBase64.Length * 3 / 4 > MaxTorrentBytes)
            {
                response.StatusCode = 413;
                await WriteJsonResponseAsync(response, new InterceptDownloadResponse { Success = false, Message = "Decoded torrent payload exceeds 10 MB limit." }, (HttpStatusCode)413, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                torrentBytes = Convert.FromBase64String(interceptReq.FileBytesBase64);
                if (torrentBytes.Length > MaxTorrentBytes)
                {
                    response.StatusCode = 413;
                    await WriteJsonResponseAsync(response, new InterceptDownloadResponse { Success = false, Message = "Decoded torrent payload exceeds 10 MB limit." }, (HttpStatusCode)413, ct).ConfigureAwait(false);
                    return;
                }

                var parsedTorrent = MonoTorrent.Torrent.Load(torrentBytes);
                infoHash = parsedTorrent.InfoHashes.V1OrV2.ToHex();
                if (string.IsNullOrWhiteSpace(rawFileName))
                {
                    rawFileName = parsedTorrent.Name;
                }
                isTorrent = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse torrent bytes in loopback request.");
            }
        }

        if (!isTorrent && !string.IsNullOrWhiteSpace(interceptReq.Url))
        {
            isTorrent = interceptReq.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
                        interceptReq.Url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ||
                        (rawFileName != null && rawFileName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase));

            if (interceptReq.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) &&
                MagnetUriParser.TryParse(interceptReq.Url, out var parsedMagnet, out _))
            {
                infoHash = parsedMagnet!.InfoHash;
                if (string.IsNullOrWhiteSpace(rawFileName) && !string.IsNullOrWhiteSpace(parsedMagnet.DisplayName))
                {
                    rawFileName = parsedMagnet.DisplayName;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(interceptReq.Url) && !isTorrent)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonResponseAsync(response, new InterceptDownloadResponse { Success = false, Message = "Url, MagnetUri, or FileBytesBase64 is required." }, HttpStatusCode.BadRequest, ct).ConfigureAwait(false);
            return;
        }

        var downloadId = Guid.NewGuid().ToString("N");
        interceptReq.DownloadId = downloadId;

        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            rawFileName = _fileNameResolver.ResolveInitialCandidate(interceptReq.Url ?? string.Empty);
        }

        var fallbackFileName = isTorrent ? "torrent_" + downloadId[..8] + ".torrent" : "download_" + downloadId[..8] + ".bin";
        var fileName = _fileNameResolver.SanitizeFileName(rawFileName ?? string.Empty, fallbackFileName);
        bool isMedia = !isTorrent && !string.IsNullOrWhiteSpace(interceptReq.Url) && UrlMediaDetector.IsStreamingMediaPlatformUrl(interceptReq.Url);
        JobKind jobKind = isTorrent ? JobKind.Torrent : (isMedia ? JobKind.Media : JobKind.Http);

        if (_db != null && _categoryManager != null)
        {
            var category = _categoryManager.ResolveCategory(fileName);
            var categoryId = isTorrent ? "Torrents" : (isMedia ? "video" : category.Id);
            if (!string.IsNullOrWhiteSpace(interceptReq.CategoryId))
            {
                var categories = await _categoryManager.GetCategoriesAsync(ct).ConfigureAwait(false);
                var existingCat = categories.FirstOrDefault(c => string.Equals(c.Id, interceptReq.CategoryId, StringComparison.OrdinalIgnoreCase));
                if (existingCat != null)
                {
                    category = existingCat;
                    categoryId = existingCat.Id;
                }
            }

            var queueId = "default";
            if (!string.IsNullOrWhiteSpace(interceptReq.QueueId))
            {
                var queues = await _db.GetAllQueuesAsync(ct).ConfigureAwait(false);
                if (queues.Any(q => string.Equals(q.Id, interceptReq.QueueId, StringComparison.OrdinalIgnoreCase)))
                {
                    queueId = interceptReq.QueueId;
                }
            }

            string candidateName = fileName;
            if (!string.IsNullOrWhiteSpace(interceptReq.DestinationPath))
            {
                var destName = _fileNameResolver.SanitizeFileName(interceptReq.DestinationPath);
                if (!string.IsNullOrWhiteSpace(destName))
                {
                    candidateName = _fileNameResolver.SanitizeFileName(destName, fileName);
                }
            }

            var targetDir = Environment.ExpandEnvironmentVariables(category.TargetDirectory);
            var destPath = PathTraversalGuard.EnsureSafePath(targetDir, candidateName, fileName);
            interceptReq.FileName = fileName;
            interceptReq.DestinationPath = destPath;

            var item = new DownloadItem
            {
                Id = downloadId,
                JobKind = jobKind,
                InfoHash = infoHash,
                Url = interceptReq.Url ?? string.Empty,
                FileName = fileName,
                DestinationPath = destPath,
                CategoryId = categoryId,
                QueueId = queueId,
                CookiesJson = interceptReq.Cookies,
                Referrer = interceptReq.Referrer,
                UserAgent = interceptReq.UserAgent,
                Status = DownloadStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow
            };

            await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);

            if (isTorrent && !string.IsNullOrWhiteSpace(infoHash))
            {
                var torrent = new TorrentItem
                {
                    Id = downloadId,
                    InfoHash = infoHash,
                    Name = fileName,
                    MagnetUri = interceptReq.Url != null && interceptReq.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ? interceptReq.Url : null,
                    TotalBytes = torrentBytes != null ? torrentBytes.Length : 0,
                    CreatedAtUtc = DateTime.UtcNow
                };
                await _db.SaveTorrentAsync(torrent, ct).ConfigureAwait(false);
            }
        }

        DownloadIntercepted?.Invoke(this, interceptReq);

        var result = new InterceptDownloadResponse
        {
            Success = true,
            DownloadId = downloadId,
            InfoHash = infoHash,
            IsTorrent = isTorrent,
            Message = isTorrent ? "Torrent enqueued successfully." : "Download enqueued successfully."
        };

        await WriteJsonResponseAsync(response, result, HttpStatusCode.OK, ct).ConfigureAwait(false);
    }

    private async Task HandleCreateBatchAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var (body, tooLarge) = await ReadBoundedBodyAsync(request.InputStream, MaxRequestBodyBytes, ct).ConfigureAwait(false);
        if (tooLarge || body == null)
        {
            response.StatusCode = 413;
            await WriteJsonResponseAsync(response, new { success = false, message = "Payload exceeds maximum allowed size (2 MB)." }, (HttpStatusCode)413, ct).ConfigureAwait(false);
            return;
        }

        InterceptBatchRequest? batchReq;
        try
        {
            batchReq = JsonSerializer.Deserialize<InterceptBatchRequest>(body, JsonOptions);
        }
        catch
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonResponseAsync(response, new { success = false, message = "Malformed JSON payload." }, HttpStatusCode.BadRequest, ct).ConfigureAwait(false);
            return;
        }

        if (batchReq == null || batchReq.Downloads.Count == 0)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonResponseAsync(response, new { success = false, message = "Downloads list is empty." }, HttpStatusCode.BadRequest, ct).ConfigureAwait(false);
            return;
        }

        var count = 0;
        foreach (var item in batchReq.Downloads)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(item.FileName))
            {
                item.FileName = _fileNameResolver.SanitizeFileName(item.FileName);
            }
            if (!string.IsNullOrWhiteSpace(item.DestinationPath))
            {
                var destName = _fileNameResolver.SanitizeFileName(item.DestinationPath);
                item.DestinationPath = _fileNameResolver.SanitizeFileName(destName, item.FileName ?? "download.bin");
            }
            DownloadIntercepted?.Invoke(this, item);
            count++;
        }

        await WriteJsonResponseAsync(response, new { success = true, count, message = $"Enqueued {count} downloads." }, HttpStatusCode.OK, ct).ConfigureAwait(false);
    }

    private async Task HandleListDownloadsAsync(HttpListenerResponse response, CancellationToken ct)
    {
        if (_db == null)
        {
            await WriteJsonResponseAsync(response, Array.Empty<DownloadItem>(), HttpStatusCode.OK, ct).ConfigureAwait(false);
            return;
        }

        var items = await _db.GetAllDownloadsAsync(ct).ConfigureAwait(false);
        await WriteJsonResponseAsync(response, items, HttpStatusCode.OK, ct).ConfigureAwait(false);
    }

    private static async Task WriteJsonResponseAsync<T>(HttpListenerResponse response, T payload, HttpStatusCode statusCode, CancellationToken ct)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static int GetRandomAvailablePort()
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static bool IsAllowedHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        string host;
        if (hostHeader.StartsWith('['))
        {
            var closingBracket = hostHeader.IndexOf(']');
            if (closingBracket < 0) return false;
            host = hostHeader[1..closingBracket];
        }
        else
        {
            host = hostHeader.Split(':')[0];
        }

        return host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return uri.IsLoopback;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
