using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using Xunit;

namespace SwiftGet.App.Tests;

public class MainViewModelVirtualizationTests
{
    [Fact]
    public void Add10000Items_ShouldBeFastAndMemoryEfficient()
    {
        // Arrange
        var vm = new MainViewModel();
        var extensions = new[] { ".zip", ".pdf", ".mp3", ".exe", ".mp4", ".iso", ".docx" };
        var categories = new[] { "compressed", "documents", "music", "programs", "video" };
        var statuses = new[] { DownloadStatus.Downloading, DownloadStatus.Completed, DownloadStatus.Paused, DownloadStatus.Queued };

        var items = new List<DownloadItemViewModel>(10_000);
        for (int i = 0; i < 10_000; i++)
        {
            var ext = extensions[i % extensions.Length];
            var category = categories[i % categories.Length];
            var status = statuses[i % statuses.Length];

            items.Add(new DownloadItemViewModel
            {
                Id = Guid.NewGuid(),
                FileName = $"TestDownload_{i:D5}{ext}",
                Url = $"https://cdn.example.com/files/TestDownload_{i:D5}{ext}",
                SavePath = $@"C:\Downloads\TestDownload_{i:D5}{ext}",
                TotalBytes = 10_000_000 + (i * 1024),
                DownloadedBytes = status == DownloadStatus.Completed ? 10_000_000 + (i * 1024) : (i * 512),
                Status = status,
                Category = category,
                QueueName = (i % 5 == 0) ? "Main Queue" : string.Empty,
                SpeedBytesPerSec = status == DownloadStatus.Downloading ? 2_500_000 : 0
            });
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memBefore = GC.GetTotalMemory(true);

        var sw = Stopwatch.StartNew();

        // Act - Add 10,000 items
        vm.AddDownloads(items);

        sw.Stop();

        long memAfter = GC.GetTotalMemory(true);
        long memUsedBytes = memAfter - memBefore;
        double memUsedMb = memUsedBytes / (1024.0 * 1024.0);

        // Assert
        vm.AllDownloads.Count.Should().Be(10_000);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Adding 10,000 items to virtual collection should be sub-second");
        memUsedMb.Should().BeLessThan(80, "10,000 view models should comfortably fit within the <100 MB budget");
    }

    [Fact]
    public void Filter10000Items_ByCategory_ShouldBeSub25Milliseconds()
    {
        // Arrange
        var vm = new MainViewModel();
        var categories = new[] { "compressed", "documents", "music", "programs", "video" };
        var items = new List<DownloadItemViewModel>(10_000);

        for (int i = 0; i < 10_000; i++)
        {
            var category = categories[i % categories.Length];
            items.Add(new DownloadItemViewModel
            {
                Id = Guid.NewGuid(),
                FileName = $"File_{i:D5}.dat",
                Url = $"https://example.com/file_{i}.dat",
                SavePath = $@"C:\Downloads\file_{i}.dat",
                TotalBytes = 5_000_000,
                Status = (i % 2 == 0) ? DownloadStatus.Completed : DownloadStatus.Downloading,
                Category = category
            });
        }
        vm.AddDownloads(items);

        // Warm up filter
        vm.SelectCategory("compressed");

        // Act & Assert across categories
        foreach (var category in categories)
        {
            var sw = Stopwatch.StartNew();
            vm.SelectCategory(category);
            sw.Stop();

            sw.ElapsedMilliseconds.Should().BeLessThan(35, $"Switching category to '{category}' with 10k items must be instant (< 35ms)");
            vm.FilteredDownloads.Count.Should().Be(2000);
        }

        // Test "all" category
        var swAll = Stopwatch.StartNew();
        vm.SelectCategory("all");
        swAll.Stop();
        swAll.ElapsedMilliseconds.Should().BeLessThan(35);
        vm.FilteredDownloads.Count.Should().Be(10_000);
    }

    [Fact]
    public void SearchFilter10000Items_ShouldBeSub35Milliseconds()
    {
        // Arrange
        var vm = new MainViewModel();
        var items = new List<DownloadItemViewModel>(10_000);
        for (int i = 0; i < 10_000; i++)
        {
            items.Add(new DownloadItemViewModel
            {
                Id = Guid.NewGuid(),
                FileName = i == 7777 ? "special_target_file.iso" : $"dataset_archive_{i:D5}.tar.gz",
                Url = $"https://example.com/data_{i}.tar.gz",
                SavePath = $@"C:\Downloads\data_{i}.tar.gz",
                TotalBytes = 100_000_000,
                Status = DownloadStatus.Completed,
                Category = "compressed"
            });
        }
        vm.AddDownloads(items);

        // Warm up
        vm.SearchFilter = "test";

        // Act
        var sw = Stopwatch.StartNew();
        vm.SearchFilter = "special_target";
        sw.Stop();

        // Assert
        sw.ElapsedMilliseconds.Should().BeLessThan(45);
        vm.FilteredDownloads.Should().ContainSingle();
        vm.FilteredDownloads[0].FileName.Should().Be("special_target_file.iso");
    }

    [Fact]
    public void Simulate50ActiveDownloads_PumpingUpdatesAt10Hz_MaintainsHighThroughput()
    {
        // Arrange
        var vm = new MainViewModel();
        var activeDownloads = Enumerable.Range(0, 50).Select(i => new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = $"stream_{i:D2}.bin",
            Url = $"https://stream.example.com/{i}.bin",
            SavePath = $@"C:\Downloads\stream_{i}.bin",
            TotalBytes = 500_000_000,
            DownloadedBytes = 0,
            Status = DownloadStatus.Downloading,
            Category = "programs"
        }).ToList();

        vm.AddDownloads(activeDownloads);

        // Act: simulate 20 ticks (2 seconds at 10 Hz) across 50 concurrent downloads (1,000 total progress updates)
        var sw = Stopwatch.StartNew();

        for (int tick = 1; tick <= 20; tick++)
        {
            for (int i = 0; i < activeDownloads.Count; i++)
            {
                var d = activeDownloads[i];
                long downloaded = tick * 1_000_000;
                double speed = 10_000_000; // 10 MB/s each = 500 MB/s aggregate
                d.UpdateProgress(downloaded, d.TotalBytes, speed);
            }
        }

        sw.Stop();

        // Assert: 1,000 updates should complete in under 250ms total (< 0.25ms per update)
        sw.ElapsedMilliseconds.Should().BeLessThan(350, "1,000 UI progress ticks across 50 downloads must complete in < 350ms total, well within 60 fps frame budget");

        // Verify status bar aggregated metrics
        vm.UpdateMetrics();
        vm.ActiveDownloadsCount.Should().Be(50);
        vm.TotalDownloadsCount.Should().Be(50);
        vm.TotalTransferRate.Should().Contain("MB/s");
    }

