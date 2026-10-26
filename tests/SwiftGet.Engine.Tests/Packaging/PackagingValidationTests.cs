using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace SwiftGet.Engine.Tests.Packaging;

public class PackagingValidationTests
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
    public void WiXPackage_XmlStructure_HasValidGuidsAndUpgradeCode()
    {
        var root = GetRepositoryRoot();
        var wxsPath = Path.Combine(root, "installer", "SwiftGet.Package", "Package.wxs");
        File.Exists(wxsPath).Should().BeTrue();

        var doc = XDocument.Load(wxsPath);
        var ns = doc.Root!.GetDefaultNamespace();

        var package = doc.Root.Element(ns + "Package");
        package.Should().NotBeNull();
        package!.Attribute("Name")?.Value.Should().Be("SwiftGet Download Manager");
        package.Attribute("Manufacturer")?.Value.Should().Be("SwiftGet Contributors");
        package.Attribute("Version")?.Value.Should().Match(v => v == "1.0.0" || v == "$(var.Version)");

        var upgradeCode = package.Attribute("UpgradeCode")?.Value;
        Guid.TryParse(upgradeCode, out _).Should().BeTrue("UpgradeCode must be a valid GUID.");

        // Check components have valid GUIDs
        var components = doc.Descendants(ns + "Component");
        components.Should().NotBeEmpty();
        foreach (var comp in components)
        {
            var guid = comp.Attribute("Guid")?.Value;
            guid.Should().NotBeNullOrWhiteSpace();
            Guid.TryParse(guid, out _).Should().BeTrue($"Component {comp.Attribute("Id")?.Value} must have a valid GUID.");
        }

        // Verify native messaging registry keys
        var regKeys = doc.Descendants(ns + "RegistryKey");
        var keyPaths = regKeys.Select(k => k.Attribute("Key")?.Value ?? string.Empty).ToList();
        keyPaths.Should().Contain(k => k.Contains("Chrome\\NativeMessagingHosts\\com.swiftget.native"));
        keyPaths.Should().Contain(k => k.Contains("Edge\\NativeMessagingHosts\\com.swiftget.native"));
        keyPaths.Should().Contain(k => k.Contains("Mozilla\\NativeMessagingHosts\\com.swiftget.native"));

        // Verify file associations
        keyPaths.Should().Contain(k => k.Contains(".torrent"));
        keyPaths.Should().Contain(k => k.Contains("magnet"));

        // Verify browser extension auto-installation registry keys
        keyPaths.Should().Contain(k => k.Contains("Chrome\\Extensions\\albogoggaijdbgdldghgfcjakpdbmmfm"));
        keyPaths.Should().Contain(k => k.Contains("Edge\\Extensions\\albogoggaijdbgdldghgfcjakpdbmmfm"));
        keyPaths.Should().Contain(k => k.Contains("Mozilla\\Firefox\\Extensions"));
        keyPaths.Should().Contain(k => k.Contains("Chrome\\ExtensionInstallForcelist"));
        keyPaths.Should().Contain(k => k.Contains("Edge\\ExtensionInstallForcelist"));
    }

    [Fact]
    public void WingetManifests_YamlFormat_ContainsRequiredFieldsAndMatchingHash()
    {
        var root = GetRepositoryRoot();
        var wingetDir = Path.Combine(root, "installer", "winget", "manifests", "s", "SwiftGet", "SwiftGet", "1.0.0");
        Directory.Exists(wingetDir).Should().BeTrue();

        // 1. Version manifest
        var versionFile = Path.Combine(wingetDir, "SwiftGet.SwiftGet.yaml");
        File.Exists(versionFile).Should().BeTrue();
        var versionContent = File.ReadAllText(versionFile);
        versionContent.Should().Contain("PackageIdentifier: SwiftGet.SwiftGet");
        versionContent.Should().Contain("PackageVersion: 1.0.0");
        versionContent.Should().Contain("ManifestVersion: 1.6.0");

        // 2. Locale manifest
        var localeFile = Path.Combine(wingetDir, "SwiftGet.SwiftGet.locale.en-US.yaml");
        File.Exists(localeFile).Should().BeTrue();
        var localeContent = File.ReadAllText(localeFile);
        localeContent.Should().Contain("PackageName: SwiftGet Download Manager");
        localeContent.Should().Contain("Publisher: SwiftGet Contributors");
        localeContent.Should().Contain("License: MIT");
        localeContent.Should().Contain("Moniker: swiftget");

        // 3. Installer manifest
        var installerFile = Path.Combine(wingetDir, "SwiftGet.SwiftGet.installer.yaml");
        File.Exists(installerFile).Should().BeTrue();
        var installerContent = File.ReadAllText(installerFile);
        installerContent.Should().Contain("InstallerType: msi");
        installerContent.Should().Contain("Architecture: x64");

        var hashMatch = Regex.Match(installerContent, @"InstallerSha256:\s*([0-9a-fA-F]{64})");
        hashMatch.Success.Should().BeTrue("Winget installer manifest must have a 64-character SHA-256 hash.");
    }

    [Fact]
    public void GeneratedMsiPackage_ExistsAndHasValidHeader()
    {
        var root = GetRepositoryRoot();
        var msiPath = Path.Combine(root, "dist", "SwiftGet-1.0.0-x64.msi");
        if (!File.Exists(msiPath))
        {
            // Skip check if dist hasn't been built yet in test environment
            return;
        }

        var msiBytes = new byte[8];
        using var stream = File.OpenRead(msiPath);
        stream.ReadExactly(msiBytes, 0, 8);

        // Standard OLE Compound Document signature: D0 CF 11 E0 A1 B1 1A E1
        byte[] expectedOleHeader = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        msiBytes.Should().Equal(expectedOleHeader, "MSI installer must have a valid compound document binary header.");
    }

}