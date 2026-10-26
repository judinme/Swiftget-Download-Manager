using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Notifications;
using SwiftGet.Engine.Torrent;
using SwiftGet.Engine.Updating;

namespace SwiftGet.App.ViewModels;

public sealed partial class OptionsViewModel : ObservableObject
{
    private readonly ISwiftGetDatabase _db;
    private readonly ICategoryManager _categoryManager;
    private readonly IAudioNotificationService _audioService;
    private readonly ITorrentEngine? _torrentEngine;
    private readonly ITorrentAssociationManager? _associationManager;
    private readonly IUpdateService? _updateService;

    // --- TAB 1: Categories ---
    [ObservableProperty]
    private ObservableCollection<CategoryRule> _categories = [];

    [ObservableProperty]
    private CategoryRule? _selectedCategory;

    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private string _categoryTargetDirectory = string.Empty;

    [ObservableProperty]
    private string _categoryFileExtensions = string.Empty;

    // --- TAB 2: Antivirus Hook ---
    [ObservableProperty]
    private bool _antivirusEnabled;

    [ObservableProperty]
    private bool _useWindowsDefender = true;

    [ObservableProperty]
    private string _customScannerPath = string.Empty;

    [ObservableProperty]
    private string _customScannerArguments = "\"{path}\"";

    // --- TAB 3: Sounds ---
    [ObservableProperty]
    private bool _soundDownloadCompleteEnabled = true;

    [ObservableProperty]
    private bool _soundDownloadErrorEnabled = true;

    [ObservableProperty]
    private bool _soundQueueCompleteEnabled = true;

    [ObservableProperty]
    private string _customSoundPath = string.Empty;

    // --- TAB 4: Connection ---
    [ObservableProperty]
    private double _maxConnections = 8;

    [ObservableProperty]
    private bool _isLimiterEnabled;

    [ObservableProperty]
    private double _speedLimitKbps = 1024;

    // --- TAB 5: BitTorrent ---
    [ObservableProperty]
    private double _torrentListenPort = 52138;

    [ObservableProperty]
    private bool _torrentEnableUpnp = true;

    [ObservableProperty]
    private bool _torrentEnableDht = true;

    [ObservableProperty]
    private bool _torrentEnablePex = true;

    [ObservableProperty]
    private bool _torrentEnableLsd = true;

    [ObservableProperty]
    private bool _torrentPreferEncryption = true;

    [ObservableProperty]
    private double _torrentMaxConnections = 150;

    [ObservableProperty]
    private double _torrentMaxDownloadRateKbps;

    [ObservableProperty]
    private double _torrentMaxUploadRateKbps;

    [ObservableProperty]
    private double _torrentDefaultSeedRatio = 1.0;

    [ObservableProperty]
    private double _torrentDefaultSeedTimeMins;

    [ObservableProperty]
    private bool _torrentIsMagnetAssociated;

    [ObservableProperty]
    private bool _torrentIsDotTorrentAssociated;

    [ObservableProperty]
    private string _associationStatus = "Status unknown";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    // --- TAB 6: Auto-Updater ---
    [ObservableProperty]
    private string _updateStatusText = "SwiftGet v1.0.0 — Up to date";

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string _updateFeedUrl = "https://raw.githubusercontent.com/swiftget/swiftget/main/releases/updates.json";

