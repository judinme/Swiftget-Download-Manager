using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using Xunit;

namespace SwiftGet.App.Tests;

public class AddTorrentViewModelTests
{
    [Fact]
    public async Task InitializeFromSourceAsync_ValidMagnet_ExtractsMetadataAndSetsCategory()
    {
        var vm = new AddTorrentViewModel();
        var magnetUri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Ubuntu+Desktop";

        await vm.InitializeFromSourceAsync(magnetUri);

        vm.Source.Should().Be(magnetUri);
        vm.TorrentName.Should().Be("Ubuntu Desktop");
        vm.InfoHash.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
        vm.SelectedCategory.Should().Be("Torrents");
        vm.QueueName.Should().Be("Main");
        vm.StartImmediately.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeFromSourceAsync_InvalidMagnet_SetsDescriptiveStatusMessage()
    {
        var vm = new AddTorrentViewModel();
        var malformedUri = "magnet:?invalid=true";

        await vm.InitializeFromSourceAsync(malformedUri);

        vm.StatusMessage.Should().Contain("missing a valid 'xt'");
    }

    [Fact]
    public void SelectAllAndDeselectAll_TogglesAllFilesSelection()
    {
        var vm = new AddTorrentViewModel();
        vm.Files.Add(new AddTorrentFileEntryViewModel { FileIndex = 0, RelativePath = "file1.txt", IsSelected = true });
        vm.Files.Add(new AddTorrentFileEntryViewModel { FileIndex = 1, RelativePath = "file2.txt", IsSelected = true });

        vm.DeselectAllFilesCommand.Execute(null);
        vm.Files[0].IsSelected.Should().BeFalse();
        vm.Files[1].IsSelected.Should().BeFalse();

        vm.SelectAllFilesCommand.Execute(null);
        vm.Files[0].IsSelected.Should().BeTrue();
        vm.Files[1].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void UpdateFreeSpace_CalculatesDriveSpaceAccurately()
    {
        var vm = new AddTorrentViewModel();
        vm.UpdateFreeSpace();

        vm.FreeDiskSpace.Should().NotBeNullOrWhiteSpace();
        vm.FreeDiskSpace.Should().Contain("GB free");
    }

    [Fact]
    public async Task InitializeFromSourceAsync_TorrentFile_LoadsMetadataAndPopulatesFileList()
    {
        // Create a temporary torrent file using MonoTorrent's creator
        var tempDir = Path.Combine(Path.GetTempPath(), $"SwiftGet_AddTorrent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var payloadFile = Path.Combine(tempDir, "sample.bin");
            await File.WriteAllBytesAsync(payloadFile, new byte[16384]);

            var creator = new MonoTorrent.TorrentCreator();
            var fileSource = new MonoTorrent.TorrentFileSource(tempDir);
            var torrentBytes = await creator.CreateAsync(fileSource);

            var torrentPath = Path.Combine(tempDir, "sample.torrent");
            await File.WriteAllBytesAsync(torrentPath, torrentBytes.Encode());

            var vm = new AddTorrentViewModel();
            await vm.InitializeFromSourceAsync(torrentPath);

            vm.TorrentName.Should().NotBeNullOrWhiteSpace();
            vm.InfoHash.Should().HaveLength(40);
            vm.Files.Should().NotBeEmpty();
            vm.TotalBytes.Should().Be(16384);
            vm.FormattedTotalSize.Should().Be("16 KB");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RecalculateSelectedSize_WhenFileSelectionToggled_UpdatesSelectedBytesAndSummary()
    {
        var vm = new AddTorrentViewModel();
        var f1 = new AddTorrentFileEntryViewModel
        {
            FileIndex = 0,
            RelativePath = "video.mp4",
            FileSizeBytes = 1024 * 1024 * 100, // 100 MB
            IsSelected = true,
            SelectionChangedCallback = vm.RecalculateSelectedSize
        };
        var f2 = new AddTorrentFileEntryViewModel
        {
            FileIndex = 1,
            RelativePath = "sample.nfo",
            FileSizeBytes = 1024 * 10, // 10 KB
            IsSelected = true,
            SelectionChangedCallback = vm.RecalculateSelectedSize
        };

        vm.Files.Add(f1);
        vm.Files.Add(f2);
        vm.TotalBytes = f1.FileSizeBytes + f2.FileSizeBytes;
        vm.RecalculateSelectedSize();

        vm.SelectedBytes.Should().Be(f1.FileSizeBytes + f2.FileSizeBytes);
        vm.SizeSummaryText.Should().Be(vm.FormattedTotalSize);

        // Deselect f1
        f1.IsSelected = false;
        vm.SelectedBytes.Should().Be(f2.FileSizeBytes);
        vm.SizeSummaryText.Should().Contain(vm.FormattedSelectedSize);
    }
}
