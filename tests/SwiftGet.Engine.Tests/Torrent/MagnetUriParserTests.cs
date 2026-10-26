using System;
using FluentAssertions;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public class MagnetUriParserTests
{
    [Fact]
    public void Parse_Valid40HexInfoHash_ReturnsParsedMagnet()
    {
        var uri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Ubuntu+22.04+Desktop&tr=udp%3A%2F%2Ftracker.openbittorrent.com%3A6969%2Fannounce&tr=http%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce&ws=https%3A%2F%2Fubuntu.com%2Fdownload%2Fdesktop.iso";

        var result = MagnetUriParser.Parse(uri);

        result.Should().NotBeNull();
        result.InfoHash.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
        result.DisplayName.Should().Be("Ubuntu 22.04 Desktop");
        result.Trackers.Should().HaveCount(2);
        result.Trackers.Should().Contain("udp://tracker.openbittorrent.com:6969/announce");
        result.Trackers.Should().Contain("http://tracker.opentrackr.org:1337/announce");
        result.WebSeeds.Should().ContainSingle().Which.Should().Be("https://ubuntu.com/download/desktop.iso");
        result.IsV2.Should().BeFalse();
    }

    [Fact]
    public void Parse_ValidBase32InfoHash_ConvertsToHexProperly()
    {
        // 32 chars base32 = 20 bytes SHA-1
        // B32: "4XNXK5E2NLB7XN62O2B5M5ZXZ5Z2N2P7"
        var uri = "magnet:?xt=urn:btih:4XNXK5E2NLB7XN62O2B5M5ZXZ5Z2N2P7&dn=Arch+Linux";

        var result = MagnetUriParser.Parse(uri);

        result.Should().NotBeNull();
        result.InfoHash.Should().HaveLength(40);
        result.DisplayName.Should().Be("Arch Linux");
    }

    [Fact]
    public void Parse_ValidBitTorrentV2Multihash_DetectsV2()
    {
        var sha256Hex = "bb9a907e59b58097b6a655ad1e3ed99fb551df4c4ecb7b4a2d815774a3f3ca77";
        var uri = $"magnet:?xt=urn:btmh:1220{sha256Hex}&dn=V2Payload";

        var result = MagnetUriParser.Parse(uri);

        result.Should().NotBeNull();
        result.InfoHash.Should().Be(sha256Hex);
        result.IsV2.Should().BeTrue();
    }

    [Fact]
    public void Parse_SelectOnlyIndicesAndRanges_ParsesAllDistinctIndices()
    {
        var uri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&so=0,2,4-6,8";

        var result = MagnetUriParser.Parse(uri);

        result.Should().NotBeNull();
        result.SelectOnlyIndices.Should().Equal(0, 2, 4, 5, 6, 8);
    }

    [Fact]
    public void Parse_PathTraversalInDisplayName_SanitizesSafely()
    {
        var uri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=..%2F..%2FWindows%2FSystem32%2Fevil.dll";

        var result = MagnetUriParser.Parse(uri);

        result.DisplayName.Should().NotContain("..");
        result.DisplayName.Should().NotContain("/");
        result.DisplayName.Should().NotContain("\\");
        result.DisplayName.Should().Be("Windows_System32_evil.dll");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.com/not-a-magnet")]
    [InlineData("magnet:?")]
    [InlineData("magnet:?dn=TestWithoutXt")]
    [InlineData("magnet:?xt=urn:btih:shortHex")]
    [InlineData("magnet:?xt=urn:btih:invalid_non_hex_character_in_the_hash_string")]
    [InlineData("magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&so=-1")]
    [InlineData("magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&so=5-2")]
    public void TryParse_MalformedInput_ReturnsFalseWithDescriptiveError(string? malformedUri)
    {
        var success = MagnetUriParser.TryParse(malformedUri, out var result, out var errorMessage);

        success.Should().BeFalse();
        result.Should().BeNull();
        errorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryParse_ExcessiveLength_RejectsWithBufferLimitError()
    {
        var hugeUri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=" + new string('A', 9000);

        var success = MagnetUriParser.TryParse(hugeUri, out var result, out var errorMessage);

        success.Should().BeFalse();
        result.Should().BeNull();
        errorMessage.Should().Contain("exceeds maximum allowed length");
    }
}
