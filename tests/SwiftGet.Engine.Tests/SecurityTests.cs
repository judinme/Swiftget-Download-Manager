using System;
using System.IO;
using FluentAssertions;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Security;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class SecurityTests
{
    private readonly string _tempBaseDir;

    public SecurityTests()
    {
        _tempBaseDir = Path.Combine(Path.GetTempPath(), "swiftget_sec_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempBaseDir);
    }

    [Theory]
    [InlineData("../../Windows/System32/calc.exe")]
    [InlineData("..\\..\\Windows\\System32\\cmd.exe")]
    [InlineData("/../../etc/shadow")]
    [InlineData("\\..\\..\\boot.ini")]
    public void EnsureSafePath_DirectoryTraversal_ConfinesWithinBaseDirectory(string maliciousRelativePath)
    {
        var resolved = PathTraversalGuard.EnsureSafePath(_tempBaseDir, maliciousRelativePath);

        resolved.Should().StartWith(_tempBaseDir);
        PathTraversalGuard.IsSafePath(_tempBaseDir, resolved).Should().BeTrue();
    }

    [Theory]
    [InlineData("\\\\attacker-ip\\share\\payload.dll")]
    [InlineData("//192.168.1.100/downloads/script.bat")]
    [InlineData("\\\\?\\C:\\Windows\\System32\\kernel32.dll")]
    [InlineData("\\\\.\\PhysicalDrive0")]
    public void EnsureSafePath_UncAndDevicePaths_NeutralizesAndConfinesWithinBaseDirectory(string uncPath)
    {
        var resolved = PathTraversalGuard.EnsureSafePath(_tempBaseDir, uncPath);

        resolved.Should().StartWith(_tempBaseDir);
        resolved.Should().NotStartWith("\\\\");
        resolved.Should().NotStartWith("//");
        PathTraversalGuard.IsSafePath(_tempBaseDir, resolved).Should().BeTrue();
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("CON.txt")]
    [InlineData("prn.dat")]
    [InlineData("AUX.zip")]
    [InlineData("NUL")]
    [InlineData("com1.mp4")]
    [InlineData("lpt1.pdf")]
    [InlineData("CON .tar.gz")]
    public void EnsureSafePath_WindowsReservedDeviceNames_PrefixesUnderscoreToPreventLockup(string reservedName)
    {
        var resolved = PathTraversalGuard.EnsureSafePath(_tempBaseDir, reservedName);
        var fileName = Path.GetFileName(resolved);

        fileName.Should().StartWith("_");
        resolved.Should().StartWith(_tempBaseDir);
    }

    [Fact]
    public void EnsureSafePath_NtfsAlternateDataStream_ReplacesColonToPreventAdsInjection()
    {
        var adsInput = "normal_file.txt:hidden_payload.exe";
        var resolved = PathTraversalGuard.EnsureSafePath(_tempBaseDir, adsInput);
        var fileName = Path.GetFileName(resolved);

        fileName.Should().NotContain(":");
        fileName.Should().Be("normal_file.txthidden_payload.exe");
    }

    [Fact]
    public void FileNameSanitizer_RightToLeftOverrideSpoofing_StripsSpoofingCharacters()
    {
        // Unicode Right-To-Left Override (\u202E) makes "document\u202Eexe.pdf" look like "documentfdp.exe"
        var rloSpoofed = "invoice_\u202Efdp.exe";
        var sanitized = FileNameSanitizer.Sanitize(rloSpoofed);

        sanitized.Should().NotContain("\u202E");
        sanitized.Should().Be("invoice_fdp.exe");
    }

    [Fact]
    public void FileNameSanitizer_ReservedNameWithMultipleExtensions_PrefixesUnderscore()
    {
        var input = "CON.tar.gz";
        var sanitized = FileNameSanitizer.Sanitize(input);

        sanitized.Should().Be("_CON.tar.gz");
    }

    [Fact]
    public void IsSafePath_UncOrEscapingPath_ReturnsFalse()
    {
        PathTraversalGuard.IsSafePath(_tempBaseDir, "\\\\remote\\share\\file.exe").Should().BeFalse();
        PathTraversalGuard.IsSafePath(_tempBaseDir, "C:\\Windows\\System32\\cmd.exe").Should().BeFalse();
        PathTraversalGuard.IsSafePath(_tempBaseDir, Path.Combine(_tempBaseDir, "safe_file.txt:ads")).Should().BeFalse();
    }
}
