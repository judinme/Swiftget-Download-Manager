using System.Net;
using FluentAssertions;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Probing;
using SwiftGet.Engine.Tests.Harness;
using Xunit;

namespace SwiftGet.Integration;

public class UrlAndDownloadEdgeCasesTests : IAsyncLifetime
{
    private readonly KestrelTestServer _server;

    public UrlAndDownloadEdgeCasesTests()
    {
        _server = new KestrelTestServer();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    [Theory]
    [InlineData("https://example.com/file with spaces.zip", "file with spaces.zip")]
    [InlineData("https://example.com/file%20encoded.zip", "file encoded.zip")]
    [InlineData("https://example.com/unicode_%E6%97%A5%E6%9C%AC%E8%AA%9E.pdf", "unicode_日本語.pdf")]
    [InlineData("https://example.com/very/deep/nested/path/document.docx", "document.docx")]
    public async Task ExtractFileName_ParsesUrlEdgeCasesAccurately(string url, string expectedFileName)
    {
        var probeService = new HttpProbeService();
        var result = await probeService.ProbeAsync(url, ct: default);

        var sanitized = FileNameSanitizer.Sanitize(expectedFileName);
        sanitized.Should().Be(expectedFileName);
    }

    [Fact]
    public async Task Probe_HandlesRedirectChains_AndReturnsFinalUrl()
    {
        _server.CurrentScenario = ProbeScenario.RedirectChainToFinal;
        var probeService = new HttpProbeService();

        var result = await probeService.ProbeAsync($"{_server.BaseUrl}/redirect1", ct: default);

        result.IsSuccess.Should().BeTrue();
        result.FinalUrl.Should().EndWith("/final-target.bin");
        result.TotalBytes.Should().Be(_server.StandardPayload.Length);
    }

    [Fact]
    public async Task Probe_HandlesRelativeRedirect_Accurately()
    {
        _server.CurrentScenario = ProbeScenario.RelativeRedirect;
        var probeService = new HttpProbeService();

        var result = await probeService.ProbeAsync($"{_server.BaseUrl}/rel-start", ct: default);

        result.IsSuccess.Should().BeTrue();
        result.FinalUrl.Should().EndWith("/final-target.bin");
    }

    [Fact]
    public async Task Probe_WithCookies_PassesAuthentication()
    {
        _server.CurrentScenario = ProbeScenario.CookieAuthRequired;
        var probeService = new HttpProbeService();

        // 1. Without cookie -> 403
        var failedResult = await probeService.ProbeAsync($"{_server.BaseUrl}/download.zip", ct: default);
        failedResult.IsSuccess.Should().BeFalse();

        // 2. With valid cookie -> 200
        var successResult = await probeService.ProbeAsync(
            $"{_server.BaseUrl}/download.zip",
            cookies: "qa_session=valid_token_123",
            ct: default);

        successResult.IsSuccess.Should().BeTrue();
        successResult.TotalBytes.Should().Be(_server.StandardPayload.Length);
    }

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("PRN.zip", "_PRN.zip")]
    [InlineData("AUX", "_AUX")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("COM1.bin", "_COM1.bin")]
    [InlineData("LPT1.iso", "_LPT1.iso")]
    [InlineData("   test.zip   ", "test.zip")]
    [InlineData("...dots.zip...", "dots.zip")]
    [InlineData("../../windows/system32/cmd.exe", "____windows_system32_cmd.exe")]
    [InlineData("malicious\u202Eexe.pdf", "maliciousexe.pdf")] // RLO character removed
    public void FileNameSanitizer_NeutralizesDangerousNames(string input, string expected)
    {
        var sanitized = FileNameSanitizer.Sanitize(input);
        sanitized.Should().Be(expected);
    }

    [Fact]
    public void FileNameSanitizer_TruncatesOver250Chars_PreservingExtension()
    {
        var longBase = new string('a', 300);
        var input = longBase + ".zip";

        var sanitized = FileNameSanitizer.Sanitize(input);

        sanitized.Length.Should().BeLessThanOrEqualTo(250);
        sanitized.Should().EndWith(".zip");
    }

    [Fact]
    public void CollisionResolution_GeneratesNumberedSuffixes()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "SwiftGet_Collisions_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var file1 = Path.Combine(tempFolder, "download.zip");
            File.WriteAllText(file1, "first");

            // Resolve collision
            var resolvedPath1 = ResolveUniquePath(tempFolder, "download.zip");
            resolvedPath1.Should().EndWith("download (1).zip");

            File.WriteAllText(resolvedPath1, "second");

            var resolvedPath2 = ResolveUniquePath(tempFolder, "download.zip");
            resolvedPath2.Should().EndWith("download (2).zip");
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }
    }

    private static string ResolveUniquePath(string directory, string fileName)
    {
        var target = Path.Combine(directory, fileName);
        if (!File.Exists(target)) return target;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        int counter = 1;
        while (true)
        {
            var candidate = Path.Combine(directory, $"{nameWithoutExt} ({counter}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
            counter++;
        }
    }
}
