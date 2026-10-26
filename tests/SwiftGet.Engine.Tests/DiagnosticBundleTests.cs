using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Diagnostics;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class DiagnosticBundleTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;

    public DiagnosticBundleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_diag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
    }

    public async ValueTask DisposeAsync()
    {
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
    public void RedactionEngine_RedactsSensitiveDataInText()
    {
        var rawText = $@"
            User log directory: C:\Users\{Environment.UserName}\Downloads
            Source URL: http://admin:supersecret@download.example.com/file.zip?token=mysecrettoken123&apikey=abc
            Headers:
            Authorization: Bearer eyJhbGciOiJIUzI1Ni...
            X-SwiftGet-Token: secret_loopback_token_888
            Cookie: sessionid=xyz123; user=test
        ";

        var redacted = RedactionEngine.RedactText(rawText);

        redacted.Should().NotContain(Environment.UserName);
        redacted.Should().Contain("C:\\Users\\<USER>\\Downloads");
        redacted.Should().NotContain("supersecret");
        redacted.Should().Contain("http://<REDACTED>@download.example.com");
        redacted.Should().NotContain("mysecrettoken123");
        redacted.Should().Contain("token=<REDACTED>");
        redacted.Should().NotContain("secret_loopback_token_888");
        redacted.Should().Contain("X-SwiftGet-Token: <REDACTED>");
        redacted.Should().NotContain("sessionid=xyz123");
        redacted.Should().Contain("Cookie: <REDACTED>");
    }

    [Fact]
    public async Task DiagnosticBundleService_GeneratesSanitizedZipArchive()
    {
        await _db.InitializeAsync();
        await _db.SaveDownloadAsync(new DownloadItem
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = "test.zip",
            Url = "https://example.com/test.zip",
            DestinationPath = Path.Combine(_tempDir, "test.zip"),
            Status = DownloadStatus.Completed,
            TotalBytes = 1024,
            DownloadedBytes = 1024
        });

        // Create sample log directory with a log file
        var logDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(logDir);
        var sampleLogPath = Path.Combine(logDir, "swiftget-sample.log");
        await File.WriteAllTextAsync(sampleLogPath, $"Sample log for user {Environment.UserName} at https://example.com?token=xyz");

        var service = new DiagnosticBundleService(_db, customLogDirectory: logDir);
        var zipPath = Path.Combine(_tempDir, "diagnostics_out.zip");

        var resultPath = await service.ExportDiagnosticBundleAsync(zipPath);

        File.Exists(resultPath).Should().BeTrue();

        using var archive = ZipFile.OpenRead(resultPath);
        archive.Entries.Should().Contain(e => e.FullName == "system_info.json");
        archive.Entries.Should().Contain(e => e.FullName == "network_info.json");
        archive.Entries.Should().Contain(e => e.FullName == "database_status.json");
        archive.Entries.Should().Contain(e => e.FullName.StartsWith("logs/"));

        // Verify system_info content
        var sysEntry = archive.GetEntry("system_info.json");
        sysEntry.Should().NotBeNull();
        using (var stream = sysEntry!.Open())
        using (var reader = new StreamReader(stream))
        {
            var content = await reader.ReadToEndAsync();
            content.Should().Contain("OSDescription");
            content.Should().Contain("FrameworkDescription");
        }

        // Verify redacted log content
        var logEntry = archive.GetEntry("logs/swiftget-sample.log");
        logEntry.Should().NotBeNull();
        using (var stream = logEntry!.Open())
        using (var reader = new StreamReader(stream))
        {
            var content = await reader.ReadToEndAsync();
            content.Should().NotContain(Environment.UserName);
            content.Should().Contain("<USER>");
            content.Should().Contain("token=<REDACTED>");
        }
    }
}
