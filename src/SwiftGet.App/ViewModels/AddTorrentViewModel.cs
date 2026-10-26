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

public sealed partial class AddTorrentViewModel : ObservableObject
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
    private int _pieceCount;

    [ObservableProperty]
    private long _pieceLength;

    [ObservableProperty]
    private string _freeDiskSpace = string.Empty;

    [ObservableProperty]
    private bool _hasDiskSpaceWarning;

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

    public AddTorrentViewModel(ITorrentEngine? torrentEngine = null)
    {
        _torrentEngine = torrentEngine;
        _saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        UpdateFreeSpace();
    }

    public async Task InitializeFromSourceAsync(string source, CancellationToken ct = default)
    {
        Source = source.Trim().Trim('"');
        Files.Clear();

        if (Source.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(Source))
        {
            await LoadTorrentFileAsync(Source, ct).ConfigureAwait(false);
        }
        else if (Source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            await LoadMagnetUriAsync(Source, ct).ConfigureAwait(false);
        }
        else
        {
            StatusMessage = "Unrecognized torrent source format. Please enter a valid magnet link or .torrent file path.";
        }

        UpdateFreeSpace();
    }

    private async Task LoadTorrentFileAsync(string filePath, CancellationToken ct)
    {
        IsFetchingMetadata = true;
        StatusMessage = "Reading .torrent metadata...";

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
            var torrent = MonoTorrent.Torrent.Load(bytes);

            TorrentName = torrent.Name;
            InfoHash = torrent.InfoHashes.V1OrV2.ToHex();
            TotalBytes = torrent.Size;
            PieceCount = torrent.PieceCount;
            PieceLength = torrent.PieceLength;

            for (int i = 0; i < torrent.Files.Count; i++)
            {
                var tf = torrent.Files[i];
                Files.Add(new AddTorrentFileEntryViewModel
                {
                    FileIndex = i,
                    RelativePath = tf.Path,
                    FileSizeBytes = tf.Length,
                    IsSelected = true,
                    Priority = TorrentFilePriority.Normal
                });
            }

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

        InfoHash = parsed!.InfoHash;
        TorrentName = parsed.DisplayName ?? $"Torrent_{parsed.InfoHash[..8]}";

        if (_torrentEngine != null)
        {
            IsFetchingMetadata = true;
            StatusMessage = "Connecting to swarm to retrieve metadata (BEP 9)...";

            var tcs = new TaskCompletionSource<TorrentMetadataReceivedEventArgs>();
            var tempJobId = Guid.NewGuid().ToString("N");

            EventHandler<TorrentMetadataReceivedEventArgs> handler = (s, e) =>
            {
                if (e.JobId == tempJobId)
                {
                    tcs.TrySetResult(e);
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
                }, ct).ConfigureAwait(false);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8), ct)).ConfigureAwait(false);
                if (completedTask == tcs.Task)
                {
                    var meta = await tcs.Task.ConfigureAwait(false);
                    TorrentName = meta.Name;
                    TotalBytes = meta.TotalBytes;

                    for (int i = 0; i < meta.Files.Count; i++)
                    {
                        var mf = meta.Files[i];
                        Files.Add(new AddTorrentFileEntryViewModel
                        {
                            FileIndex = i,
                            RelativePath = mf.RelativePath,
                            FileSizeBytes = mf.FileSizeBytes,
                            IsSelected = true,
                            Priority = TorrentFilePriority.Normal
                        });
                    }
                    StatusMessage = $"Metadata resolved ({Files.Count} files, {FormattedTotalSize})";
                }
                else
                {
                    StatusMessage = "Metadata fetch in background. Download will start immediately upon confirmation.";
                }

                await _torrentEngine.RemoveAsync(tempJobId, deleteFiles: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Note: Swarm metadata probe encountered: {ex.Message}";
            }
            finally
            {
                _torrentEngine.MetadataReceived -= handler;
                IsFetchingMetadata = false;
            }
        }
        else
        {
            StatusMessage = "Magnet link validated. Files will be populated when download starts.";
        }
    }

    [RelayCommand]
    private void SelectAllFiles()
    {
        foreach (var file in Files)
        {
            file.IsSelected = true;
        }
    }

    [RelayCommand]
    private void DeselectAllFiles()
    {
        foreach (var file in Files)
        {
            file.IsSelected = false;
        }
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
                HasDiskSpaceWarning = TotalBytes > 0 && TotalBytes > drive.AvailableFreeSpace;
            }
        }
        catch
        {
            FreeDiskSpace = "Unknown";
            HasDiskSpaceWarning = false;
        }
    }
}
