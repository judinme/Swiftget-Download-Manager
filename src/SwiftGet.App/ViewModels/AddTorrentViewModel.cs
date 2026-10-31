using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.App.ViewModels;

public sealed partial class AddTorrentFileEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private int _fileIndex;

    [ObservableProperty]
    private string _relativePath = string.Empty;

    [ObservableProperty]
    private long _fileSizeBytes;

    [ObservableProperty]
    private bool _isSelected = true;

    public Action? SelectionChangedCallback { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChangedCallback?.Invoke();
    }

    [ObservableProperty]
    private TorrentFilePriority _priority = TorrentFilePriority.Normal;

    public string FormattedSize => FileSizeBytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", FileSizeBytes / 1024.0),
        < 1024 * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", FileSizeBytes / (1024.0 * 1024.0)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", FileSizeBytes / (1024.0 * 1024.0 * 1024.0))
    };
}

public sealed partial class AddTorrentViewModel : ObservableObject, IDisposable
{
    private readonly ITorrentEngine? _torrentEngine;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _torrentName = "Fetching metadata...";

    [ObservableProperty]
    private string _infoHash = string.Empty;

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = "Torrents";

    [ObservableProperty]
    private string _queueName = "Main";

    [ObservableProperty]
    private bool _startImmediately = true;

    [ObservableProperty]
    private bool _sequentialMode;

    [ObservableProperty]
    private bool _isFetchingMetadata;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _selectedBytes;

    [ObservableProperty]
    private int _pieceCount;

    [ObservableProperty]
    private long _pieceLength;

    [ObservableProperty]
    private string _freeDiskSpace = string.Empty;

    [ObservableProperty]
    private bool _hasDiskSpaceWarning;

    [ObservableProperty]
    private string _seedsText = "—";

    [ObservableProperty]
    private string _peersText = "—";

    private CancellationTokenSource? _probingCts;
    private string? _activeProbingJobId;

    public ObservableCollection<AddTorrentFileEntryViewModel> Files { get; } = [];

    public ObservableCollection<string> Categories { get; } =
    [
        "Torrents",
        "General",
        "Video",
        "Music",
        "Programs",
        "Compressed",
        "Documents"
    ];

    public ObservableCollection<string> Queues { get; } =
    [
        "Main",
        "Sync",
        "None"
    ];

