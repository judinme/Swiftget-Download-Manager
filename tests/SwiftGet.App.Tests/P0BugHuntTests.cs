using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Probing;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.App.Tests;

public sealed class P0BugHuntTests
{
    [Fact]
    public void P02_ProgressEvent_RaisesAllExpectedPropertyChangedEvents()
    {
        var vm = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "test.bin",
            TotalBytes = 1000,
            DownloadedBytes = 0
        };

        var raisedProperties = new List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null)
            {
                raisedProperties.Add(e.PropertyName);
            }
        };

        // Hop 3: ViewModel update
        vm.UpdateProgress(500, 1000, 250.0);

        // Verify all UI bound properties fired PropertyChanged
        raisedProperties.Should().Contain(nameof(DownloadItemViewModel.Progress));
        raisedProperties.Should().Contain(nameof(DownloadItemViewModel.ProgressPercentage));
        raisedProperties.Should().Contain(nameof(DownloadItemViewModel.FormattedDownloaded));
        raisedProperties.Should().Contain(nameof(DownloadItemViewModel.FormattedSpeed));
        raisedProperties.Should().Contain(nameof(DownloadItemViewModel.FormattedTimeLeft));
        raisedProperties.Should().Contain(nameof(DownloadItemViewModel.StatusText));
        raisedProperties.Should().Contain(nameof(DownloadItemViewModel.StatusColor));

        vm.ProgressPercentage.Should().Be(50.0);
        vm.Progress.Should().Be(50.0);
    }

    [Fact]
    public void P02_DownloadDialog_ReadsSameModelInstanceAsMainList()
    {
        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "archive.zip",
            TotalBytes = 1000,
            DownloadedBytes = 0,
            Status = DownloadStatus.Downloading
        };

        var dialogVm = new DownloadDialogViewModel(item);

        // Verify Dialog reads the exact same instance
        dialogVm.Item.Should().BeSameAs(item);

        // Update progress on item
        item.UpdateProgress(750, 1000, 500.0);

        dialogVm.Item.ProgressPercentage.Should().Be(75.0);
        dialogVm.Item.DownloadedBytes.Should().Be(750);
    }

    [Fact]
    public void P03_AddUrlViewModel_WithoutProbe_LeavesFileSizeUnknown()
    {
        var probe = new HttpProbeService();
        var addVm = new AddUrlViewModel(probe)
        {
            Url = "https://example.com/test.zip"
        };

        // When ProbeAsync is NOT called, FileSize stays -1
        addVm.FileSize.Should().Be(-1);
        addVm.FormattedSize.Should().Be("Unknown");
    }

    [Fact]
    public void P03_FormattedSize_Matrix_Verification()
    {
        var vm = new DownloadItemViewModel();

        // 0 or negative
        vm.TotalBytes = -1;
        vm.FormattedSize.Should().Be("Unknown");

        vm.TotalBytes = 0;
        vm.FormattedSize.Should().Be("Unknown");

        // Bytes
        vm.TotalBytes = 500;
        vm.FormattedSize.Should().Be("500 B");

        // KB
        vm.TotalBytes = 2048;
        vm.FormattedSize.Should().Be("2 KB");

        // MB
        vm.TotalBytes = 15 * 1024 * 1024;
        vm.FormattedSize.Should().Be("15 MB");

        // GB (above 2GB and 4GB without overflow)
        vm.TotalBytes = 5_000_000_000L;
        vm.FormattedSize.Should().Be("4.66 GB");
    }

    [Fact]
    public void P01_TorrentInMainWindow_WithoutEnginePolling_ProgressStaysZero()
    {
        // Demonstrates why torrents in MainWindow never showed progress:
        // MainWindow adds DownloadItemViewModel, but never polls MonoTorrentEngine.GetJobStats
        var torrentVm = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            JobKind = JobKind.Torrent,
            FileName = "linux.iso",
            Status = DownloadStatus.Downloading,
            TotalBytes = 1000,
            DownloadedBytes = 0
        };

        // Without an active polling timer or event handler, ViewModel remains unchanged
        torrentVm.ProgressPercentage.Should().Be(0);
        torrentVm.SpeedBytesPerSec.Should().Be(0);

        // When UpdateTorrentProgress is called with engine stats, progress updates correctly:
        torrentVm.UpdateTorrentProgress(
            downloaded: 400,
            total: 1000,
            downSpeed: 1024 * 1024,
            upSpeed: 128 * 1024,
            uploaded: 50,
            seeds: 5,
            peers: 12,
            status: DownloadStatus.Downloading);

        torrentVm.ProgressPercentage.Should().Be(40.0);
        torrentVm.SpeedBytesPerSec.Should().Be(1024 * 1024);
        torrentVm.SeedsConnected.Should().Be(5);
        torrentVm.PeersConnected.Should().Be(12);
    }
}
