using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using SwiftGet.Engine.Integration;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class BrowserIntegrationServiceTests
{
    private static string GetRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "SwiftGet.sln")))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("Could not locate repository root directory.");
    }

    [Fact]
    public void DetectInstalledBrowsers_ShouldReturnConfiguredBrowsersWithStoreUrls()
    {
        var service = new BrowserIntegrationService();
        var browsers = service.DetectInstalledBrowsers();

        browsers.Should().NotBeNull();
        browsers.Should().HaveCountGreaterThanOrEqualTo(5);

        var chrome = browsers.FirstOrDefault(b => b.Type == BrowserType.Chrome);
        chrome.Should().NotBeNull();
        chrome!.Name.Should().Be("Google Chrome");
        chrome.StoreUrl.Should().Contain(BrowserIntegrationService.ExtensionId);

        var edge = browsers.FirstOrDefault(b => b.Type == BrowserType.Edge);
        edge.Should().NotBeNull();
        edge!.Name.Should().Be("Microsoft Edge");
        edge.StoreUrl.Should().Contain(BrowserIntegrationService.ExtensionId);

        var brave = browsers.FirstOrDefault(b => b.Type == BrowserType.Brave);
        brave.Should().NotBeNull();
        brave!.Name.Should().Be("Brave Browser");

        var firefox = browsers.FirstOrDefault(b => b.Type == BrowserType.Firefox);
        firefox.Should().NotBeNull();
        firefox!.Name.Should().Be("Mozilla Firefox");
        firefox.StoreUrl.Should().Contain("swiftget");
    }

    [Fact]
    public void GetUnpackedExtensionDirectory_ShouldFindExistingExtensionWithManifest()
    {
        var service = new BrowserIntegrationService();
        var unpackedDir = service.GetUnpackedExtensionDirectory();

        unpackedDir.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(unpackedDir).Should().BeTrue();
        File.Exists(Path.Combine(unpackedDir, "manifest.json")).Should().BeTrue();
    }

    [Fact]
    public void ExtensionId_ShouldMatchManifestPublicKey()
    {
        // Must match albogoggaijdbgdldghgfcjakpdbmmfm derived from manifest.json "key"
        BrowserIntegrationService.ExtensionId.Should().Be("albogoggaijdbgdldghgfcjakpdbmmfm");
    }
}
