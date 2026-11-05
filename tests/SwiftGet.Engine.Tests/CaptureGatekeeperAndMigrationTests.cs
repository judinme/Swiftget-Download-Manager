using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Integration;
using SwiftGet.Engine.Server;
using SwiftGet.NativeHost.Routing;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class CaptureGatekeeperAndMigrationTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private SqliteDatabase _db = null!;
    private CategoryManager _categoryManager = null!;
    private CaptureGatekeeper _gatekeeper = null!;

    public CaptureGatekeeperAndMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SwiftGet_CaptureTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test_swiftget.db");
    }

    public async Task InitializeAsync()
    {
        CaptureGatekeeper.CustomSettingsDirectory = _tempDir;
        CaptureGatekeeper.ProcessStartupUtc = DateTime.UtcNow;
        _db = new SqliteDatabase(_dbPath);
        await _db.InitializeAsync();
        _categoryManager = new CategoryManager(_db);
        await _categoryManager.InitializeAsync();
        _gatekeeper = new CaptureGatekeeper();
    }

    public async Task DisposeAsync()
    {
        _categoryManager.Dispose();
        await _db.DisposeAsync();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void EvaluateCapture_RejectsExpiredTtl_OlderThan15Seconds()
    {
        var now = DateTime.UtcNow;
        var staleCreatedAt = now.AddSeconds(-20);

        var res = _gatekeeper.EvaluateCapture(
            url: "https://releases.ubuntu.com/24.04/ubuntu.iso",
            fileName: "ubuntu.iso",
            eventId: Guid.NewGuid().ToString("N"),
            createdAtUtc: staleCreatedAt,
            nowUtcOverride: now);

        res.Allowed.Should().BeFalse();
        res.Reason.Should().Contain("Expired capture TTL");
    }

    [Fact]
    public void EvaluateCapture_EnforcesEventTimeGuard_PredatingAppStartup()
    {
        var startup = DateTime.UtcNow;
        CaptureGatekeeper.ProcessStartupUtc = startup;

        var preStartupEvent = startup.AddSeconds(-5);
        var res = _gatekeeper.EvaluateCapture(
            url: "https://releases.ubuntu.com/24.04/ubuntu.iso",
            fileName: "ubuntu.iso",
            eventId: Guid.NewGuid().ToString("N"),
            createdAtUtc: preStartupEvent,
            enforceStartupTimeGuard: true,
            nowUtcOverride: startup);

        res.Allowed.Should().BeFalse();
        res.Reason.Should().Contain("predates app startup");
    }

    [Fact]
    public void EvaluateCapture_DeduplicatesSameEventId_AndSameUrlWithin3Seconds()
    {
        var now = DateTime.UtcNow;
        var evtId = Guid.NewGuid().ToString("N");
        var url = "https://example.com/files/archive.zip";

        var first = _gatekeeper.EvaluateCapture(url, "archive.zip", eventId: evtId, createdAtUtc: now, nowUtcOverride: now);
        first.Allowed.Should().BeTrue();

        // Duplicate by eventId
        var dupById = _gatekeeper.EvaluateCapture("https://example.com/files/other.zip", "other.zip", eventId: evtId, createdAtUtc: now, nowUtcOverride: now.AddSeconds(5));
        dupById.Allowed.Should().BeFalse();
        dupById.Reason.Should().Contain("Duplicate eventId");

        // Duplicate by same URL within 3 seconds
        var dupByUrl = _gatekeeper.EvaluateCapture(url, "archive.zip", eventId: Guid.NewGuid().ToString("N"), createdAtUtc: now.AddSeconds(1), nowUtcOverride: now.AddSeconds(1));
        dupByUrl.Allowed.Should().BeFalse();
        dupByUrl.Reason.Should().Contain("Duplicate URL within 3s window");

        // Allowed again after >3 seconds
        var afterWindow = _gatekeeper.EvaluateCapture(url, "archive.zip", eventId: Guid.NewGuid().ToString("N"), createdAtUtc: now.AddSeconds(4), nowUtcOverride: now.AddSeconds(4));
        afterWindow.Allowed.Should().BeTrue();
    }

    [Fact]
    public void EvaluateCapture_RejectsClaudeApi_BlobUrl_GradleProperties_AppInstaller_AndSmallText()
    {
        // 1. claude.ai/api markdown attachment
        var claudeRes = _gatekeeper.EvaluateCapture(
            url: "https://claude.ai/api/organizations/org-123/chat_conversations/conv-456/DocuForge-PDF-Toolbox-Documentation.md",
            fileName: "DocuForge-PDF-Toolbox-Documentation.md",
            mimeType: "text/markdown");
        claudeRes.Allowed.Should().BeFalse();

        // 2. blob: URL
        var blobRes = _gatekeeper.EvaluateCapture(
            url: "blob:https://claude.ai/99887766-5544-3322-1100",
            fileName: "DocuForge-PDF-Toolbox-Documentation.md");
        blobRes.Allowed.Should().BeFalse();
        blobRes.Reason.Should().Contain("Non-network URL scheme");

        // 3. gradle.properties
        var gradleRes = _gatekeeper.EvaluateCapture(
            url: "https://raw.githubusercontent.com/user/repo/main/gradle.properties",
            fileName: "gradle.properties",
            fileSizeBytes: 512,
            mimeType: "text/plain");
        gradleRes.Allowed.Should().BeFalse();

        // 4. Microsoft.SurfaceITToolkit.UI_x64.appinstaller
        var appInstallerRes = _gatekeeper.EvaluateCapture(
            url: "https://download.microsoft.com/download/surface/Microsoft.SurfaceITToolkit.UI_x64.appinstaller",
            fileName: "Microsoft.SurfaceITToolkit.UI_x64.appinstaller");
        appInstallerRes.Allowed.Should().BeFalse();

        // 5. Unknown-size application/json
        var jsonRes = _gatekeeper.EvaluateCapture(
            url: "https://example.org/data/config",
            fileName: "config.json",
            fileSizeBytes: null,
            mimeType: "application/json");
        jsonRes.Allowed.Should().BeFalse();
        jsonRes.Reason.Should().Contain("Non-binary MIME type");

        // 6. Small binary file below 256 KB threshold
        var smallBin = _gatekeeper.EvaluateCapture(
            url: "https://example.org/files/tiny.zip",
            fileName: "tiny.zip",
            fileSizeBytes: 12 * 1024, // 12 KB < 256 KB
            mimeType: "application/zip");
        smallBin.Allowed.Should().BeFalse();
        smallBin.Reason.Should().Contain("below threshold");
    }

    [Fact]
    public void EvaluateCapture_EnforcesLoopPreventionHeader_AndActiveUrlGuard()
    {
        var headers = new Dictionary<string, string>
        {
            [CaptureGatekeeper.OriginHeaderName] = "1"
        };

        var loopRes = _gatekeeper.EvaluateCapture(
            url: "https://releases.ubuntu.com/24.04/ubuntu.iso",
            fileName: "ubuntu.iso",
            headers: headers);

        loopRes.Allowed.Should().BeFalse();
        loopRes.Reason.Should().Contain("X-SwiftGet-Origin");

        // Active URL guard
        var activeUrl = "https://releases.ubuntu.com/24.04/ubuntu-desktop.iso";
        _gatekeeper.MarkUrlActive(activeUrl);

        var activeRes = _gatekeeper.EvaluateCapture(activeUrl, "ubuntu-desktop.iso");
        activeRes.Allowed.Should().BeFalse();
        activeRes.Reason.Should().Contain("already active");

        _gatekeeper.UnmarkUrlActive(activeUrl);
        var afterUnmark = _gatekeeper.EvaluateCapture(activeUrl, "ubuntu-desktop.iso");
        afterUnmark.Allowed.Should().BeTrue();
    }

    [Fact]
    public void SanitizeMediaUrl_StripsYouTubeMixAndPlaylistParams_AndRequiresUserGesture()
    {
        var mixUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ&start_radio=1&index=3";
        var cleaned = CaptureGatekeeper.SanitizeMediaUrl(mixUrl);
        cleaned.Should().Be("https://www.youtube.com/watch?v=dQw4w9WgXcQ");

        // Auto-capture without userGesture must be rejected
        var noGesture = _gatekeeper.EvaluateCapture(
            url: cleaned,
            fileName: "Video.mp4",
            userGesture: false);
        noGesture.Allowed.Should().BeFalse();
        noGesture.Reason.Should().Contain("user gesture");

        // Explicit user gesture succeeds
        var withGesture = _gatekeeper.EvaluateCapture(
            url: cleaned,
            fileName: "Video.mp4",
            userGesture: true);
        withGesture.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CaptureStateMigrationService_PurgesPhantomAndTransientRows_PreservingCompletedDownloads()
    {
        // Seed DB with:
        // 1. Genuine completed download (MUST BE PRESERVED)
        var completedFile = Path.Combine(_tempDir, "ubuntu-24.04.iso");
        await File.WriteAllBytesAsync(completedFile, new byte[1024]);
        await _db.SaveDownloadAsync(new DownloadItem
        {
            Id = "completed_1",
            Url = "https://releases.ubuntu.com/24.04/ubuntu-24.04.iso",
            FileName = "ubuntu-24.04.iso",
            DestinationPath = completedFile,
            TotalBytes = 1024,
            DownloadedBytes = 1024,
            Status = DownloadStatus.Completed,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-2)
        });

        // 2. Polluted claude.ai API markdown capture
        await _db.SaveDownloadAsync(new DownloadItem
        {
            Id = "phantom_claude",
            Url = "https://claude.ai/api/organizations/123/DocuForge-PDF-Toolbox-Documentation.md",
            FileName = "DocuForge-PDF-Toolbox-Documentation.md",
            DestinationPath = Path.Combine(_tempDir, "DocuForge-PDF-Toolbox-Documentation.md"),
            TotalBytes = 0,
            DownloadedBytes = 0,
            Status = DownloadStatus.Queued,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-5)
        });

        // 3. Polluted gradle.properties capture
        await _db.SaveDownloadAsync(new DownloadItem
        {
            Id = "phantom_gradle",
            Url = "https://example.com/gradle.properties",
            FileName = "gradle.properties",
            DestinationPath = Path.Combine(_tempDir, "gradle.properties"),
            TotalBytes = 0,
            DownloadedBytes = 0,
            Status = DownloadStatus.Failed,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-4)
        });

        // 4. Polluted YouTube Mix auto-capture sitting as Paused with 0 bytes
        await _db.SaveDownloadAsync(new DownloadItem
        {
            Id = "phantom_yt",
            JobKind = JobKind.Media,
            Url = "https://www.youtube.com/watch?v=abc&list=RDabc",
            FileName = "YouTube_Mix.mp4",
            DestinationPath = Path.Combine(_tempDir, "YouTube_Mix.mp4"),
            TotalBytes = 0,
            DownloadedBytes = 0,
            Status = DownloadStatus.Paused,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-3)
        });

        // 5. Transient Probing row
        await _db.SaveDownloadAsync(new DownloadItem
        {
            Id = "transient_probing",
            Url = "https://example.com/installer.exe",
            FileName = "installer.exe",
            DestinationPath = Path.Combine(_tempDir, "installer.exe"),
            TotalBytes = 0,
            DownloadedBytes = 0,
            Status = DownloadStatus.Probing,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        });

        var migration = new CaptureStateMigrationService(_db, _gatekeeper);
        var result = await migration.RunStartupCleanupAsync(_tempDir);

        result.WasOneTimeMigrationRun.Should().BeTrue();
        result.CleanedCount.Should().Be(4);

        var remaining = await _db.GetAllDownloadsAsync();
        remaining.Should().ContainSingle(d => d.Id == "completed_1" && d.Status == DownloadStatus.Completed);

        // Second run should see the marker file and report WasOneTimeMigrationRun = false, CleanedCount = 0
        var secondResult = await migration.RunStartupCleanupAsync(_tempDir);
        secondResult.WasOneTimeMigrationRun.Should().BeFalse();
        secondResult.CleanedCount.Should().Be(0);
    }

    [Fact]
    public async Task NativeMessageRouter_RejectsStaleAndIgnoredCaptures_WithAck()
    {
        var router = new NativeMessageRouter(_db, _categoryManager, gatekeeper: _gatekeeper);

        // 1. Stale message (createdAt 60s ago)
        var staleTime = DateTime.UtcNow.AddSeconds(-60).ToString("O");
        var stalePayload = JsonSerializer.Serialize(new
        {
            action = "interceptDownload",
            eventId = Guid.NewGuid().ToString("N"),
            createdAt = staleTime,
            url = "https://example.com/archive.zip",
            fileName = "archive.zip"
        });

        var staleResp = await router.RouteMessageAsync(stalePayload);
        var staleJson = JsonSerializer.Serialize(staleResp);
        staleJson.Should().Contain("\"success\":false");
        staleJson.Should().Contain("\"ack\":true");
        staleJson.Should().Contain("\"rejected\":true");

        // 2. Ignored claude.ai/api URL
        var apiPayload = JsonSerializer.Serialize(new
        {
            action = "interceptDownload",
            eventId = Guid.NewGuid().ToString("N"),
            createdAt = DateTime.UtcNow.ToString("O"),
            url = "https://claude.ai/api/organizations/123/DocuForge-PDF-Toolbox-Documentation.md",
            fileName = "DocuForge-PDF-Toolbox-Documentation.md"
        });

        var apiResp = await router.RouteMessageAsync(apiPayload);
        var apiJson = JsonSerializer.Serialize(apiResp);
        apiJson.Should().Contain("\"success\":false");
        apiJson.Should().Contain("\"rejected\":true");

        var all = await _db.GetAllDownloadsAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task LoopbackServer_EnforcesGatekeeperRules_AndStartupPurgesUnconfirmedQueuedPlaceholders()
    {
        var tokenManager = new LoopbackTokenManager(Path.Combine(_tempDir, "auth.token"));
        await using var server = new LoopbackServer(tokenManager, _db, _categoryManager, port: 0, gatekeeper: _gatekeeper);

        InterceptDownloadRequest? intercepted = null;
        server.DownloadIntercepted += (s, req) => intercepted = req;
        server.Start();

        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("X-SwiftGet-Token", tokenManager.GetOrCreateToken());

        // 1. Rejected capture: claude.ai/api
        var rejectedPayload = new InterceptDownloadRequest
        {
            EventId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            Url = "https://claude.ai/api/organizations/123/DocuForge-PDF-Toolbox-Documentation.md",
            FileName = "DocuForge-PDF-Toolbox-Documentation.md"
        };
        var rejectedContent = new System.Net.Http.StringContent(
            JsonSerializer.Serialize(rejectedPayload),
            System.Text.Encoding.UTF8,
            "application/json");
        var rejectedResponse = await http.PostAsync($"{server.BaseUrl}api/v1/downloads", rejectedContent);
        var rejectedResult = await rejectedResponse.Content.ReadFromJsonAsync<InterceptDownloadResponse>();
        rejectedResult!.Success.Should().BeFalse();
        rejectedResult.Message.Should().Contain("rejected");

        // 2. Allowed capture
        var allowedPayload = new InterceptDownloadRequest
        {
            EventId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            Url = "https://releases.ubuntu.com/24.04/ubuntu.iso",
            FileName = "ubuntu.iso",
            TotalBytes = 1024 * 1024 * 50
        };

        var allowedContent = new System.Net.Http.StringContent(
            JsonSerializer.Serialize(allowedPayload),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await http.PostAsync($"{server.BaseUrl}api/v1/downloads", allowedContent);
        response.IsSuccessStatusCode.Should().BeTrue();

        intercepted.Should().NotBeNull();
        intercepted!.FileName.Should().Be("ubuntu.iso");

        // If the user cancelled or the app restarted without confirmation, startup cleanup purges it
        var migration = new CaptureStateMigrationService(_db, _gatekeeper);
        var cleanupResult = await migration.RunStartupCleanupAsync(_tempDir);
        cleanupResult.CleanedCount.Should().Be(1);

        var dbRows = await _db.GetAllDownloadsAsync();
        dbRows.Should().BeEmpty();
    }
}
