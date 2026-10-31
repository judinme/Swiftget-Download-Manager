using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftGet.Engine.Torrent;

public static class TorrentSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string? CustomSettingsPath { get; set; }

    public static string GetDefaultSettingsPath(string? customDir = null)
    {
        if (!string.IsNullOrWhiteSpace(CustomSettingsPath))
        {
            return CustomSettingsPath;
        }

        string dir = !string.IsNullOrWhiteSpace(customDir)
            ? customDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet");
        return Path.Combine(dir, "torrent_settings.json");
    }

    public static TorrentEngineOptions Load(string? settingsPath = null)
    {
        string path = settingsPath ?? GetDefaultSettingsPath();
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var opts = JsonSerializer.Deserialize<TorrentEngineOptions>(json, JsonOptions);
                if (opts != null)
                {
                    return opts;
                }
            }
        }
        catch
        {
            // Fallback to default options if read/parse fails
        }

        return new TorrentEngineOptions();
    }

    public static void Save(TorrentEngineOptions options, string? settingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string path = settingsPath ?? GetDefaultSettingsPath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(options, JsonOptions);
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Ignore disk write failure in fallback
        }
    }
}
