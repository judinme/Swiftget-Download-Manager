using System;
using System.IO;
using System.Text.Json;

namespace SwiftGet.Core.Models;

public sealed class AppSettings
{
    public int MaxConcurrentDownloads { get; set; } = 3;
    public bool ShowMediaProgressDialog { get; set; }
    public bool DeletePartialFilesOnCancel { get; set; }
    public int MaxConnectionsPerDownload { get; set; } = 8;
    public long DefaultSpeedLimitBytesPerSec { get; set; }
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "SwiftGet");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "app_settings.json");
    }

    public static AppSettings Load(string? path = null)
    {
        var filePath = path ?? GetDefaultSettingsPath();
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.MaxConcurrentDownloads = Math.Clamp(loaded.MaxConcurrentDownloads, 1, 10);
                    return loaded;
                }
            }
        }
        catch { }

        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        if (settings == null) return;
        var filePath = path ?? GetDefaultSettingsPath();
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch { }
    }
}
