using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.NativeHost.Framing;
using SwiftGet.NativeHost.Routing;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class NativeHostTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly CategoryManager _categoryManager;

    public NativeHostTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_nativehost_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
        _categoryManager = new CategoryManager(_db);
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
    public async Task Framing_RoundTrip_ShouldPreserveMessageFidelity()
    {
        using var stream = new MemoryStream();
        var originalPayload = new { action = "ping", timestamp = 123456789 };

        await NativeMessageFraming.WriteMessageAsync(stream, originalPayload);
        stream.Position = 0;

        var readBackJson = await NativeMessageFraming.ReadMessageAsync(stream);
        readBackJson.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(readBackJson!);
        doc.RootElement.GetProperty("action").GetString().Should().Be("ping");
        doc.RootElement.GetProperty("timestamp").GetInt64().Should().Be(123456789);
    }

    [Fact]
    public async Task Framing_ExceedingLimit_ShouldThrowInvalidDataException()
    {
        using var stream = new MemoryStream();
        var hugeString = new string('x', NativeMessageFraming.MaxMessageSizeBytes + 10);
        var hugePayload = new { data = hugeString };

        var act = async () => await NativeMessageFraming.WriteMessageAsync(stream, hugePayload);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public void ValidateOrigin_ShouldDistinguishExtensionOrigins()
    {
        NativeMessageRouter.ValidateOrigin("chrome-extension://abcdefghijklmnop/").Should().BeTrue();
        NativeMessageRouter.ValidateOrigin("moz-extension://12345678-abcd/").Should().BeTrue();
        NativeMessageRouter.ValidateOrigin("https://evil.com").Should().BeFalse();
        NativeMessageRouter.ValidateOrigin(null).Should().BeFalse();
        NativeMessageRouter.ValidateOrigin("").Should().BeFalse();
    }

    [Fact]
    public async Task RouteMessage_Ping_ShouldReturnPong()
    {
        var router = new NativeMessageRouter(_db, _categoryManager);
        var response = await router.RouteMessageAsync("{\"action\":\"ping\"}");

        response.Should().NotBeNull();
        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("\"status\":\"pong\"");
        json.Should().Contain("\"version\":\"1.1.0\"");
        json.Should().Contain("\"torrentSupported\":true");
    }

    [Fact]
    public async Task RouteMessage_InterceptDownload_ShouldEnqueueToDatabase()
    {
        await _db.InitializeAsync();
        var router = new NativeMessageRouter(_db, _categoryManager);
        var requestJson = "{\"action\":\"interceptDownload\",\"url\":\"https://example.com/testfile.iso\",\"fileName\":\"testfile.iso\"}";

        var response = await router.RouteMessageAsync(requestJson);
        response.Should().NotBeNull();
        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("\"success\":true");

        var all = await _db.GetAllDownloadsAsync();
        all.Should().ContainSingle(d => d.FileName == "testfile.iso" && d.Status == DownloadStatus.Queued);
    }

    [Fact]
    public async Task RouteMessage_Status_ShouldReturnRunningAndVersion()
    {
        var router = new NativeMessageRouter(_db, _categoryManager);
        var response = await router.RouteMessageAsync("{\"action\":\"status\"}");

        response.Should().NotBeNull();
        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("\"action\":\"status\"");
        json.Should().Contain("\"torrentSupported\":true");
        json.Should().Contain("\"version\":\"1.1.0\"");
    }

    [Fact]
    public async Task RouteMessage_AddMagnet_ValidMagnet_ShouldEnqueueToDatabaseAsTorrent()
    {
        await _db.InitializeAsync();
        var router = new NativeMessageRouter(_db, _categoryManager);
        var magnetUri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Debian+Netinst";
        var requestJson = $"{{\"action\":\"addMagnet\",\"magnetUri\":\"{magnetUri}\"}}";

        var response = await router.RouteMessageAsync(requestJson);
        response.Should().NotBeNull();

        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("\"action\":\"addMagnet\"");
        json.Should().Contain("\"success\":true");
        json.Should().Contain("da39a3ee5e6b4b0d3255bfef95601890afd80709");

        var all = await _db.GetAllDownloadsAsync();
        var torrentItem = all.Should().ContainSingle(d => d.JobKind == JobKind.Torrent).Which;
        torrentItem.FileName.Should().Be("Debian Netinst");
        torrentItem.InfoHash.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
    }

    [Fact]
    public async Task RouteMessage_AddMagnet_MalformedMagnet_ShouldReturnError()
    {
        var router = new NativeMessageRouter(_db, _categoryManager);
        var requestJson = "{\"action\":\"addMagnet\",\"magnetUri\":\"magnet:?invalid=true\"}";

        var response = await router.RouteMessageAsync(requestJson);
        response.Should().NotBeNull();

        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("\"action\":\"addMagnet\"");
        json.Should().Contain("\"success\":false");
    }

    [Fact]
    public async Task RouteMessage_AddTorrentFile_FromBytes_ShouldParseAndEnqueue()
    {
        await _db.InitializeAsync();
        var router = new NativeMessageRouter(_db, _categoryManager);

        var tempDir = Path.Combine(Path.GetTempPath(), $"SwiftGet_NH_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var payloadFile = Path.Combine(tempDir, "file.bin");
            await File.WriteAllBytesAsync(payloadFile, new byte[8192]);

            var creator = new MonoTorrent.TorrentCreator();
            var fileSource = new MonoTorrent.TorrentFileSource(tempDir);
            var torrent = await creator.CreateAsync(fileSource);
            var base64 = Convert.ToBase64String(torrent.Encode());

            var requestJson = $"{{\"action\":\"addTorrentFile\",\"fileBytesBase64\":\"{base64}\"}}";
            var response = await router.RouteMessageAsync(requestJson);
            response.Should().NotBeNull();

            var json = JsonSerializer.Serialize(response);
            json.Should().Contain("\"action\":\"addTorrentFile\"");
            json.Should().Contain("\"success\":true");

            var all = await _db.GetAllDownloadsAsync();
            all.Should().ContainSingle(d => d.JobKind == JobKind.Torrent);
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
    public async Task RouteMessage_AddTorrentFile_InvalidBytes_ShouldReturnError()
    {
        var router = new NativeMessageRouter(_db, _categoryManager);
        var requestJson = "{\"action\":\"addTorrentFile\",\"fileBytesBase64\":\"bm90X2FfdG9ycmVudF9hdF9hbGw=\"}";

        var response = await router.RouteMessageAsync(requestJson);
        response.Should().NotBeNull();

        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("\"action\":\"addTorrentFile\"");
        json.Should().Contain("\"success\":false");
    }

    [Fact]
    public async Task RouteMessage_InterceptDownload_TorrentExtension_ShouldSetJobKindTorrent()
    {
        await _db.InitializeAsync();
        var router = new NativeMessageRouter(_db, _categoryManager);
        var requestJson = "{\"action\":\"interceptDownload\",\"url\":\"https://example.com/archlinux.torrent\",\"fileName\":\"archlinux.torrent\"}";

        var response = await router.RouteMessageAsync(requestJson);
        response.Should().NotBeNull();

        var all = await _db.GetAllDownloadsAsync();
        var item = all.Should().ContainSingle(d => d.FileName == "archlinux.torrent").Which;
        item.JobKind.Should().Be(JobKind.Torrent);
        item.CategoryId.Should().Be("Torrents");
    }

    [Fact]
    public async Task RouteMessage_InterceptVideoDownload_ShouldCategorizeUnderVideo()
    {
        await _db.InitializeAsync();
        await _categoryManager.InitializeAsync();
        var router = new NativeMessageRouter(_db, _categoryManager);
        var requestJson = "{\"action\":\"interceptDownload\",\"url\":\"https://example.com/clip.mp4\",\"fileName\":\"clip.mp4\"}";

        var response = await router.RouteMessageAsync(requestJson);
        response.Should().NotBeNull();
        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("\"success\":true");

        var all = await _db.GetAllDownloadsAsync();
        var item = all.Should().ContainSingle(d => d.FileName == "clip.mp4").Which;
        item.CategoryId.Should().Be("video");
        item.DestinationPath.Should().Contain("Video");
    }

    [Fact]
    public async Task RouteMessage_InterceptDownload_StreamingMediaPlatformUrl_ShouldSetJobKindMedia()
    {
        await _db.InitializeAsync();
        await _categoryManager.InitializeAsync();
        var router = new NativeMessageRouter(_db, _categoryManager);
        var requestJson = "{\"action\":\"interceptDownload\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"fileName\":\"Video.mp4\"}";

        var response = await router.RouteMessageAsync(requestJson);
        response.Should().NotBeNull();

        var all = await _db.GetAllDownloadsAsync();
        var item = all.Should().ContainSingle(d => d.Url == "https://www.youtube.com/watch?v=dQw4w9WgXcQ").Which;
        item.JobKind.Should().Be(JobKind.Media);
    }

    [Fact]
    public async Task RouteMessage_InterceptDownload_WithCorrelationId_DeduplicatesSubsequentRequests()
    {
        await _db.InitializeAsync();
        await _categoryManager.InitializeAsync();
        var router = new NativeMessageRouter(_db, _categoryManager);
        var correlationId = Guid.NewGuid().ToString("N");
        var requestJson = $"{{\"action\":\"interceptDownload\",\"url\":\"https://example.com/unique.zip\",\"correlationId\":\"{correlationId}\"}}";

        var response1 = await router.RouteMessageAsync(requestJson);
        var response2 = await router.RouteMessageAsync(requestJson);

        var json1 = JsonSerializer.Serialize(response1);
        var json2 = JsonSerializer.Serialize(response2);

        json1.Should().Contain("\"success\":true");
        json2.Should().Contain("\"success\":true");
        json2.Should().Contain("\"deduplicated\":true");

        var all = await _db.GetAllDownloadsAsync();
        all.Should().ContainSingle(d => d.Url == "https://example.com/unique.zip");
    }
}

