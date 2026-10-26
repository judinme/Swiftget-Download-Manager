using System;
using FluentAssertions;
using SwiftGet.Engine.Batch;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class BatchPatternParserTests
{
    [Fact]
    public void GenerateUrls_NumericRange_ShouldGenerateSequentialUrls()
    {
        var urls = BatchPatternParser.GenerateUrls("https://example.com/file[1-4].bin");
        urls.Should().Equal(
            "https://example.com/file1.bin",
            "https://example.com/file2.bin",
            "https://example.com/file3.bin",
            "https://example.com/file4.bin"
        );
    }

    [Fact]
    public void GenerateUrls_ZeroPaddedRange_ShouldPreserveLeadingZeros()
    {
        var urls = BatchPatternParser.GenerateUrls("https://example.com/img_[008-011].jpg");
        urls.Should().Equal(
            "https://example.com/img_008.jpg",
            "https://example.com/img_009.jpg",
            "https://example.com/img_010.jpg",
            "https://example.com/img_011.jpg"
        );
    }

    [Fact]
    public void GenerateUrls_NumericRangeWithStep_ShouldIncrementByStep()
    {
        var urls = BatchPatternParser.GenerateUrls("https://example.com/chunk_[0-10:3].dat");
        urls.Should().Equal(
            "https://example.com/chunk_0.dat",
            "https://example.com/chunk_3.dat",
            "https://example.com/chunk_6.dat",
            "https://example.com/chunk_9.dat"
        );
    }

    [Fact]
    public void GenerateUrls_CharacterRange_ShouldGenerateAlphabeticalSequence()
    {
        var urls = BatchPatternParser.GenerateUrls("https://mirror.org/part_[a-d].tar");
        urls.Should().Equal(
            "https://mirror.org/part_a.tar",
            "https://mirror.org/part_b.tar",
            "https://mirror.org/part_c.tar",
            "https://mirror.org/part_d.tar"
        );
    }

    [Fact]
    public void GenerateUrls_MultipleRangeTokens_ShouldGenerateCartesianProduct()
    {
        var urls = BatchPatternParser.GenerateUrls("https://example.com/[a-b]/file_[1-2].png");
        urls.Should().Equal(
            "https://example.com/a/file_1.png",
            "https://example.com/a/file_2.png",
            "https://example.com/b/file_1.png",
            "https://example.com/b/file_2.png"
        );
    }

    [Fact]
    public void IsValidPattern_WithExcessiveRange_ShouldReturnFalseWithErrorMessage()
    {
        var isValid = BatchPatternParser.IsValidPattern("https://example.com/[1-10000].bin", out var error);
        isValid.Should().BeFalse();
        error.Should().Contain("exceeding limit");
    }

    [Fact]
    public void IsValidPattern_WithoutPattern_ShouldReturnFalse()
    {
        var isValid = BatchPatternParser.IsValidPattern("https://example.com/static_file.bin", out var error);
        isValid.Should().BeFalse();
        error.Should().Contain("No valid range pattern");
    }

    [Fact]
    public void IsValidPattern_ValidSyntax_ShouldReturnTrue()
    {
        var isValid = BatchPatternParser.IsValidPattern("https://example.com/img_[001-050].jpg", out var error);
        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void LargeRange_RejectedFast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var isValid = BatchPatternParser.IsValidPattern("https://example.com/file_[1-999999].bin", out var error);
        sw.Stop();

        isValid.Should().BeFalse();
        error.Should().Contain("exceeding limit");
        sw.ElapsedMilliseconds.Should().BeLessThan(50, "arithmetic range validation must reject giant ranges within 50 ms");

        var generateAction = () => BatchPatternParser.GenerateUrls("https://example.com/file_[1-999999].bin");
        generateAction.Should().Throw<InvalidOperationException>()
            .WithMessage("*exceeded max limit*");
    }
}
