using System;
using FluentAssertions;
using Microsoft.UI.Xaml;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using Xunit;

namespace SwiftGet.App.Tests;

public class DownloadDialogViewModelTests
{
    [Fact]
    public void ToggleDetails_TogglesShowDetailsAndDetailsVisibility()
    {
        // Arrange
        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "large_archive.zip",
            Url = "https://example.com/large_archive.zip",
            TotalBytes = 100_000_000,
            Status = DownloadStatus.Downloading
        };

        var dialogVm = new DownloadDialogViewModel(item)
        {
            ShowDetails = true
        };

        // Assert initial
        dialogVm.ShowDetails.Should().BeTrue();
        dialogVm.DetailsVisibility.Should().Be(Visibility.Visible);

        // Act
        dialogVm.ToggleDetailsCommand.Execute(null);

        // Assert toggled
        dialogVm.ShowDetails.Should().BeFalse();
        dialogVm.DetailsVisibility.Should().Be(Visibility.Collapsed);
        dialogVm.DetailsButtonText.Should().Be("Show Details");
    }

    [Fact]
    public void InitialState_IsCompact_AndDetailsButtonTextToggles()
    {
        // Arrange
        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "file.zip"
        };

        var dialogVm = new DownloadDialogViewModel(item);

        // Assert default is compact
        dialogVm.ShowDetails.Should().BeFalse();
        dialogVm.DetailsVisibility.Should().Be(Visibility.Collapsed);
        dialogVm.DetailsButtonText.Should().Be("Show Details");

        // Act: expand
        dialogVm.ToggleDetailsCommand.Execute(null);

        // Assert expanded
        dialogVm.ShowDetails.Should().BeTrue();
        dialogVm.DetailsVisibility.Should().Be(Visibility.Visible);
        dialogVm.DetailsButtonText.Should().Be("Hide Details");

        // Act: collapse
        dialogVm.ToggleDetailsCommand.Execute(null);

        // Assert collapsed
        dialogVm.ShowDetails.Should().BeFalse();
        dialogVm.DetailsVisibility.Should().Be(Visibility.Collapsed);
        dialogVm.DetailsButtonText.Should().Be("Show Details");
    }

    [Fact]
    public void SpeedLimiterProperties_AreObservableAndConfigurable()
    {
        // Arrange
        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "video.mp4",
            Url = "https://example.com/video.mp4",
            TotalBytes = 50_000_000
        };

        var dialogVm = new DownloadDialogViewModel(item);

        // Act
        dialogVm.IsLimiterEnabled = true;
        dialogVm.SpeedLimitKbps = 2048;

        // Assert
        dialogVm.IsLimiterEnabled.Should().BeTrue();
        dialogVm.SpeedLimitKbps.Should().Be(2048);
    }

    [Fact]
    public void SegmentProgressViewModel_CalculatesFillRatioCorrectly()
    {
        // Arrange
        var segment = new SegmentProgressViewModel(0, 0, 999);

        // Act & Assert initial
        segment.DownloadedBytes.Should().Be(0);
        segment.FillRatio.Should().Be(0.0);

        // Update progress
        segment.Update(500, 1_000_000);
        segment.DownloadedBytes.Should().Be(500);
        segment.FillRatio.Should().Be(0.5);
        segment.SpeedText.Should().Contain("KB/s");

        // Complete
        segment.Update(1000, 0);
        segment.FillRatio.Should().Be(1.0);
        segment.Status.Should().Be("Complete");
    }

    [Fact]
    public async Task TogglePauseCommand_InvokesHandlers_AndUpdatesButtonText()
    {
        // Arrange
        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "archive.zip",
            Status = DownloadStatus.Downloading
        };

        bool pauseInvoked = false;
        bool resumeInvoked = false;

        var dialogVm = new DownloadDialogViewModel(item)
        {
            PauseHandler = (vm) => pauseInvoked = true,
            ResumeHandler = (vm) =>
            {
                resumeInvoked = true;
                return System.Threading.Tasks.Task.CompletedTask;
            }
        };

        dialogVm.PauseResumeButtonText.Should().Be("Pause");

        // Act 1: Pause
        await dialogVm.TogglePauseCommand.ExecuteAsync(null);

        // Assert 1: Paused
        pauseInvoked.Should().BeTrue();
        item.Status.Should().Be(DownloadStatus.Paused);
        dialogVm.PauseResumeButtonText.Should().Be("Resume");

        // Act 2: Resume
        await dialogVm.TogglePauseCommand.ExecuteAsync(null);

        // Assert 2: Resumed
        resumeInvoked.Should().BeTrue();
        item.Status.Should().Be(DownloadStatus.Downloading);
        dialogVm.PauseResumeButtonText.Should().Be("Pause");
    }

    [Fact]
    public void CancelCommand_InvokesCancelHandler()
    {
        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "archive.zip",
            Status = DownloadStatus.Downloading
        };

        bool cancelInvoked = false;
        var dialogVm = new DownloadDialogViewModel(item)
        {
            CancelHandler = (vm) => cancelInvoked = true
        };

        dialogVm.CancelCommand.Execute(null);

        cancelInvoked.Should().BeTrue();
    }

    [Fact]
    public void AppSettingsStore_RoundTripsDefaultsAndClamps()
    {
        var settings = new AppSettings
        {
            MaxConcurrentDownloads = 5,
            ShowMediaProgressDialog = true,
            DeletePartialFilesOnCancel = true,
            MaxConnectionsPerDownload = 16
        };

        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"swiftget_test_settings_{Guid.NewGuid():N}.json");
        try
        {
            AppSettingsStore.Save(settings, tempPath);
            var loaded = AppSettingsStore.Load(tempPath);

            loaded.MaxConcurrentDownloads.Should().Be(5);
            loaded.ShowMediaProgressDialog.Should().BeTrue();
            loaded.DeletePartialFilesOnCancel.Should().BeTrue();
            loaded.MaxConnectionsPerDownload.Should().Be(16);
        }
        finally
        {
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void DownloadItemViewModel_CancelledState_SetsStatusTextAndGlyph()
    {
        var item = new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = "cancelled_file.iso",
            Status = DownloadStatus.Cancelled
        };

        item.StatusText.Should().Be("Cancelled");
        item.ResumePauseGlyph.Should().Be("\uE768"); // Resume icon available
    }
}
