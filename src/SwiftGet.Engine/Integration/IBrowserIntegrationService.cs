using System;
using System.Collections.Generic;

namespace SwiftGet.Engine.Integration;

public enum BrowserType
{
    Chrome,
    Edge,
    Brave,
    Opera,
    OperaGX,
    Firefox
}

public sealed class BrowserInfo
{
    public required BrowserType Type { get; init; }
    public required string Name { get; init; }
    public required string ExecutablePath { get; init; }
    public required bool IsInstalled { get; init; }
    public required bool IsDefault { get; init; }
    public required bool IsNativeHostRegistered { get; init; }
    public required string ExtensionManagementUrl { get; init; }
    public required string StoreUrl { get; init; }
}

public interface IBrowserIntegrationService
{
    IReadOnlyList<BrowserInfo> DetectInstalledBrowsers();
    BrowserType? GetDefaultBrowserType();
    bool IsExtensionRegistered(BrowserType browser);
    string GetUnpackedExtensionDirectory();
    void LaunchBrowserExtensionPage(BrowserType browser);
    void OpenStorePage(BrowserType browser);
    void EnsureExtensionStaged();
}
