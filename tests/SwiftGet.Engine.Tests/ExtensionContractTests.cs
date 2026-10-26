using System.IO;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class ExtensionContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void ExtensionManifest_ShouldBeValidManifestV3()
    {
        var manifestPath = Path.Combine(RepoRoot, "extension", "manifest.json");
        File.Exists(manifestPath).Should().BeTrue("manifest.json must exist in extension directory");

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("manifest_version").GetInt32().Should().Be(3);
        root.GetProperty("name").GetString().Should().Contain("SwiftGet");

        var permissions = root.GetProperty("permissions");
        var permList = new System.Collections.Generic.List<string>();
        foreach (var p in permissions.EnumerateArray())
        {
            permList.Add(p.GetString()!);
        }

        permList.Should().Contain(["downloads", "cookies", "contextMenus", "storage", "nativeMessaging"]);

        var background = root.GetProperty("background").GetProperty("service_worker").GetString();
        background.Should().Be("background.js");
        File.Exists(Path.Combine(RepoRoot, "extension", background!)).Should().BeTrue();

        var contentJs = root.GetProperty("content_scripts")[0].GetProperty("js")[0].GetString();
        File.Exists(Path.Combine(RepoRoot, "extension", contentJs!)).Should().BeTrue();

        var popupHtml = root.GetProperty("action").GetProperty("default_popup").GetString();
        File.Exists(Path.Combine(RepoRoot, "extension", popupHtml!)).Should().BeTrue();

        var optionsHtml = root.GetProperty("options_ui").GetProperty("page").GetString();
        File.Exists(Path.Combine(RepoRoot, "extension", optionsHtml!)).Should().BeTrue();
    }

    [Fact]
    public void NativeHostManifests_ShouldBeValidAndConfigured()
    {
        var chromeManifestPath = Path.Combine(RepoRoot, "src", "SwiftGet.NativeHost", "manifests", "com.swiftget.downloader.chrome.json");
        var firefoxManifestPath = Path.Combine(RepoRoot, "src", "SwiftGet.NativeHost", "manifests", "com.swiftget.downloader.firefox.json");

        File.Exists(chromeManifestPath).Should().BeTrue();
        File.Exists(firefoxManifestPath).Should().BeTrue();

        var chromeDoc = JsonDocument.Parse(File.ReadAllText(chromeManifestPath));
        chromeDoc.RootElement.GetProperty("name").GetString().Should().Be("com.swiftget.downloader");
        chromeDoc.RootElement.GetProperty("type").GetString().Should().Be("stdio");
        chromeDoc.RootElement.GetProperty("allowed_origins").GetArrayLength().Should().BeGreaterThan(0);

        var firefoxDoc = JsonDocument.Parse(File.ReadAllText(firefoxManifestPath));
        firefoxDoc.RootElement.GetProperty("name").GetString().Should().Be("com.swiftget.downloader");
        firefoxDoc.RootElement.GetProperty("type").GetString().Should().Be("stdio");
        firefoxDoc.RootElement.GetProperty("allowed_extensions").GetArrayLength().Should().BeGreaterThan(0);
    }
}
