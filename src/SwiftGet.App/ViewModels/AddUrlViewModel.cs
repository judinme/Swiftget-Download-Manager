using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core;
using SwiftGet.Core.IO;
using SwiftGet.Engine.Probing;

namespace SwiftGet.App.ViewModels;

public sealed partial class AddUrlViewModel : ObservableObject
{
    private readonly HttpProbeService _probeService;
    private readonly IFileNameResolver _fileNameResolver;
    private bool _isInternalFileNameUpdate;

    public bool UserHasExplicitlyEditedFileName { get; set; }

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullSavePath))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryPath))]
    [NotifyPropertyChangedFor(nameof(FullSavePath))]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryPath))]
    [NotifyPropertyChangedFor(nameof(FullSavePath))]
    private string _selectedCategory = "General";

    private string _queueName = "default";
    public string QueueName
    {
        get => _queueName;
        set
        {
            var normalized = string.Equals(value, "Main", StringComparison.OrdinalIgnoreCase) ? "default" : (string.Equals(value, "Sync", StringComparison.OrdinalIgnoreCase) ? "sync" : value);
            SetProperty(ref _queueName, normalized);
        }
    }

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _rememberCategoryPath = true;

    [ObservableProperty]
    private bool _dontAskAgainForSite;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _startImmediately = true;

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedSize))]
    private long _fileSize = -1;

    [ObservableProperty]
    private bool _resumeSupported = true;

    [ObservableProperty]
    private string _freeDiskSpace = string.Empty;

    public string? CookiesJson { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }

    public string CategoryPath => PathResolver.GetPhysicalPath(ResolveCategoryDirectory(SaveDirectory, SelectedCategory)) + Path.DirectorySeparatorChar;

    public string FullSavePath => PathResolver.GetPhysicalPath(Path.Combine(ResolveCategoryDirectory(SaveDirectory, SelectedCategory), FileName));

    private static string ResolveCategoryDirectory(string saveDirectory, string selectedCategory)
    {
        if (string.IsNullOrWhiteSpace(saveDirectory))
        {
            return selectedCategory ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(selectedCategory))
        {
            return saveDirectory;
        }

        var trimmed = saveDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var lastSegment = Path.GetFileName(trimmed);
        if (string.Equals(lastSegment, selectedCategory, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return Path.Combine(trimmed, selectedCategory);
    }

    public ObservableCollection<string> Categories { get; } =
    [
        "General",
        "Compressed",
        "Documents",
        "Music",
        "Programs",
        "Video"
    ];

    public ObservableCollection<string> Queues { get; } =
    [
        "Main",
        "Sync",
        "None"
    ];

    public AddUrlViewModel(HttpProbeService probeService, IFileNameResolver? fileNameResolver = null)
    {
        _probeService = probeService ?? throw new ArgumentNullException(nameof(probeService));
        _fileNameResolver = fileNameResolver ?? new FileNameResolver();
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        _saveDirectory = PathResolver.GetPhysicalPath(downloads);
        UpdateFreeSpace();
    }

    public string FormattedSize => FileSize switch
    {
        <= 0 => "Unknown",
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", FileSize / 1024.0),
        < 1024 * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", FileSize / (1024.0 * 1024.0)),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", FileSize / (1024.0 * 1024.0 * 1024.0))
    };

    partial void OnUrlChanged(string value)
    {
        if (UrlMediaDetector.IsStreamingMediaPlatformUrl(value))
        {
            SelectedCategory = "Video";
            if ((string.IsNullOrWhiteSpace(FileName) || FileName == "download.bin" || FileName == "watch") && !UserHasExplicitlyEditedFileName)
            {
                _isInternalFileNameUpdate = true;
                try
                {
                    FileName = MediaDownloadViewModel.DeriveInitialMediaFileName(value, "mp4");
                }
                finally
                {
                    _isInternalFileNameUpdate = false;
                }
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(FileName) && !UserHasExplicitlyEditedFileName)
        {
            string candidate = _fileNameResolver.ResolveInitialCandidate(value);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                _isInternalFileNameUpdate = true;
                try
                {
                    FileName = candidate;
                }
                finally
                {
                    _isInternalFileNameUpdate = false;
                }
                AutoAssignCategory(candidate);
            }
        }
    }

    partial void OnFileNameChanged(string value)
    {
        if (!_isInternalFileNameUpdate && !string.IsNullOrWhiteSpace(value))
        {
            UserHasExplicitlyEditedFileName = true;
        }
        AutoAssignCategory(value);
    }

    partial void OnSaveDirectoryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Contains('%'))
        {
            var expanded = Environment.ExpandEnvironmentVariables(value);
            if (!string.Equals(expanded, value, StringComparison.Ordinal))
            {
                SaveDirectory = expanded;
                return;
            }
        }
        UpdateFreeSpace();
    }

    [RelayCommand]
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            StatusMessage = "Please enter a valid HTTP or HTTPS URL.";
            return;
        }

        if (UrlMediaDetector.IsStreamingMediaPlatformUrl(Url))
        {
            SelectedCategory = "Video";
            ResumeSupported = true;
            StatusMessage = "Streaming media detected (will download via high-speed media engine).";
            return;
        }

        try
        {
            IsProbing = true;
            StatusMessage = "Probing server...";

            ProbeResult result = await _probeService.ProbeAsync(
                Url,
                cookies: CookiesJson,
                userAgent: UserAgent,
                referrer: Referrer,
                ct: cancellationToken).ConfigureAwait(true);
            FileSize = result.TotalBytes;
            ResumeSupported = result.SupportsRanges;

            if (!UserHasExplicitlyEditedFileName && !string.IsNullOrWhiteSpace(result.SuggestedFileName))
            {
                _isInternalFileNameUpdate = true;
                try
                {
                    FileName = result.SuggestedFileName;
                }
                finally
                {
                    _isInternalFileNameUpdate = false;
                }
                AutoAssignCategory(result.SuggestedFileName);
            }

            StatusMessage = $"Size: {FormattedSize} | Resume: {(ResumeSupported ? "Yes" : "No")}";
            OnPropertyChanged(nameof(FormattedSize));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Probe failed: {ex.Message}";
        }
        finally
        {
            IsProbing = false;
        }
    }

    public void AutoAssignCategory(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        string ext = _fileNameResolver.GetCompoundExtension(fileName).ToLowerInvariant();
        SelectedCategory = ext switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst" or ".iso" or ".tgz"
                or ".tar.gz" or ".tar.bz2" or ".tar.xz" or ".tar.zst" => "Compressed",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".epub" or ".csv" => "Documents",
            ".mp3" or ".flac" or ".wav" or ".aac" or ".m4a" or ".ogg" or ".wma" or ".opus" => "Music",
            ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".apk" or ".appx" or ".msix" or ".dmg" => "Programs",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".wmv" or ".flv" or ".m4v" or ".ts" => "Video",
            _ => "General"
        };
    }

    private void UpdateFreeSpace()
    {
        try
        {
            if (Directory.Exists(SaveDirectory))
            {
                string root = Path.GetPathRoot(SaveDirectory) ?? "C:\\";
                DriveInfo drive = new(root);
                FreeDiskSpace = string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB free", drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0));
            }
            else
            {
                FreeDiskSpace = "Drive info unavailable";
            }
        }
        catch
        {
            FreeDiskSpace = "Drive info unavailable";
        }
    }
}
