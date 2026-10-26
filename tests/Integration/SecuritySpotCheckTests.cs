using FluentAssertions;
using SwiftGet.Engine.Security;
using SwiftGet.Engine.Server;
using SwiftGet.NativeHost.Routing;
using Xunit;

namespace SwiftGet.Integration;

public class SecuritySpotCheckTests
{
    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnop", true)]
    [InlineData("moz-extension://12345-67890", true)]
    [InlineData("extension://swiftget", true)]
    [InlineData("https://malicious-site.com", false)]
    [InlineData("http://evil.org", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void NativeMessageRouter_ValidateOrigin_AcceptsOnlyTrustedExtensionSchemes(string? origin, bool expectedValid)
    {
        var isValid = NativeMessageRouter.ValidateOrigin(origin);
        isValid.Should().Be(expectedValid);
    }

    [Fact]
    public void NativeMessageRouter_ValidateOrigin_HonorsExplicitWhitelist()
    {
        var allowed = new[] { "chrome-extension://trusted-id-1234" };

        NativeMessageRouter.ValidateOrigin("chrome-extension://trusted-id-1234", allowed).Should().BeTrue();
        NativeMessageRouter.ValidateOrigin("chrome-extension://untrusted-id-5678", allowed).Should().BeFalse();
    }

    [Fact]
    public void LoopbackTokenManager_GeneratesHighEntropyTokens_AndValidatesCorrectly()
    {
        var tokenManager = new LoopbackTokenManager();
        var token = tokenManager.GetOrCreateToken();

        token.Should().NotBeNullOrWhiteSpace();
        token.Length.Should().BeGreaterThanOrEqualTo(32);

        tokenManager.ValidateToken(token).Should().BeTrue();
        tokenManager.ValidateToken("tampered_token").Should().BeFalse();
        tokenManager.ValidateToken(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("../../windows/system32/cmd.exe", "cmd.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe", "calc.exe")]
    [InlineData("\\\\evil-share\\malware.exe", "malware.exe")]
    [InlineData("sub/../../traversal.zip", "traversal.zip")]
    public void PathTraversalGuard_EnsureSafePath_LocksToDirectoryBoundary(string maliciousRelative, string expectedFileName)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "swiftget_sec_sandbox");
        Directory.CreateDirectory(baseDir);

        try
        {
            var result = PathTraversalGuard.EnsureSafePath(baseDir, maliciousRelative);

            // Must reside strictly inside baseDir
            result.Should().StartWith(baseDir);
            Path.GetFileName(result).Should().Be(expectedFileName);
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        }
    }
}
