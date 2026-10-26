using System.Runtime.InteropServices;
using FluentAssertions;
using SwiftGet.Engine.Security;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class MarkOfTheWebTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"motw_test_{Guid.NewGuid():N}.exe");

    public MarkOfTheWebTests()
    {
        File.WriteAllBytes(_tempFile, [0x4D, 0x5A, 0x90, 0x00]); // DOS stub header dummy
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempFile))
            {
                File.Delete(_tempFile);
            }
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ApplyAndReadZoneIdentifier_ShouldPersistZoneIdAndUrl()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Only applies to Windows NTFS
        }

        var sourceUrl = "https://cdn.example.com/downloads/setup.exe";
        var applied = await MarkOfTheWeb.ApplyZoneIdentifierAsync(_tempFile, sourceUrl, MarkOfTheWeb.InternetZoneId);

        applied.Should().BeTrue();

        var zoneId = MarkOfTheWeb.ReadZoneId(_tempFile);
        zoneId.Should().Be(3);

        var fullContent = MarkOfTheWeb.ReadZoneIdentifier(_tempFile);
        fullContent.Should().NotBeNull();
        fullContent.Should().Contain("[ZoneTransfer]");
        fullContent.Should().Contain("ZoneId=3");
        fullContent.Should().Contain($"HostUrl={sourceUrl}");

        var removed = MarkOfTheWeb.RemoveZoneIdentifier(_tempFile);
        removed.Should().BeTrue();

        var zoneIdAfterRemoval = MarkOfTheWeb.ReadZoneId(_tempFile);
        zoneIdAfterRemoval.Should().BeNull();
    }

    [Fact]
    public async Task ApplyZoneIdentifier_NonExistentFile_ShouldReturnFalse()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.tmp");
        var result = await MarkOfTheWeb.ApplyZoneIdentifierAsync(nonExistent);
        result.Should().BeFalse();
    }
}
