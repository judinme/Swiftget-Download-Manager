using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using Xunit;

namespace SwiftGet.App.Tests;

public sealed class BatchDownloadViewModelTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly CategoryManager _categoryManager;

    public BatchDownloadViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_batch_vm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
        _categoryManager = new CategoryManager(_db);
    }

    public async ValueTask DisposeAsync()
    {
        _categoryManager.Dispose();
        await _db.DisposeAsync().ConfigureAwait(false);
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
    public void UpdatePreview_WithNumericPattern_GeneratesUrlsAndSummary()
    {
        var vm = new BatchDownloadViewModel(_db, _categoryManager)
        {
            AddressPattern = "https://example.com/archive_[01-05].zip"
        };

        vm.UpdatePreview();

        vm.GeneratedUrls.Should().HaveCount(5);
        vm.GeneratedCount.Should().Be(5);
        vm.GeneratedCountSummary.Should().Be("Generated URLs (5):");
        vm.GeneratedUrls[0].Should().Be("https://example.com/archive_01.zip");
        vm.GeneratedUrls[4].Should().Be("https://example.com/archive_05.zip");
    }

    [Fact]
    public void UpdatePreview_WithInvalidPattern_ClearsUrlsAndSetsErrorStatus()
    {
        var vm = new BatchDownloadViewModel(_db, _categoryManager)
        {
            AddressPattern = "https://example.com/item_[1-z].zip"
        };

        vm.UpdatePreview();

        vm.GeneratedUrls.Should().BeEmpty();
        vm.GeneratedCount.Should().Be(0);
        vm.GeneratedCountSummary.Should().Be("Generated URLs (0):");
        vm.StatusMessage.Should().Contain("Unsupported range bounds");
    }

    [Fact]
    public async Task AddBatchDownloadsAsync_SavesAllGeneratedItemsToDatabase()
    {
        await _db.InitializeAsync();
        var vm = new BatchDownloadViewModel(_db, _categoryManager)
        {
            AddressPattern = "https://example.com/doc_[1-3].pdf",
            QueueName = "default"
        };

        vm.UpdatePreview();
        var count = await vm.AddBatchDownloadsAsync();

        count.Should().Be(3);
        var all = await _db.GetAllDownloadsAsync();
        all.Should().HaveCount(3);
        all.Should().OnlyContain(d => d.Status == DownloadStatus.Queued && d.QueueId == "default");
    }
}
