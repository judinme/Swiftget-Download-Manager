using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Serialization;
using Xunit;

namespace SwiftGet.Engine.Tests;

public sealed class ImportExportTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDbPath;
    private readonly string _targetDbPath;
    private readonly SqliteDatabase _sourceDb;
    private readonly SqliteDatabase _targetDb;
    private readonly CategoryManager _categoryManager;

    public ImportExportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_impexp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sourceDbPath = Path.Combine(_tempDir, "source.db");
        _targetDbPath = Path.Combine(_tempDir, "target.db");
        _sourceDb = new SqliteDatabase(_sourceDbPath);
        _targetDb = new SqliteDatabase(_targetDbPath);
        _categoryManager = new CategoryManager(_targetDb);
    }

    public async ValueTask DisposeAsync()
    {
        _categoryManager.Dispose();
        await _sourceDb.DisposeAsync().ConfigureAwait(false);
        await _targetDb.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExportAndImportJson_ShouldRoundTripAllRecordsSuccessfully()
    {
        await _sourceDb.InitializeAsync();
        await _targetDb.InitializeAsync();
        await _categoryManager.InitializeAsync();

        var item = new DownloadItem
        {
            Id = "item_exp_1",
            Url = "https://example.com/test_archive.zip",
            FileName = "test_archive.zip",
            DestinationPath = "C:\\Downloads\\test_archive.zip",
            QueueId = "default",
            CategoryId = "compressed",
            TotalBytes = 1024 * 1024 * 50,
            Status = DownloadStatus.Completed
        };
        await _sourceDb.SaveDownloadAsync(item);

        var exportFile = Path.Combine(_tempDir, "backup.json");
        var exportService = new ExportService(_sourceDb);
        await exportService.ExportToJsonFileAsync(exportFile);

        File.Exists(exportFile).Should().BeTrue();

        var importService = new ImportService(_targetDb, _categoryManager);
        var importedCount = await importService.ImportFromJsonFileAsync(exportFile);

        importedCount.Should().Be(1);

        var reloaded = await _targetDb.GetDownloadAsync("item_exp_1");
        reloaded.Should().NotBeNull();
        reloaded!.Url.Should().Be(item.Url);
        reloaded.FileName.Should().Be(item.FileName);
        reloaded.TotalBytes.Should().Be(item.TotalBytes);
        reloaded.Status.Should().Be(DownloadStatus.Completed);
    }

    [Fact]
    public async Task ExportAndImportTextUrlList_ShouldFilterCommentsAndAutoRouteCategories()
    {
        await _targetDb.InitializeAsync();
        await _categoryManager.InitializeAsync();

        var listFile = Path.Combine(_tempDir, "links.txt");
        await File.WriteAllLinesAsync(listFile, [
            "# Daily batch downloads",
            "https://example.com/files/manual.pdf",
            "",
            "// Mirror link",
            "https://example.com/media/podcast.mp3"
        ]);

        var importService = new ImportService(_targetDb, _categoryManager);
        var count = await importService.ImportFromTextUrlListAsync(listFile, "sync");

        count.Should().Be(2);

        var allDownloads = await _targetDb.GetAllDownloadsAsync();
        allDownloads.Should().HaveCount(2);

        var pdf = System.Linq.Enumerable.FirstOrDefault(allDownloads, d => d.FileName == "manual.pdf");
        pdf.Should().NotBeNull();
        pdf!.CategoryId.Should().Be("documents");
        pdf.QueueId.Should().Be("sync");

        var mp3 = System.Linq.Enumerable.FirstOrDefault(allDownloads, d => d.FileName == "podcast.mp3");
        mp3.Should().NotBeNull();
        mp3!.CategoryId.Should().Be("music");
        mp3.QueueId.Should().Be("sync");
    }
}
