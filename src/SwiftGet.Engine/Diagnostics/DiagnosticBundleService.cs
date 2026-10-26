using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SwiftGet.Core.Storage;

namespace SwiftGet.Engine.Diagnostics;

public sealed class DiagnosticBundleService : IDiagnosticService
{
    private readonly ISwiftGetDatabase _db;
    private readonly ILogger<DiagnosticBundleService>? _logger;
    private readonly string? _customLogDirectory;

    public DiagnosticBundleService(
        ISwiftGetDatabase db,
        ILogger<DiagnosticBundleService>? logger = null,
        string? customLogDirectory = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger;
        _customLogDirectory = customLogDirectory;
    }

    public async Task<string> ExportDiagnosticBundleAsync(string outputPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var targetDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var zipStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);

        // 1. system_info.json
        var systemInfo = new
        {
            AppVersion = "1.0.0",
            TimestampUtc = DateTime.UtcNow,
            OSDescription = RuntimeInformation.OSDescription,
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
            WorkingSetMb = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 2)
        };
        await AddJsonEntryAsync(archive, "system_info.json", systemInfo, ct).ConfigureAwait(false);

        // 2. network_info.json (anonymized metadata only, zero IP/MAC addresses)
        var networkInfo = GetAnonymizedNetworkInfo();
        await AddJsonEntryAsync(archive, "network_info.json", networkInfo, ct).ConfigureAwait(false);

        // 3. database_status.json
        var dbStatus = await GetDatabaseStatusAsync(ct).ConfigureAwait(false);
        await AddJsonEntryAsync(archive, "database_status.json", dbStatus, ct).ConfigureAwait(false);

        // 4. logs/ (redacted)
        await AddRedactedLogsAsync(archive, ct).ConfigureAwait(false);

        _logger?.LogInformation("Successfully generated diagnostic bundle at {OutputPath}", outputPath);
        return outputPath;
    }

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static async Task AddJsonEntryAsync<T>(ZipArchive archive, string entryName, T data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data, IndentedJsonOptions);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
    }

    private static List<object> GetAnonymizedNetworkInfo()
    {
        var list = new List<object>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                list.Add(new
                {
                    Name = nic.Name,
                    Type = nic.NetworkInterfaceType.ToString(),
                    Status = nic.OperationalStatus.ToString(),
                    SpeedMbps = nic.Speed > 0 ? (nic.Speed / 1_000_000) : 0,
                    SupportsMulticast = nic.SupportsMulticast
                });
            }
        }
        catch
        {
            list.Add(new { Error = "Failed to query network interfaces." });
        }
        return list;
    }

    private async Task<object> GetDatabaseStatusAsync(CancellationToken ct)
    {
        try
        {
            var allDownloads = await _db.GetAllDownloadsAsync(ct).ConfigureAwait(false);
            var categories = await _db.GetAllCategoriesAsync(ct).ConfigureAwait(false);
            var queues = await _db.GetAllQueuesAsync(ct).ConfigureAwait(false);

            var statusGroups = allDownloads
                .GroupBy(d => d.Status.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            return new
            {
                TotalDownloads = allDownloads.Count,
                StatusBreakdown = statusGroups,
                TotalCategories = categories.Count,
                TotalQueues = queues.Count
            };
        }
        catch (Exception ex)
        {
            return new
            {
                Error = $"Failed to read database metrics: {ex.Message}"
            };
        }
    }

    private async Task AddRedactedLogsAsync(ZipArchive archive, CancellationToken ct)
    {
        var searchDirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(_customLogDirectory) && Directory.Exists(_customLogDirectory))
        {
            searchDirs.Add(_customLogDirectory);
        }

        var localAppLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "logs");
        if (Directory.Exists(localAppLog))
        {
            searchDirs.Add(localAppLog);
        }

        var currentDirLog = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(currentDirLog) && !searchDirs.Contains(currentDirLog))
        {
            searchDirs.Add(currentDirLog);
        }

        var addedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in searchDirs)
        {
            try
            {
                var files = Directory.GetFiles(dir, "*.log");
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (addedFiles.Contains(fileName))
                    {
                        continue;
                    }

                    addedFiles.Add(fileName);
                    string rawContent;
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        rawContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        continue;
                    }

                    var redactedContent = RedactionEngine.RedactText(rawContent);

                    var entry = archive.CreateEntry($"logs/{fileName}", CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var writer = new StreamWriter(entryStream, Encoding.UTF8);
                    await writer.WriteAsync(redactedContent.AsMemory(), ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Directory access issue
            }
        }
    }
}
