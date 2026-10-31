using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;

namespace SwiftGet.Engine.Serialization;

public interface IImportService
{
    Task<int> ImportFromJsonFileAsync(string filePath, CancellationToken ct = default);
    Task<int> ImportFromTextUrlListAsync(string filePath, string? targetQueueId = null, CancellationToken ct = default);
}

public sealed class ImportService : IImportService
{
    private readonly ISwiftGetDatabase _db;
    private readonly ICategoryManager _categoryManager;
    private readonly SwiftGet.Core.IO.IFileNameResolver _fileNameResolver;

    public ImportService(ISwiftGetDatabase db, ICategoryManager categoryManager, SwiftGet.Core.IO.IFileNameResolver? fileNameResolver = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
        _fileNameResolver = fileNameResolver ?? new SwiftGet.Core.IO.FileNameResolver();
    }

    public async Task<int> ImportFromJsonFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Import file not found.", filePath);
        }

        var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var package = JsonSerializer.Deserialize<SwiftGetExportPackage>(json);
        if (package == null)
        {
            return 0;
        }

        foreach (var category in package.Categories)
        {
            await _db.SaveCategoryAsync(category, ct).ConfigureAwait(false);
        }

        foreach (var queue in package.Queues)
        {
            await _db.SaveQueueAsync(queue, ct).ConfigureAwait(false);
        }

        var count = 0;
        foreach (var item in package.Downloads)
        {
            await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    public async Task<int> ImportFromTextUrlListAsync(string filePath, string? targetQueueId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Import file not found.", filePath);
        }

        var lines = await File.ReadAllLinesAsync(filePath, ct).ConfigureAwait(false);
        var count = 0;
        var queueId = string.IsNullOrWhiteSpace(targetQueueId) ? "default" : targetQueueId;

        foreach (var rawLine in lines)
        {
            ct.ThrowIfCancellationRequested();
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ftp"))
            {
                continue;
            }

            var fileName = _fileNameResolver.ResolveInitialCandidate(line);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "download.bin";
            }

            var category = _categoryManager.ResolveCategory(fileName);
            var destPath = Path.Combine(category.TargetDirectory, fileName);

            var item = new DownloadItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = line,
                FileName = fileName,
                DestinationPath = destPath,
                QueueId = queueId,
                CategoryId = category.Id,
                Status = DownloadStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow
            };

            await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }
}
