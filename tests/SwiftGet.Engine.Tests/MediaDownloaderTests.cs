using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Media.Extractor;
using SwiftGet.Media.Models;
using SwiftGet.Media.Recorder;
using SwiftGet.Media.Sidecars;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class MediaDownloaderTests : IDisposable
{
    private readonly string _tempDir;

    public MediaDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_media_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void SidecarManager_VerifyFileSha256_ValidatesCorrectly()
    {
        var sampleFile = Path.Combine(_tempDir, "sample.bin");
        var content = "SwiftGet media sidecar test content"u8.ToArray();
        File.WriteAllBytes(sampleFile, content);

        var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        SidecarManager.VerifyFileSha256(sampleFile, expectedHash).Should().BeTrue();
        SidecarManager.VerifyFileSha256(sampleFile, "0000000000000000000000000000000000000000000000000000000000000000").Should().BeFalse();
        SidecarManager.VerifyFileSha256(Path.Combine(_tempDir, "nonexistent.bin"), expectedHash).Should().BeFalse();
    }

    [Fact]
    public void SidecarManager_FindBinary_FindsFileInSidecarsDirectory()
    {
        var sidecarDir = Path.Combine(_tempDir, "custom_sidecars");
        Directory.CreateDirectory(sidecarDir);

        var fakeYtDlp = Path.Combine(sidecarDir, "yt-dlp.exe");
        File.WriteAllText(fakeYtDlp, "fake binary content");

        var manager = new SidecarManager(sidecarDir);
        manager.FindYtDlpPath().Should().Be(fakeYtDlp);
        manager.IsYtDlpAvailable().Should().BeTrue();
    }

    [Fact]
    public void MediaExtractor_ParseMetadataFromJson_ParsesFieldsAndFormats()
    {
        var sampleJson = """
        {
            "title": "4K Wildlife Nature Documentary",
            "duration": 360.5,
            "thumbnail": "https://example.com/thumb.jpg",
            "uploader": "Earth Discovery",
            "description": "Breathtaking landscapes in 4K 60fps.",
            "formats": [
                {
                    "format_id": "sb0",
                    "vcodec": "mhtml",
                    "acodec": "none",
                    "resolution": "storyboard"
                },
                {
                    "format_id": "140",
                    "ext": "m4a",
                    "vcodec": "none",
                    "acodec": "mp4a.40.2",
                    "tbr": 128.0,
                    "filesize": 5760000
                },
                {
                    "format_id": "137",
                    "ext": "mp4",
                    "vcodec": "avc1.640028",
                    "acodec": "none",
                    "height": 1080,
                    "tbr": 4500.0,
                    "filesize": 150000000,
                    "fps": 60
                }
            ],
            "subtitles": {
                "en": [
                    { "url": "https://example.com/sub_en.vtt", "ext": "vtt" }
                ]
            }
        }
        """;

        var meta = YtDlpMediaExtractor.ParseMetadataFromJson(sampleJson, "https://example.com/watch?v=123");

        meta.Title.Should().Be("4K Wildlife Nature Documentary");
        meta.Duration.TotalSeconds.Should().BeApproximately(360.5, 0.1);
        meta.ThumbnailUrl.Should().Be("https://example.com/thumb.jpg");
        meta.Uploader.Should().Be("Earth Discovery");

        // Formats: storyboard format "sb0" was filtered out
        meta.Formats.Should().HaveCount(2);

        var audioFormat = meta.Formats.Find(f => f.FormatId == "140");
        audioFormat.Should().NotBeNull();
        audioFormat!.HasVideo.Should().BeFalse();
        audioFormat.HasAudio.Should().BeTrue();
        audioFormat.Resolution.Should().Be("Audio Only");

        var videoFormat = meta.Formats.Find(f => f.FormatId == "137");
        videoFormat.Should().NotBeNull();
        videoFormat!.HasVideo.Should().BeTrue();
        videoFormat.Resolution.Should().Be("1080p");
        videoFormat.Fps.Should().Be(60);
        videoFormat.QualityLabel.Should().Contain("1080p (MP4)");

        // Subtitles
        meta.Subtitles.Should().ContainSingle();
        meta.Subtitles[0].Language.Should().Be("en");
        meta.Subtitles[0].DisplayName.Should().Be("English");
    }

    [Fact]
    public void MediaExtractor_DrmStream_ThrowsDrmProtectionDetectedException()
    {
        var drmJson = """
        {
            "title": "Protected Premium Movie",
            "_has_drm": true,
            "formats": []
        }
        """;

        var act = () => YtDlpMediaExtractor.ParseMetadataFromJson(drmJson, "https://example.com/drm");

        act.Should().Throw<DrmProtectionDetectedException>()
            .WithMessage($"*{DrmProtectionDetectedException.DefaultNotice}*");
    }

    [Fact]
    public void MediaExtractor_NullHasDrm_ParsesSuccessfully()
    {
        var jsonWithNullDrm = """
        {
            "title": "YouTube Video with Null DRM Fields",
            "_has_drm": null,
            "formats": [
                {
                    "format_id": "18",
                    "ext": "mp4",
                    "resolution": "360p",
                    "vcodec": "avc1.42001E",
                    "acodec": "mp4a.40.2",
                    "has_drm": null,
                    "filesize": 1234567
                }
            ]
        }
        """;

        var meta = YtDlpMediaExtractor.ParseMetadataFromJson(jsonWithNullDrm, "https://www.youtube.com/watch?v=AdKEVQvA9SY");

        meta.Title.Should().Be("YouTube Video with Null DRM Fields");
        meta.Formats.Should().HaveCount(1);
        meta.Formats[0].FormatId.Should().Be("18");
        meta.Formats[0].Extension.Should().Be("mp4");
        meta.Formats[0].Resolution.Should().Be("360p");
        meta.Formats[0].FileSizeBytes.Should().Be(1234567);
    }

    [Fact]
    public async Task MediaExtractor_RealSidecar_ParsesYouTubeLiveUrlWithoutException()
    {
        var manager = new SidecarManager();
        if (!manager.IsYtDlpAvailable()) return;

        var extractor = new YtDlpMediaExtractor(manager);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var meta = await extractor.ExtractMetadataAsync("https://www.youtube.com/watch?v=AdKEVQvA9SY", cts.Token);

        meta.Should().NotBeNull();
        meta.Title.Should().NotBeNullOrWhiteSpace();
        meta.Title.Should().NotBe("Unknown Title");
        meta.Formats.Should().NotBeEmpty();
    }

    [Fact]
    public void StreamRecorder_BuildFfmpegMuxArguments_BuildsCorrectLosslessMuxCommand()
    {
        var videoPath = "C:\\Temp\\video.mp4";
        var audioPath = "C:\\Temp\\audio.m4a";
        var outputPath = "C:\\Temp\\final.mp4";

        var args = StreamRecorder.BuildFfmpegMuxArguments(videoPath, audioPath, outputPath);

        args.Should().Contain("-c copy");
        args.Should().Contain("-movflags +faststart");
        args.Should().Contain(videoPath);
        args.Should().Contain(audioPath);
        args.Should().Contain(outputPath);
    }
}
