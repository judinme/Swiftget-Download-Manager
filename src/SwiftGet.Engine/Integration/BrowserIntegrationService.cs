using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SwiftGet.Engine.Integration;

public sealed class BrowserIntegrationService : IBrowserIntegrationService
{
    public const string ExtensionId = "albogoggaijdbgdldghgfcjakpdbmmfm";
    public const string FirefoxExtensionId = "swiftget@swiftget.net";

    public IReadOnlyList<BrowserInfo> DetectInstalledBrowsers()
    {
        var defaultType = GetDefaultBrowserType();
        var result = new List<BrowserInfo>();

        var candidates = new (BrowserType Type, string Name, string[] Paths, string ExtUrl, string StoreUrl)[]
        {
            (
                BrowserType.Chrome,
                "Google Chrome",
                new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
                },
                "chrome://extensions",
                $"https://chromewebstore.google.com/detail/{ExtensionId}"
            ),
            (
                BrowserType.Edge,
                "Microsoft Edge",
                new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
                },
                "edge://extensions",
                $"https://microsoftedge.microsoft.com/addons/detail/{ExtensionId}"
            ),
            (
                BrowserType.Brave,
                "Brave Browser",
                new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe")
                },
                "brave://extensions",
                $"https://chromewebstore.google.com/detail/{ExtensionId}"
            ),
            (
                BrowserType.Opera,
                "Opera",
                new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Opera\launcher.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Opera\launcher.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Opera\launcher.exe")
                },
                "opera://extensions",
                $"https://chromewebstore.google.com/detail/{ExtensionId}"
            ),
            (
                BrowserType.OperaGX,
                "Opera GX",
                new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Opera GX\launcher.exe")
                },
                "opera://extensions",
                $"https://chromewebstore.google.com/detail/{ExtensionId}"
            ),
            (
                BrowserType.Firefox,
                "Mozilla Firefox",
                new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Mozilla Firefox\firefox.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Mozilla Firefox\firefox.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Mozilla Firefox\firefox.exe")
                },
                "about:debugging#/runtime/this-firefox",
                "https://addons.mozilla.org/firefox/addon/swiftget/"
            )
        };

        foreach (var c in candidates)
        {
            string foundPath = string.Empty;
            bool isInstalled = false;

            foreach (var p in c.Paths)
            {
                if (File.Exists(p))
                {
                    foundPath = p;
                    isInstalled = true;
                    break;
                }
            }

            bool nativeRegistered = isInstalled && IsExtensionRegistered(c.Type);

            result.Add(new BrowserInfo
            {
                Type = c.Type,
                Name = c.Name,
                ExecutablePath = foundPath,
                IsInstalled = isInstalled,
                IsDefault = (c.Type == defaultType),
                IsNativeHostRegistered = nativeRegistered,
                ExtensionManagementUrl = c.ExtUrl,
                StoreUrl = c.StoreUrl
            });
        }

        return result;
    }

    public BrowserType? GetDefaultBrowserType()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var progId = key?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId))
            {
                return null;
            }

            if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return BrowserType.Chrome;
            if (progId.Contains("Edge", StringComparison.OrdinalIgnoreCase) || progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) return BrowserType.Edge;
            if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return BrowserType.Brave;
            if (progId.Contains("OperaGX", StringComparison.OrdinalIgnoreCase)) return BrowserType.OperaGX;
            if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return BrowserType.Opera;
            if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return BrowserType.Firefox;
        }
        catch
        {
            // fallback
        }

        return null;
    }

    public bool IsExtensionRegistered(BrowserType browser)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var regPaths = browser switch
        {
            BrowserType.Chrome => new[] { @"Software\Google\Chrome\NativeMessagingHosts\com.swiftget.downloader" },
            BrowserType.Edge => new[] { @"Software\Microsoft\Edge\NativeMessagingHosts\com.swiftget.downloader" },
            BrowserType.Brave => new[] { @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.swiftget.downloader" },
            BrowserType.Opera => new[] { @"Software\Opera Software\Opera Stable\NativeMessagingHosts\com.swiftget.downloader" },
            BrowserType.OperaGX => new[] { @"Software\Opera Software\Opera GX Stable\NativeMessagingHosts\com.swiftget.downloader" },
            BrowserType.Firefox => new[] { @"Software\Mozilla\NativeMessagingHosts\com.swiftget.downloader" },
            _ => Array.Empty<string>()
        };

        foreach (var subKey in regPaths)
        {
            // Check HKLM then HKCU
            try
            {
                using var hklmKey = Registry.LocalMachine.OpenSubKey(subKey);
                var val = hklmKey?.GetValue(string.Empty) as string;
                if (!string.IsNullOrWhiteSpace(val) && File.Exists(val))
                {
                    return true;
                }
            }
            catch { }

            try
            {
                using var hkcuKey = Registry.CurrentUser.OpenSubKey(subKey);
                var val = hkcuKey?.GetValue(string.Empty) as string;
                if (!string.IsNullOrWhiteSpace(val) && File.Exists(val))
                {
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    public string GetUnpackedExtensionDirectory()
    {
        // 1. Check local AppData staging
        string localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "extension");
        if (File.Exists(Path.Combine(localApp, "manifest.json")))
        {
            return localApp;
        }

        return FindBundledExtensionDirectory() ?? localApp;
    }

    private static string? FindBundledExtensionDirectory()
    {
        // 1. Check application base directory
        string baseExt = Path.Combine(AppContext.BaseDirectory, "extension");
        if (File.Exists(Path.Combine(baseExt, "manifest.json")))
        {
            return baseExt;
        }

        // 2. Fallback to repository structure if in dev/test
        var parent = AppContext.BaseDirectory;
        for (int i = 0; i < 7 && !string.IsNullOrEmpty(parent); i++)
        {
            string repoExt = Path.Combine(parent, "extension");
            if (File.Exists(Path.Combine(repoExt, "manifest.json")))
            {
                return repoExt;
            }
            parent = Path.GetDirectoryName(parent);
        }

        return null;
    }

    public void EnsureExtensionStaged()
    {
        string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "extension");
        string? sourceDir = FindBundledExtensionDirectory();
        if (string.IsNullOrWhiteSpace(sourceDir) ||
            string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(sourceDir))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(sourceDir, file);
                string dest = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }
        catch
        {
            // Best effort copy
        }
    }

    public void LaunchBrowserExtensionPage(BrowserType browser)
    {
        EnsureExtensionStaged();

        var browsers = DetectInstalledBrowsers();
        var target = browsers.FirstOrDefault(b => b.Type == browser);
        if (target == null || !target.IsInstalled)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target.ExecutablePath,
                Arguments = target.ExtensionManagementUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // fallback to default browser shell execute
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target.ExtensionManagementUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    public void OpenStorePage(BrowserType browser)
    {
        var browsers = DetectInstalledBrowsers();
        var target = browsers.FirstOrDefault(b => b.Type == browser);
        string url = target?.StoreUrl ?? $"https://chromewebstore.google.com/detail/{ExtensionId}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
