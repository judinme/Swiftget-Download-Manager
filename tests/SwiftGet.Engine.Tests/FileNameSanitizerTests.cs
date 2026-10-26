using FluentAssertions;
using SwiftGet.Engine.IO;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("normal_file.zip", "normal_file.zip")]
    [InlineData("../../etc/passwd", "____etc_passwd")]
    [InlineData("..\\..\\Windows\\System32\\cmd.exe", "____Windows_System32_cmd.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("aux.tar.gz", "_aux.tar.gz")]
    [InlineData("NUL", "_NUL")]
    [InlineData("com1.dat", "_com1.dat")]
    [InlineData("file?with*invalid:chars.bin", "filewithinvalidchars.bin")]
    [InlineData("   spaced.iso   ", "spaced.iso")]
    [InlineData("...dots.pdf...", "dots.pdf")]
    [InlineData("", "download.bin")]
    [InlineData("   ", "download.bin")]
    public void Sanitize_ShouldProduceSafeFileName(string input, string expected)
    {
        var result = FileNameSanitizer.Sanitize(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("(50) The new iPad mini: Thinner. Lighter. Ultraportable. - YouTube", "The new iPad mini - Thinner. Lighter. Ultraportable")]
    [InlineData("(1) Breaking News: Important Update! - Vimeo", "Breaking News - Important Update!")]
    [InlineData("Video Title With: Multiple: Colons - Dailymotion", "Video Title With - Multiple - Colons")]
    [InlineData("Just A Title... ", "Just A Title")]
    public void SanitizeMediaTitle_ShouldStripCountersSiteSuffixesAndColons(string input, string expected)
    {
        var result = FileNameSanitizer.SanitizeMediaTitle(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void SanitizeFileNameWithExtension_ShouldNeverProduceDoubleDot()
    {
        var baseTitle = "The new iPad mini: Thinner. Lighter. Ultraportable.";
        var result = FileNameSanitizer.SanitizeFileNameWithExtension(baseTitle, "mp4");
        result.Should().Be("The new iPad mini - Thinner. Lighter. Ultraportable.mp4");
        result.Should().NotContain("..mp4");
    }

    [Theory]
    [InlineData(20502549L, "19.55 MB (20,502,549 bytes)")]
    [InlineData(1024L, "1.00 KB (1,024 bytes)")]
    [InlineData(0L, "0 B (0 bytes)")]
    public void FormatFileSizeWithExactBytes_ShouldFormatConsistently(long bytes, string expected)
    {
        var result = FileNameSanitizer.FormatFileSizeWithExactBytes(bytes);
        result.Should().Be(expected);
    }
}
