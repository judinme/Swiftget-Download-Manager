using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;
using SwiftGet.Core.IO;
using SwiftGet.Core.Models;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Services;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.App.ViewModels;

public sealed partial class TrackerItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _announceUrl = string.Empty;

    [ObservableProperty]
    private string _status = "Working";

    [ObservableProperty]
    private bool _isError;

    public string StatusColor => IsError ? "#F87171" : (Status.Equals("Working", StringComparison.OrdinalIgnoreCase) ? "#2CCB70" : "#D4D4D4");
}

public sealed partial class ConnectionItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private int _connectionCount = 1;

    [ObservableProperty]
    private string _direction = "In/Out";

    [ObservableProperty]
    private string _client = string.Empty;
}

public sealed partial class DownloadDetailsViewModel : ObservableObject
{
    private readonly IDownloadDetailsSnapshotService _snapshotService;
    private readonly ITorrentEngine? _torrentEngine;
    private string? _currentDownloadId;

    [ObservableProperty]
    private bool _isOpen = false;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Visibility _panelVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnIsOpenChanged(bool value)
    {
        PanelVisibility = value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    [ObservableProperty]
    private double _panelHeight = 185;

    [ObservableProperty]
    private string _activeTab = "GENERAL";

    [ObservableProperty]
    private bool _isMultiSelect = false;

    [ObservableProperty]
    private bool _hasSelection = false;

    [ObservableProperty]
    private bool _isTorrent = false;

    [ObservableProperty]
    private bool _isHttpOrMedia = true;

    // GENERAL TAB PROPERTIES
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _saveFolder = string.Empty;

    [ObservableProperty]
    private string _fullSavePath = string.Empty;

    [ObservableProperty]
    private bool _isSavePathCopiedNoticeVisible;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _progressPercentText = "0%";

    [ObservableProperty]
    private string _remainingTimeText = "Remaining: —";

    [ObservableProperty]
    private string _downloadedText = "0 B / 0 B";

    [ObservableProperty]
    private string _uploadedText = "0 B (ratio: 0)";

    [ObservableProperty]
    private string _addedAtText = "—";

    [ObservableProperty]
    private string _downloadSpeedText = "0 B/s";

    [ObservableProperty]
    private string _uploadSpeedText = "0 B/s";

    [ObservableProperty]
    private string _priorityText = "normal";

    [ObservableProperty]
    private string _sourcePageUrl = string.Empty;

    [ObservableProperty]
    private string _formattedSourcePageUrl = string.Empty;

    [ObservableProperty]
    private string _fileIconGlyph = "\uE7C3";

    [ObservableProperty]
    private string _fileIconColor = "#59B9F7";

    [ObservableProperty]
    private bool _isFolderIcon;

    // DETAILS TAB - TORRENT
    [ObservableProperty]
    private string _peersText = "0 (connected: 0)";

    [ObservableProperty]
    private string _seedsText = "0 (connected: 0)";

    [ObservableProperty]
    private string _availabilityText = "0";

    [ObservableProperty]
    private string _lastSeenCompleteText = "—";

    public ObservableCollection<TrackerItemViewModel> Trackers { get; } = new();

    // DETAILS TAB - HTTP
    [ObservableProperty]
    private string _httpServer = "—";

    [ObservableProperty]
    private string _httpResumeSupport = "No";

    [ObservableProperty]
    private string _httpContentType = "—";

    [ObservableProperty]
    private string _httpRedirectTarget = "—";

    [ObservableProperty]
    private string _httpSegmentCount = "1";

    // PROGRESS TAB
    [ObservableProperty]
    private string _piecesSummaryText = "pieces: 0 / 0 (0.0%)";

    [ObservableProperty]
    private PieceState[] _pieceCells = Array.Empty<PieceState>();

    // CONNECTIONS TAB
    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = new();

    private string _sortColumn = "Host";
    private bool _sortAscending = true;

    // LOG TAB
    [ObservableProperty]
    private string _logText = string.Empty;

    public DownloadDetailsViewModel(
        IDownloadDetailsSnapshotService snapshotService,
        ITorrentEngine? torrentEngine = null)
    {
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _torrentEngine = torrentEngine;

        if (_torrentEngine != null)
        {
            _torrentEngine.LogReceived += (s, e) =>
            {
                if (IsOpen && !string.IsNullOrWhiteSpace(_currentDownloadId))
                {
                    if (string.IsNullOrWhiteSpace(e.Entry.JobId) || string.Equals(e.Entry.JobId, _currentDownloadId, StringComparison.OrdinalIgnoreCase))
                    {
                        _ = App.CurrentWindow?.DispatcherQueue?.TryEnqueue(() =>
                        {
                            var line = e.Entry.ToString();
                            LogText = string.IsNullOrEmpty(LogText) ? line : LogText + Environment.NewLine + line;
                        });
                    }
                }
            };
        }
    }

    [RelayCommand]
    public async Task ForceAnnounceAsync()
    {
        if (IsTorrent && !string.IsNullOrWhiteSpace(_currentDownloadId) && _torrentEngine != null)
        {
            try
            {
                await _torrentEngine.ForceAnnounceAsync(_currentDownloadId).ConfigureAwait(true);
            }
            catch { }
        }
    }

    [RelayCommand]
    public void SelectTab(string tabName)
    {
        if (string.IsNullOrWhiteSpace(tabName)) return;
        ActiveTab = tabName.ToUpperInvariant();
    }

    [RelayCommand]
    public void ClosePanel()
    {
        IsOpen = false;
    }

    [RelayCommand]
    public async Task CopySavePathAsync()
    {
        if (string.IsNullOrWhiteSpace(FullSavePath)) return;
        var package = new DataPackage();
        package.SetText(FullSavePath);
        Clipboard.SetContent(package);

        IsSavePathCopiedNoticeVisible = true;
        await Task.Delay(2000).ConfigureAwait(true);
        IsSavePathCopiedNoticeVisible = false;
    }

    [RelayCommand]
    public async Task OpenFolderAsync()
    {
        var target = Directory.Exists(FullSavePath) ? FullSavePath : SaveFolder;
        if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
        {
            await Windows.System.Launcher.LaunchFolderPathAsync(target);
        }
    }

    [RelayCommand]
    public async Task OpenWebPageAsync()
    {
        if (!string.IsNullOrWhiteSpace(SourcePageUrl) && Uri.TryCreate(SourcePageUrl, UriKind.Absolute, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    [RelayCommand]
    public void CopyConnectionIp(ConnectionItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Host)) return;
        var package = new DataPackage();
        package.SetText(item.Host);
        Clipboard.SetContent(package);
    }

    [RelayCommand]
    public void CopyConnectionIpPort(ConnectionItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Host)) return;
        var package = new DataPackage();
        package.SetText($"{item.Host}:{item.Port}");
        Clipboard.SetContent(package);
    }

