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
    public bool CanDownload => !IsProbing && !IsDrmProtected && !HasError && SelectedFormat != null && Formats.Count > 0;
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
        if (string.IsNullOrWhiteSpace(MediaUrl))
        {
            StatusMessage = "Please enter a valid media stream URL.";
            return;
        }

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
            var meta = await _extractor.ExtractMetadataAsync(MediaUrl, ct).ConfigureAwait(true);
            Title = meta.Title;
            Uploader = meta.Uploader ?? "Unknown Channel";
            ThumbnailUrl = meta.ThumbnailUrl;
            DurationFormatted = meta.Duration > TimeSpan.Zero ? meta.Duration.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) : "-";

            Formats.Clear();
            foreach (var f in meta.Formats)
            {
                Formats.Add(f);
            }

            Subtitles.Clear();
            foreach (var s in meta.Subtitles)
            {
                Subtitles.Add(s);
            }

            // Pick highest resolution video format as default
            SelectedFormat = Formats.FirstOrDefault(f => f.HasVideo && f.HasAudio) ??
                             Formats.FirstOrDefault(f => f.HasVideo) ??
                             Formats.FirstOrDefault();

            if (SelectedFormat != null && !string.IsNullOrWhiteSpace(Title))
            {
                FileName = FileNameSanitizer.SanitizeFileNameWithExtension(Title, SelectedFormat.Extension);
            }

            StatusMessage = $"Discovered {Formats.Count} formats.";
        }
        catch (DrmProtectionDetectedException drmEx)
        {
            IsDrmProtected = true;
            DrmMessage = drmEx.Message;
            StatusMessage = "Stream cannot be downloaded due to DRM protection.";
        }
        catch (Exception ex)
        {
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
            IsProbing = false;
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
        if (SelectedFormat == null || IsDrmProtected || HasError)
        {
            return null;
        }

        var cleanName = string.IsNullOrWhiteSpace(FileName)
            ? $"media_{DateTime.UtcNow.Ticks}.{SelectedFormat.Extension}"
            : FileName;

        var category = _categoryManager.ResolveCategory(cleanName);
        var targetDir = string.IsNullOrWhiteSpace(SaveDirectory) ? category.TargetDirectory : SaveDirectory;
        targetDir = PathResolver.GetPhysicalPath(targetDir);
        var destPath = Path.Combine(targetDir, cleanName);

        var itemId = !string.IsNullOrWhiteSpace(ExistingDownloadId)
            ? ExistingDownloadId!
            : Guid.NewGuid().ToString("N");
        ExistingDownloadId = null;

        LastSelectedFormatSelector = BuildFormatSelector(SelectedFormat);

        var item = new DownloadItem
        {
            Id = itemId,
            JobKind = JobKind.Media,
            Url = MediaUrl,
            FileName = cleanName,
            DestinationPath = destPath,
            CategoryId = category.Id,
            QueueId = string.IsNullOrWhiteSpace(QueueName) ? "default" : QueueName,
            TotalBytes = SelectedFormat.FileSizeBytes ?? 0,
            Status = DownloadStatus.Queued,
            CreatedAtUtc = DateTime.UtcNow
        };

        await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
        LastEnqueuedItem = item;
        StatusMessage = $"Enqueued {cleanName} to queue '{item.QueueId}'.";
        return item;
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
            return $"{format.FormatId}+ba/{format.FormatId}/bv*+ba/b";
        }

        if (!format.HasVideo && format.HasAudio)
        {
            return $"{format.FormatId}/ba/b";
        }

        return $"{format.FormatId}/bv*+ba/b";
    }
}
