using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Services;
using Xunit;

namespace SwiftGet.App.Tests;

public class DownloadDetailsViewModelTests
{
    private sealed class MockSnapshotService : IDownloadDetailsSnapshotService
    {
        public DownloadDetailsSnapshot? SnapshotToReturn { get; set; }
        public int CaptureCallCount { get; private set; }

        public Task<DownloadDetailsSnapshot> CaptureSnapshotAsync(
            string downloadId,
            DownloadItem? fallbackItem = null,
            double currentSpeed = 0,
            TimeSpan? currentRemainingTime = null,
            double? currentProgressPercentage = null,
            CancellationToken ct = default)
        {
            CaptureCallCount++;
            if (SnapshotToReturn != null)
            {
                return Task.FromResult(SnapshotToReturn);
            }

            var general = new GeneralDetailSnapshot
            {
                FileName = fallbackItem?.FileName ?? "test.bin",
                SaveFolder = @"C:\Downloads",
                FullSavePath = @"C:\Downloads\" + (fallbackItem?.FileName ?? "test.bin"),
                ProgressPercentage = currentProgressPercentage ?? 45.5,
                RemainingTime = currentRemainingTime ?? TimeSpan.FromMinutes(5),
                DownloadedBytes = fallbackItem?.DownloadedBytes ?? 455_000_000,
                TotalBytes = fallbackItem?.TotalBytes ?? 1_000_000_000,
                UploadedBytes = 10_000_000,
                ShareRatio = 0.02,
                AddedAt = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                DownloadSpeedBytesPerSec = currentSpeed > 0 ? (long)currentSpeed : 2_500_000,
                UploadSpeedBytesPerSec = 50_000,
                Priority = "Normal",
                SourcePageUrl = "https://example.com/files/download",
                IsFolder = false
            };

            var torrent = new TorrentDetailSnapshot
            {
                TotalPeers = 100,
                ConnectedPeers = 15,
                TotalSeeds = 50,
                ConnectedSeeds = 8,
                Availability = 3.25,
                LastSeenComplete = DateTime.UtcNow,
                Trackers = new List<TrackerDetailSnapshot>
                {
                    new() { AnnounceUrl = "udp://tracker.openbittorrent.com:80/announce", Status = "Working", IsError = false },
                    new() { AnnounceUrl = "udp://dead.tracker.com:6969/announce", Status = "No such host is known", IsError = true }
                }
            };

            var http = new HttpDetailSnapshot
            {
                Server = "nginx/1.24",
                ResumeSupported = true,
                ContentType = "application/octet-stream",
                RedirectTarget = "https://cdn.example.com/file",
                SegmentCount = 8
            };

            var grid = new PieceGridSnapshot
            {
                TotalPiecesOrSegments = 100,
                DownloadedPieces = 45,
                Cells = new PieceState[100]
            };

            var connections = new List<ConnectionDetailSnapshot>
            {
                new() { Host = "192.168.1.50", Port = 6881, ConnectionCount = 1 },
                new() { Host = "10.0.0.2", Port = 51413, ConnectionCount = 2 },
                new() { Host = "172.16.0.1", Port = 8080, ConnectionCount = 1 }
            };

            var snap = new DownloadDetailsSnapshot
            {
                DownloadId = downloadId,
                JobKind = JobKind.Torrent,
                General = general,
                Torrent = torrent,
                Http = http,
                ProgressGrid = grid,
                Connections = connections
            };

            return Task.FromResult(snap);
        }
    }

    [Fact]
    public void InitialState_IsNotOpen_AndPanelVisibilityCollapsed()
    {
        var service = new MockSnapshotService();
        var vm = new DownloadDetailsViewModel(service);

        Assert.False(vm.IsOpen);
        Assert.Equal(Visibility.Collapsed, vm.PanelVisibility);
        Assert.Equal("GENERAL", vm.ActiveTab);
    }

    [Fact]
    public void SetSelectionState_NullItem_ClearsSelection()
    {
        var service = new MockSnapshotService();
        var vm = new DownloadDetailsViewModel(service);

        vm.SetSelectionState(null, 0);

        Assert.False(vm.HasSelection);
        Assert.False(vm.IsMultiSelect);
    }

