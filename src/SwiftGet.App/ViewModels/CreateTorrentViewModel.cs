using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.App.ViewModels;

public sealed partial class CreateTorrentViewModel : ObservableObject
{
    private readonly ITorrentEngine _torrentEngine;

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private string _saveTorrentPath = string.Empty;

    [ObservableProperty]
    private string _trackersText = string.Join(Environment.NewLine, MonoTorrentEngine.DefaultPublicTrackers);

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    private bool _isPrivateTorrent;

    [ObservableProperty]
    private int _selectedPieceSizeIndex = 0;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ObservableCollection<string> PieceSizes { get; } =
    [
        "Auto (Recommended)",
        "32 KB",
        "64 KB",
        "128 KB",
        "256 KB",
        "512 KB",
        "1 MB",
        "2 MB",
        "4 MB",
        "8 MB",
        "16 MB"
    ];

    private static readonly char[] TrackerSeparators = ['\r', '\n'];

    public CreateTorrentViewModel(ITorrentEngine torrentEngine)
    {
        _torrentEngine = torrentEngine ?? throw new ArgumentNullException(nameof(torrentEngine));
        _saveTorrentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "CreatedTorrent.torrent");
    }

    public async Task<bool> CreateTorrentFileAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || (!File.Exists(SourcePath) && !Directory.Exists(SourcePath)))
        {
            StatusMessage = "Please select a valid existing file or folder to share.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SaveTorrentPath))
        {
            StatusMessage = "Please select an output path for the .torrent file.";
            return false;
        }

        IsCreating = true;
        StatusMessage = "Hashing files and generating piece payloads...";

        try
        {
            int pieceSize = SelectedPieceSizeIndex switch
            {
                1 => 32 * 1024,
                2 => 64 * 1024,
                3 => 128 * 1024,
                4 => 256 * 1024,
                5 => 512 * 1024,
                6 => 1024 * 1024,
                7 => 2 * 1024 * 1024,
                8 => 4 * 1024 * 1024,
                9 => 8 * 1024 * 1024,
                10 => 16 * 1024 * 1024,
                _ => 0
            };

            var trackers = TrackersText
                .Split(TrackerSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .ToArray();

            var torrentBytes = await _torrentEngine.CreateTorrentAsync(
                SourcePath,
                trackers.Length > 0 ? trackers : null,
                string.IsNullOrWhiteSpace(Comment) ? null : Comment,
                "SwiftGet 1.0",
                pieceSize,
                isPrivate: IsPrivateTorrent,
                ct: ct).ConfigureAwait(false);

            var dir = Path.GetDirectoryName(SaveTorrentPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(SaveTorrentPath, torrentBytes, ct).ConfigureAwait(false);
            StatusMessage = $"Torrent created successfully ({torrentBytes.Length} bytes).";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Creation failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsCreating = false;
        }
    }
}