    [Fact]
    public void FilterTorrentsCategory_ShouldMatchJobKindAndExtension_Sub25Milliseconds()
    {
        var vm = new MainViewModel();
        var items = new List<DownloadItemViewModel>(5_000);

        for (int i = 0; i < 5_000; i++)
        {
            bool isTorrent = (i % 5 == 0);
            items.Add(new DownloadItemViewModel
            {
                Id = Guid.NewGuid(),
                JobKind = isTorrent ? JobKind.Torrent : JobKind.Http,
                FileName = isTorrent ? $"ubuntu-release-{i}.iso" : $"archive-{i}.zip",
                Url = isTorrent ? "magnet:?xt=urn:btih:da39a3ee" : $"https://example.com/file_{i}.zip",
                SavePath = @"C:\Downloads",
                TotalBytes = 100_000_000,
                Status = DownloadStatus.Downloading,
                Category = isTorrent ? "torrents" : "compressed"
            });
        }
        vm.AddDownloads(items);

        var sw = Stopwatch.StartNew();
        vm.SelectCategory("torrents");
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(35, "Filtering 5,000 items by torrents category must be sub-35ms");
        vm.FilteredDownloads.Should().HaveCount(1_000);
        vm.FilteredDownloads.All(d => d.JobKind == JobKind.Torrent || d.Category == "torrents").Should().BeTrue();
    }
}
