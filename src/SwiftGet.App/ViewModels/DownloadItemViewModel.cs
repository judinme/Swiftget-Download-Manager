using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SwiftGet.Core.Models;

namespace SwiftGet.App.ViewModels;

public sealed partial class DownloadItemViewModel : ObservableObject
{
    public Guid Id { get; set; }

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _savePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private long _totalBytes;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private DownloadStatus _status = DownloadStatus.Queued;

    public Microsoft.UI.Xaml.Media.Brush? StatusBrush => SwiftGet.App.Converters.StatusToBrushConverter.GetBrush(Status);

    [ObservableProperty]
    private double _speedBytesPerSec;

    [ObservableProperty]
    private TimeSpan _remainingTime = TimeSpan.Zero;

    [ObservableProperty]
    private DateTime _lastTryDate = DateTime.Now;

    [ObservableProperty]
    private string _queueName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _resumeSupported = true;

    [ObservableProperty]
    private bool _isGrabberProject;

    [ObservableProperty]
    private string _category = "all";

    [ObservableProperty]
    private bool _isSelected;

    // BitTorrent properties
    [ObservableProperty]
    private JobKind _jobKind = JobKind.Http;

    [ObservableProperty]
    private string _infoHash = string.Empty;

    [ObservableProperty]
    private double _uploadSpeedBytesPerSec;

    [ObservableProperty]
    private long _uploadedBytes;

    [ObservableProperty]
    private int _seedsConnected;

    [ObservableProperty]
    private int _totalSeeds;

    [ObservableProperty]
    private int _peersConnected;

    [ObservableProperty]
    private int _totalPeers;

    [ObservableProperty]
    private bool _isSequential;

    public double ShareRatio => DownloadedBytes > 0
        ? (double)UploadedBytes / DownloadedBytes
        : (UploadedBytes > 0 ? 100.0 : 0.0);

    public ObservableCollection<SegmentProgressViewModel> Segments { get; } = [];

    public string QueueIndicator => string.IsNullOrEmpty(QueueName) ? "-" : QueueName[..Math.Min(1, QueueName.Length)].ToUpperInvariant();

    public string IconGlyph => JobKind switch
    {
        JobKind.Torrent => "\uE896",
        JobKind.Media => "\uE714",
        _ => (Category ?? "all").ToLowerInvariant() switch
        {
            "video" => "\uE714",
            "programs" => "\uE756",
            "compressed" => "\uF012",
            "documents" => "\uE8A5",
            "music" => "\uE8D6",
            "torrents" => "\uE896",
            _ => "\uE8B7"
        }
    };

