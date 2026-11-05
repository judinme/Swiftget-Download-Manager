using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Server;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class LoopbackServerSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:5000", true)]
    [InlineData("localhost", true)]
    [InlineData("localhost:8080", true)]
    [InlineData("[::1]", true)]
    [InlineData("[::1]:9000", true)]
    [InlineData("attacker.com", false)]
    [InlineData("evil.attacker.com:8080", false)]
    [InlineData("192.168.1.50", false)]
    [InlineData("10.0.0.1:5000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedHost_EnforcesStrictLoopbackHostHeader_PreventingDnsRebinding(string? hostHeader, bool expectedAllowed)
    {
        var result = LoopbackServer.IsAllowedHost(hostHeader);
        result.Should().Be(expectedAllowed);
    }

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnop", true)]
    [InlineData("moz-extension://12345678-1234-1234-1234-123456789abc", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("null", false)]
    [InlineData("https://malicious-site.com", false)]
    [InlineData("http://evil-tracker.org", false)]
    [InlineData("https://google.com", false)]
    public void IsAllowedOrigin_RestrictsCORS_ToBrowserExtensionsAndLocalOrigins(string origin, bool expectedAllowed)
    {
        var result = LoopbackServer.IsAllowedOrigin(origin);
        result.Should().Be(expectedAllowed);
    }

    [Fact]
    public async Task PathTraversal_FileNameRejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_lb_sec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");
        var db = new SqliteDatabase(dbPath);
        await db.InitializeAsync();
        using var categoryManager = new CategoryManager(db);
        await categoryManager.InitializeAsync();
        var tokenPath = Path.Combine(tempDir, "auth.token");
        var tokenManager = new LoopbackTokenManager(tokenPath);
        var token = tokenManager.GetOrCreateToken();

        await using var server = new LoopbackServer(tokenManager, db, categoryManager, port: 0);
        server.Start();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
            client.DefaultRequestHeaders.Add("X-SwiftGet-Token", token);

            var payload = new InterceptDownloadRequest
            {
                Url = "https://example.com/downloads/file.bin",
                FileName = "../../Windows/StartUp/pwned.bat",
                DestinationPath = @"C:\Windows\Temp\pwned.bat"
            };

            var response = await client.PostAsJsonAsync("api/v1/downloads", payload);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var downloads = await db.GetAllDownloadsAsync();
            downloads.Should().ContainSingle();
            var item = downloads[0];

            var defaultDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            item.DestinationPath.Should().NotContain("..");
            item.DestinationPath.Should().StartWith(defaultDownloads);
            item.DestinationPath.Should().NotStartWith(@"C:\Windows\Temp");
        }
        finally
        {
            await db.DisposeAsync();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task OversizedBody_Returns413()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_lb_sec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");
        var db = new SqliteDatabase(dbPath);
        await db.InitializeAsync();
        using var categoryManager = new CategoryManager(db);
        var tokenPath = Path.Combine(tempDir, "auth.token");
        var tokenManager = new LoopbackTokenManager(tokenPath);
        var token = tokenManager.GetOrCreateToken();

        await using var server = new LoopbackServer(tokenManager, db, categoryManager, port: 0);
        server.Start();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
            client.DefaultRequestHeaders.Add("X-SwiftGet-Token", token);

            // 3 MB payload (limit is 2 MB)
            var hugeData = new string('A', 3 * 1024 * 1024);
            var content = new StringContent($"{{\"url\":\"http://example.com/test\",\"extra\":\"{hugeData}\"}}", System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync("api/v1/downloads", content);
            response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        }
        finally
        {
            await db.DisposeAsync();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task OriginNull_Rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_cors_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");
        var db = new SqliteDatabase(dbPath);
        await db.InitializeAsync();
        using var categoryManager = new CategoryManager(db);
        var tokenPath = Path.Combine(tempDir, "auth.token");
        var tokenManager = new LoopbackTokenManager(tokenPath);
        var token = tokenManager.GetOrCreateToken();

        await using var server = new LoopbackServer(tokenManager, db, categoryManager, port: 0);
        server.Start();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
            client.DefaultRequestHeaders.Add("X-SwiftGet-Token", token);
            client.DefaultRequestHeaders.Add("Origin", "null");

            var response = await client.GetAsync("api/v1/downloads");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        }
        finally
        {
            await db.DisposeAsync();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