    [RelayCommand]
    public void CopyLogs()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(LogText);
            Clipboard.SetContent(package);
        }
        catch { }
    }

    [RelayCommand]
    public async Task ExportLogsAsync()
    {
        try
        {
            var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloadsDir)) Directory.CreateDirectory(downloadsDir);
            var safeName = string.Join("_", FileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (safeName.Length > 25) safeName = safeName[..25];
            var destPath = Path.Combine(downloadsDir, $"{safeName}_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            if (_torrentEngine != null)
            {
                await _torrentEngine.ExportLogsAsync(destPath, _currentDownloadId).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(destPath, LogText).ConfigureAwait(false);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = destPath, UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    public void OpenLogsFolder()
    {
        try
        {
            var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "logs");
            Directory.CreateDirectory(logsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = logsDir, UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    public void SortConnections(string column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;

        if (_sortColumn.Equals(column, StringComparison.OrdinalIgnoreCase))
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        ApplyConnectionsSort();
    }

    private long _lastSnapshotTick;
    private bool _isCapturingSnapshot;

    public void SetSelectionState(DownloadItemViewModel? selectedItem, int totalSelectedCount)
    {
        if (totalSelectedCount > 1)
        {
            IsMultiSelect = true;
            HasSelection = false;
            FileName = $"{totalSelectedCount} items selected";
            _currentDownloadId = null;
            return;
        }

        IsMultiSelect = false;

        if (selectedItem == null)
        {
            HasSelection = false;
            _currentDownloadId = null;
            return;
        }

        HasSelection = true;
        _currentDownloadId = selectedItem.Id.ToString("N");

        if (_torrentEngine != null && selectedItem.JobKind == JobKind.Torrent)
        {
            var logs = _torrentEngine.GetRecentLogs(_currentDownloadId, 200);
            LogText = string.Join(Environment.NewLine, logs.Select(l => l.ToString()));
        }
        else
        {
            LogText = string.Empty;
        }

        ApplyLiveMetrics(selectedItem);
    }

    public void ApplyLiveMetrics(DownloadItemViewModel item)
    {
        if (item == null) return;

        FileName = item.FileName;
        SaveFolder = !string.IsNullOrWhiteSpace(item.SavePath)
            ? (Directory.Exists(item.SavePath) ? item.SavePath : Path.GetDirectoryName(item.SavePath) ?? item.SavePath)
            : string.Empty;
        FullSavePath = item.SavePath ?? string.Empty;

        double pct = item.ProgressPercentage;
        long downloaded = item.DownloadedBytes;
        long total = item.TotalBytes;

        if (pct <= 0 && total > 0 && downloaded > 0)
        {
            pct = Math.Min(100.0, (double)downloaded / total * 100.0);
        }
        else if (downloaded <= 0 && total > 0 && pct > 0)
        {
            downloaded = (long)(total * (pct / 100.0));
        }

        ProgressPercentage = Math.Clamp(pct, 0.0, 100.0);
        ProgressPercentText = FormatProgressPercentage(ProgressPercentage, downloaded, total);

        RemainingTimeText = item.RemainingTime > TimeSpan.Zero
            ? $"Remaining: {FormatTimeSpan(item.RemainingTime)}"
            : "Remaining: —";

        var downloadedFormatted = FormatBytes(downloaded);
        var totalFormatted = total > 0 ? FormatBytes(total) : "Unknown";
        DownloadedText = $"{downloadedFormatted} / {totalFormatted}";

        DownloadSpeedText = FormatSpeed(item.SpeedBytesPerSec);
        UploadSpeedText = FormatSpeed(item.UploadSpeedBytesPerSec);

        IsTorrent = item.JobKind == JobKind.Torrent;
        IsHttpOrMedia = !IsTorrent;
        IsFolderIcon = item.JobKind == JobKind.Torrent;
        FileIconGlyph = IsFolderIcon ? "\uED25" : GetFileIcon(item.FileName);
        FileIconColor = IsFolderIcon ? "#F4D03F" : GetFileIconColor(item.FileName);
    }

    public async Task RefreshSnapshotAsync(
        DownloadItemViewModel? item,
        bool forceRefresh = false,
        CancellationToken externalCt = default)
    {
        if (!IsOpen || item == null) return;

        // 1. Immediately apply live in-memory metrics on UI thread for instant real-time UI
        ApplyLiveMetrics(item);

        var downloadId = item.Id.ToString("N");
        _currentDownloadId = downloadId;

        // 2. Throttle heavier background snapshot capture (trackers, connections, piece grid) to max 2Hz
        long now = Environment.TickCount64;
        if (!forceRefresh && _isCapturingSnapshot) return;
        if (!forceRefresh && (now - _lastSnapshotTick) < 500 && downloadId == _currentDownloadId) return;

        _isCapturingSnapshot = true;
        _lastSnapshotTick = now;

        try
        {
            var fallback = new DownloadItem
            {
                Id = downloadId,
                JobKind = item.JobKind,
                Url = item.Url,
                FileName = item.FileName,
                DestinationPath = item.SavePath,
                TotalBytes = item.TotalBytes,
                DownloadedBytes = item.DownloadedBytes,
                Status = item.Status,
                CreatedAtUtc = DateTime.UtcNow
            };

            var snapshot = await _snapshotService.CaptureSnapshotAsync(
                downloadId,
                fallbackItem: fallback,
                currentSpeed: item.SpeedBytesPerSec,
                currentRemainingTime: item.RemainingTime,
                currentProgressPercentage: item.ProgressPercentage,
                ct: externalCt).ConfigureAwait(true);

            if (downloadId == _currentDownloadId)
            {
                ApplySnapshot(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on fast selection switch
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to refresh details snapshot for {Id}", downloadId);
        }
        finally
        {
            _isCapturingSnapshot = false;
        }
    }

    private void ApplySnapshot(DownloadDetailsSnapshot snap)
    {
        IsTorrent = snap.JobKind == JobKind.Torrent;
        IsHttpOrMedia = !IsTorrent;

        // General
        FileName = snap.General.FileName;
        SaveFolder = snap.General.SaveFolder;
        FullSavePath = snap.General.FullSavePath;

        double snapPct = snap.General.ProgressPercentage;
        long snapDownloaded = snap.General.DownloadedBytes;
        long snapTotal = snap.General.TotalBytes;

        if (snapPct <= 0 && snapTotal > 0 && snapDownloaded > 0)
        {
            snapPct = Math.Min(100.0, (double)snapDownloaded / snapTotal * 100.0);
        }
        else if (snapDownloaded <= 0 && snapTotal > 0 && snapPct > 0)
        {
            snapDownloaded = (long)(snapTotal * (snapPct / 100.0));
        }

        if (snapPct > 0 || ProgressPercentage == 0)
        {
            ProgressPercentage = Math.Clamp(snapPct, 0.0, 100.0);
            ProgressPercentText = FormatProgressPercentage(ProgressPercentage, snapDownloaded, snapTotal);
        }

        RemainingTimeText = snap.General.RemainingTime > TimeSpan.Zero
            ? $"Remaining: {FormatTimeSpan(snap.General.RemainingTime)}"
            : "Remaining: —";

        if (snapDownloaded > 0 || DownloadedText == "0 B / 0 B" || DownloadedText.StartsWith("0 B /", StringComparison.OrdinalIgnoreCase))
        {
            var downloadedFormatted = FormatBytes(snapDownloaded);
            var totalFormatted = snapTotal > 0
                ? FormatBytes(snapTotal)
                : "Unknown";
            DownloadedText = $"{downloadedFormatted} / {totalFormatted}";
        }

        var uploadedFormatted = FormatBytes(snap.General.UploadedBytes);
        UploadedText = $"{uploadedFormatted} (ratio: {snap.General.ShareRatio:0.##})";

        AddedAtText = snap.General.AddedAt.ToLocalTime().ToString("MMM dd yyyy, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        DownloadSpeedText = FormatSpeed(snap.General.DownloadSpeedBytesPerSec);
        UploadSpeedText = FormatSpeed(snap.General.UploadSpeedBytesPerSec);

        PriorityText = snap.General.Priority;
        SourcePageUrl = snap.General.SourcePageUrl;
        FormattedSourcePageUrl = MiddleTruncateUrl(snap.General.SourcePageUrl, 85);

        IsFolderIcon = snap.General.IsFolder;
        FileIconGlyph = IsFolderIcon ? "\uED25" : GetFileIcon(snap.General.FileName);
        FileIconColor = IsFolderIcon ? "#F4D03F" : GetFileIconColor(snap.General.FileName);

        // Torrent Details
        if (snap.Torrent != null)
        {
            PeersText = $"{snap.Torrent.TotalPeers} (connected: {snap.Torrent.ConnectedPeers})";
            SeedsText = $"{snap.Torrent.TotalSeeds} (connected: {snap.Torrent.ConnectedSeeds})";
            AvailabilityText = snap.Torrent.Availability > 0 ? snap.Torrent.Availability.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "0";
            LastSeenCompleteText = snap.Torrent.LastSeenComplete.HasValue
                ? snap.Torrent.LastSeenComplete.Value.ToLocalTime().ToString("MM/dd/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture)
                : "—";

            DiffUpdateTrackers(snap.Torrent.Trackers);
        }
        else
        {
            Trackers.Clear();
        }

        // HTTP Details
        if (snap.Http != null)
        {
            HttpServer = !string.IsNullOrWhiteSpace(snap.Http.Server) ? snap.Http.Server : "—";
            HttpResumeSupport = snap.Http.ResumeSupported ? "Yes" : "No";
            HttpContentType = !string.IsNullOrWhiteSpace(snap.Http.ContentType) ? snap.Http.ContentType : "—";
            HttpRedirectTarget = !string.IsNullOrWhiteSpace(snap.Http.RedirectTarget) ? snap.Http.RedirectTarget : "—";
            HttpSegmentCount = snap.Http.SegmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Progress Grid
        PieceCells = snap.ProgressGrid.Cells;
        int total = snap.ProgressGrid.TotalPiecesOrSegments;
        int downloaded = snap.ProgressGrid.DownloadedPieces;
        double pct = total > 0 ? (double)downloaded / total * 100.0 : 0.0;
        PiecesSummaryText = $"pieces: {downloaded:N0} / {total:N0} ({pct:0.0}%)";

        // Connections
        DiffUpdateConnections(snap.Connections);
    }

    private void DiffUpdateTrackers(IReadOnlyList<TrackerDetailSnapshot> newTrackers)
    {
        // Simple sync
        if (Trackers.Count == newTrackers.Count)
        {
            bool match = true;
            for (int i = 0; i < Trackers.Count; i++)
            {
                if (Trackers[i].AnnounceUrl != newTrackers[i].AnnounceUrl ||
                    Trackers[i].Status != newTrackers[i].Status ||
                    Trackers[i].IsError != newTrackers[i].IsError)
                {
                    match = false;
                    break;
                }
            }
            if (match) return;
        }

        Trackers.Clear();
        foreach (var t in newTrackers)
        {
            Trackers.Add(new TrackerItemViewModel
            {
                AnnounceUrl = t.AnnounceUrl,
                Status = t.Status,
                IsError = t.IsError
            });
        }
    }

    private void DiffUpdateConnections(IReadOnlyList<ConnectionDetailSnapshot> newConnections)
    {
        var existingDict = Connections.ToDictionary(c => $"{c.Host}:{c.Port}");
        var incomingKeys = new HashSet<string>();

        foreach (var incoming in newConnections)
        {
            var key = $"{incoming.Host}:{incoming.Port}";
            incomingKeys.Add(key);

            if (existingDict.TryGetValue(key, out var existing))
            {
                if (existing.ConnectionCount != incoming.ConnectionCount)
                {
                    existing.ConnectionCount = incoming.ConnectionCount;
                }
                if (existing.Direction != incoming.Direction)
                {
                    existing.Direction = incoming.Direction;
                }
            }
            else
            {
                Connections.Add(new ConnectionItemViewModel
                {
                    Host = incoming.Host,
                    Port = incoming.Port,
                    ConnectionCount = incoming.ConnectionCount,
                    Direction = incoming.Direction,
                    Client = incoming.Client
                });
            }
        }

        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            var key = $"{Connections[i].Host}:{Connections[i].Port}";
            if (!incomingKeys.Contains(key))
            {
                Connections.RemoveAt(i);
            }
        }

        ApplyConnectionsSort();
    }

    private void ApplyConnectionsSort()
    {
        if (Connections.Count <= 1) return;

        List<ConnectionItemViewModel> sorted = _sortColumn switch
        {
            "Port" => _sortAscending
                ? Connections.OrderBy(c => c.Port).ToList()
                : Connections.OrderByDescending(c => c.Port).ToList(),
            "ConnectionCount" => _sortAscending
                ? Connections.OrderBy(c => c.ConnectionCount).ToList()
                : Connections.OrderByDescending(c => c.ConnectionCount).ToList(),
            _ => _sortAscending
                ? Connections.OrderBy(c => c.Host, StringComparer.OrdinalIgnoreCase).ToList()
                : Connections.OrderByDescending(c => c.Host, StringComparer.OrdinalIgnoreCase).ToList()
        };

        for (int i = 0; i < sorted.Count; i++)
        {
            int oldIdx = Connections.IndexOf(sorted[i]);
            if (oldIdx != i && oldIdx >= 0)
            {
                Connections.Move(oldIdx, i);
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:0.#} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:0.#} KB/s";
        return $"{bytesPerSec / (1024.0 * 1024.0):0.##} MB/s";
    }

    public static string FormatProgressPercentage(double pct, long downloaded = 0, long total = 0)
    {
        if (total > 0 && downloaded >= total) return "100%";
        if (pct <= 0) return "0%";
        if (pct < 0.1) return $"{pct:0.00}%";
        if (pct < 10.0) return $"{pct:0.0}%";
        return $"{pct:0.#}%";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{ts.Days}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private static string MiddleTruncateUrl(string url, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length <= maxLength) return url;
        int half = (maxLength - 3) / 2;
        return string.Concat(url.AsSpan(0, half), "...", url.AsSpan(url.Length - half));
    }

    private static string GetFileIcon(string fileName)
    {
        var category = SwiftGet.Core.IO.FileNameResolver.ResolveCategory(fileName);
        return category.ToLowerInvariant() switch
        {
            "video" => "\uE714",
            "music" => "\uE8D6",
            "compressed" => "\uF012",
            "programs" => "\uE7B5",
            "documents" => "\uE8A5",
            "images" => "\uEB9F",
            "torrents" => "\uE896",
            _ => "\uE7C3"
        };
    }

    private static string GetFileIconColor(string fileName)
    {
        var category = SwiftGet.Core.IO.FileNameResolver.ResolveCategory(fileName);
        return category.ToLowerInvariant() switch
        {
            "video" => "#A855F7",
            "music" => "#10B981",
            "compressed" => "#F59E0B",
            "programs" => "#3B82F6",
            "documents" => "#818CF8",
            "images" => "#F97316",
            _ => "#59B9F7"
        };
    }
}
