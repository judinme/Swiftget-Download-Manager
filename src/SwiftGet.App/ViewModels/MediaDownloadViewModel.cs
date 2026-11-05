using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using SwiftGet.Core.IO;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.IO;
using SwiftGet.Media.Extractor;
using SwiftGet.Media.Models;
using SwiftGet.Media.Sidecars;

namespace SwiftGet.App.ViewModels;

public sealed partial class MediaDownloadViewModel : ObservableObject
{
    private readonly IMediaExtractor _extractor;
    private readonly ISwiftGetDatabase _db;
    private readonly ICategoryManager _categoryManager;
    private CancellationTokenSource? _probeCts;
    private Task? _activeProbeTask;
    private string? _activeProbeUrl;
    private int _probeVersion;

    [ObservableProperty]
    private string _mediaUrl = string.Empty;

    [ObservableProperty]
    private string _title = "Loading media stream info...";

    [ObservableProperty]
    private string _durationFormatted = "-";

    [ObservableProperty]
    private string _uploader = string.Empty;

    [ObservableProperty]
    private string? _thumbnailUrl;

    [ObservableProperty]
    private ObservableCollection<MediaFormat> _formats = [];

    [ObservableProperty]
    private MediaFormat? _selectedFormat;

    [ObservableProperty]
    private ObservableCollection<MediaSubtitleTrack> _subtitles = [];

    [ObservableProperty]
    private MediaSubtitleTrack? _selectedSubtitle;

    [ObservableProperty]
    private bool _downloadSubtitles;

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _queueName = "default";

    [ObservableProperty]
    private string? _requestedQuality;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _errorDetails = string.Empty;

    [ObservableProperty]
    private bool _isDrmProtected;

    [ObservableProperty]
    private string _drmMessage = string.Empty;

    [ObservableProperty]
    private DownloadItem? _lastEnqueuedItem;

    /// <summary>
    /// Optional pre-created download ID (e.g. from browser extension interception) to update in-place instead of creating a duplicate row.
    /// </summary>
    public string? ExistingDownloadId { get; set; }

    /// <summary>
    /// The yt-dlp format selector computed from the user's selected format at the time of enqueueing.
    /// </summary>
    public string? LastSelectedFormatSelector { get; private set; }

    public Visibility DrmNoticeVisibility => IsDrmProtected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => (!IsDrmProtected && !HasError) ? Visibility.Visible : Visibility.Collapsed;
    public bool CanDownload => !IsDrmProtected && !HasError && !string.IsNullOrWhiteSpace(MediaUrl);
    public bool CanProbe => !IsProbing;

