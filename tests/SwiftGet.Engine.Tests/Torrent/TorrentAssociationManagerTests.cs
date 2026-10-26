using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public class TorrentAssociationManagerTests
{
    [Fact]
    public void AssociationManager_OnWindows_RegistersAndUnregistersCleanly()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var manager = new TorrentAssociationManager();
        manager.IsSupported.Should().BeTrue();

        var fakeExePath = Path.Combine(Path.GetTempPath(), "SwiftGet_Test.exe");

        // Act: Register
        var registerSuccess = manager.RegisterAssociations(fakeExePath);
        registerSuccess.Should().BeTrue("RegisterAssociations should succeed under HKCU without elevation.");

        // Assert: Keys present
        manager.IsMagnetRegistered(fakeExePath).Should().BeTrue();
        manager.IsTorrentFileRegistered().Should().BeTrue();

        // Act: Unregister
        var unregisterSuccess = manager.UnregisterAssociations();
        unregisterSuccess.Should().BeTrue("UnregisterAssociations should clean up registry keys.");

        // Assert: Keys removed
        manager.IsMagnetRegistered().Should().BeFalse();
        manager.IsTorrentFileRegistered().Should().BeFalse();
    }
}
