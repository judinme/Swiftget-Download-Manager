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
}
