using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Integration;
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
    private readonly IBrowserIntegrationService? _browserService;

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
    private double _maxConcurrentDownloads = 3;

    [ObservableProperty]
    private bool _showProgressWindowForMediaDownloads;

    [ObservableProperty]
    private bool _deletePartialFilesOnCancel;

    [ObservableProperty]
    private bool _isLimiterEnabled;

    [ObservableProperty]
    private double _speedLimitKbps = 1024;

    // --- TAB 5: BitTorrent ---
    [ObservableProperty]
    private double _torrentListenPort = 52138;

    [ObservableProperty]
    private bool _torrentRandomizePortOnStartup;

    [ObservableProperty]
    private bool _isTestingPort;

    [ObservableProperty]
    private string _portTestStatus = "Click 'Test Port' to verify incoming connectivity.";

    [ObservableProperty]
    private bool _torrentEnableUpnp = true;

    [ObservableProperty]
    private bool _torrentEnableDht = true;

    [ObservableProperty]
    private bool _torrentEnablePex = true;

    [ObservableProperty]
    private bool _torrentEnableLsd = true;

    [ObservableProperty]
    private int _torrentEncryptionModeIndex; // 0: Prefer, 1: Require, 2: Plaintext

    [ObservableProperty]
    private bool _torrentPreferEncryption = true;

    [ObservableProperty]
    private double _torrentMaxConnections = 150;

    [ObservableProperty]
    private double _torrentMaxConnectionsPerTorrent = 50;

    [ObservableProperty]
    private double _torrentMaxDownloadRateKbps;

    [ObservableProperty]
    private double _torrentMaxUploadRateKbps;

    [ObservableProperty]
    private double _torrentAltDownloadRateKbps = 512;

    [ObservableProperty]
    private double _torrentAltUploadRateKbps = 128;

    [ObservableProperty]
    private bool _torrentEnableSchedule;

    [ObservableProperty]
    private double _torrentScheduleStartHour = 8;

    [ObservableProperty]
    private double _torrentScheduleEndHour = 20;

    [ObservableProperty]
    private string _torrentDefaultSaveDirectory = string.Empty;

    [ObservableProperty]
    private bool _torrentUsePartialFiles = true;

    [ObservableProperty]
    private double _torrentDefaultSeedRatio = 1.0;

    [ObservableProperty]
    private double _torrentDefaultSeedTimeMins;

    [ObservableProperty]
    private bool _torrentStopSeedingWhenLimitReached = true;

    [ObservableProperty]
    private string _torrentDefaultTrackersText = string.Empty;

    [ObservableProperty]
    private bool _torrentEnableIpFilter;

    [ObservableProperty]
    private string _torrentIpFilterPath = string.Empty;

    [ObservableProperty]
    private string _ipFilterStatus = "Blocklist disabled";

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

    // --- TAB 7: Browser Integration ---
    [ObservableProperty]
    private ObservableCollection<BrowserInfo> _installedBrowsers = [];

    [ObservableProperty]
    private string _unpackedExtensionDirectory = string.Empty;

    [ObservableProperty]
    private string _browserStatusMessage = "Ready";

    [ObservableProperty]
    private bool _browserCaptureEnabled = true;

    [ObservableProperty]
    private int _browserCaptureModeIndex; // 0: Ask me, 1: Auto-start, 2: Off

    [ObservableProperty]
    private double _browserMinFileSizeKb = 256;

    [ObservableProperty]
    private string _browserIgnorePatternsText = string.Empty;

    [ObservableProperty]
    private string _browserAllowedExtensionsText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _captureLogLines = [];

    private readonly CaptureGatekeeper? _captureGatekeeper;

    public OptionsViewModel(
        ISwiftGetDatabase db,
        ICategoryManager categoryManager,
        IAudioNotificationService audioService,
        ITorrentEngine? torrentEngine = null,
        ITorrentAssociationManager? associationManager = null,
        IUpdateService? updateService = null,
        IBrowserIntegrationService? browserService = null,
        CaptureGatekeeper? captureGatekeeper = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _torrentEngine = torrentEngine;
        _associationManager = associationManager;
        _updateService = updateService;
        _browserService = browserService;
        _captureGatekeeper = captureGatekeeper ?? new CaptureGatekeeper();

        var appSettings = AppSettingsStore.Load();
        _maxConcurrentDownloads = appSettings.MaxConcurrentDownloads;
        _showProgressWindowForMediaDownloads = appSettings.ShowMediaProgressDialog;
        _deletePartialFilesOnCancel = appSettings.DeletePartialFilesOnCancel;
        _maxConnections = appSettings.MaxConnectionsPerDownload;
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
            TorrentRandomizePortOnStartup = opts.RandomizePortOnStartup;
            TorrentEnableUpnp = opts.EnableUpnp;
            TorrentEnableDht = opts.EnableDht;
            TorrentEnablePex = opts.EnablePex;
            TorrentEnableLsd = opts.EnableLocalPeerDiscovery;
            TorrentEncryptionModeIndex = (int)opts.EncryptionMode;
            TorrentPreferEncryption = opts.PreferEncryption;
            TorrentMaxConnections = opts.MaxConnections > 0 ? opts.MaxConnections : 150;
            TorrentMaxConnectionsPerTorrent = opts.MaxConnectionsPerTorrent > 0 ? opts.MaxConnectionsPerTorrent : 50;
            TorrentMaxDownloadRateKbps = opts.GlobalMaxDownloadRateBytesPerSec > 0 ? opts.GlobalMaxDownloadRateBytesPerSec / 1024.0 : 0;
            TorrentMaxUploadRateKbps = opts.GlobalMaxUploadRateBytesPerSec > 0 ? opts.GlobalMaxUploadRateBytesPerSec / 1024.0 : 0;
            TorrentAltDownloadRateKbps = opts.AltDownloadRateBytesPerSec > 0 ? opts.AltDownloadRateBytesPerSec / 1024.0 : 512;
            TorrentAltUploadRateKbps = opts.AltUploadRateBytesPerSec > 0 ? opts.AltUploadRateBytesPerSec / 1024.0 : 128;
            TorrentEnableSchedule = opts.EnableRateLimitSchedule;
            TorrentScheduleStartHour = opts.ScheduleStartHour;
            TorrentScheduleEndHour = opts.ScheduleEndHour;
            TorrentDefaultSaveDirectory = !string.IsNullOrWhiteSpace(opts.SaveDirectory)
                ? opts.SaveDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            TorrentUsePartialFiles = opts.UsePartialFiles;
            TorrentDefaultSeedRatio = opts.DefaultSeedRatio;
            TorrentDefaultSeedTimeMins = opts.DefaultSeedTimeMins;
            TorrentStopSeedingWhenLimitReached = opts.StopSeedingWhenLimitReached;
            TorrentDefaultTrackersText = !string.IsNullOrWhiteSpace(opts.DefaultTrackersText)
                ? opts.DefaultTrackersText
                : string.Join(Environment.NewLine, MonoTorrentEngine.DefaultPublicTrackers);
            TorrentEnableIpFilter = opts.EnableIpFilter;
            TorrentIpFilterPath = opts.IpFilterPath ?? string.Empty;
            IpFilterStatus = opts.EnableIpFilter && File.Exists(opts.IpFilterPath)
                ? $"Blocklist configured: {Path.GetFileName(opts.IpFilterPath)}"
                : "Blocklist disabled";
        }

        RefreshAssociationStatus();
        RefreshBrowsers();
        LoadBrowserCaptureSettings();
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
    public async Task TestPortAsync()
    {
        IsTestingPort = true;
        PortTestStatus = "Testing incoming port...";
        int port = (int)Math.Clamp(TorrentListenPort, 1024, 65535);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            PortTestStatus = $"Port {port} is OPEN and accepting incoming BitTorrent connections.";
        }
        catch
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                PortTestStatus = $"Port {port} is AVAILABLE for listening.";
            }
            catch (Exception ex)
            {
                PortTestStatus = $"Port {port} test error: {ex.Message}";
            }
        }
        finally
        {
            IsTestingPort = false;
        }
    }

    [RelayCommand]
    public void ResetTrackers()
    {
        TorrentDefaultTrackersText = string.Join(Environment.NewLine, MonoTorrentEngine.DefaultPublicTrackers);
        StatusMessage = "Reset BitTorrent trackers to recommended defaults";
    }

    [RelayCommand]
    public void ResetTorrentDefaults()
    {
        TorrentListenPort = 52138;
        TorrentRandomizePortOnStartup = false;
        TorrentEnableUpnp = true;
        TorrentEnableDht = true;
        TorrentEnablePex = true;
        TorrentEnableLsd = true;
        TorrentEncryptionModeIndex = 0; // Prefer
        TorrentPreferEncryption = true;
        TorrentMaxConnections = 150;
        TorrentMaxConnectionsPerTorrent = 50;
        TorrentMaxDownloadRateKbps = 0;
        TorrentMaxUploadRateKbps = 0;
        TorrentAltDownloadRateKbps = 512;
        TorrentAltUploadRateKbps = 128;
        TorrentEnableSchedule = false;
        TorrentScheduleStartHour = 8;
        TorrentScheduleEndHour = 20;
        TorrentDefaultSaveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        TorrentUsePartialFiles = true;
        TorrentDefaultSeedRatio = 1.0;
        TorrentDefaultSeedTimeMins = 0;
        TorrentStopSeedingWhenLimitReached = true;
        TorrentDefaultTrackersText = string.Join(Environment.NewLine, MonoTorrentEngine.DefaultPublicTrackers);
        TorrentEnableIpFilter = false;
        TorrentIpFilterPath = string.Empty;
        IpFilterStatus = "Blocklist disabled";
        PortTestStatus = "Defaults restored. Click 'Test Port' to test.";
        StatusMessage = "BitTorrent settings reset to recommended defaults";
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
            opts.RandomizePortOnStartup = TorrentRandomizePortOnStartup;
            opts.EnableUpnp = TorrentEnableUpnp;
            opts.EnableDht = TorrentEnableDht;
            opts.EnablePex = TorrentEnablePex;
            opts.EnableLocalPeerDiscovery = TorrentEnableLsd;
            opts.EncryptionMode = (TorrentEncryptionMode)Math.Clamp(TorrentEncryptionModeIndex, 0, 2);
            opts.MaxConnections = (int)Math.Max(10, TorrentMaxConnections);
            opts.MaxConnectionsPerTorrent = (int)Math.Max(5, TorrentMaxConnectionsPerTorrent);

            long downBytes = (long)Math.Max(0, TorrentMaxDownloadRateKbps * 1024.0);
            long upBytes = (long)Math.Max(0, TorrentMaxUploadRateKbps * 1024.0);
            opts.GlobalMaxDownloadRateBytesPerSec = downBytes;
            opts.GlobalMaxUploadRateBytesPerSec = upBytes;

            opts.AltDownloadRateBytesPerSec = (long)Math.Max(0, TorrentAltDownloadRateKbps * 1024.0);
            opts.AltUploadRateBytesPerSec = (long)Math.Max(0, TorrentAltUploadRateKbps * 1024.0);
            opts.EnableRateLimitSchedule = TorrentEnableSchedule;
            opts.ScheduleStartHour = (int)TorrentScheduleStartHour;
            opts.ScheduleEndHour = (int)TorrentScheduleEndHour;

            opts.SaveDirectory = TorrentDefaultSaveDirectory;
            opts.UsePartialFiles = TorrentUsePartialFiles;
            opts.DefaultSeedRatio = Math.Max(0, TorrentDefaultSeedRatio);
            opts.DefaultSeedTimeMins = (int)Math.Max(0, TorrentDefaultSeedTimeMins);
            opts.StopSeedingWhenLimitReached = TorrentStopSeedingWhenLimitReached;
            opts.DefaultTrackersText = TorrentDefaultTrackersText;
            opts.EnableIpFilter = TorrentEnableIpFilter;
            opts.IpFilterPath = TorrentIpFilterPath;

            TorrentSettingsStore.Save(opts);
            await _torrentEngine.UpdateOptionsAsync(opts).ConfigureAwait(false);
            await _torrentEngine.SetGlobalRateLimitsAsync(downBytes, upBytes).ConfigureAwait(false);
        }

        SaveBrowserCaptureSettings();

        var appSettings = AppSettingsStore.Load();
        appSettings.MaxConcurrentDownloads = (int)Math.Clamp(MaxConcurrentDownloads, 1, 10);
        appSettings.ShowMediaProgressDialog = ShowProgressWindowForMediaDownloads;
        appSettings.DeletePartialFilesOnCancel = DeletePartialFilesOnCancel;
        appSettings.MaxConnectionsPerDownload = (int)Math.Clamp(MaxConnections, 1, 32);
        AppSettingsStore.Save(appSettings);

        StatusMessage = "Options saved successfully";
    }

    public void LoadBrowserCaptureSettings()
    {
        if (_captureGatekeeper == null) return;
        var settings = _captureGatekeeper.LoadSettings();
        BrowserCaptureEnabled = settings.CaptureEnabled;
        BrowserCaptureModeIndex = (int)settings.Mode;
        BrowserMinFileSizeKb = Math.Max(0, settings.MinFileSizeKb);
        BrowserIgnorePatternsText = string.Join(Environment.NewLine, settings.IgnorePatterns);
        BrowserAllowedExtensionsText = string.Join(" ", settings.AllowedExtensions);
        RefreshCaptureLog();
    }

    public void SaveBrowserCaptureSettings()
    {
        if (_captureGatekeeper == null) return;
        var ignoreList = BrowserIgnorePatternsText
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var extList = BrowserAllowedExtensionsText
            .Split([' ', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s.StartsWith('.') ? s : "." + s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var settings = new BrowserCaptureSettings
        {
            CaptureEnabled = BrowserCaptureEnabled,
            Mode = (CaptureMode)Math.Clamp(BrowserCaptureModeIndex, 0, 2),
            MinFileSizeKb = (int)Math.Max(0, BrowserMinFileSizeKb),
            IgnorePatterns = ignoreList,
            AllowedExtensions = extList
        };
        _captureGatekeeper.SaveSettings(settings);
    }

    [RelayCommand]
    public void RefreshCaptureLog()
    {
        if (_captureGatekeeper == null) return;
        CaptureLogLines.Clear();
        foreach (var entry in _captureGatekeeper.GetRecentDecisions())
        {
            CaptureLogLines.Add(entry.DisplayLine);
        }
    }

    [RelayCommand]
    public void ClearCaptureState()
    {
        _captureGatekeeper?.ClearState();
        CaptureLogLines.Clear();
        BrowserStatusMessage = "Cleared pending capture state, deduplication cache, and capture decision log.";
        StatusMessage = BrowserStatusMessage;
    }

    public void RefreshBrowsers()
    {
        if (_browserService != null)
        {
            var browsers = _browserService.DetectInstalledBrowsers();
            InstalledBrowsers.Clear();
            foreach (var b in browsers)
            {
                InstalledBrowsers.Add(b);
            }
            UnpackedExtensionDirectory = _browserService.GetUnpackedExtensionDirectory();
        }
    }

    [RelayCommand]
    public void LaunchBrowserExtension(BrowserInfo? browser)
    {
        if (browser == null || _browserService == null) return;
        _browserService.LaunchBrowserExtensionPage(browser.Type);
        BrowserStatusMessage = $"Opened extension management in {browser.Name}";
        StatusMessage = BrowserStatusMessage;
    }

    [RelayCommand]
    public void OpenBrowserStore(BrowserInfo? browser)
    {
        if (browser == null || _browserService == null) return;
        _browserService.OpenStorePage(browser.Type);
        BrowserStatusMessage = $"Opened extension store page for {browser.Name}";
        StatusMessage = BrowserStatusMessage;
    }

    [RelayCommand]
    public void OpenUnpackedFolder()
    {
        string dir = _browserService?.GetUnpackedExtensionDirectory() ?? string.Empty;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
            BrowserStatusMessage = "Opened extension directory";
            StatusMessage = BrowserStatusMessage;
        }
    }
}