    public OptionsViewModel(
        ISwiftGetDatabase db,
        ICategoryManager categoryManager,
        IAudioNotificationService audioService,
        ITorrentEngine? torrentEngine = null,
        ITorrentAssociationManager? associationManager = null,
        IUpdateService? updateService = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _torrentEngine = torrentEngine;
        _associationManager = associationManager;
        _updateService = updateService;
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        if (_updateService == null)
        {
            UpdateStatusText = "Update service not configured.";
            StatusMessage = "Update check unavailable.";
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStatusText = "Checking for updates...";
        StatusMessage = "Connecting to update server...";

        try
        {
            var result = await _updateService.CheckForUpdatesAsync(UpdateFeedUrl, new Version(1, 0, 0)).ConfigureAwait(false);
            if (result.IsUpdateAvailable && result.Manifest != null)
            {
                UpdateStatusText = $"Version {result.LatestVersion} is available! Notes: {result.Manifest.ReleaseNotes}";
                StatusMessage = $"Update available: v{result.LatestVersion}";
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                UpdateStatusText = $"Update check failed: {result.ErrorMessage}";
                StatusMessage = "Update check error.";
            }
            else
            {
                UpdateStatusText = "SwiftGet is up to date (v1.0.0).";
                StatusMessage = "No updates found.";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Error checking updates: {ex.Message}";
            StatusMessage = "Update check failed.";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        var cats = await _db.GetAllCategoriesAsync(ct).ConfigureAwait(false);
        Categories.Clear();
        foreach (var c in cats)
        {
            Categories.Add(c);
        }

        SelectedCategory = Categories.FirstOrDefault();

        if (_torrentEngine != null)
        {
            var opts = _torrentEngine.Options;
            TorrentListenPort = opts.ListenPort > 0 ? opts.ListenPort : 52138;
            TorrentEnableUpnp = opts.EnableUpnp;
            TorrentEnableDht = opts.EnableDht;
            TorrentEnablePex = opts.EnablePex;
            TorrentEnableLsd = opts.EnableLocalPeerDiscovery;
            TorrentPreferEncryption = opts.PreferEncryption;
            TorrentMaxConnections = opts.MaxConnections > 0 ? opts.MaxConnections : 150;
            TorrentMaxDownloadRateKbps = opts.GlobalMaxDownloadRateBytesPerSec > 0 ? opts.GlobalMaxDownloadRateBytesPerSec / 1024.0 : 0;
            TorrentMaxUploadRateKbps = opts.GlobalMaxUploadRateBytesPerSec > 0 ? opts.GlobalMaxUploadRateBytesPerSec / 1024.0 : 0;
        }

        RefreshAssociationStatus();
    }

    public void RefreshAssociationStatus()
    {
        if (_associationManager != null && _associationManager.IsSupported)
        {
            TorrentIsMagnetAssociated = _associationManager.IsMagnetRegistered();
            TorrentIsDotTorrentAssociated = _associationManager.IsTorrentFileRegistered();

            if (TorrentIsMagnetAssociated && TorrentIsDotTorrentAssociated)
            {
                AssociationStatus = "SwiftGet is registered as default handler for magnet: and .torrent files.";
            }
            else if (TorrentIsMagnetAssociated)
            {
                AssociationStatus = "SwiftGet is registered for magnet: links only.";
            }
            else if (TorrentIsDotTorrentAssociated)
            {
                AssociationStatus = "SwiftGet is registered for .torrent files only.";
            }
            else
            {
                AssociationStatus = "SwiftGet is not currently registered as default handler.";
            }
        }
        else
        {
            AssociationStatus = "File association management is only supported on Windows.";
        }
    }

    partial void OnSelectedCategoryChanged(CategoryRule? value)
    {
        if (value != null)
        {
            CategoryName = value.Name;
            CategoryTargetDirectory = value.TargetDirectory;
            CategoryFileExtensions = value.FileExtensions;
        }
    }

    [RelayCommand]
    public async Task SaveCategoryAsync()
    {
        if (SelectedCategory == null) return;

        SelectedCategory.Name = CategoryName;
        SelectedCategory.TargetDirectory = CategoryTargetDirectory;
        SelectedCategory.FileExtensions = CategoryFileExtensions;

        await _db.SaveCategoryAsync(SelectedCategory).ConfigureAwait(false);
        await _categoryManager.InitializeAsync().ConfigureAwait(false);

        StatusMessage = $"Category '{SelectedCategory.Name}' updated";
    }

    [RelayCommand]
    public void TestSound(string soundType)
    {
        var type = soundType switch
        {
            "error" => SoundNotificationType.DownloadError,
            "queue" => SoundNotificationType.QueueComplete,
            _ => SoundNotificationType.DownloadComplete
        };

        _audioService.PlaySound(type, string.IsNullOrWhiteSpace(CustomSoundPath) ? null : CustomSoundPath);
        StatusMessage = $"Played {soundType} audio notification";
    }

    [RelayCommand]
    public void RandomizePort()
    {
        TorrentListenPort = Random.Shared.Next(1024, 65535);
        StatusMessage = $"Assigned random BitTorrent listen port: {TorrentListenPort}";
    }

    [RelayCommand]
    public void RegisterAssociations()
    {
        if (_associationManager == null || !_associationManager.IsSupported)
        {
            StatusMessage = "Associations not supported on this platform.";
            return;
        }

        var success = _associationManager.RegisterAssociations();
        RefreshAssociationStatus();
        StatusMessage = success
            ? "Successfully registered magnet: and .torrent file associations"
            : "Failed to register file associations. Administrator privileges may be required.";
    }

    [RelayCommand]
    public void OpenDefaultApps()
    {
        if (_associationManager != null && _associationManager.IsSupported)
        {
            _associationManager.OpenDefaultAppsSettings();
            StatusMessage = "Opened Windows Default Apps Settings";
        }
    }

    [RelayCommand]
    public async Task SaveAllOptionsAsync()
    {
        if (SelectedCategory != null)
        {
            await SaveCategoryAsync().ConfigureAwait(false);
        }

        if (_torrentEngine != null)
        {
            var opts = _torrentEngine.Options;
            opts.ListenPort = (int)Math.Clamp(TorrentListenPort, 1024, 65535);
            opts.EnableUpnp = TorrentEnableUpnp;
            opts.EnableDht = TorrentEnableDht;
            opts.EnablePex = TorrentEnablePex;
            opts.EnableLocalPeerDiscovery = TorrentEnableLsd;
            opts.PreferEncryption = TorrentPreferEncryption;
            opts.MaxConnections = (int)Math.Max(10, TorrentMaxConnections);

            long downBytes = (long)Math.Max(0, TorrentMaxDownloadRateKbps * 1024.0);
            long upBytes = (long)Math.Max(0, TorrentMaxUploadRateKbps * 1024.0);
            opts.GlobalMaxDownloadRateBytesPerSec = downBytes;
            opts.GlobalMaxUploadRateBytesPerSec = upBytes;

            await _torrentEngine.SetGlobalRateLimitsAsync(downBytes, upBytes).ConfigureAwait(false);
        }

        StatusMessage = "Options saved successfully";
    }
}
