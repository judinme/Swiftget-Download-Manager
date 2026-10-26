using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace SwiftGet.Integration;

public class MediaTestHarnessTests : IDisposable
{
    private readonly string _tempDir;

    public MediaTestHarnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"swiftget_media_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GenerateTestVideo_1080p_FfprobeValidates1080pAndH264()
    {
        var outPath = Path.Combine(_tempDir, "video_1080p.mp4");
        await MediaTestHarness.GenerateTestVideoAsync(outPath, 1920, 1080, "libx264", "aac", durationSec: 1);

        File.Exists(outPath).Should().BeTrue();
        new FileInfo(outPath).Length.Should().BeGreaterThan(1024);

        var metrics = await MediaTestHarness.FfprobeInspectAsync(outPath);
        metrics.Width.Should().Be(1920);
        metrics.Height.Should().Be(1080);
        metrics.VideoCodec.Should().Be("h264");
        metrics.AudioCodec.Should().Be("aac");
        metrics.Fps.Should().Be(30);
        metrics.DurationSeconds.Should().BeGreaterThanOrEqualTo(0.9);

        var (isValid, errorMsg) = await MediaTestHarness.FfmpegDecodeCheckAsync(outPath);
        isValid.Should().BeTrue(because: $"Decode check failed: {errorMsg}");
        errorMsg.Should().BeNull();
    }

    [Fact]
    public async Task GenerateTestVideo_720p_FfprobeValidates720p()
    {
        var outPath = Path.Combine(_tempDir, "video_720p.mp4");
        await MediaTestHarness.GenerateTestVideoAsync(outPath, 1280, 720, "libx264", "aac", durationSec: 1);

        File.Exists(outPath).Should().BeTrue();
        var metrics = await MediaTestHarness.FfprobeInspectAsync(outPath);
        metrics.Width.Should().Be(1280);
        metrics.Height.Should().Be(720);

        var (isValid, errorMsg) = await MediaTestHarness.FfmpegDecodeCheckAsync(outPath);
        isValid.Should().BeTrue();
    }
}
