using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Notifications;
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
        vm.TorrentMaxDownloadRateKbps = 20_000;
        vm.TorrentMaxUploadRateKbps = 5_000;

        await vm.SaveAllOptionsAsync();

        engine.Options.ListenPort.Should().Be(53333);
        engine.Options.EnableUpnp.Should().BeTrue();
        engine.Options.MaxConnections.Should().Be(200);
        engine.Options.GlobalMaxDownloadRateBytesPerSec.Should().Be(20_000 * 1024);
        engine.Options.GlobalMaxUploadRateBytesPerSec.Should().Be(5_000 * 1024);
        engine.LastGlobalRateLimit.Should().Be((20_000L * 1024L, 5_000L * 1024L));
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
