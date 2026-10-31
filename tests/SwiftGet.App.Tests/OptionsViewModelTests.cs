using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Notifications;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.App.Tests;

public sealed class OptionsViewModelTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly CategoryManager _categoryManager;
    private readonly MockAudioNotificationService _audioService;

    public OptionsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_opt_vm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
        _categoryManager = new CategoryManager(_db);
        _audioService = new MockAudioNotificationService();
    }

    public async ValueTask DisposeAsync()
    {
        _categoryManager.Dispose();
        await _db.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InitializeAsync_ShouldLoadCategories()
    {
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService);
        await vm.InitializeAsync();

        vm.Categories.Should().NotBeEmpty();
        vm.SelectedCategory.Should().NotBeNull();
        vm.CategoryName.Should().Be(vm.SelectedCategory!.Name);
    }

    [Fact]
    public async Task SaveCategoryAsync_ShouldUpdateDatabaseAndCategoryManager()
    {
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService);
        await vm.InitializeAsync();

        var selected = vm.Categories[0];
        vm.SelectedCategory = selected;
        vm.CategoryTargetDirectory = "C:\\CustomDownloads";
        vm.CategoryFileExtensions = ".custom1 .custom2";

        await vm.SaveCategoryAsync();

        var reloaded = await _db.GetCategoryAsync(selected.Id);
        reloaded.Should().NotBeNull();
        reloaded!.TargetDirectory.Should().Be("C:\\CustomDownloads");
        reloaded.FileExtensions.Should().Be(".custom1 .custom2");
    }

    [Fact]
    public void TestSound_ShouldInvokeAudioService()
    {
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService);

        vm.TestSound("complete");
        _audioService.LastPlayedType.Should().Be(SoundNotificationType.DownloadComplete);

        vm.TestSound("error");
        _audioService.LastPlayedType.Should().Be(SoundNotificationType.DownloadError);

        vm.TestSound("queue");
        _audioService.LastPlayedType.Should().Be(SoundNotificationType.QueueComplete);
    }

    [Fact]
    public void ConnectionAndSpeedLimits_ShouldBeConfigurable()
    {
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService);

        vm.MaxConnections = 16;
        vm.IsLimiterEnabled = true;
        vm.SpeedLimitKbps = 2048;

        vm.MaxConnections.Should().Be(16);
        vm.IsLimiterEnabled.Should().BeTrue();
        vm.SpeedLimitKbps.Should().Be(2048);
    }

    [Fact]
    public async Task BitTorrentOptions_ShouldInitializeFromTorrentEngineAndSave()
    {
        var engine = new MockTorrentEngine();
        engine.Options.ListenPort = 58921;
        engine.Options.EnableUpnp = false;
        engine.Options.EnableDht = true;
        engine.Options.MaxConnections = 120;
        engine.Options.GlobalMaxDownloadRateBytesPerSec = 10_240_000;
        engine.Options.GlobalMaxUploadRateBytesPerSec = 2_048_000;

        var assoc = new MockTorrentAssociationManager();
        assoc.MagnetRegistered = true;
        assoc.TorrentFileRegistered = true;

        var vm = new OptionsViewModel(_db, _categoryManager, _audioService, engine, assoc);
        await vm.InitializeAsync();

        vm.TorrentListenPort.Should().Be(58921);
        vm.TorrentEnableUpnp.Should().BeFalse();
        vm.TorrentEnableDht.Should().BeTrue();
        vm.TorrentMaxConnections.Should().Be(120);
        vm.TorrentMaxDownloadRateKbps.Should().Be(10_000);
        vm.TorrentMaxUploadRateKbps.Should().Be(2_000);
        vm.TorrentIsMagnetAssociated.Should().BeTrue();
        vm.TorrentIsDotTorrentAssociated.Should().BeTrue();
        vm.AssociationStatus.Should().Contain("SwiftGet is registered as default handler");

        // Mutate options and save
        vm.TorrentListenPort = 53333;
        vm.TorrentEnableUpnp = true;
        vm.TorrentMaxConnections = 200;
        vm.TorrentMaxConnectionsPerTorrent = 60;
        vm.TorrentEncryptionModeIndex = 1; // Require
        vm.TorrentMaxDownloadRateKbps = 20_000;
        vm.TorrentMaxUploadRateKbps = 5_000;
        vm.TorrentAltDownloadRateKbps = 1024;
        vm.TorrentAltUploadRateKbps = 256;
        vm.TorrentEnableSchedule = true;
        vm.TorrentScheduleStartHour = 10;
        vm.TorrentScheduleEndHour = 22;
        vm.TorrentUsePartialFiles = false;
        vm.TorrentDefaultSeedRatio = 2.0;
        vm.TorrentDefaultSeedTimeMins = 60;
        vm.TorrentStopSeedingWhenLimitReached = true;
        vm.TorrentEnableIpFilter = true;
        vm.TorrentIpFilterPath = "C:\\test\\ipfilter.dat";

        var tempSettingsFile = Path.Combine(Path.GetTempPath(), $"swiftget_opts_test_{Guid.NewGuid():N}.json");
        TorrentSettingsStore.CustomSettingsPath = tempSettingsFile;
        try
        {
            await vm.SaveAllOptionsAsync();

            engine.Options.ListenPort.Should().Be(53333);
            engine.Options.EnableUpnp.Should().BeTrue();
            engine.Options.MaxConnections.Should().Be(200);
            engine.Options.MaxConnectionsPerTorrent.Should().Be(60);
            engine.Options.EncryptionMode.Should().Be(SwiftGet.Core.Models.TorrentEncryptionMode.RequireEncryption);
            engine.Options.GlobalMaxDownloadRateBytesPerSec.Should().Be(20_000 * 1024);
            engine.Options.GlobalMaxUploadRateBytesPerSec.Should().Be(5_000 * 1024);
            engine.Options.AltDownloadRateBytesPerSec.Should().Be(1024 * 1024);
            engine.Options.AltUploadRateBytesPerSec.Should().Be(256 * 1024);
            engine.Options.EnableRateLimitSchedule.Should().BeTrue();
            engine.Options.ScheduleStartHour.Should().Be(10);
            engine.Options.ScheduleEndHour.Should().Be(22);
            engine.Options.UsePartialFiles.Should().BeFalse();
            engine.Options.DefaultSeedRatio.Should().Be(2.0);
            engine.Options.DefaultSeedTimeMins.Should().Be(60);
            engine.Options.StopSeedingWhenLimitReached.Should().BeTrue();
            engine.Options.EnableIpFilter.Should().BeTrue();
            engine.Options.IpFilterPath.Should().Be("C:\\test\\ipfilter.dat");
            engine.LastUpdatedOptions.Should().NotBeNull();
            engine.LastGlobalRateLimit.Should().Be((20_000L * 1024L, 5_000L * 1024L));
        }
        finally
        {
            TorrentSettingsStore.CustomSettingsPath = null;
            if (File.Exists(tempSettingsFile))
            {
                File.Delete(tempSettingsFile);
            }
        }
    }

    [Fact]
    public void ResetTorrentDefaultsCommand_ResetsAllPropertiesToDefaults()
    {
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService);
        vm.TorrentListenPort = 12345;
        vm.TorrentMaxConnections = 999;
        vm.TorrentEncryptionModeIndex = 2;
        vm.TorrentEnableUpnp = false;
        vm.TorrentEnableDht = false;

        vm.ResetTorrentDefaults();

        vm.TorrentListenPort.Should().Be(52138);
        vm.TorrentMaxConnections.Should().Be(150);
        vm.TorrentMaxConnectionsPerTorrent.Should().Be(50);
        vm.TorrentEncryptionModeIndex.Should().Be(0);
        vm.TorrentEnableUpnp.Should().BeTrue();
        vm.TorrentEnableDht.Should().BeTrue();
        vm.TorrentEnablePex.Should().BeTrue();
        vm.TorrentEnableLsd.Should().BeTrue();
        vm.TorrentUsePartialFiles.Should().BeTrue();
        vm.TorrentDefaultSeedRatio.Should().Be(1.0);
        vm.StatusMessage.Should().Contain("reset to recommended defaults");
    }

    [Fact]
    public async Task TestPortCommand_TestsLocalPortAndSetsStatus()
    {
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService);
        vm.TorrentListenPort = 58123;

        await vm.TestPortAsync();

        vm.PortTestStatus.Should().NotBeNullOrWhiteSpace();
        vm.PortTestStatus.Should().Contain("58123");
        vm.IsTestingPort.Should().BeFalse();
    }

    [Fact]
    public void ResetTrackersCommand_RestoresDefaultTrackers()
    {
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService);
        vm.TorrentDefaultTrackersText = "some_custom_dead_tracker";

        vm.ResetTrackers();

        vm.TorrentDefaultTrackersText.Should().Contain("udp://tracker.opentrackr.org:1337/announce");
        vm.StatusMessage.Should().Contain("Reset BitTorrent trackers to recommended defaults");
    }

    [Fact]
    public void RandomizePortCommand_ShouldGenerateValidPort()
    {
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService);
        vm.RandomizePort();

        vm.TorrentListenPort.Should().BeInRange(1024, 65535);
        vm.StatusMessage.Should().Contain("Assigned random BitTorrent listen port");
    }

    [Fact]
    public void RegisterAssociationsCommand_ShouldInvokeManagerAndRefreshStatus()
    {
        var assoc = new MockTorrentAssociationManager();
        assoc.MagnetRegistered = false;
        assoc.TorrentFileRegistered = false;

        var vm = new OptionsViewModel(_db, _categoryManager, _audioService, null, assoc);
        vm.RefreshAssociationStatus();
        vm.TorrentIsMagnetAssociated.Should().BeFalse();

        vm.RegisterAssociations();

        assoc.RegisterCalled.Should().BeTrue();
        vm.TorrentIsMagnetAssociated.Should().BeTrue();
        vm.TorrentIsDotTorrentAssociated.Should().BeTrue();
        vm.StatusMessage.Should().Contain("Successfully registered");
    }

    [Fact]
    public async Task InitializeAsync_ShouldDetectBrowsersAndUnpackedDir()
    {
        var mockBrowserService = new MockBrowserIntegrationService();
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService, null, null, null, mockBrowserService);
        await vm.InitializeAsync();

        vm.InstalledBrowsers.Should().NotBeEmpty();
        vm.InstalledBrowsers.Should().Contain(b => b.Name == "Mock Chrome");
        vm.UnpackedExtensionDirectory.Should().Be(@"C:\Mock\Extension");
    }

    [Fact]
    public void LaunchBrowserExtensionCommand_ShouldInvokeService()
    {
        var mockBrowserService = new MockBrowserIntegrationService();
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService, null, null, null, mockBrowserService);
        vm.RefreshBrowsers();

        var browser = vm.InstalledBrowsers.First();
        vm.LaunchBrowserExtension(browser);

        mockBrowserService.LastLaunchedBrowser.Should().Be(browser.Type);
        vm.StatusMessage.Should().Contain("Opened extension management");
    }

    [Fact]
    public void OpenBrowserStoreCommand_ShouldInvokeService()
    {
        var mockBrowserService = new MockBrowserIntegrationService();
        var vm = new OptionsViewModel(_db, _categoryManager, _audioService, null, null, null, mockBrowserService);
        vm.RefreshBrowsers();

        var browser = vm.InstalledBrowsers.First();
        vm.OpenBrowserStore(browser);

        mockBrowserService.LastStoreBrowser.Should().Be(browser.Type);
        vm.StatusMessage.Should().Contain("Opened extension store page");
    }

    private sealed class MockBrowserIntegrationService : SwiftGet.Engine.Integration.IBrowserIntegrationService
    {
        public SwiftGet.Engine.Integration.BrowserType? LastLaunchedBrowser { get; private set; }
        public SwiftGet.Engine.Integration.BrowserType? LastStoreBrowser { get; private set; }

        public System.Collections.Generic.IReadOnlyList<SwiftGet.Engine.Integration.BrowserInfo> DetectInstalledBrowsers()
        {
            return new[]
            {
                new SwiftGet.Engine.Integration.BrowserInfo
                {
                    Type = SwiftGet.Engine.Integration.BrowserType.Chrome,
                    Name = "Mock Chrome",
                    ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    IsInstalled = true,
                    IsDefault = true,
                    IsNativeHostRegistered = true,
                    ExtensionManagementUrl = "chrome://extensions",
                    StoreUrl = "https://chromewebstore.google.com"
                }
            };
        }

        public SwiftGet.Engine.Integration.BrowserType? GetDefaultBrowserType() => SwiftGet.Engine.Integration.BrowserType.Chrome;
        public bool IsExtensionRegistered(SwiftGet.Engine.Integration.BrowserType browser) => true;
        public string GetUnpackedExtensionDirectory() => @"C:\Mock\Extension";
        public void LaunchBrowserExtensionPage(SwiftGet.Engine.Integration.BrowserType browser) => LastLaunchedBrowser = browser;
        public void OpenStorePage(SwiftGet.Engine.Integration.BrowserType browser) => LastStoreBrowser = browser;
        public void EnsureExtensionStaged() { }
    }

    private sealed class MockAudioNotificationService : IAudioNotificationService
    {
        public SoundNotificationType? LastPlayedType { get; private set; }
        public string? LastCustomPath { get; private set; }

        public void PlaySound(SoundNotificationType type, string? customWaveFilePath = null)
        {
            LastPlayedType = type;
            LastCustomPath = customWaveFilePath;
        }
    }
}
