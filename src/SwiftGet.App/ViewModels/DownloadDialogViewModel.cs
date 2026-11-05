using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Coordinator;
using SwiftGet.Engine.Throttling;

namespace SwiftGet.App.ViewModels;

public sealed partial class DownloadDialogViewModel : ObservableObject
{
    private readonly TokenBucketLimiter? _limiter;

    [ObservableProperty]
    private DownloadItemViewModel _item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsVisibility))]
    [NotifyPropertyChangedFor(nameof(DetailsButtonText))]
    private bool _showDetails;

    public Microsoft.UI.Xaml.Visibility DetailsVisibility =>
        ShowDetails ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string DetailsButtonText => ShowDetails ? "Hide Details" : "Show Details";

    [ObservableProperty]
    private int _selectedTabIndex;

    // Speed limiter tab
    [ObservableProperty]
    private bool _isLimiterEnabled;

    [ObservableProperty]
    private double _speedLimitKbps = 1024; // 1 MB/s default

    // Completion options tab
    [ObservableProperty]
    private bool _closeOnCompletion = true;

    [ObservableProperty]
    private bool _exitAppOnCompletion;

    [ObservableProperty]
    private bool _shutdownOnCompletion;

    [ObservableProperty]
    private bool _playSoundOnCompletion = true;

    public ObservableCollection<SegmentProgressViewModel> ConnectionRows => Item.Segments;

    public Action<DownloadItemViewModel>? PauseHandler { get; set; }
    public Func<DownloadItemViewModel, Task>? ResumeHandler { get; set; }
    public Action<DownloadItemViewModel>? CancelHandler { get; set; }

    public string PauseResumeButtonText =>
        Item.Status is DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.Failed ? "Resume" : "Pause";

    public DownloadDialogViewModel(DownloadItemViewModel item, TokenBucketLimiter? limiter = null)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _limiter = limiter;

        _item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DownloadItemViewModel.Status))
            {
                OnPropertyChanged(nameof(PauseResumeButtonText));
            }
        };
    }

    [RelayCommand]
    private void ToggleDetails()
    {
        ShowDetails = !ShowDetails;
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (Item.Status is DownloadStatus.Downloading or DownloadStatus.Allocating or DownloadStatus.Probing or DownloadStatus.FetchingMetadata or DownloadStatus.Checking or DownloadStatus.Seeding)
        {
            PauseHandler?.Invoke(Item);
            Item.Status = DownloadStatus.Paused;
            Item.SpeedBytesPerSec = 0;
            Item.UpdateProgress(Item.DownloadedBytes, Item.TotalBytes, 0);
        }
        else if (Item.Status is DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.Failed)
        {
            Item.Status = DownloadStatus.Downloading;
            if (ResumeHandler != null)
            {
                await ResumeHandler.Invoke(Item);
            }
        }
        OnPropertyChanged(nameof(PauseResumeButtonText));
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelHandler?.Invoke(Item);
    }

    partial void OnIsLimiterEnabledChanged(bool value)
    {
        ApplySpeedLimit();
    }

    partial void OnSpeedLimitKbpsChanged(double value)
    {
        ApplySpeedLimit();
    }

    private void ApplySpeedLimit()
    {
        if (_limiter == null)
        {
            return;
        }

        if (IsLimiterEnabled && SpeedLimitKbps > 0)
        {
            long bytesPerSec = (long)(SpeedLimitKbps * 1024);
            _limiter.BytesPerSecond = bytesPerSec;
        }
        else
        {
            _limiter.BytesPerSecond = 0; // Unlimited
        }
    }
}
