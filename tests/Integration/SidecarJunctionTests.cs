using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.IO;
using SwiftGet.Media.Sidecars;
using Xunit;

namespace SwiftGet.Integration;

public sealed class SidecarJunctionTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly string _realDir;
    private readonly string _junctionDir;

    public SidecarJunctionTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "SwiftGet_JunctionTest_" + Guid.NewGuid().ToString("N"));
        _realDir = Path.Combine(_testBaseDir, "RealPhysicalTarget");
        _junctionDir = Path.Combine(_testBaseDir, "JunctionMountPoint");

        Directory.CreateDirectory(_realDir);

        // Create junction using Windows mklink /J
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{_junctionDir}\" \"{_realDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit();
    }

    [Fact]
    public void PathResolver_WithJunctionPath_ResolvesToPhysicalTarget()
    {
        var resolved = PathResolver.GetPhysicalPath(_junctionDir);
        resolved.Should().NotBeNullOrWhiteSpace();
        resolved.TrimEnd('\\', '/').Should().BeEquivalentTo(_realDir.TrimEnd('\\', '/'));
    }

    [Fact]
    public void PathResolver_WithNestedPathInsideJunction_ResolvesToNestedPhysicalTarget()
    {
        var nestedInJunction = Path.Combine(_junctionDir, "subfolder", "video.mp4");
        var expectedReal = Path.Combine(_realDir, "subfolder", "video.mp4");

        var resolved = PathResolver.GetPhysicalPath(nestedInJunction);
        resolved.TrimEnd('\\', '/').Should().BeEquivalentTo(expectedReal.TrimEnd('\\', '/'));
    }

    [Fact]
    public void PathResolver_ContainsReparsePoint_DetectsJunctionAccurately()
    {
        PathResolver.ContainsReparsePoint(_junctionDir).Should().BeTrue();
        PathResolver.ContainsReparsePoint(_realDir).Should().BeFalse();
    }

    [Fact]
    public async Task SidecarManager_ExecuteSidecarAsync_SafelyExecutesWithIsolatedTempAndCache()
    {
        var manager = new SidecarManager();
        var ytDlp = manager.FindYtDlpPath();
        if (string.IsNullOrEmpty(ytDlp))
        {
            // If sidecar binary is not present in local test environment, skip execution
            return;
        }

        var result = await SidecarManager.ExecuteSidecarAsync(ytDlp, ["--version"]);
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().NotBeNullOrWhiteSpace();
        result.StandardError.Should().NotContain("WinError 448");
    }

    [Fact]
    public void FormatUntrustedMountPointError_ProvidesFriendlyMessageAndRemedy()
    {
        var rawError = "yt-dlp extraction failed: ERROR: [WinError 448] The path cannot be traversed because it contains an untrusted mount point";
        var friendly = SidecarManager.FormatSidecarError(rawError, _junctionDir);

        friendly.Should().Contain("untrusted mount point");
        friendly.Should().Contain(_junctionDir);
        friendly.Should().Contain("Fix automatically");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_junctionDir))
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c rmdir \"{_junctionDir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }

            if (Directory.Exists(_testBaseDir))
            {
                Directory.Delete(_testBaseDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
