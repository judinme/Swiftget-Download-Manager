using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.UI.Xaml;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Grabber;
using Xunit;

namespace SwiftGet.App.Tests;

public sealed class GrabberWizardViewModelTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly CategoryManager _categoryManager;
    private readonly FakeCrawler _crawler;

    public GrabberWizardViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_grabber_vm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
        _categoryManager = new CategoryManager(_db);
        _crawler = new FakeCrawler();
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
    public async Task Navigation_ShouldProgressThroughStepsAndTriggerCrawl()
    {
        var vm = new GrabberWizardViewModel(_db, _crawler, _categoryManager)
        {
            StartUrl = "https://example.com/gallery"
        };

        // Step 1
        vm.CurrentStep.Should().Be(1);
        vm.Step1Visibility.Should().Be(Visibility.Visible);
        vm.Step2Visibility.Should().Be(Visibility.Collapsed);

        // Next to Step 2
        await vm.NextStepAsync();
        vm.CurrentStep.Should().Be(2);
        vm.Step2Visibility.Should().Be(Visibility.Visible);

        // Next to Step 3 & 4 (auto-crawls in fake crawler)
        _crawler.FilesToReturn =
        [
            new GrabberFileItem { FileName = "photo1.jpg", Url = "https://example.com/photo1.jpg", Extension = ".jpg", IsSelected = true },
            new GrabberFileItem { FileName = "photo2.jpg", Url = "https://example.com/photo2.jpg", Extension = ".jpg", IsSelected = true }
        ];

        await vm.NextStepAsync();

        vm.CurrentStep.Should().Be(4);
        vm.Step4Visibility.Should().Be(Visibility.Visible);
        vm.DiscoveredFiles.Should().HaveCount(2);
        vm.SelectedCountSummary.Should().Be("2 of 2 files selected");
    }

    [Fact]
    public void SelectAllAndDeselectAll_ShouldToggleSelectionAndSummary()
    {
        var vm = new GrabberWizardViewModel(_db, _crawler, _categoryManager);
        vm.DiscoveredFiles.Add(new GrabberFileItemViewModel(new GrabberFileItem { FileName = "a.png", Url = "https://a.png" }));
        vm.DiscoveredFiles.Add(new GrabberFileItemViewModel(new GrabberFileItem { FileName = "b.png", Url = "https://b.png" }));

        vm.DeselectAll();
        vm.DiscoveredFiles.Should().OnlyContain(f => !f.IsSelected);
        vm.SelectedCountSummary.Should().Be("0 of 2 files selected");

        vm.SelectAll();
        vm.DiscoveredFiles.Should().OnlyContain(f => f.IsSelected);
        vm.SelectedCountSummary.Should().Be("2 of 2 files selected");
    }

    [Fact]
    public async Task AddSelectedDownloadsAsync_ShouldSaveOnlySelectedFiles()
    {
        await _db.InitializeAsync();
        var vm = new GrabberWizardViewModel(_db, _crawler, _categoryManager);
        var item1 = new GrabberFileItemViewModel(new GrabberFileItem { FileName = "selected.mp4", Url = "https://example.com/selected.mp4" }) { IsSelected = true };
        var item2 = new GrabberFileItemViewModel(new GrabberFileItem { FileName = "unselected.mp4", Url = "https://example.com/unselected.mp4" }) { IsSelected = false };

        vm.DiscoveredFiles.Add(item1);
        vm.DiscoveredFiles.Add(item2);

        var addedCount = await vm.AddSelectedDownloadsAsync();
        addedCount.Should().Be(1);

        var downloads = await _db.GetAllDownloadsAsync();
        downloads.Should().HaveCount(1);
        downloads[0].FileName.Should().Be("selected.mp4");
        downloads[0].Status.Should().Be(DownloadStatus.Queued);
    }

    private sealed class FakeCrawler : IWebCrawler
    {
        public event EventHandler<GrabberProgressEventArgs>? ProgressUpdated;
        public event EventHandler<GrabberFileItem>? FileDiscovered;

        public List<GrabberFileItem> FilesToReturn { get; set; } = [];

        public Task<IReadOnlyList<GrabberFileItem>> CrawlAsync(GrabberProject project, CancellationToken ct = default)
        {
            ProgressUpdated?.Invoke(this, new GrabberProgressEventArgs(1, FilesToReturn.Count, project.StartUrl));
            foreach (var f in FilesToReturn)
            {
                FileDiscovered?.Invoke(this, f);
            }
            return Task.FromResult<IReadOnlyList<GrabberFileItem>>(FilesToReturn);
        }
    }
}