    [Fact]
    public void SetSelectionState_MultiSelect_SetsMultiSelectState()
    {
        var service = new MockSnapshotService();
        var vm = new DownloadDetailsViewModel(service);

        vm.SetSelectionState(null, 3);

        Assert.True(vm.IsMultiSelect);
        Assert.Contains("3 items selected", vm.FileName);
    }

    [Fact]
    public void TabSelection_ChangesActiveTab()
    {
        var service = new MockSnapshotService();
        var vm = new DownloadDetailsViewModel(service);

        vm.SelectTab("DETAILS");
        Assert.Equal("DETAILS", vm.ActiveTab);

        vm.SelectTab("PROGRESS");
        Assert.Equal("PROGRESS", vm.ActiveTab);

        vm.SelectTab("CONNECTIONS");
        Assert.Equal("CONNECTIONS", vm.ActiveTab);

        vm.SelectTab("GENERAL");
        Assert.Equal("GENERAL", vm.ActiveTab);
    }

    [Fact]
    public async Task RefreshSnapshotAsync_PopulatesTorrentAndGeneralDetails()
    {
        var service = new MockSnapshotService();
        var vm = new DownloadDetailsViewModel(service);
        vm.IsOpen = true;

        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "ubuntu-26.04.iso",
            Url = "magnet:?xt=urn:btih:123",
            SavePath = @"D:\Downloads",
            JobKind = JobKind.Torrent,
            DownloadedBytes = 455_000_000,
            TotalBytes = 1_000_000_000,
            ProgressPercentage = 45.5,
            SpeedBytesPerSec = 1024 * 1024,
            Status = DownloadStatus.Downloading
        };

        await vm.RefreshSnapshotAsync(item, forceRefresh: true);