    public Microsoft.UI.Xaml.Media.Brush AccentColor => JobKind switch
    {
        JobKind.Torrent => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248)),
        JobKind.Media => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 167, 139, 250)),
        _ => (Category ?? "all").ToLowerInvariant() switch
        {
            "video" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 167, 139, 250)),
            "programs" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 165, 250)),
            "compressed" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 191, 36)),
            "documents" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 129, 140, 248)),
            "music" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 211, 153)),
            _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248))
        }
    };

    public Microsoft.UI.Xaml.Media.Brush StatusColor => StatusBrush ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175));

    public double Progress => ProgressPercentage;
    public string SpeedText => FormattedSpeed;
    public string EtaText => FormattedTimeLeft;
    public string SizeText => FormattedSize;
    public string AddedDate => FormattedLastTryDate;
    public string Folder => SavePath;

    public bool IsIndeterminate => Status switch
    {
        DownloadStatus.FetchingMetadata => true,
        DownloadStatus.Checking => TotalBytes <= 0,
        DownloadStatus.Downloading => TotalBytes <= 0,
        _ => false
    };

    public string FormattedSize => TotalBytes switch
    {
        <= 0 => "Unknown",
        < 1024 => $"{TotalBytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", TotalBytes / 1024.0),
        < 1024 * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", TotalBytes / (1024.0 * 1024.0)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", TotalBytes / (1024.0 * 1024.0 * 1024.0))
    };

    public string FormattedDownloaded => DownloadedBytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{DownloadedBytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", DownloadedBytes / 1024.0),
        < 1024 * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", DownloadedBytes / (1024.0 * 1024.0)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", DownloadedBytes / (1024.0 * 1024.0 * 1024.0))
    };

    public string FormattedSpeed
    {
        get
        {
            if (JobKind == JobKind.Torrent)
            {
                if (Status == DownloadStatus.Downloading)
                {
                    return $"↓ {FormatSpeed(SpeedBytesPerSec)}  ↑ {FormatSpeed(UploadSpeedBytesPerSec)}";
                }
                if (Status == DownloadStatus.Seeding)
                {
                    return $"↑ {FormatSpeed(UploadSpeedBytesPerSec)}";
                }
                return "-";
            }

            if (Status != DownloadStatus.Downloading || SpeedBytesPerSec <= 0)
            {
                return "-";
            }

            return FormatSpeed(SpeedBytesPerSec);
        }
    }

    private static string FormatSpeed(double speed)
    {
        if (speed <= 0) return "0 B/s";
        if (speed < 1024) return $"{speed:0} B/s";
        if (speed < 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB/s", speed / 1024.0);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB/s", speed / (1024.0 * 1024.0));
    }

    public string FormattedTimeLeft
    {
        get
        {
            if (Status == DownloadStatus.Completed)
            {
                return "Complete";
            }

            if (Status == DownloadStatus.Seeding)
            {
                return "Seeding";
            }

            if (Status != DownloadStatus.Downloading || RemainingTime <= TimeSpan.Zero)
            {
                return "--:--:--";
            }

            if (RemainingTime.TotalHours >= 24)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}d {1:00}:{2:00}", (int)RemainingTime.TotalDays, RemainingTime.Hours, RemainingTime.Minutes);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)RemainingTime.TotalHours, RemainingTime.Minutes, RemainingTime.Seconds);
        }
    }

    public string StatusText => Status switch
    {
        DownloadStatus.FetchingMetadata => "Fetching metadata...",
        DownloadStatus.Checking => $"Checking ({ProgressPercentage:0.0}%)",
        DownloadStatus.Downloading => $"Downloading ({ProgressPercentage:0.0}%)",
        DownloadStatus.Seeding => $"Seeding (Ratio: {ShareRatio:0.00})",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Complete",
        DownloadStatus.Failed => "Error",
        DownloadStatus.Queued => "Queued",
        _ => Status.ToString()
    };

    public string FormattedLastTryDate => LastTryDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public void UpdateProgress(long downloaded, long total, double speed)
    {
        DownloadedBytes = downloaded;
        TotalBytes = total;
        SpeedBytesPerSec = speed;
        ProgressPercentage = total > 0 ? Math.Min(100.0, (double)downloaded / total * 100.0) : 0;

        if (speed > 0 && total > downloaded)
        {
            double remainingSeconds = (total - downloaded) / speed;
            RemainingTime = TimeSpan.FromSeconds(Math.Min(remainingSeconds, 864000.0)); // Cap at 10 days
        }
        else
        {
            RemainingTime = TimeSpan.Zero;
        }

        OnPropertyChanged(nameof(FormattedDownloaded));
        OnPropertyChanged(nameof(FormattedSize));
        OnPropertyChanged(nameof(FormattedSpeed));
        OnPropertyChanged(nameof(FormattedTimeLeft));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(ShareRatio));
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    public void UpdateTorrentProgress(
        long downloaded,
        long total,
        double downSpeed,
        double upSpeed,
        long uploaded,
        int seeds,
        int peers,
        DownloadStatus status)
    {
        DownloadedBytes = downloaded;
        TotalBytes = total;
        SpeedBytesPerSec = downSpeed;
        UploadSpeedBytesPerSec = upSpeed;
        UploadedBytes = uploaded;
        SeedsConnected = seeds;
        PeersConnected = peers;
        Status = status;
        ProgressPercentage = total > 0 ? Math.Min(100.0, (double)downloaded / total * 100.0) : 0;

        if (downSpeed > 0 && total > downloaded)
        {
            double remainingSeconds = (total - downloaded) / downSpeed;
            RemainingTime = TimeSpan.FromSeconds(Math.Min(remainingSeconds, 864000.0));
        }
        else
        {
            RemainingTime = TimeSpan.Zero;
        }

        OnPropertyChanged(nameof(FormattedDownloaded));
        OnPropertyChanged(nameof(FormattedSize));
        OnPropertyChanged(nameof(FormattedSpeed));
        OnPropertyChanged(nameof(FormattedTimeLeft));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(ShareRatio));
    }
}
