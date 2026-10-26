using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Torrent;
using Windows.ApplicationModel.DataTransfer;

namespace SwiftGet.App.ViewModels;

public sealed partial class TorrentDetailsViewModel : ObservableObject, IDisposable
{
    private readonly ITorrentEngine _torrentEngine;
    private readonly ISwiftGetDatabase? _db;
    private CancellationTokenSource? _pollingCts;
    private bool _isDisposed;

    public DownloadItemViewModel Item { get; }
    public string JobId { get; }

    [ObservableProperty]
    private string _torrentName = string.Empty;

    [ObservableProperty]
    private string _infoHash = string.Empty;

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private string _formattedSize = "0 B";

    [ObservableProperty]
    private string _formattedDownloaded = "0 B";

    [ObservableProperty]
    private string _formattedUploaded = "0 B";

    [ObservableProperty]
    private string _statusText = "Queued";

    [ObservableProperty]
    private Microsoft.UI.Xaml.Media.Brush? _statusBrush;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _formattedSpeed = "-";

    [ObservableProperty]
    private string _formattedTimeLeft = "--:--:--";

    [ObservableProperty]
    private string _shareRatioText = "0.00";

    [ObservableProperty]
    private string _swarmSeedsText = "0 connected (0 total)";

    [ObservableProperty]
    private string _swarmPeersText = "0 connected (0 total)";

    [ObservableProperty]
    private string _pieceStatsText = "-";

    [ObservableProperty]
    private bool _sequentialMode;

    [ObservableProperty]
    private string _pieceMapSummary = "0 / 0 pieces (0%)";

    [ObservableProperty]
    private double _pieceCompletionPercentage;

    [ObservableProperty]
    private double _downloadLimitKbps;

    [ObservableProperty]
    private double _uploadLimitKbps;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public ObservableCollection<TorrentFileItemViewModel> Files { get; } = [];
    public ObservableCollection<TorrentPeerDisplayItem> Peers { get; } = [];
    public ObservableCollection<TorrentTrackerInfo> Trackers { get; } = [];
    public ObservableCollection<PieceBlockViewModel> PieceBlocks { get; } = [];

    public TorrentDetailsViewModel(
        DownloadItemViewModel item,
        ITorrentEngine torrentEngine,
        ISwiftGetDatabase? db = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _torrentEngine = torrentEngine ?? throw new ArgumentNullException(nameof(torrentEngine));
        _db = db;

        JobId = item.Id.ToString("N");
        TorrentName = item.FileName;
        InfoHash = item.InfoHash;
        SaveDirectory = item.SavePath;
        SequentialMode = item.IsSequential;
        StatusText = item.StatusText;
        StatusBrush = item.StatusBrush;
    }

    public async Task InitializeAsync()
    {
        await RefreshAllStatsAsync().ConfigureAwait(false);
        StartPolling();
    }

