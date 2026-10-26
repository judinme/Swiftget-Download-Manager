using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Server;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class LoopbackServerTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly CategoryManager _categoryManager;
    private readonly string _tokenPath;
    private readonly LoopbackTokenManager _tokenManager;

    public LoopbackServerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_loopback_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
        _categoryManager = new CategoryManager(_db);
        _tokenPath = Path.Combine(_tempDir, "auth.token");
        _tokenManager = new LoopbackTokenManager(_tokenPath);
    }

    public async ValueTask DisposeAsync()
    {
        _categoryManager.Dispose();
        await _db.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TokenManager_GeneratesAndValidatesToken_Correctly()
    {
        var token = _tokenManager.GetOrCreateToken();
        token.Should().NotBeNullOrWhiteSpace();
        token.Length.Should().BeGreaterOrEqualTo(32);

        _tokenManager.ValidateToken(token).Should().BeTrue();
        _tokenManager.ValidateToken(token.ToUpperInvariant()).Should().BeFalse();
        _tokenManager.ValidateToken("wrong_token_123456789012345678901234").Should().BeFalse();
        _tokenManager.ValidateToken(null).Should().BeFalse();
        _tokenManager.ValidateToken("").Should().BeFalse();
    }

    [Fact]
    public async Task HealthEndpoint_ShouldReturnOkWithoutToken()
    {
        await using var server = new LoopbackServer(_tokenManager, _db, _categoryManager, port: 0);
        server.Start();

        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var response = await client.GetAsync("api/v1/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var health = await response.Content.ReadFromJsonAsync<ServerHealthResponse>();
        health.Should().NotBeNull();
        health!.Status.Should().Be("ok");
        health.Version.Should().Be("1.1.0");
        health.TorrentSupported.Should().BeTrue();
    }

    [Fact]
    public async Task DownloadsEndpoint_WithoutToken_ShouldReturnUnauthorized()
    {
        await using var server = new LoopbackServer(_tokenManager, _db, _categoryManager, port: 0);
        server.Start();

        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var payload = new InterceptDownloadRequest
        {
            Url = "https://example.com/unauthorized.zip"
        };

        var response = await client.PostAsJsonAsync("api/v1/downloads", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DownloadsEndpoint_WithValidToken_ShouldEnqueueAndSaveToDatabase()
    {
        await _db.InitializeAsync();
        await using var server = new LoopbackServer(_tokenManager, _db, _categoryManager, port: 0);
        server.Start();

        var token = _tokenManager.GetOrCreateToken();
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        client.DefaultRequestHeaders.Add("X-SwiftGet-Token", token);

        InterceptDownloadRequest? eventPayload = null;
        server.DownloadIntercepted += (s, req) => eventPayload = req;

        var payload = new InterceptDownloadRequest
        {
            Url = "https://example.com/archive.zip",
            FileName = "archive.zip",
            Referrer = "https://example.com/downloads"
        };

        var response = await client.PostAsJsonAsync("api/v1/downloads", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<InterceptDownloadResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.DownloadId.Should().NotBeNullOrWhiteSpace();

        eventPayload.Should().NotBeNull();
        eventPayload!.Url.Should().Be("https://example.com/archive.zip");

        var allDownloads = await _db.GetAllDownloadsAsync();
        allDownloads.Should().ContainSingle(d => d.FileName == "archive.zip" && d.Status == DownloadStatus.Queued);
    }

    [Fact]
    public async Task DownloadsEndpoint_WithMagnetUri_ShouldEnqueueAsTorrentJob()
    {
        await _db.InitializeAsync();
        await using var server = new LoopbackServer(_tokenManager, _db, _categoryManager, port: 0);
        server.Start();

        var token = _tokenManager.GetOrCreateToken();
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        client.DefaultRequestHeaders.Add("X-SwiftGet-Token", token);

        var magnetUri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Fedora+Workstation";
        var payload = new InterceptDownloadRequest
        {
            Url = magnetUri
        };

        var response = await client.PostAsJsonAsync("api/v1/downloads", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<InterceptDownloadResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.IsTorrent.Should().BeTrue();
        result.InfoHash.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");

        var allDownloads = await _db.GetAllDownloadsAsync();
        var torrent = allDownloads.Should().ContainSingle(d => d.JobKind == JobKind.Torrent).Which;
        torrent.FileName.Should().Be("Fedora Workstation");
        torrent.InfoHash.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
    }

    [Fact]
    public async Task TorrentsEndpoint_WithBase64Payload_ShouldParseAndEnqueueAsTorrentJob()
    {
        await _db.InitializeAsync();
        await using var server = new LoopbackServer(_tokenManager, _db, _categoryManager, port: 0);
        server.Start();

        var token = _tokenManager.GetOrCreateToken();
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        client.DefaultRequestHeaders.Add("X-SwiftGet-Token", token);

        var tempDir = Path.Combine(Path.GetTempPath(), $"SwiftGet_LB_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var payloadFile = Path.Combine(tempDir, "loopback_sample.bin");
            await File.WriteAllBytesAsync(payloadFile, new byte[4096]);

            var creator = new MonoTorrent.TorrentCreator();
            var fileSource = new MonoTorrent.TorrentFileSource(tempDir);
            var torrent = await creator.CreateAsync(fileSource);
            var base64 = Convert.ToBase64String(torrent.Encode());

            var payload = new InterceptDownloadRequest
            {
                FileBytesBase64 = base64
            };

            var response = await client.PostAsJsonAsync("api/v1/torrents", payload);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var result = await response.Content.ReadFromJsonAsync<InterceptDownloadResponse>();
            result.Should().NotBeNull();
            result!.Success.Should().BeTrue();
            result.IsTorrent.Should().BeTrue();
            result.InfoHash.Should().NotBeNullOrWhiteSpace();

            var allDownloads = await _db.GetAllDownloadsAsync();
            allDownloads.Should().ContainSingle(d => d.JobKind == JobKind.Torrent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task AuthTokenEndpoint_ShouldReturnValidToken()
    {
        await using var server = new LoopbackServer(_tokenManager, _db, _categoryManager, port: 0);
        server.Start();

        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var response = await client.GetAsync("api/v1/auth/token");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("authToken").GetString();
        token.Should().NotBeNullOrWhiteSpace();
        _tokenManager.ValidateToken(token).Should().BeTrue();
    }

    [Fact]
    public async Task DownloadsEndpoint_WithExtensionOrigin_ShouldSucceedWithoutTokenHeader()
    {
        await _db.InitializeAsync();
        await using var server = new LoopbackServer(_tokenManager, _db, _categoryManager, port: 0);
        server.Start();

        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        client.DefaultRequestHeaders.Add("Origin", "chrome-extension://albogoggaijdbgdldghgfcjakpdbmmfm");

        var payload = new InterceptDownloadRequest
        {
            Url = "https://example.com/test-intercept.zip",
            FileName = "test-intercept.zip"
        };

        var response = await client.PostAsJsonAsync("api/v1/downloads", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<InterceptDownloadResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task DownloadsEndpoint_VideoFile_ShouldCategorizeAsVideoAndEnqueue()
    {
        await _db.InitializeAsync();
        await _categoryManager.InitializeAsync();
        await using var server = new LoopbackServer(_tokenManager, _db, _categoryManager, port: 0);
        server.Start();

        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        client.DefaultRequestHeaders.Add("Origin", "chrome-extension://albogoggaijdbgdldghgfcjakpdbmmfm");

        var payload = new InterceptDownloadRequest
        {
            Url = "https://cdn.example.org/videos/nature-4k.mp4",
            FileName = "nature-4k.mp4",
            Referrer = "https://example.org/watch"
        };

        var response = await client.PostAsJsonAsync("api/v1/downloads", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<InterceptDownloadResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();

        var allDownloads = await _db.GetAllDownloadsAsync();
        var item = allDownloads.Should().ContainSingle(d => d.FileName == "nature-4k.mp4").Which;
        item.CategoryId.Should().Be("video");
        item.DestinationPath.Should().Contain("Video");
    }
}

