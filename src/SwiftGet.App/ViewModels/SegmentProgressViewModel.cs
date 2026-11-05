using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SwiftGet.App.ViewModels;

public sealed partial class SegmentProgressViewModel : ObservableObject
{
    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeDescription))]
    private long _startOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeDescription))]
    private long _endOffset;

    [ObservableProperty]
    private long _currentOffset;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private double _speedBytesPerSec;

    [ObservableProperty]
    private string _status = "Idle";

    [ObservableProperty]
    private double _leftRatio;

    [ObservableProperty]
    private double _widthRatio;

    [ObservableProperty]
    private double _fillRatio;

    public string RangeDescription => $"{StartOffset:N0} - {EndOffset:N0}";

    public string FormattedDownloaded => string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", DownloadedBytes / (1024.0 * 1024.0));

    public string SpeedText => FormattedSpeed;

    public string FormattedSpeed
    {
        get
        {
            if (SpeedBytesPerSec < 1024)
            {
                return $"{SpeedBytesPerSec:0} B/s";
            }

            if (SpeedBytesPerSec < 1024 * 1024)
            {
                return $"{SpeedBytesPerSec / 1024.0:0.0} KB/s";
            }

            return $"{SpeedBytesPerSec / (1024.0 * 1024.0):0.00} MB/s";
        }
    }

    public SegmentProgressViewModel()
    {
    }

    public SegmentProgressViewModel(int index, long startOffset, long endOffset)
    {
        _index = index;
        _startOffset = startOffset;
        _endOffset = endOffset;
        _currentOffset = startOffset;
    }

    public void Update(long downloaded, double speed)
    {
        DownloadedBytes = downloaded;
        CurrentOffset = StartOffset + downloaded;
        SpeedBytesPerSec = speed;
        long segmentTotal = EndOffset - StartOffset + 1;
        FillRatio = segmentTotal > 0 ? Math.Min(1.0, (double)downloaded / segmentTotal) : 0;
        Status = FillRatio >= 1.0 ? "Complete" : "Downloading";
        OnPropertyChanged(nameof(FormattedDownloaded));
        OnPropertyChanged(nameof(FormattedSpeed));
        OnPropertyChanged(nameof(SpeedText));
    }

    public void UpdateMetrics(long totalFileSize)
    {
        if (totalFileSize <= 0)
        {
            LeftRatio = 0;
            WidthRatio = 0;
            FillRatio = 0;
            return;
        }

        long segmentTotal = EndOffset - StartOffset + 1;
        LeftRatio = (double)StartOffset / totalFileSize;
        WidthRatio = (double)segmentTotal / totalFileSize;
        FillRatio = segmentTotal > 0 ? (double)DownloadedBytes / segmentTotal : 0;
        if (FillRatio > 1.0)
        {
            FillRatio = 1.0;
        }

        OnPropertyChanged(nameof(RangeDescription));
        OnPropertyChanged(nameof(FormattedDownloaded));
        OnPropertyChanged(nameof(FormattedSpeed));
    }
}