    public void StartPolling()
    {
        if (_pollingCts != null) return;

        _pollingCts = new CancellationTokenSource();
        var token = _pollingCts.Token;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (token.IsCancellationRequested) break;
                    await RefreshAllStatsAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal exit on StopPolling
            }
        }, token);
    }

    public void StopPolling()
    {
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
    }

    public async Task RefreshAllStatsAsync()
    {
        var stats = _torrentEngine.GetJobStats(JobId);
        if (stats != null)
        {
            TorrentName = stats.Name;
            InfoHash = stats.InfoHash;
            ProgressPercentage = stats.ProgressPercentage;
            StatusText = stats.State switch
            {
                DownloadStatus.FetchingMetadata => "Fetching metadata...",
                DownloadStatus.Checking => $"Checking ({stats.ProgressPercentage:0.0}%)",
                DownloadStatus.Downloading => $"Downloading ({stats.ProgressPercentage:0.0}%)",
                DownloadStatus.Seeding => $"Seeding (Ratio: {stats.CurrentRatio:0.00})",
                DownloadStatus.Paused => "Paused",
                DownloadStatus.Completed => "Complete",
                _ => stats.State.ToString()
            };
            StatusBrush = SwiftGet.App.Converters.StatusToBrushConverter.GetBrush(stats.State);

            FormattedSize = FormatBytes(stats.TotalBytes);
            FormattedDownloaded = FormatBytes(stats.DownloadedBytes);
            FormattedUploaded = FormatBytes(stats.UploadedBytes);
            ShareRatioText = string.Format(CultureInfo.InvariantCulture, "{0:0.00}", stats.CurrentRatio);

            FormattedSpeed = stats.State == DownloadStatus.Seeding
                ? $"↑ {FormatSpeed(stats.UploadSpeedBytesPerSec)}"
                : $"↓ {FormatSpeed(stats.DownloadSpeedBytesPerSec)}  ↑ {FormatSpeed(stats.UploadSpeedBytesPerSec)}";

            if (stats.State == DownloadStatus.Completed)
            {
                FormattedTimeLeft = "Complete";
            }
            else if (stats.State == DownloadStatus.Seeding)
            {
                FormattedTimeLeft = "Seeding";
            }
            else if (stats.DownloadSpeedBytesPerSec > 0 && stats.TotalBytes > stats.DownloadedBytes)
            {
                var sec = (stats.TotalBytes - stats.DownloadedBytes) / stats.DownloadSpeedBytesPerSec;
                var ts = TimeSpan.FromSeconds(Math.Min(sec, 864000.0));
                FormattedTimeLeft = ts.TotalHours >= 24
                    ? string.Format(CultureInfo.InvariantCulture, "{0}d {1:00}:{2:00}", (int)ts.TotalDays, ts.Hours, ts.Minutes)
                    : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            }
            else
            {
                FormattedTimeLeft = "--:--:--";
            }

            SwarmSeedsText = $"{stats.ConnectedSeeds} connected ({stats.TotalSeeds} total)";
            SwarmPeersText = $"{stats.ConnectedPeers} connected ({stats.TotalPeers} total)";

            if (stats.PieceCount > 0)
            {
                PieceStatsText = $"{stats.PieceCount} pieces × {FormatBytes(stats.PieceLength)}";
            }

            // Sync with parent item
            Item.UpdateTorrentProgress(
                stats.DownloadedBytes,
                stats.TotalBytes,
                stats.DownloadSpeedBytesPerSec,
                stats.UploadSpeedBytesPerSec,
                stats.UploadedBytes,
                stats.ConnectedSeeds,
                stats.ConnectedPeers,
                stats.State);
        }

        // Files
        var files = _torrentEngine.GetFiles(JobId);
        if (files != null && files.Count > 0)
        {
            if (Files.Count != files.Count)
            {
                Files.Clear();
                foreach (var f in files)
                {
                    Files.Add(new TorrentFileItemViewModel(this, f));
                }
            }
            else
            {
                for (int i = 0; i < files.Count; i++)
                {
                    Files[i].UpdateFrom(files[i]);
                }
            }
        }

        // Peers
        var peers = _torrentEngine.GetPeers(JobId);
        if (peers != null)
        {
            Peers.Clear();
            foreach (var p in peers)
            {
                Peers.Add(new TorrentPeerDisplayItem(p));
            }
        }

        // Trackers
        var trackers = _torrentEngine.GetTrackers(JobId);
        if (trackers != null)
        {
            Trackers.Clear();
            foreach (var t in trackers)
            {
                Trackers.Add(t);
            }
        }

        // Piece Map Bitfield
        UpdatePieceMap();

        await Task.CompletedTask;
    }

    private void UpdatePieceMap()
    {
        var bitField = _torrentEngine.GetPieceBitField(JobId);
        if (bitField.IsEmpty) return;

        var bytes = bitField.Span;
        int totalBits = bytes.Length * 8;
        int completed = 0;

        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) != 0)
                {
                    completed++;
                }
            }
        }

        var stats = _torrentEngine.GetJobStats(JobId);
        int realTotalPieces = stats != null && stats.PieceCount > 0 ? stats.PieceCount : totalBits;
        completed = Math.Min(completed, realTotalPieces);

        double pct = realTotalPieces > 0 ? (double)completed / realTotalPieces * 100.0 : 0.0;
        PieceCompletionPercentage = pct;
        PieceMapSummary = $"{completed:N0} / {realTotalPieces:N0} pieces verified ({pct:0.0}%)";

        // Generate 60 visual preview blocks
        const int displayBlocksCount = 60;
        if (PieceBlocks.Count != displayBlocksCount)
        {
            PieceBlocks.Clear();
            for (int i = 0; i < displayBlocksCount; i++)
            {
                PieceBlocks.Add(new PieceBlockViewModel { Index = i, IsVerified = false });
            }
        }

        if (realTotalPieces > 0)
        {
            double piecesPerBlock = (double)realTotalPieces / displayBlocksCount;
            for (int i = 0; i < displayBlocksCount; i++)
            {
                int startPiece = (int)(i * piecesPerBlock);
                int endPiece = Math.Min(realTotalPieces - 1, (int)((i + 1) * piecesPerBlock));
                bool anyVerified = false;

                for (int p = startPiece; p <= endPiece; p++)
                {
                    int byteIdx = p / 8;
                    int bitIdx = 7 - (p % 8);
                    if (byteIdx < bytes.Length && (bytes[byteIdx] & (1 << bitIdx)) != 0)
                    {
                        anyVerified = true;
                        break;
                    }
                }

                PieceBlocks[i].IsVerified = anyVerified;
            }
        }
    }

    public async Task SetFilePriorityAsync(int fileIndex, TorrentFilePriority priority)
    {
        try
        {
            await _torrentEngine.SetFilePriorityAsync(JobId, fileIndex, priority).ConfigureAwait(false);
            StatusMessage = $"Updated file #{fileIndex} priority to {priority}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to set file priority: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ForceRecheckAsync()
    {
        try
        {
            await _torrentEngine.ForceRecheckAsync(JobId).ConfigureAwait(false);
            StatusMessage = "Hash check initiated across torrent piece payload";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Recheck failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ToggleSequential()
    {
        SequentialMode = !SequentialMode;
        Item.IsSequential = SequentialMode;
        StatusMessage = $"Sequential downloading is now {(SequentialMode ? "enabled" : "disabled")}";
    }

    [RelayCommand]
    public void CopyMagnetUri()
    {
        string uri = !string.IsNullOrWhiteSpace(Item.Url) && Item.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
            ? Item.Url
            : $"magnet:?xt=urn:btih:{InfoHash}&dn={Uri.EscapeDataString(TorrentName)}";

        DataPackage package = new();
        package.SetText(uri);
        Clipboard.SetContent(package);
        StatusMessage = "Magnet URI copied to clipboard";
    }

    [RelayCommand]
    public async Task ApplySpeedLimitsAsync()
    {
        try
        {
            long downBytes = (long)Math.Max(0, DownloadLimitKbps * 1024.0);
            long upBytes = (long)Math.Max(0, UploadLimitKbps * 1024.0);
            await _torrentEngine.SetTorrentRateLimitsAsync(JobId, downBytes, upBytes).ConfigureAwait(false);
            StatusMessage = $"Applied speed limits: Down={DownloadLimitKbps} KB/s, Up={UploadLimitKbps} KB/s";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to apply limits: {ex.Message}";
        }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", bytes / 1024.0),
        < 1024 * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", bytes / (1024.0 * 1024.0)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", bytes / (1024.0 * 1024.0 * 1024.0))
    };

    public static string FormatSpeed(double speed)
    {
        if (speed <= 0) return "0 B/s";
        if (speed < 1024) return $"{speed:0} B/s";
        if (speed < 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB/s", speed / 1024.0);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB/s", speed / (1024.0 * 1024.0));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        StopPolling();
        GC.SuppressFinalize(this);
    }
}

public sealed partial class TorrentFileItemViewModel : ObservableObject
{
    private readonly TorrentDetailsViewModel _parent;

    public int FileIndex { get; }
    public string RelativePath { get; }
    public long FileSizeBytes { get; private set; }
    public string FormattedSize => TorrentDetailsViewModel.FormatBytes(FileSizeBytes);

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private int _selectedPriorityIndex;

    public string FormattedDownloaded => TorrentDetailsViewModel.FormatBytes(DownloadedBytes);

    public IReadOnlyList<string> PriorityOptions => ["Normal", "High", "Low", "DoNotDownload"];

    public static int PriorityToOptionIndex(TorrentFilePriority priority) => priority switch
    {
        TorrentFilePriority.High or TorrentFilePriority.Highest => 1,
        TorrentFilePriority.Low or TorrentFilePriority.Lowest => 2,
        TorrentFilePriority.DoNotDownload => 3,
        _ => 0 // Normal
    };

    public static TorrentFilePriority OptionIndexToPriority(int index) => index switch
    {
        1 => TorrentFilePriority.High,
        2 => TorrentFilePriority.Low,
        3 => TorrentFilePriority.DoNotDownload,
        _ => TorrentFilePriority.Normal
    };

    public TorrentFileItemViewModel(TorrentDetailsViewModel parent, TorrentFileInfo info)
    {
        _parent = parent;
        FileIndex = info.FileIndex;
        RelativePath = info.RelativePath;
        FileSizeBytes = info.FileSizeBytes;
        DownloadedBytes = info.DownloadedBytes;
        ProgressPercentage = info.ProgressPercentage;
        _selectedPriorityIndex = PriorityToOptionIndex(info.Priority);
    }

    public void UpdateFrom(TorrentFileInfo info)
    {
        DownloadedBytes = info.DownloadedBytes;
        ProgressPercentage = info.ProgressPercentage;
        OnPropertyChanged(nameof(FormattedDownloaded));
    }

    partial void OnSelectedPriorityIndexChanged(int value)
    {
        var priority = OptionIndexToPriority(value);
        _ = _parent.SetFilePriorityAsync(FileIndex, priority);
    }
}

public sealed class TorrentPeerDisplayItem
{
    public string Endpoint { get; }
    public string Client { get; }
    public string DownloadSpeedText { get; }
    public string UploadSpeedText { get; }
    public string Flags { get; }
    public string EncryptionText { get; }
    public string ProtocolText { get; }

    public TorrentPeerDisplayItem(TorrentPeerInfo peer)
    {
        Endpoint = $"{peer.IpAddress}:{peer.Port}";
        Client = string.IsNullOrWhiteSpace(peer.Client) ? "Unknown Peer" : peer.Client;
        DownloadSpeedText = $"↓ {TorrentDetailsViewModel.FormatSpeed(peer.DownloadSpeedBytesPerSec)}";
        UploadSpeedText = $"↑ {TorrentDetailsViewModel.FormatSpeed(peer.UploadSpeedBytesPerSec)}";
        Flags = string.IsNullOrWhiteSpace(peer.Flags) ? "-" : peer.Flags;
        EncryptionText = peer.IsEncrypted ? "Encrypted" : "Plain";
        ProtocolText = peer.IsUtp ? "uTP" : "TCP";
    }
}

public sealed partial class PieceBlockViewModel : ObservableObject
{
    public int Index { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockBrush))]
    private bool _isVerified;

    private static Microsoft.UI.Xaml.Media.SolidColorBrush? _verifiedBrush;
    private static Microsoft.UI.Xaml.Media.SolidColorBrush? _pendingBrush;

    public Microsoft.UI.Xaml.Media.SolidColorBrush? BlockBrush
    {
        get
        {
            try
            {
                return IsVerified
                    ? _verifiedBrush ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 16, 124, 65))
                    : _pendingBrush ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 200, 200, 200));
            }
            catch
            {
                return null;
            }
        }
    }
}
