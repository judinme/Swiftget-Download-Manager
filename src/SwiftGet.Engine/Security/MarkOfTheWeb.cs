using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SwiftGet.Engine.Security;

public static class MarkOfTheWeb
{
    public const int InternetZoneId = 3;

    public static async Task<bool> ApplyZoneIdentifierAsync(
        string filePath,
        string? sourceUrl = null,
        int zoneId = InternetZoneId,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ZoneTransfer]");
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"ZoneId={zoneId}");
            if (!string.IsNullOrWhiteSpace(sourceUrl))
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"HostUrl={sourceUrl}");
            }

            var adsPath = $"{filePath}:Zone.Identifier";
            await File.WriteAllTextAsync(adsPath, sb.ToString(), Encoding.ASCII, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (logger ?? NullLogger.Instance).LogWarning(ex, "Failed to apply Mark-of-the-Web (Zone.Identifier) to {FilePath}. Filesystem may not support Alternate Data Streams.", filePath);
            return false;
        }
    }

    public static bool ApplyZoneIdentifier(
        string filePath,
        string? sourceUrl = null,
        int zoneId = InternetZoneId,
        ILogger? logger = null)
    {
        return ApplyZoneIdentifierAsync(filePath, sourceUrl, zoneId, logger, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static string? ReadZoneIdentifier(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            var adsPath = $"{filePath}:Zone.Identifier";
            if (File.Exists(adsPath))
            {
                return File.ReadAllText(adsPath, Encoding.ASCII);
            }
        }
        catch
        {
            // Non-NTFS or stream not found
        }

        return null;
    }

    public static int? ReadZoneId(string filePath)
    {
        var content = ReadZoneIdentifier(filePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase))
            {
                var valStr = line["ZoneId=".Length..].Trim();
                if (int.TryParse(valStr, out var zoneId))
                {
                    return zoneId;
                }
            }
        }

        return null;
    }

    public static bool RemoveZoneIdentifier(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            var adsPath = $"{filePath}:Zone.Identifier";
            if (File.Exists(adsPath))
            {
                File.Delete(adsPath);
                return true;
            }
        }
        catch
        {
            // Ignore
        }

        return false;
    }
}
