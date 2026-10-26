using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using SwiftGet.Engine.Updating;
using Xunit;

namespace SwiftGet.Engine.Tests.Updating;

public class UpdateServiceTests : IAsyncLifetime
{
    private string _tempDir = null!;

    public Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"swiftget_update_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NewerVersionAvailable_ReturnsUpdateAvailable()
    {
        var manifest = new UpdateManifest
        {
            Version = "1.2.0",
            ReleaseDate = DateTimeOffset.UtcNow,
            ReleaseNotes = "Fixed swarm performance and added new features.",
            DownloadUrl = "http://localhost/downloads/SwiftGet-1.2.0-x64.msi",
            Sha256Checksum = new string('a', 64)
        };

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(ctx =>
                    {
                        ctx.Response.ContentType = "application/json";
                        return ctx.Response.WriteAsync(JsonSerializer.Serialize(manifest));
                    });
                });
            })
            .Start();

        var client = host.GetTestClient();
        var service = new UpdateService(client);

        var result = await service.CheckForUpdatesAsync("http://localhost/updates.json", new Version(1, 0, 0));

        result.IsUpdateAvailable.Should().BeTrue();
        result.LatestVersion.Should().Be(new Version(1, 2, 0));
        result.Manifest.Should().NotBeNull();
        result.Manifest!.ReleaseNotes.Should().Be("Fixed swarm performance and added new features.");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SameOrOlderVersion_ReturnsNoUpdate()
    {
        var manifest = new UpdateManifest
        {
            Version = "1.0.0",
            DownloadUrl = "http://localhost/downloads/SwiftGet-1.0.0-x64.msi",
            Sha256Checksum = new string('b', 64)
        };

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(ctx => ctx.Response.WriteAsync(JsonSerializer.Serialize(manifest)));
                });
            })
            .Start();

        var client = host.GetTestClient();
        var service = new UpdateService(client);

        var resultSame = await service.CheckForUpdatesAsync("http://localhost/updates.json", new Version(1, 0, 0));
        resultSame.IsUpdateAvailable.Should().BeFalse();

        var resultOlder = await service.CheckForUpdatesAsync("http://localhost/updates.json", new Version(1, 5, 0));
        resultOlder.IsUpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_HttpError_ReturnsErrorResult()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        return ctx.Response.WriteAsync("Server Error");
                    });
                });
            })
            .Start();

        var client = host.GetTestClient();
        var service = new UpdateService(client);

        var result = await service.CheckForUpdatesAsync("http://localhost/updates.json", new Version(1, 0, 0));
        result.IsUpdateAvailable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("500");
    }

    [Fact]
    public async Task DownloadUpdateAsync_ValidChecksum_SucceedsAndMatchesPayload()
    {
        var msiPayload = new byte[32 * 1024];
        RandomNumberGenerator.Fill(msiPayload);
        var expectedHash = Convert.ToHexString(SHA256.HashData(msiPayload));

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        ctx.Response.ContentType = "application/x-msi";
                        ctx.Response.ContentLength = msiPayload.Length;
                        await ctx.Response.Body.WriteAsync(msiPayload);
                    });
                });
            })
            .Start();

        var client = host.GetTestClient();
        var service = new UpdateService(client);

        var manifest = new UpdateManifest
        {
            Version = "1.0.1",
            DownloadUrl = "http://localhost/SwiftGet-1.0.1-x64.msi",
            Sha256Checksum = expectedHash
        };

        var progressReported = false;
        var progress = new Progress<double>(p => { if (p > 0) progressReported = true; });

        var downloadedPath = await service.DownloadUpdateAsync(manifest, _tempDir, progress);

        File.Exists(downloadedPath).Should().BeTrue();
        var downloadedBytes = await File.ReadAllBytesAsync(downloadedPath);
        downloadedBytes.Should().Equal(msiPayload);
        progressReported.Should().BeTrue();
    }

    [Fact]
    public async Task DownloadUpdateAsync_CorruptedChecksum_ThrowsSecurityExceptionAndDeletesFile()
    {
        var msiPayload = new byte[16 * 1024];
        RandomNumberGenerator.Fill(msiPayload);

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        ctx.Response.ContentLength = msiPayload.Length;
                        await ctx.Response.Body.WriteAsync(msiPayload);
                    });
                });
            })
            .Start();

        var client = host.GetTestClient();
        var service = new UpdateService(client);

        var manifest = new UpdateManifest
        {
            Version = "1.0.1",
            DownloadUrl = "http://localhost/SwiftGet-1.0.1-x64.msi",
            Sha256Checksum = new string('f', 64) // Intentionally wrong hash
        };

        var action = async () => await service.DownloadUpdateAsync(manifest, _tempDir);
        await action.Should().ThrowAsync<SecurityException>();

        var expectedFile = Path.Combine(_tempDir, "SwiftGet-1.0.1-x64.msi");
        File.Exists(expectedFile).Should().BeFalse("Corrupted installer must be purged immediately.");
    }

    [Fact]
    public async Task OldManifest_NotTreatedAsUpdate()
    {
        var manifest = new UpdateManifest
        {
            Version = "0.9.0",
            MinSupportedVersion = "1.0.0",
            DownloadUrl = "http://localhost/SwiftGet-0.9.0-x64.msi",
            Sha256Checksum = new string('c', 64)
        };

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(ctx => ctx.Response.WriteAsync(JsonSerializer.Serialize(manifest)));
                });
            })
            .Start();

        var client = host.GetTestClient();
        var service = new UpdateService(client);

        var result = await service.CheckForUpdatesAsync("http://localhost/updates.json", new Version(1, 0, 0));
        result.IsUpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public void LaunchInstaller_EscapesSpaces()
    {
        var installerPath = Path.Combine(_tempDir, "Program Files", "SwiftGet Setup", "installer with spaces.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
        File.WriteAllText(installerPath, "dummy msi payload");

        var psi = UpdateService.CreateInstallerStartInfo(installerPath, arguments: "/passive /norestart");

        psi.FileName.Should().Be("msiexec.exe");
        psi.ArgumentList.Should().Contain("/i");
        psi.ArgumentList.Should().Contain(installerPath);
        psi.ArgumentList.Should().Contain("/passive");
        psi.ArgumentList.Should().Contain("/norestart");
        psi.UseShellExecute.Should().BeFalse();
    }
}
