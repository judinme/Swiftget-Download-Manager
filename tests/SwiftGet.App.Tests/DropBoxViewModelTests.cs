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

public sealed class DropBoxViewModelTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly CategoryManager _categoryManager;

    public DropBoxViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_dropbox_vm_test_" + Guid.NewGuid().ToString("N"));
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
    public async Task HandleDroppedTextAsync_WithValidUrls_EnqueuesDownloads()
    {
        await _db.InitializeAsync();
        var vm = new DropBoxViewModel(_db, _categoryManager);

        var text = "https://example.com/file1.zip\r\nhttps://example.com/file2.mp4\r\n";
        var count = await vm.HandleDroppedTextAsync(text);

        count.Should().Be(2);
        vm.DroppedCount.Should().Be(2);
        vm.LastItemAdded.Should().Be("file2.mp4");

        var all = await _db.GetAllDownloadsAsync();
        all.Should().HaveCount(2);
        all.Should().OnlyContain(d => d.Status == DownloadStatus.Queued);
    }

    [Fact]
    public async Task HandleDroppedTextAsync_WithInvalidLines_IgnoresThem()
    {
        await _db.InitializeAsync();
        var vm = new DropBoxViewModel(_db, _categoryManager);

        var text = "not_a_url\r\n\r\nftp://invalid-scheme.com/test.bin\r\n";
        var count = await vm.HandleDroppedTextAsync(text);

        count.Should().Be(0);
        vm.DroppedCount.Should().Be(0);

        var all = await _db.GetAllDownloadsAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public void ResetCounter_ResetsCounterToZero()
    {
        var vm = new DropBoxViewModel(_db, _categoryManager)
        {
            DroppedCount = 15
        };

        vm.ResetCounterCommand.Execute(null);

        vm.DroppedCount.Should().Be(0);
        vm.StatusText.Should().Be("Counter reset");
    }
}
