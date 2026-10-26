using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SwiftGet.Engine.Torrent;

public interface ITorrentAssociationManager
{
    bool IsSupported { get; }
    bool IsMagnetRegistered(string? executablePath = null);
    bool IsTorrentFileRegistered();
    bool RegisterAssociations(string? executablePath = null);
    bool UnregisterAssociations();
    bool OpenDefaultAppsSettings();
}

[SupportedOSPlatform("windows")]
public sealed class TorrentAssociationManager : ITorrentAssociationManager
{
    public const string MagnetProgId = "SwiftGet.Magnet";
    public const string TorrentProgId = "SwiftGet.TorrentFile";
    public const string AppCapabilitiesKey = @"Software\SwiftGet\Capabilities";

    public bool IsSupported => OperatingSystem.IsWindows();

    public static string GetDefaultExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && Path.GetExtension(processPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "SwiftGet.exe");
        return candidate;
    }

    public bool IsMagnetRegistered(string? executablePath = null)
    {
        if (!IsSupported) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command");
            var val = key?.GetValue(null)?.ToString();
            if (string.IsNullOrWhiteSpace(val)) return false;

            var exePath = executablePath ?? GetDefaultExecutablePath();
            return val.Contains(Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool IsTorrentFileRegistered()
    {
        if (!IsSupported) return false;

        try
        {
            using var extKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent");
            var progId = extKey?.GetValue(null)?.ToString();
            return string.Equals(progId, TorrentProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool RegisterAssociations(string? executablePath = null)
    {
        if (!IsSupported) return false;

        var exePath = executablePath ?? GetDefaultExecutablePath();
        var quotedExe = $"\"{exePath}\" \"%1\"";
        var iconPath = $"\"{exePath}\",0";

        try
        {
            // 1. Register magnet URL scheme: HKCU\Software\Classes\magnet
            using (var magnetKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet"))
            {
                magnetKey.SetValue(null, "URL:BitTorrent Magnet Link");
                magnetKey.SetValue("URL Protocol", string.Empty);

                using var iconKey = magnetKey.CreateSubKey("DefaultIcon");
                iconKey.SetValue(null, iconPath);

                using var cmdKey = magnetKey.CreateSubKey(@"shell\open\command");
                cmdKey.SetValue(null, quotedExe);
            }

            // 2. Register ProgID for Magnet
            using (var magnetProgKey = Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{MagnetProgId}"))
            {
                magnetProgKey.SetValue(null, "URL:BitTorrent Magnet Link");
                magnetProgKey.SetValue("URL Protocol", string.Empty);

                using var iconKey = magnetProgKey.CreateSubKey("DefaultIcon");
                iconKey.SetValue(null, iconPath);

                using var cmdKey = magnetProgKey.CreateSubKey(@"shell\open\command");
                cmdKey.SetValue(null, quotedExe);
            }

            // 3. Register .torrent extension: HKCU\Software\Classes\.torrent
            using (var torrentExtKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent"))
            {
                torrentExtKey.SetValue(null, TorrentProgId);
                torrentExtKey.SetValue("Content Type", "application/x-bittorrent");
            }

            // 4. Register ProgID for .torrent: HKCU\Software\Classes\SwiftGet.TorrentFile
            using (var torrentProgKey = Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{TorrentProgId}"))
            {
                torrentProgKey.SetValue(null, "BitTorrent File");

                using var iconKey = torrentProgKey.CreateSubKey("DefaultIcon");
                iconKey.SetValue(null, iconPath);

                using var cmdKey = torrentProgKey.CreateSubKey(@"shell\open\command");
                cmdKey.SetValue(null, quotedExe);
            }

            // 5. Register Capabilities for Windows Default Apps (Settings > Apps > Default apps)
            using (var capKey = Registry.CurrentUser.CreateSubKey(AppCapabilitiesKey))
            {
                capKey.SetValue("ApplicationName", "SwiftGet");
                capKey.SetValue("ApplicationDescription", "SwiftGet High-Performance Download Manager");

                using var fileAssocKey = capKey.CreateSubKey("FileAssociations");
                fileAssocKey.SetValue(".torrent", TorrentProgId);

                using var urlAssocKey = capKey.CreateSubKey("URLAssociations");
                urlAssocKey.SetValue("magnet", MagnetProgId);
            }

            // 6. Register in RegisteredApplications
            using (var regAppsKey = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            {
                regAppsKey.SetValue("SwiftGet", AppCapabilitiesKey);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool UnregisterAssociations()
    {
        if (!IsSupported) return false;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\magnet", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@$"Software\Classes\{MagnetProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.torrent", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@$"Software\Classes\{TorrentProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\SwiftGet\Capabilities", throwOnMissingSubKey: false);

            using var regAppsKey = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            regAppsKey?.DeleteValue("SwiftGet", throwOnMissingValue: false);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenDefaultAppsSettings()
    {
        if (!IsSupported) return false;

        try
        {
            // Windows 10/11 Default Apps URI
            var psi = new ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
