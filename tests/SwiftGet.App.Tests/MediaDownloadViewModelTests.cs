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
using SwiftGet.Media.Extractor;
using SwiftGet.Media.Models;
using SwiftGet.Media.Sidecars;
using Xunit;

namespace SwiftGet.App.Tests;

public sealed class MediaDownloadViewModelTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly CategoryManager _categoryManager;

    public MediaDownloadViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_media_vm_test_" + Guid.NewGuid().ToString("N"));
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
    public async Task ProbeMediaAsync_SuccessfulMetadata_PopulatesFormatsAndDefaults()
    {
        await _db.InitializeAsync();
        var stubExtractor = new StubMediaExtractor
        {
            MetadataToReturn = new MediaMetadata
            {
                Url = "https://youtube.com/watch?v=test1234",
                Title = "NASA Launch Livestream",
                Uploader = "NASA",
                Duration = TimeSpan.FromSeconds(3600),
                Formats = new List<MediaFormat>
                {
                    new()
                    {
                        FormatId = "137",
                        Extension = "mp4",
                        Resolution = "1080p",
                        HasVideo = true,
                        HasAudio = true,
                        FileSizeBytes = 50_000_000
                    },
                    new()
                    {
                        FormatId = "140",
                        Extension = "m4a",
                        Resolution = "audio only",
                        HasVideo = false,
                        HasAudio = true,
                        FileSizeBytes = 5_000_000
                    }
                }
            }
        };

        var vm = new MediaDownloadViewModel(stubExtractor, _db, _categoryManager)
        {
            MediaUrl = "https://youtube.com/watch?v=test1234"
        };

        await vm.ProbeMediaAsync();

        vm.Title.Should().Be("NASA Launch Livestream");
        vm.Uploader.Should().Be("NASA");
        vm.Formats.Should().HaveCount(2);
        vm.SelectedFormat.Should().NotBeNull();
        vm.SelectedFormat!.FormatId.Should().Be("137");
        vm.FileName.Should().Be("NASA Launch Livestream.mp4");
        vm.IsDrmProtected.Should().BeFalse();
        vm.DrmNoticeVisibility.Should().Be(Visibility.Collapsed);
        vm.ContentVisibility.Should().Be(Visibility.Visible);
        vm.CanDownload.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeMediaAsync_DrmProtectedStream_DisplaysDrmNoticeAndBlocksDownload()
    {
        await _db.InitializeAsync();
        var stubExtractor = new StubMediaExtractor
        {
            ExceptionToThrow = new DrmProtectionDetectedException(
                "Widevine",
                "Widevine DRM detected. SwiftGet respects digital rights management and does not bypass DRM.")
        };

        var vm = new MediaDownloadViewModel(stubExtractor, _db, _categoryManager)
        {
            MediaUrl = "https://netflix.com/watch/999"
        };

        await vm.ProbeMediaAsync();

        vm.IsDrmProtected.Should().BeTrue();
        vm.DrmNoticeVisibility.Should().Be(Visibility.Visible);
        vm.ContentVisibility.Should().Be(Visibility.Collapsed);
        vm.DrmMessage.Should().Contain("Widevine DRM");
        vm.CanDownload.Should().BeFalse();

        var item = await vm.EnqueueDownloadAsync();
        item.Should().BeNull();
    }

    [Fact]
    public async Task EnqueueDownloadAsync_ValidFormat_SavesQueuedDownloadToDatabase()
    {
        await _db.InitializeAsync();
        var stubExtractor = new StubMediaExtractor
        {
            MetadataToReturn = new MediaMetadata
            {
                Url = "https://example.com/summit.m3u8",
                Title = "Open Source Summit",
                Formats = new List<MediaFormat>
                {
                    new()
                    {
                        FormatId = "best",
                        Extension = "mkv",
                        Resolution = "720p",
                        HasVideo = true,
                        HasAudio = true,
                        FileSizeBytes = 120_000_000
                    }
                }
            }
        };

        var vm = new MediaDownloadViewModel(stubExtractor, _db, _categoryManager)
        {
            MediaUrl = "https://example.com/summit.m3u8",
            SaveDirectory = _tempDir,
            QueueName = "media_queue"
        };

        await vm.ProbeMediaAsync();
        var enqueued = await vm.EnqueueDownloadAsync();

        enqueued.Should().NotBeNull();
        enqueued!.Status.Should().Be(DownloadStatus.Queued);
        enqueued.QueueId.Should().Be("media_queue");
        enqueued.FileName.Should().Be("Open Source Summit.mkv");
        enqueued.TotalBytes.Should().Be(120_000_000);

        var dbItem = await _db.GetDownloadAsync(enqueued.Id);
        dbItem.Should().NotBeNull();
        dbItem!.FileName.Should().Be("Open Source Summit.mkv");
    }

    [Fact]
    public async Task ProbeMediaAsync_EmptyUrl_SetsStatusAndDoesNotProbe()
    {
        await _db.InitializeAsync();
        var stubExtractor = new StubMediaExtractor();
        var vm = new MediaDownloadViewModel(stubExtractor, _db, _categoryManager)
        {
            MediaUrl = "   "
        };

        await vm.ProbeMediaAsync();

        vm.StatusMessage.Should().Contain("Please enter a valid media stream URL");
        vm.Formats.Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeMediaAsync_OnFailure_LeavesLoadingState_SetsHasError_AndDisablesButtons()
    {
        await _db.InitializeAsync();
        var stubExtractor = new StubMediaExtractor
        {
            ExceptionToThrow = new InvalidOperationException("yt-dlp extraction failed: ERROR: [WinError 448] The path cannot be traversed because it contains an untrusted mount point")
        };

        var vm = new MediaDownloadViewModel(stubExtractor, _db, _categoryManager)
        {
            MediaUrl = "https://youtube.com/watch?v=broken"
        };

        await vm.ProbeMediaAsync();

        vm.IsProbing.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        vm.Title.Should().Be("Failed to load media info");
        vm.ErrorMessage.Should().Contain("untrusted mount point");
        vm.ErrorDetails.Should().Contain("WinError 448");
        vm.CanDownload.Should().BeFalse();
        vm.Formats.Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeMediaAsync_OnSuccess_SanitizesTitleAndExtensionWithoutDoubleDot()
    {
        await _db.InitializeAsync();
        var stubExtractor = new StubMediaExtractor
        {
            MetadataToReturn = new MediaMetadata
            {
                Url = "https://youtube.com/watch?v=ipadmini",
                Title = "(50) The new iPad mini: Thinner. Lighter. Ultraportable. - YouTube",
                Formats = new List<MediaFormat>
                {
                    new()
                    {
                        FormatId = "22",
                        Extension = "mp4",
                        Resolution = "720p",
                        HasVideo = true,
                        HasAudio = true,
                        FileSizeBytes = 20_502_549
                    }
                }
            }
        };

        var vm = new MediaDownloadViewModel(stubExtractor, _db, _categoryManager)
        {
            MediaUrl = "https://youtube.com/watch?v=ipadmini"
        };

        await vm.ProbeMediaAsync();

        vm.HasError.Should().BeFalse();
        vm.Title.Should().Be("(50) The new iPad mini: Thinner. Lighter. Ultraportable. - YouTube");
        vm.FileName.Should().Be("The new iPad mini - Thinner. Lighter. Ultraportable.mp4");
        vm.FileName.Should().NotContain("..mp4");
        vm.FileName.Should().NotContain("(50)");
        vm.FileName.Should().NotContain(" - YouTube");
    }

    [Fact]
    public async Task ProbeMediaAsync_WithRealExtractor_ProbesSuccessfully()
    {
        await _db.InitializeAsync();
        var manager = new SidecarManager();
        if (!manager.IsYtDlpAvailable()) return;

        var extractor = new YtDlpMediaExtractor(manager);
        var vm = new MediaDownloadViewModel(extractor, _db, _categoryManager)
        {
            MediaUrl = "https://www.youtube.com/watch?v=P6pcTK0wTYA"
        };

        await vm.ProbeMediaAsync();
        if (vm.HasError)
        {
            throw new InvalidOperationException($"VM Error: {vm.ErrorMessage}\nDetails: {vm.ErrorDetails}");
        }

        vm.HasError.Should().BeFalse();
        vm.Formats.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FixPathAutomatically_ResolvesPhysicalPath_ClearsError_AndRetriesProbe()
    {
        await _db.InitializeAsync();
        var stubExtractor = new StubMediaExtractor
        {
            ExceptionToThrow = new InvalidOperationException("yt-dlp extraction failed: ERROR: [WinError 448] The path cannot be traversed because it contains an untrusted mount point")
        };

        var vm = new MediaDownloadViewModel(stubExtractor, _db, _categoryManager)
        {
            MediaUrl = "https://youtube.com/watch?v=broken",
            SaveDirectory = "C:\\Temp"
        };

        await vm.ProbeMediaAsync();
        vm.HasError.Should().BeTrue();

        // Stub now succeeds after path fix
        stubExtractor.ExceptionToThrow = null;
        stubExtractor.MetadataToReturn = new MediaMetadata
        {
            Title = "Fixed Video",
            Formats = [new MediaFormat { FormatId = "18", Extension = "mp4", HasVideo = true, HasAudio = true }]
        };

        await vm.FixPathAutomaticallyCommand.ExecuteAsync(null);

        vm.HasError.Should().BeFalse();
        vm.Formats.Should().NotBeEmpty();
        vm.Title.Should().Be("Fixed Video");
        vm.CanDownload.Should().BeTrue();
    }

    private sealed class StubMediaExtractor : IMediaExtractor
    {
        public MediaMetadata? MetadataToReturn { get; set; }
        public Exception? ExceptionToThrow { get; set; }

        public Task<MediaMetadata> ExtractMetadataAsync(string mediaUrl, CancellationToken ct = default)
        {
            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(MetadataToReturn ?? new MediaMetadata { Title = "Stub" });
        }
    }
}
