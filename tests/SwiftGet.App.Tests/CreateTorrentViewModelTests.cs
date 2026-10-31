using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using Xunit;

namespace SwiftGet.App.Tests;

public class CreateTorrentViewModelTests
{
    [Fact]
    public void Constructor_InitializesWithDefaultPieceSizesAndTrackers()
    {
        var mockEngine = new MockTorrentEngine();
        var vm = new CreateTorrentViewModel(mockEngine);

        vm.PieceSizes.Should().NotBeEmpty();
        vm.PieceSizes.Should().Contain("Auto (Recommended)");
        vm.TrackersText.Should().NotBeNullOrWhiteSpace();
        vm.SaveTorrentPath.Should().EndWith(".torrent");
    }

    [Fact]
    public async Task CreateTorrentFileAsync_NonExistentSource_ReturnsFalseWithErrorMessage()
    {
        var mockEngine = new MockTorrentEngine();
        var vm = new CreateTorrentViewModel(mockEngine)
        {
            SourcePath = @"C:\NonExistent_SwiftGet_Path_12345"
        };

        var result = await vm.CreateTorrentFileAsync();
        result.Should().BeFalse();
        vm.StatusMessage.Should().Contain("valid existing file or folder");
    }

    [Fact]
    public async Task CreateTorrentFileAsync_ValidFile_InvokesEngineAndWritesTorrent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"SwiftGet_CreateTorrentTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourceFile = Path.Combine(tempDir, "payload.dat");
            await File.WriteAllBytesAsync(sourceFile, new byte[1024]);

            var destTorrent = Path.Combine(tempDir, "output.torrent");

            var mockEngine = new MockTorrentEngine();
            var vm = new CreateTorrentViewModel(mockEngine)
            {
                SourcePath = sourceFile,
                SaveTorrentPath = destTorrent,
                Comment = "Test Comment",
                SelectedPieceSizeIndex = 1 // 32 KB
            };

            var result = await vm.CreateTorrentFileAsync();
            result.Should().BeTrue();
            File.Exists(destTorrent).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
