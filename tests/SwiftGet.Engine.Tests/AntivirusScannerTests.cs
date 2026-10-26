using FluentAssertions;
using SwiftGet.Engine.Security;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class AntivirusScannerTests : IDisposable
{
    private readonly string _testFile = Path.Combine(Path.GetTempPath(), $"av_test_{Guid.NewGuid():N}.bin");

    public AntivirusScannerTests()
    {
        File.WriteAllText(_testFile, "clean payload content");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testFile)) File.Delete(_testFile);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ScanFile_WithCustomScanner_ShouldSubstitutePathAndReturnSuccess()
    {
        var scanner = new AntivirusScanner
        {
            IsEnabled = true,
            ScannerExecutablePath = "cmd.exe",
            CommandLineArguments = "/c echo Scanning {path} & exit 0"
        };

        var result = await scanner.ScanFileAsync(_testFile);
        result.Should().NotBeNull();
        result.ExitCode.Should().Be(0);
        result.IsClean.Should().BeTrue();
        result.ThreatDetected.Should().BeFalse();
        result.Output.Should().Contain(_testFile);
    }

    [Fact]
    public async Task ScanFile_WithNonZeroExitCode_ShouldReportThreat()
    {
        var scanner = new AntivirusScanner
        {
            IsEnabled = true,
            ScannerExecutablePath = "cmd.exe",
            CommandLineArguments = "/c echo Threat found in {path} & exit 1"
        };

        var result = await scanner.ScanFileAsync(_testFile);
        result.Should().NotBeNull();
        result.ExitCode.Should().Be(1);
        result.IsClean.Should().BeFalse();
        result.ThreatDetected.Should().BeTrue();
    }

    [Fact]
    public async Task ScanFile_NonExistentFile_ShouldReturnError()
    {
        var scanner = new AntivirusScanner { IsEnabled = true };
        var result = await scanner.ScanFileAsync("C:\\non_existent_file_xyz.exe");

        result.IsClean.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Fact]
    public async Task ScanFile_Timeout_ShouldCancelAndReturnTimeoutError()
    {
        var scanner = new AntivirusScanner
        {
            IsEnabled = true,
            ScannerExecutablePath = "cmd.exe",
            CommandLineArguments = "/c ping 127.0.0.1 -n 4 > nul",
            Timeout = TimeSpan.FromMilliseconds(250)
        };

        var result = await scanner.ScanFileAsync(_testFile);
        result.IsClean.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
    }
}
