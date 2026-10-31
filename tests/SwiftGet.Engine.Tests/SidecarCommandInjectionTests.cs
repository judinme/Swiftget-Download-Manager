using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Media.Extractor;
using SwiftGet.Media.Recorder;
using SwiftGet.Media.Sidecars;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class SidecarCommandInjectionTests
{
    [Fact]
    public async Task UrlWithQuotes_DoesNotEscapeArgs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_cmd_sec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var maliciousUrl = "https://example.com/video\" --exec \"calc.exe";
            var manager = new SidecarManager(tempDir);
            var extractor = new YtDlpMediaExtractor(manager);

            // 1. Extractor URL validation rejects quotes/control chars
            var act = async () => await extractor.ExtractMetadataAsync(maliciousUrl);
            await act.Should().ThrowAsync<ArgumentException>();

            // 2. Direct ArgumentList execution isolates quotes without shell interpolation
            var argWithQuotes = "param\" --exec \"calc.exe";
            var result = await SidecarManager.ExecuteSidecarAsync("node.exe", ["-e", "console.log(JSON.stringify(process.argv.slice(1)))", argWithQuotes]);
            result.Success.Should().BeTrue();
            var json = result.StandardOutput.Trim();
            var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
            parsed.Should().NotBeNull();
            parsed!.Length.Should().Be(1);
            parsed[0].Should().Be(argWithQuotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StreamRecorder_UrlWithQuotes_ThrowsArgumentException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_cmd_sec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var manager = new SidecarManager(tempDir);
            var recorder = new StreamRecorder(manager);
            var act = async () => await recorder.RecordStreamAsync("https://example.com/video\" --exec \"calc.exe", Path.Combine(tempDir, "out.mp4"));
            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FakeSidecar_WithWrongHash_IsNotAllowed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_sidecar_sec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var fakeYtDlp = Path.Combine(tempDir, "yt-dlp.exe");
            File.WriteAllText(fakeYtDlp, "malicious sidecar binary");

            var manager = new SidecarManager(tempDir);
            manager.RegisterAllowedHash("yt-dlp.exe", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

            manager.VerifyBinary(fakeYtDlp).Should().BeFalse();
            manager.FindYtDlpPath().Should().BeNull();
            manager.IsYtDlpAvailable().Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ResolveProducedOutputPath_HandlesAppendedOrReplacedContainerExtension()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "swiftget_mux_ext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var expectedMp4 = Path.Combine(tempDir, "video_stream.mp4");
            var appendedMkv = expectedMp4 + ".mkv";
            File.WriteAllBytes(appendedMkv, [0x1A, 0x45, 0xDF, 0xA3]);

            var resolved = StreamRecorder.ResolveProducedOutputPath(expectedMp4);
            resolved.Should().Be(Path.Combine(tempDir, "video_stream.mkv"));
            File.Exists(resolved).Should().BeTrue();
            File.Exists(appendedMkv).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