    public MediaDownloadViewModel(
        IMediaExtractor extractor,
        ISwiftGetDatabase db,
        ICategoryManager categoryManager)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        _saveDirectory = PathResolver.GetPhysicalPath(downloads);
    }

    partial void OnMediaUrlChanged(string value)
    {
        OnPropertyChanged(nameof(CanDownload));
        if (!string.IsNullOrWhiteSpace(value) && !IsProbing && !IsDrmProtected && Formats.Count == 0)
        {
            if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                _ = ProbeMediaAsync();
            }
        }
    }

    partial void OnIsDrmProtectedChanged(bool value)
    {
        OnPropertyChanged(nameof(DrmNoticeVisibility));
        OnPropertyChanged(nameof(ContentVisibility));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnHasErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ContentVisibility));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnIsProbingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanProbe));
    }

    partial void OnSelectedFormatChanged(MediaFormat? value)
    {
        OnPropertyChanged(nameof(CanDownload));
        if (value != null && !string.IsNullOrWhiteSpace(Title) && Title != "Loading media stream info..." && Title != "Failed to load media info")
        {
            FileName = FileNameSanitizer.SanitizeFileNameWithExtension(Title, value.Extension);
        }
    }

    [RelayCommand]
    public async Task ProbeMediaAsync(CancellationToken ct = default)
    {
        var targetUrl = MediaUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            StatusMessage = "Please enter a valid media stream URL.";
            return;
        }

        // If an extraction is already in-flight for this exact URL, join it instead of cancelling and spawning a duplicate yt-dlp process
        if (IsProbing &&
            _activeProbeTask is { IsCompleted: false } existingTask &&
            string.Equals(_activeProbeUrl, targetUrl, StringComparison.OrdinalIgnoreCase))
        {
            await existingTask.ConfigureAwait(true);
            return;
        }

        // Cancel any previous probe for a different URL
        if (_probeCts != null)
        {
            try
            {
                await _probeCts.CancelAsync().ConfigureAwait(true);
            }
            catch { }
            _probeCts.Dispose();
            _probeCts = null;
        }

        var myVersion = Interlocked.Increment(ref _probeVersion);
        var myCts = new CancellationTokenSource();
        _probeCts = myCts;
        _activeProbeUrl = targetUrl;

        var task = ExecuteProbeInternalAsync(targetUrl, myVersion, myCts.Token);
        _activeProbeTask = task;
        await task.ConfigureAwait(true);
    }

    private async Task ExecuteProbeInternalAsync(string targetUrl, int myVersion, CancellationToken ct)
    {
        IsProbing = true;
        HasError = false;
        ErrorMessage = string.Empty;
        ErrorDetails = string.Empty;
        IsDrmProtected = false;
        DrmMessage = string.Empty;
        Title = "Loading media stream info...";
        StatusMessage = "Fetching media formats and stream info...";

        try
        {
            var meta = await _extractor.ExtractMetadataAsync(targetUrl, ct).ConfigureAwait(true);
            if (myVersion != Volatile.Read(ref _probeVersion))
            {
                return;
            }

            HasError = false;
            ErrorMessage = string.Empty;
            ErrorDetails = string.Empty;
            IsDrmProtected = false;

            Title = meta.Title;
            Uploader = meta.Uploader ?? "Unknown Channel";
            ThumbnailUrl = meta.ThumbnailUrl;
            DurationFormatted = meta.Duration > TimeSpan.Zero ? meta.Duration.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) : "-";

            // 1. Organize and prioritize formats by resolution descending (Highest Quality First)
            var videoFormats = meta.Formats
                .Where(f => f.HasVideo)
                .Select(f =>
                {
                    if (!f.Height.HasValue || f.Height.Value <= 0)
                    {
                        f.Height = MediaFormat.ParseHeightFromResolution(f.Resolution);
                    }
                    return f;
                })
                .GroupBy(f => new { Height = f.Height ?? 0, Ext = f.Extension.ToLowerInvariant() })
                .Select(g => g.OrderByDescending(f => f.Fps ?? 0)
                              .ThenByDescending(f => f.FileSizeBytes ?? 0)
                              .ThenByDescending(f => f.BitrateKbps ?? 0)
                              .First())
                .OrderByDescending(f => f.Height ?? 0)
                .ThenByDescending(f => f.Fps ?? 0)
                .ThenBy(f => f.Extension.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

            var audioFormats = meta.Formats
                .Where(f => !f.HasVideo && f.HasAudio)
                .GroupBy(f => f.Extension.ToLowerInvariant())
                .Select(g => g.OrderByDescending(f => f.BitrateKbps ?? 0)
                              .ThenByDescending(f => f.FileSizeBytes ?? 0)
                              .First())
                .OrderByDescending(f => f.BitrateKbps ?? 0);

            Formats.Clear();
            foreach (var f in videoFormats)
            {
                Formats.Add(f);
            }
            foreach (var f in audioFormats)
            {
                Formats.Add(f);
            }

            if (Formats.Count == 0)
            {
                foreach (var f in meta.Formats)
                {
                    Formats.Add(f);
                }
            }

            Subtitles.Clear();
            foreach (var s in meta.Subtitles)
            {
                Subtitles.Add(s);
            }

            // Pick highest resolution video format as default (e.g. 1080p, 1440p, 4K), or matching requested quality
            MediaFormat? defaultChoice = null;
            if (!string.IsNullOrWhiteSpace(RequestedQuality))
            {
                var q = RequestedQuality.ToLowerInvariant();
                if (q.Contains("1080"))
                {
                    defaultChoice = Formats.FirstOrDefault(f => f.HasVideo && ((f.Height ?? 0) == 1080 || f.Resolution.Contains("1080")));
                }
                else if (q.Contains("720"))
                {
                    defaultChoice = Formats.FirstOrDefault(f => f.HasVideo && ((f.Height ?? 0) == 720 || f.Resolution.Contains("720")));
                }
                else if (q.Contains("480"))
                {
                    defaultChoice = Formats.FirstOrDefault(f => f.HasVideo && ((f.Height ?? 0) == 480 || f.Resolution.Contains("480")));
                }
                else if (q.Contains("360"))
                {
                    defaultChoice = Formats.FirstOrDefault(f => f.HasVideo && ((f.Height ?? 0) == 360 || f.Resolution.Contains("360")));
                }
                else if (q.Contains("audio"))
                {
                    defaultChoice = Formats.FirstOrDefault(f => !f.HasVideo && f.HasAudio);
                }
            }

            SelectedFormat = defaultChoice ??
                             Formats.FirstOrDefault(f => f.HasVideo && (f.Height ?? 0) >= 1080) ??
                             Formats.FirstOrDefault(f => f.HasVideo && (f.Height ?? 0) >= 720) ??
                             Formats.FirstOrDefault(f => f.HasVideo) ??
                             Formats.FirstOrDefault();

            if (SelectedFormat != null && !string.IsNullOrWhiteSpace(Title))
            {
                FileName = FileNameSanitizer.SanitizeFileNameWithExtension(Title, SelectedFormat.Extension);
            }

            StatusMessage = $"Discovered {Formats.Count} formats.";
            OnPropertyChanged(nameof(ContentVisibility));
            OnPropertyChanged(nameof(CanDownload));
        }
        catch (DrmProtectionDetectedException drmEx)
        {
            if (myVersion != Volatile.Read(ref _probeVersion))
            {
                return;
            }

            IsDrmProtected = true;
            DrmMessage = drmEx.Message;
            StatusMessage = "Stream cannot be downloaded due to DRM protection.";
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation when superseded by another probe or if formats were already loaded
            if (myVersion != Volatile.Read(ref _probeVersion) || Formats.Count > 0)
            {
                return;
            }

            StatusMessage = "Media info fetch was canceled. Click Fetch Info to retry.";
        }
        catch (Exception ex)
        {
            if (myVersion != Volatile.Read(ref _probeVersion))
            {
                return;
            }

            HasError = true;
            Title = "Failed to load media info";
            ErrorMessage = SidecarManager.FormatSidecarError(ex.Message, null);
            ErrorDetails = ex.ToString();
            StatusMessage = $"Failed to extract media: {ErrorMessage}";
            Formats.Clear();
            SelectedFormat = null;
        }
        finally
        {
            if (myVersion == Volatile.Read(ref _probeVersion))
            {
                IsProbing = false;
                OnPropertyChanged(nameof(ContentVisibility));
                OnPropertyChanged(nameof(CanDownload));
            }
        }
    }

    [RelayCommand]
    public async Task RetryProbeAsync(CancellationToken ct = default)
    {
        await ProbeMediaAsync(ct).ConfigureAwait(true);
    }

    [RelayCommand]
    public void CopyErrorDetails()
    {
        var text = string.IsNullOrWhiteSpace(ErrorDetails) ? ErrorMessage : $"{ErrorMessage}\r\n\r\nDetails:\r\n{ErrorDetails}";
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                StatusMessage = "Error details copied to clipboard.";
            }
            catch
            {
                // Fallback for non-packaged or test runner environment
            }
        }
    }

    [RelayCommand]
    public async Task FixPathAutomaticallyAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(SaveDirectory))
        {
            SaveDirectory = PathResolver.GetPhysicalPath(SaveDirectory);
            StatusMessage = $"Resolved save directory to physical path: {SaveDirectory}";
        }

        HasError = false;
        ErrorMessage = string.Empty;
        ErrorDetails = string.Empty;

        if (!string.IsNullOrWhiteSpace(MediaUrl))
        {
            await ProbeMediaAsync(ct).ConfigureAwait(true);
        }
    }

    public async Task<DownloadItem?> EnqueueDownloadAsync(CancellationToken ct = default)
    {
        if (IsDrmProtected || HasError || string.IsNullOrWhiteSpace(MediaUrl))
        {
            return null;
        }

        var ext = SelectedFormat?.Extension ?? "mp4";
        var cleanName = string.IsNullOrWhiteSpace(FileName) ||
                        FileName == "Loading media stream info..." ||
                        FileName == "Failed to load media info"
            ? DeriveInitialMediaFileName(MediaUrl, ext)
            : FileName;

        var category = _categoryManager.ResolveCategory(cleanName);
        var targetDir = string.IsNullOrWhiteSpace(SaveDirectory) ? category.TargetDirectory : SaveDirectory;
        targetDir = PathResolver.GetPhysicalPath(targetDir);
        var destPath = Path.Combine(targetDir, cleanName);

        var itemId = !string.IsNullOrWhiteSpace(ExistingDownloadId)
            ? ExistingDownloadId!
            : Guid.NewGuid().ToString("N");
        ExistingDownloadId = null;

        LastSelectedFormatSelector = SelectedFormat != null ? BuildFormatSelector(SelectedFormat) : "bv*+ba/b";

        var item = new DownloadItem
        {
            Id = itemId,
            JobKind = JobKind.Media,
            Url = MediaUrl,
            FileName = cleanName,
            DestinationPath = destPath,
            CategoryId = category.Id,
            QueueId = string.IsNullOrWhiteSpace(QueueName) ? "default" : QueueName,
            TotalBytes = SelectedFormat?.FileSizeBytes ?? 0,
            Status = DownloadStatus.Queued,
            CreatedAtUtc = DateTime.UtcNow
        };

        await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
        LastEnqueuedItem = item;
        StatusMessage = $"Enqueued {cleanName} to queue '{item.QueueId}'.";
        return item;
    }

    public static string DeriveInitialMediaFileName(string url, string defaultExt = "mp4")
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var v = query["v"];
            if (!string.IsNullOrWhiteSpace(v))
            {
                return $"{FileNameSanitizer.Sanitize(v)}.{defaultExt}";
            }

            var last = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last) && !last.Equals("watch", StringComparison.OrdinalIgnoreCase))
            {
                return $"{FileNameSanitizer.Sanitize(last)}.{defaultExt}";
            }
        }
        return $"video_{DateTime.UtcNow.Ticks}.{defaultExt}";
    }

    private static string BuildFormatSelector(MediaFormat format)
    {
        if (string.IsNullOrWhiteSpace(format.FormatId) ||
            string.Equals(format.FormatId, "best", StringComparison.OrdinalIgnoreCase))
        {
            return "bv*+ba/b";
        }

        if (format.HasVideo && !format.HasAudio)
        {
            var h = format.Height ?? MediaFormat.ParseHeightFromResolution(format.Resolution);
            if (h > 0)
            {
                return $"{format.FormatId}+ba/bv*[height<={h}]+ba/b[height<={h}]/bv*+ba/b";
            }
            return $"{format.FormatId}+ba/{format.FormatId}/bv*+ba/b";
        }

        if (!format.HasVideo && format.HasAudio)
        {
            return $"{format.FormatId}/ba/b";
        }

        return $"{format.FormatId}/bv*+ba/b";
    }
}