    public string FormattedTotalSize => TotalBytes switch
    {
        <= 0 => "Unknown",
        < 1024 => $"{TotalBytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", TotalBytes / 1024.0),
        < 1024 * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", TotalBytes / (1024.0 * 1024.0)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", TotalBytes / (1024.0 * 1024.0 * 1024.0))
    };

    public string FormattedSelectedSize => SelectedBytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{SelectedBytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", SelectedBytes / 1024.0),
        < 1024 * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", SelectedBytes / (1024.0 * 1024.0)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", SelectedBytes / (1024.0 * 1024.0 * 1024.0))
    };

    public string SizeSummaryText => TotalBytes > 0
        ? (SelectedBytes == TotalBytes ? FormattedTotalSize : $"{FormattedSelectedSize} / {FormattedTotalSize}")
        : "Unknown";

    public void RecalculateSelectedSize()
    {
        SelectedBytes = Files.Where(f => f.IsSelected).Sum(f => f.FileSizeBytes);
        OnPropertyChanged(nameof(FormattedSelectedSize));
        OnPropertyChanged(nameof(SizeSummaryText));
        UpdateFreeSpace();
    }

    public AddTorrentViewModel(ITorrentEngine? torrentEngine = null)
    {
        _torrentEngine = torrentEngine;
        _saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        UpdateFreeSpace();
    }

    partial void OnSourceChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim().Trim('"');
        if (trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed)))
        {
            if (!string.Equals(trimmed, _activeSourceLoaded, StringComparison.OrdinalIgnoreCase))
            {
                _ = InitializeFromSourceAsync(trimmed);
            }
        }
    }

    private string _activeSourceLoaded = string.Empty;

    public async Task InitializeFromSourceAsync(string source, CancellationToken ct = default)
    {
        var cleaned = source.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(cleaned)) return;
        _activeSourceLoaded = cleaned;
        if (Source != cleaned)
        {
            Source = cleaned;
        }
        Files.Clear();

        if (cleaned.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(cleaned))
        {
            await LoadTorrentFileAsync(cleaned, ct).ConfigureAwait(false);
        }
        else if (cleaned.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            await LoadMagnetUriAsync(cleaned, ct).ConfigureAwait(false);
        }
        else
        {
            StatusMessage = "Unrecognized torrent source format. Please enter a valid magnet link or .torrent file path.";
        }

        UpdateFreeSpace();
    }

    private async Task LoadTorrentFileAsync(string filePath, CancellationToken ct)
    {
        CancelProbing();
        IsFetchingMetadata = true;
        StatusMessage = "Reading .torrent metadata...";
        SeedsText = "N/A (.torrent file)";
        PeersText = "N/A (.torrent file)";

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
            var torrent = MonoTorrent.Torrent.Load(bytes);

            TorrentName = torrent.Name;
            InfoHash = torrent.InfoHashes.V1OrV2.ToHex();
            TotalBytes = torrent.Size;
            PieceCount = torrent.PieceCount;
            PieceLength = torrent.PieceLength;
            OnPropertyChanged(nameof(FormattedTotalSize));

            for (int i = 0; i < torrent.Files.Count; i++)
            {
                var tf = torrent.Files[i];
                Files.Add(new AddTorrentFileEntryViewModel
                {
                    FileIndex = i,
                    RelativePath = tf.Path,
                    FileSizeBytes = tf.Length,
                    IsSelected = true,
                    SelectionChangedCallback = RecalculateSelectedSize,
                    Priority = TorrentFilePriority.Normal
                });
            }

            RecalculateSelectedSize();
            IsFetchingMetadata = false;
            StatusMessage = $"{Files.Count} files ready ({FormattedTotalSize})";
        }
        catch (Exception ex)
        {
            IsFetchingMetadata = false;
            StatusMessage = $"Failed to parse .torrent file: {ex.Message}";
        }
    }

    private async Task LoadMagnetUriAsync(string magnetUri, CancellationToken ct)
    {
        if (!MagnetUriParser.TryParse(magnetUri, out var parsed, out var error))
        {
            StatusMessage = error ?? "Invalid magnet URI";
            return;
        }

        CancelProbing();
        _probingCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _probingCts.Token);

        InfoHash = parsed!.InfoHash;
        TorrentName = parsed.DisplayName ?? $"Torrent_{parsed.InfoHash[..8]}";
        SeedsText = "0 connected";
        PeersText = "0 connected";

        if (_torrentEngine != null)
        {
            IsFetchingMetadata = true;
            StatusMessage = "Connecting to swarm & searching for peers (DHT / Trackers)...";

            var tempJobId = Guid.NewGuid().ToString("N");
            _activeProbingJobId = tempJobId;

            EventHandler<TorrentMetadataReceivedEventArgs> handler = (s, e) =>
            {
                if (e.JobId == tempJobId || string.Equals(e.InfoHash, InfoHash, StringComparison.OrdinalIgnoreCase))
                {
                    _ = App.CurrentWindow?.DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Name)) TorrentName = e.Name;
                        if (e.TotalBytes > 0) TotalBytes = e.TotalBytes;
                        OnPropertyChanged(nameof(FormattedTotalSize));

                        Files.Clear();
                        if (e.Files != null)
                        {
                            foreach (var mf in e.Files)
                            {
                                Files.Add(new AddTorrentFileEntryViewModel
                                {
                                    FileIndex = mf.FileIndex,
                                    RelativePath = mf.RelativePath,
                                    FileSizeBytes = mf.FileSizeBytes,
                                    IsSelected = true,
                                    SelectionChangedCallback = RecalculateSelectedSize,
                                    Priority = TorrentFilePriority.Normal
                                });
                            }
                            RecalculateSelectedSize();
                        }
                        IsFetchingMetadata = false;
                        StatusMessage = $"Metadata resolved ({Files.Count} files, {FormattedTotalSize})";
                    });
                }
            };

            _torrentEngine.MetadataReceived += handler;

            try
            {
                await _torrentEngine.AddMagnetAsync(magnetUri, new TorrentAddOptions
                {
                    DownloadId = tempJobId,
                    SaveDirectory = SaveDirectory,
                    StartImmediately = true
                }, linkedCts.Token).ConfigureAwait(false);

                // Polling loop for seeds, peers, and metadata while dialog is open
                _ = Task.Run(async () =>
                {
                    while (!linkedCts.Token.IsCancellationRequested && IsFetchingMetadata)
                    {
                        try
                        {
                            var stats = _torrentEngine.GetJobStats(tempJobId) ?? _torrentEngine.GetJobStats(InfoHash);
                            if (stats != null)
                            {
                                _ = App.CurrentWindow?.DispatcherQueue?.TryEnqueue(() =>
                                {
                                    SeedsText = $"{stats.ConnectedSeeds} connected ({stats.TotalSeeds} total)";
                                    PeersText = $"{stats.ConnectedPeers} connected ({stats.TotalPeers} total)";
                                    if (IsFetchingMetadata && !stats.HasMetadata)
                                    {
                                        StatusMessage = stats.ConnectedPeers > 0 || stats.ConnectedSeeds > 0
                                            ? $"Retrieving metadata from {stats.ConnectedPeers} peers ({stats.ConnectedSeeds} seeds)..."
                                            : "Searching DHT & trackers for peers...";
                                    }
                                });

                                if (stats.HasMetadata && (Files.Count == 0 || TotalBytes <= 0))
                                {
                                    var files = _torrentEngine.GetFiles(tempJobId);
                                    if (files.Count > 0)
                                    {
                                        _ = App.CurrentWindow?.DispatcherQueue?.TryEnqueue(() =>
                                        {
                                            if (!string.IsNullOrWhiteSpace(stats.Name)) TorrentName = stats.Name;
                                            TotalBytes = stats.TotalBytes;
                                            OnPropertyChanged(nameof(FormattedTotalSize));

                                            Files.Clear();
                                            foreach (var f in files)
                                            {
                                                Files.Add(new AddTorrentFileEntryViewModel
                                                {
                                                    FileIndex = f.FileIndex,
                                                    RelativePath = f.RelativePath,
                                                    FileSizeBytes = f.FileSizeBytes,
                                                    IsSelected = f.IsSelected,
                                                    SelectionChangedCallback = RecalculateSelectedSize,
                                                    Priority = f.Priority
                                                });
                                            }
                                            RecalculateSelectedSize();
                                            IsFetchingMetadata = false;
                                            StatusMessage = $"Metadata resolved ({Files.Count} files, {FormattedTotalSize})";
                                        });
                                        break;
                                    }
                                }
                            }
                            await Task.Delay(500, linkedCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        catch { }
                    }
                }, linkedCts.Token);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Note: Swarm metadata probe encountered: {ex.Message}";
                IsFetchingMetadata = false;
            }
        }
        else
        {
            StatusMessage = "Magnet link validated. Files will be populated when download starts.";
        }
    }

    public void CancelProbing(bool isConfirmed = false)
    {
        try
        {
            _probingCts?.Cancel();
            _probingCts?.Dispose();
            _probingCts = null;
        }
        catch { }

        if (!isConfirmed && !string.IsNullOrWhiteSpace(_activeProbingJobId) && _torrentEngine != null)
        {
            var jobId = _activeProbingJobId;
            _activeProbingJobId = null;
            _ = _torrentEngine.RemoveAsync(jobId, deleteFiles: false);
        }
        else
        {
            _activeProbingJobId = null;
        }
    }

    public void Dispose()
    {
        CancelProbing();
    }

    [RelayCommand]
    private void SelectAllFiles()
    {
        foreach (var file in Files)
        {
            file.IsSelected = true;
        }
        RecalculateSelectedSize();
    }

    [RelayCommand]
    private void DeselectAllFiles()
    {
        foreach (var file in Files)
        {
            file.IsSelected = false;
        }
        RecalculateSelectedSize();
    }

    public void UpdateFreeSpace()
    {
        try
        {
            string root = Path.GetPathRoot(SaveDirectory) ?? "C:\\";
            DriveInfo drive = new(root);
            if (drive.IsReady)
            {
                FreeDiskSpace = string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB free", drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0));
                long bytesToCheck = SelectedBytes > 0 ? SelectedBytes : TotalBytes;
                HasDiskSpaceWarning = bytesToCheck > 0 && bytesToCheck > drive.AvailableFreeSpace;
            }
        }
        catch
        {
            FreeDiskSpace = "Unknown";
            HasDiskSpaceWarning = false;
        }
    }
}
