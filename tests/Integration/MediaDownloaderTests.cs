using FluentAssertions;
using SwiftGet.Media.Extractor;
using SwiftGet.Media.Models;
using SwiftGet.Media.Sidecars;
using Xunit;

namespace SwiftGet.Integration;

public class MediaDownloaderTests
{
    [Fact]
    public void SidecarManager_WhenSidecarsDirEmpty_ReportsBinariesUnavailable()
    {
        var tempSidecars = Path.Combine(Path.GetTempPath(), $"swiftget_sidecars_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSidecars);

        try
        {
            var manager = new SidecarManager(tempSidecars);
            manager.IsYtDlpAvailable().Should().BeFalse();
            manager.IsFfmpegAvailable().Should().BeFalse();
            manager.FindYtDlpPath().Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempSidecars)) Directory.Delete(tempSidecars, true);
        }
    }

    [Fact]
    public void SidecarManager_VerifyBinary_ValidatesSha256Checksum()
    {
        var tempSidecars = Path.Combine(Path.GetTempPath(), $"swiftget_sidecars_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSidecars);
        var dummyFile = Path.Combine(tempSidecars, "test-binary.exe");
        File.WriteAllText(dummyFile, "dummy binary payload");

        try
        {
            var manager = new SidecarManager(tempSidecars);

            // Compute actual hash
            var bytes = File.ReadAllBytes(dummyFile);
            var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            manager.RegisterAllowedHash("test-binary.exe", actualHash);
            manager.VerifyBinary(dummyFile).Should().BeTrue();

            manager.RegisterAllowedHash("test-binary.exe", "0000000000000000000000000000000000000000000000000000000000000000");
            manager.VerifyBinary(dummyFile).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempSidecars)) Directory.Delete(tempSidecars, true);
        }
    }

    [Fact]
    public void YtDlpMediaExtractor_ParseMetadataFromJson_ParsesStreamsAccurately()
    {
        const string json = @"
        {
            ""title"": ""Open Source Test Video"",
            ""uploader"": ""Community Test"",
            ""duration"": 125,
            ""thumbnail"": ""https://example.com/thumb.jpg"",
            ""formats"": [
                {
                    ""format_id"": ""137"",
                    ""ext"": ""mp4"",
                    ""resolution"": ""1920x1080"",
                    ""vcodec"": ""avc1"",
                    ""acodec"": ""none"",
                    ""filesize"": 52428800
                },
                {
                    ""format_id"": ""140"",
                    ""ext"": ""m4a"",
                    ""resolution"": ""audio only"",
                    ""vcodec"": ""none"",
                    ""acodec"": ""mp4a.40.2"",
                    ""filesize"": 2097152
                }
            ]
        }";

        var metadata = YtDlpMediaExtractor.ParseMetadataFromJson(json, "https://example.com/video");

        metadata.Title.Should().Be("Open Source Test Video");
        metadata.Uploader.Should().Be("Community Test");
        metadata.Duration.Should().Be(TimeSpan.FromSeconds(125));
        metadata.ThumbnailUrl.Should().Be("https://example.com/thumb.jpg");
        metadata.Formats.Should().HaveCount(2);

        var videoFormat = metadata.Formats.First(f => f.FormatId == "137");
        videoFormat.Extension.Should().Be("mp4");
        videoFormat.Resolution.Should().Be("1920x1080");
        videoFormat.HasVideo.Should().BeTrue();
        videoFormat.HasAudio.Should().BeFalse();
    }

    [Fact]
    public void YtDlpMediaExtractor_ParseMetadataFromJson_DetectsDrmFlagAndThrows()
    {
        const string drmJson = @"
        {
            ""title"": ""Encrypted Premium Stream"",
            ""_has_drm"": true,
            ""formats"": []
        }";

        var act = () => YtDlpMediaExtractor.ParseMetadataFromJson(drmJson, "https://example.com/drm-video");

        act.Should().Throw<DrmProtectionDetectedException>()
           .WithMessage("*DRM*");
    }
}
