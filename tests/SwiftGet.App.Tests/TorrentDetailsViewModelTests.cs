using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.App.Tests;

public class TorrentDetailsViewModelTests
{
    [Fact]
    public async Task InitializeAsync_ShouldPopulateStatsFilesPeersTrackersAndPieceMap()
    {
        var engine = new MockTorrentEngine();
        var jobId = Guid.NewGuid().ToString("N");

        engine.JobStats[jobId] = new TorrentJobStats
        {
            JobId = jobId,
            Name = "Ubuntu 24.04 LTS",
            InfoHash = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
            State = DownloadStatus.Downloading,
            TotalBytes = 2_000_000_000,
            DownloadedBytes = 1_000_000_000,
            UploadedBytes = 500_000_000,
            DownloadSpeedBytesPerSec = 5_242_880, // 5 MB/s
            UploadSpeedBytesPerSec = 524_288,   // 512 KB/s
            ConnectedSeeds = 15,
            TotalSeeds = 120,
            ConnectedPeers = 35,
            TotalPeers = 250,
            PieceCount = 1000,
            PieceLength = 2_000_000,
            ProgressPercentage = 50.0
        };

        engine.Files[jobId] =
        [
            new TorrentFileInfo { FileIndex = 0, RelativePath = "ubuntu-24.04-desktop-amd64.iso", FileSizeBytes = 1_900_000_000, DownloadedBytes = 950_000_000, Priority = TorrentFilePriority.High },
            new TorrentFileInfo { FileIndex = 1, RelativePath = "SHA256SUMS", FileSizeBytes = 1000, DownloadedBytes = 1000, Priority = TorrentFilePriority.Normal }
        ];

        engine.Peers[jobId] =
        [
            new TorrentPeerInfo { IpAddress = "192.168.1.100", Port = 51413, Client = "Transmission 4.0.5", DownloadSpeedBytesPerSec = 2_000_000, UploadSpeedBytesPerSec = 100_000, Flags = "D E", IsEncrypted = true, IsUtp = true }
        ];

        engine.Trackers[jobId] =
        [
            new TorrentTrackerInfo { Tier = 0, AnnounceUrl = "https://torrent.ubuntu.com/announce", Status = "Working", Seeds = 120, Peers = 250 }
        ];

        // 8 bytes bitfield: 0xFF, 0x00, 0xF0...
        byte[] bitFieldBytes = [0xFF, 0x00, 0xF0, 0x0F, 0xAA, 0x55, 0xCC, 0x33];
        engine.PieceBitFields[jobId] = bitFieldBytes;

        var item = new DownloadItemViewModel
        {
            Id = Guid.Parse(jobId),
            JobKind = JobKind.Torrent,
            FileName = "Ubuntu 24.04 LTS",
            InfoHash = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
            SavePath = "C:\\Downloads\\Ubuntu",
            Status = DownloadStatus.Downloading
        };

        using var vm = new TorrentDetailsViewModel(item, engine);
        await vm.InitializeAsync();

        // Verify General Tab
        vm.TorrentName.Should().Be("Ubuntu 24.04 LTS");
        vm.InfoHash.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
        vm.ProgressPercentage.Should().Be(50.0);
        vm.FormattedSize.Should().Be("1.86 GB");
        vm.FormattedDownloaded.Should().Be("953.67 MB");
        vm.FormattedUploaded.Should().Be("476.84 MB");
        vm.ShareRatioText.Should().Be("0.50");
        vm.FormattedSpeed.Should().Contain("↓ 5 MB/s");
        vm.FormattedSpeed.Should().Contain("↑ 512 KB/s");
        vm.SwarmSeedsText.Should().Be("15 connected (120 total)");
        vm.SwarmPeersText.Should().Be("35 connected (250 total)");
        vm.PieceStatsText.Should().Contain("1000 pieces");

        // Verify Files Tab
        vm.Files.Should().HaveCount(2);
        vm.Files[0].RelativePath.Should().Be("ubuntu-24.04-desktop-amd64.iso");
        vm.Files[0].SelectedPriorityIndex.Should().Be(1); // 1 = High
        vm.Files[1].RelativePath.Should().Be("SHA256SUMS");
        vm.Files[1].SelectedPriorityIndex.Should().Be(0); // 0 = Normal

        // Verify Peers Tab
        vm.Peers.Should().HaveCount(1);
        vm.Peers[0].Endpoint.Should().Be("192.168.1.100:51413");
        vm.Peers[0].Client.Should().Be("Transmission 4.0.5");
        vm.Peers[0].EncryptionText.Should().Be("Encrypted");
        vm.Peers[0].ProtocolText.Should().Be("uTP");

        // Verify Trackers Tab
        vm.Trackers.Should().HaveCount(1);
        vm.Trackers[0].AnnounceUrl.Should().Be("https://torrent.ubuntu.com/announce");
        vm.Trackers[0].Status.Should().Be("Working");

        // Verify Piece Map
        vm.PieceBlocks.Should().HaveCount(60);
        vm.PieceMapSummary.Should().Contain("pieces verified");

        // Verify parent item sync
        item.SpeedBytesPerSec.Should().Be(5_242_880);
        item.UploadSpeedBytesPerSec.Should().Be(524_288);
        item.DownloadedBytes.Should().Be(1_000_000_000);
        item.SeedsConnected.Should().Be(15);
        item.PeersConnected.Should().Be(35);
    }