        Assert.True(vm.IsTorrent);
        Assert.False(vm.IsHttpOrMedia);
        Assert.Equal("45.5%", vm.ProgressPercentText);
        Assert.Equal("3.25", vm.AvailabilityText);
        Assert.Equal(2, vm.Trackers.Count);
        Assert.Equal("udp://tracker.openbittorrent.com:80/announce", vm.Trackers[0].AnnounceUrl);
        Assert.Equal("Working", vm.Trackers[0].Status);
        Assert.Equal(3, vm.Connections.Count);
    }

    [Fact]
    public void ClosePanelCommand_SetsIsOpenFalse_AndCollapses()
    {
        var service = new MockSnapshotService();
        var vm = new DownloadDetailsViewModel(service);
        vm.IsOpen = true;
        Assert.Equal(Visibility.Visible, vm.PanelVisibility);

        vm.ClosePanel();

        Assert.False(vm.IsOpen);
        Assert.Equal(Visibility.Collapsed, vm.PanelVisibility);
    }

    [Fact]
    public void SortConnections_SortsAscendingAndDescending()
    {
        var service = new MockSnapshotService();
        var vm = new DownloadDetailsViewModel(service);

        vm.Connections.Add(new ConnectionItemViewModel { Host = "Beta", Port = 5000, ConnectionCount = 1 });
        vm.Connections.Add(new ConnectionItemViewModel { Host = "Alpha", Port = 8000, ConnectionCount = 2 });

        // Sort by Port ascending
        vm.SortConnections("Port");
        Assert.Equal(5000, vm.Connections[0].Port);
        Assert.Equal(8000, vm.Connections[1].Port);

        // Sort by Port descending
        vm.SortConnections("Port");
        Assert.Equal(8000, vm.Connections[0].Port);
        Assert.Equal(5000, vm.Connections[1].Port);
    }

    [Theory]
    [InlineData(0.0, 0, 0, "0%")]
    [InlineData(0.03, 100_000, 3_000_000_000, "0.03%")]
    [InlineData(0.32, 10_000_000, 3_120_000_000, "0.3%")]
    [InlineData(5.5, 55_000_000, 1_000_000_000, "5.5%")]
    [InlineData(59.0, 590_000, 1_000_000, "59%")]
    [InlineData(99.9, 999_000, 1_000_000, "99.9%")]
    [InlineData(100.0, 1_000_000, 1_000_000, "100%")]
    public void FormatProgressPercentage_FormatsExpectedPrecision(double pct, long downloaded, long total, string expected)
    {
        var result = DownloadDetailsViewModel.FormatProgressPercentage(pct, downloaded, total);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task RefreshSnapshotAsync_LiveMetrics_UpdatesRealtimeSynchronously()
    {
        var service = new MockSnapshotService();
        var vm = new DownloadDetailsViewModel(service);
        vm.IsOpen = true;

        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "video.mp4",
            SavePath = @"C:\Downloads\video.mp4",
            TotalBytes = 1_000_000,
            DownloadedBytes = 590_000,
            ProgressPercentage = 59.0,
            SpeedBytesPerSec = 33_670_000,
            RemainingTime = TimeSpan.FromSeconds(7),
            Status = DownloadStatus.Downloading
        };

        await vm.RefreshSnapshotAsync(item);

        Assert.Equal(59.0, vm.ProgressPercentage);
        Assert.Equal("59%", vm.ProgressPercentText);
        Assert.Equal("Remaining: 7s", vm.RemainingTimeText);
        Assert.Contains("576.2 KB / 976.6 KB", vm.DownloadedText);
        Assert.Contains("32.11 MB/s", vm.DownloadSpeedText);
    }

    [Fact]
    public async Task RefreshSnapshotAsync_LargeTorrentWith200PeersAnd50kPieces_ExecutesUnderBudget()
    {
        var service = new MockSnapshotService();

        // 250 connections
        var connections = new List<ConnectionDetailSnapshot>();
        for (int i = 0; i < 250; i++)
        {
            connections.Add(new ConnectionDetailSnapshot
            {
                Host = $"198.51.100.{i % 254}",
                Port = 10000 + i,
                ConnectionCount = (i % 3) + 1
            });
        }

        // 50,000 pieces downsampled to 1,500 cells
        var pieceStates = new PieceState[50_000];
        Array.Fill(pieceStates, PieceState.Downloaded, 0, 25_000);
        Array.Fill(pieceStates, PieceState.Missing, 25_000, 25_000);
        var downsampledCells = SwiftGet.Core.IO.PieceGridDownsampler.Downsample(pieceStates, 1500);

        var largeSnapshot = new DownloadDetailsSnapshot
        {
            DownloadId = Guid.NewGuid().ToString("N"),
            JobKind = JobKind.Torrent,
            General = new GeneralDetailSnapshot
            {
                FileName = "50GB-Dataset.iso",
                SaveFolder = @"C:\Downloads",
                FullSavePath = @"C:\Downloads\50GB-Dataset.iso",
                ProgressPercentage = 50.0,
                DownloadedBytes = 25_000_000_000,
                TotalBytes = 50_000_000_000,
                DownloadSpeedBytesPerSec = 45_000_000,
                RemainingTime = TimeSpan.FromMinutes(10),
                Priority = "Normal",
                IsFolder = false
            },
            Torrent = new TorrentDetailSnapshot
            {
                TotalPeers = 350,
                ConnectedPeers = 250,
                TotalSeeds = 150,
                ConnectedSeeds = 90,
                Availability = 5.8
            },
            ProgressGrid = new PieceGridSnapshot
            {
                TotalPiecesOrSegments = 50_000,
                DownloadedPieces = 25_000,
                Cells = downsampledCells
            },
            Connections = connections
        };

        var largeItem = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "50GB-Dataset.iso",
            SavePath = @"C:\Downloads\50GB-Dataset.iso",
            TotalBytes = 50_000_000_000,
            DownloadedBytes = 25_000_000_000,
            ProgressPercentage = 50.0,
            SpeedBytesPerSec = 45_000_000,
            RemainingTime = TimeSpan.FromMinutes(10),
            Status = DownloadStatus.Downloading
        };

        service.SnapshotToReturn = largeSnapshot;
        var vm = new DownloadDetailsViewModel(service);
        vm.IsOpen = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long memBefore = GC.GetAllocatedBytesForCurrentThread();

        await vm.RefreshSnapshotAsync(largeItem);

        sw.Stop();
        long memAllocated = GC.GetAllocatedBytesForCurrentThread() - memBefore;

        Assert.Equal(250, vm.Connections.Count);
        Assert.Equal(1500, vm.PieceCells.Length);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Large snapshot processing took {sw.ElapsedMilliseconds}ms, exceeding 50ms budget.");
    }
}
