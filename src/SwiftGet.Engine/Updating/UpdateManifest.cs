using System;
using System.Text.Json.Serialization;

namespace SwiftGet.Engine.Updating;

public sealed record UpdateManifest
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("releaseDate")]
    public DateTimeOffset ReleaseDate { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; init; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public required string DownloadUrl { get; init; }

    [JsonPropertyName("sha256Checksum")]
    public required string Sha256Checksum { get; init; }

    [JsonPropertyName("minSupportedVersion")]
    public string? MinSupportedVersion { get; init; }

    [JsonPropertyName("mandatoryUpdate")]
    public bool MandatoryUpdate { get; init; }

    [JsonPropertyName("installerArguments")]
    public string? InstallerArguments { get; init; }
}

public sealed record UpdateCheckResult
{
    public required bool IsUpdateAvailable { get; init; }
    public required Version CurrentVersion { get; init; }
    public Version? LatestVersion { get; init; }
    public UpdateManifest? Manifest { get; init; }
    public string? ErrorMessage { get; init; }

    public static UpdateCheckResult NoUpdate(Version currentVersion) =>
        new() { IsUpdateAvailable = false, CurrentVersion = currentVersion };

    public static UpdateCheckResult UpdateAvailable(Version currentVersion, Version latestVersion, UpdateManifest manifest) =>
        new() { IsUpdateAvailable = true, CurrentVersion = currentVersion, LatestVersion = latestVersion, Manifest = manifest };

    public static UpdateCheckResult Error(Version currentVersion, string message) =>
        new() { IsUpdateAvailable = false, CurrentVersion = currentVersion, ErrorMessage = message };
}
