using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;

namespace SwiftGet.Engine.Serialization;

public sealed class SwiftGetExportPackage
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("exportedAtUtc")]
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("downloads")]
    public List<DownloadItem> Downloads { get; set; } = [];

    [JsonPropertyName("categories")]
    public List<CategoryRule> Categories { get; set; } = [];

    [JsonPropertyName("queues")]
    public List<DownloadQueue> Queues { get; set; } = [];
}

public interface IExportService
{
    Task ExportToJsonFileAsync(string filePath, CancellationToken ct = default);
    Task ExportToTextUrlListAsync(string filePath, CancellationToken ct = default);
}

public sealed class ExportService : IExportService
{
    private readonly ISwiftGetDatabase _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ExportService(ISwiftGetDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task ExportToJsonFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var downloads = await _db.GetAllDownloadsAsync(ct).ConfigureAwait(false);
        var categories = await _db.GetAllCategoriesAsync(ct).ConfigureAwait(false);
        var queues = await _db.GetAllQueuesAsync(ct).ConfigureAwait(false);

        var package = new SwiftGetExportPackage
        {
            Version = 1,
            ExportedAtUtc = DateTime.UtcNow,
            Downloads = [.. downloads],
            Categories = [.. categories],
            Queues = [.. queues]
        };

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(package, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public async Task ExportToTextUrlListAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var downloads = await _db.GetAllDownloadsAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder();

        foreach (var item in downloads)
        {
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                sb.AppendLine(item.Url);
            }
        }

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }
}