    [Fact]
    public async Task SetFilePriority_ShouldInvokeEngine()
    {
        var engine = new MockTorrentEngine();
        var jobId = Guid.NewGuid().ToString("N");

        engine.JobStats[jobId] = new TorrentJobStats { JobId = jobId, Name = "Torrent 1" };
        engine.Files[jobId] =
        [
            new TorrentFileInfo { FileIndex = 0, RelativePath = "video.mp4", FileSizeBytes = 100_000, Priority = TorrentFilePriority.Normal }
        ];

        var item = new DownloadItemViewModel { Id = Guid.Parse(jobId), JobKind = JobKind.Torrent };
        using var vm = new TorrentDetailsViewModel(item, engine);
        await vm.InitializeAsync();

        vm.Files[0].SelectedPriorityIndex = 1; // 1 = High

        // Allow task dispatch
        await Task.Delay(50);

        engine.FilePriorityChanges.Should().Contain(c => c.JobId == jobId && c.FileIndex == 0 && c.Priority == TorrentFilePriority.High);
    }

    [Fact]
    public async Task ForceRecheck_ShouldInvokeEngineRecheck()
    {
        var engine = new MockTorrentEngine();
        var jobId = Guid.NewGuid().ToString("N");

        var item = new DownloadItemViewModel { Id = Guid.Parse(jobId), JobKind = JobKind.Torrent };
        using var vm = new TorrentDetailsViewModel(item, engine);

        await vm.ForceRecheckAsync();

        engine.RecheckedJobs.Should().Contain(jobId);
        vm.StatusMessage.Should().Contain("Hash check initiated");
    }

    [Fact]
    public async Task ApplySpeedLimits_ShouldInvokeEngineRateLimits()
    {
        var engine = new MockTorrentEngine();
        var jobId = Guid.NewGuid().ToString("N");

        var item = new DownloadItemViewModel { Id = Guid.Parse(jobId), JobKind = JobKind.Torrent };
        using var vm = new TorrentDetailsViewModel(item, engine);

        vm.DownloadLimitKbps = 2500;
        vm.UploadLimitKbps = 500;

        await vm.ApplySpeedLimitsAsync();

        engine.TorrentRateLimits.Should().Contain(c => c.JobId == jobId && c.Down == 2500 * 1024 && c.Up == 500 * 1024);
        vm.StatusMessage.Should().Contain("Applied speed limits");
    }

    [Fact]
    public void ToggleSequential_ShouldToggleViewModelAndItemState()
    {
        var engine = new MockTorrentEngine();
        var item = new DownloadItemViewModel { Id = Guid.NewGuid(), JobKind = JobKind.Torrent, IsSequential = false };
        using var vm = new TorrentDetailsViewModel(item, engine);

        vm.SequentialMode.Should().BeFalse();

        vm.ToggleSequential();

        vm.SequentialMode.Should().BeTrue();
        item.IsSequential.Should().BeTrue();

        vm.ToggleSequential();

        vm.SequentialMode.Should().BeFalse();
        item.IsSequential.Should().BeFalse();
    }
}
