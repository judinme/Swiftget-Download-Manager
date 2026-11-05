using System;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Serilog;
using SwiftGet.App.ViewModels;
using SwiftGet.App.Views;
using SwiftGet.Core.IO;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Automation;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Coordinator;
using SwiftGet.Engine.Network;
using SwiftGet.Engine.Notifications;
using SwiftGet.Engine.Clipboard;
using SwiftGet.Engine.Grabber;
using SwiftGet.Engine.Probing;
using SwiftGet.Engine.Queues;
using SwiftGet.Engine.Scheduling;
using SwiftGet.Engine.Security;
using SwiftGet.Engine.Segmentation;
using SwiftGet.Engine.Serialization;
using SwiftGet.Engine.Server;
using SwiftGet.Core.Localization;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Diagnostics;
using SwiftGet.Engine.Services;
using SwiftGet.Media.Extractor;
using SwiftGet.Media.Recorder;
using SwiftGet.Media.Sidecars;
using SwiftGet.Engine.Updating;
using SwiftGet.Engine.Integration;
using SwiftGet.App.Services;

using SwiftGet.Engine.Torrent;
using SwiftGet.Engine.Cli;

namespace SwiftGet.App;

public sealed partial class App : Application
{
    private static Window? _currentWindow;
    public static Window CurrentWindow => _currentWindow ?? throw new InvalidOperationException("Current window has not been initialized.");

    public static IHost AppHost { get; private set; } = null!;
    public static IServiceProvider Services => AppHost.Services;
    public static CliCommand? PendingLaunchCommand { get; set; }
    public static string AppDataDirectory { get; private set; } = string.Empty;
    public static int StartupCleanedItemsCount { get; set; }

    public App()
    {
        InitializeComponent();

        // 1. Configure Serilog structured logging with test-mode support
        string appDir;
        if (PendingLaunchCommand?.TestMode == true || !string.IsNullOrWhiteSpace(PendingLaunchCommand?.CustomDataDirectory))
        {
            appDir = PendingLaunchCommand?.CustomDataDirectory ?? Path.Combine(Path.GetTempPath(), $"SwiftGet_Test_{Environment.ProcessId}");
        }
        else
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            appDir = Path.Combine(localAppData, "SwiftGet");
        }
        AppDataDirectory = appDir;
        string logDir = Path.Combine(appDir, "logs");
        Directory.CreateDirectory(logDir);

        string qaLogsDir = @"d:\SwiftGet\qa-output\logs";
        try { Directory.CreateDirectory(qaLogsDir); } catch { /* ignore */ }

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(logDir, "swiftget-.log"),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{CorrelationId}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e =>
                    (e.Properties.TryGetValue("SourceContext", out var sc) && sc.ToString().Contains("Torrent", StringComparison.OrdinalIgnoreCase)) ||
                    (e.Properties.TryGetValue("SourceContext", out var msc) && msc.ToString().Contains("MonoTorrent", StringComparison.OrdinalIgnoreCase)) ||
                    e.Properties.ContainsKey("Category") ||
                    e.MessageTemplate.Text.StartsWith('['))
                .WriteTo.File(Path.Combine(logDir, "torrent-.log"),
                    formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

        if (Directory.Exists(qaLogsDir))
        {
            loggerConfig = loggerConfig.WriteTo.File(Path.Combine(qaLogsDir, "swiftget-qa-.log"),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{CorrelationId}] {Message:lj} {Properties:j}{NewLine}{Exception}");
        }

        Log.Logger = loggerConfig.CreateLogger();

        Log.Information("SwiftGet Download Manager starting up (TestMode={TestMode}, AppDir={AppDir})...", 
            PendingLaunchCommand?.TestMode ?? false, appDir);

        // 2. Global Unhandled Exception Handling
        UnhandledException += (sender, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled UI exception caught: {Message}", args.Message);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled AppDomain exception caught");
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled TaskScheduler unobserved task exception caught");
            args.SetObserved();
        };

        // 3. Build DI Host
        string dbPath = Path.Combine(appDir, "swiftget.db");
        AppHost = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                // Storage
                var db = new SqliteDatabase(dbPath);
                services.AddSingleton<ISwiftGetDatabase>(db);
                services.AddSingleton(db);
                services.AddSingleton<IFileNameResolver, FileNameResolver>();

                // Networking & Engine
                services.AddSingleton<HttpClient>(sp => new HttpClient(SwiftGet.Engine.Turbo.TurboSocketFactory.CreateTunableHandler(), disposeHandler: true));
                services.AddSingleton<HttpProbeService>();
                services.AddSingleton<DownloadCoordinator>();
                services.AddSingleton<ICategoryManager, CategoryManager>();
                services.AddSingleton<IAntivirusScanner, AntivirusScanner>();
                services.AddSingleton<IAudioNotificationService, AudioNotificationService>();
                services.AddSingleton<IAfterCompletionService, AfterCompletionService>();
                services.AddSingleton<IQueueManager, QueueManager>();
                services.AddSingleton<IScheduler, DownloadScheduler>();

                // M5 Services: Crawling, Clipboard, Serialization
                services.AddSingleton<IWebCrawler, SiteGrabberEngine>();
                services.AddSingleton<IClipboardMonitor, ClipboardMonitor>();
                services.AddSingleton<IExportService, ExportService>();
                services.AddSingleton<IImportService, ImportService>();

                // M6 Services: Loopback Server & Token Manager & Capture Gatekeeper
                services.AddSingleton<CaptureGatekeeper>();
                services.AddSingleton<CaptureStateMigrationService>();
                services.AddSingleton<LoopbackTokenManager>();
                services.AddSingleton(sp => new LoopbackServer(
                    sp.GetRequiredService<LoopbackTokenManager>(),
                    sp.GetRequiredService<ISwiftGetDatabase>(),
                    sp.GetRequiredService<ICategoryManager>(),
                    sp.GetRequiredService<IFileNameResolver>(),
                    port: PendingLaunchCommand?.CustomLoopbackPort ?? LoopbackServer.DefaultPort,
                    logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<LoopbackServer>>(),
                    gatekeeper: sp.GetRequiredService<CaptureGatekeeper>()));

                // M7 Services: Media Sidecars, Extractor, Stream Recorder
                services.AddSingleton<SidecarManager>();
                services.AddSingleton<IMediaExtractor, YtDlpMediaExtractor>();
                services.AddSingleton<StreamRecorder>();

                // M8 Services: Diagnostics, TLS Security Policy, Localization
                services.AddSingleton<ITlsSecurityPolicy, TlsSecurityPolicy>();
                services.AddSingleton<IDiagnosticService, DiagnosticBundleService>();
                services.AddSingleton<ILocalizationService, LocalizationService>();

                // M9 Services: Auto-Updater
                services.AddSingleton<IUpdateService, UpdateService>();

                // Browser Integration Service
                services.AddSingleton<IBrowserIntegrationService, BrowserIntegrationService>();
                services.AddSingleton<ProcessPriorityService>();

                // T1 & T2 BitTorrent Services
                services.AddSingleton<ITorrentEngine, MonoTorrentEngine>();
                services.AddSingleton<IDownloadDetailsSnapshotService, DownloadDetailsSnapshotService>();
                services.AddSingleton<ISingleInstanceIpc>(sp =>
                    new SingleInstanceIpc(PendingLaunchCommand?.TestMode == true || !string.IsNullOrWhiteSpace(PendingLaunchCommand?.CustomDataDirectory)
                        ? $"SwiftGet_IPC_Test_{Environment.ProcessId}"
                        : null));

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<DownloadDetailsViewModel>();
                services.AddTransient<SchedulerViewModel>();
                services.AddTransient<OptionsViewModel>();
                services.AddTransient<BatchDownloadViewModel>();
                services.AddTransient<GrabberWizardViewModel>();
                services.AddTransient<MediaDownloadViewModel>();
                services.AddTransient<AddTorrentViewModel>();
                services.AddTransient<TorrentDetailsViewModel>();
                services.AddSingleton<DropBoxViewModel>();

                // Views
                services.AddSingleton<MainWindow>();
                services.AddTransient<DropBoxWindow>();
                services.AddTransient<MediaDownloadDialog>();
                services.AddTransient<AddTorrentDialog>();
                services.AddTransient<TorrentDetailsDialog>();
                services.AddTransient<DownloadFileInfoDialog>();
                services.AddTransient<DownloadCompleteDialog>();
            })
            .Build();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Information("Entering OnLaunched...");
        // 1. Initialize core persistence and BitTorrent engine
        try
        {
            Log.Information("Initializing database...");
            var db = Services.GetRequiredService<ISwiftGetDatabase>();
            db.InitializeAsync().GetAwaiter().GetResult();
            Log.Information("Database initialized successfully.");

            Log.Information("Initializing categories...");
            var categories = Services.GetRequiredService<ICategoryManager>();
            categories.InitializeAsync().GetAwaiter().GetResult();
            Log.Information("Categories initialized successfully.");

            // Run startup transient-state purge & one-time phantom capture migration
            CaptureGatekeeper.ProcessStartupUtc = DateTime.UtcNow;
            CaptureGatekeeper.CustomSettingsDirectory = AppDataDirectory;
            var migrationService = Services.GetRequiredService<CaptureStateMigrationService>();
            var migrationResult = migrationService.RunStartupCleanupAsync(AppDataDirectory).GetAwaiter().GetResult();
            if (migrationResult.CleanedCount > 0)
            {
                Log.Information("[CaptureMigration] Cleaned up {Count} stale/phantom capture items (OneTimeMigration={WasOneTime}).",
                    migrationResult.CleanedCount, migrationResult.WasOneTimeMigrationRun);
                StartupCleanedItemsCount = migrationResult.CleanedCount;
            }

            Log.Information("Initializing torrent engine...");
            var torrentEngine = Services.GetRequiredService<ITorrentEngine>();
            var torrentSettingsPath = Path.Combine(AppDataDirectory, "torrent_settings.json");
            var torrentOptions = TorrentSettingsStore.Load(torrentSettingsPath);
            torrentOptions.CacheDirectory = Path.Combine(AppDataDirectory, "torrent_cache");
            if (torrentOptions.RandomizePortOnStartup)
            {
                torrentOptions.ListenPort = Random.Shared.Next(1024, 65535);
            }
            torrentEngine.InitializeAsync(torrentOptions).GetAwaiter().GetResult();
            Log.Information("Torrent engine initialized successfully on port {Port}.", torrentOptions.ListenPort);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize SwiftGet core database/engine services");
        }

        Log.Information("Resolving MainWindow from DI...");
        MainWindow window = Services.GetRequiredService<MainWindow>();
        _currentWindow = window;
        Log.Information("MainWindow resolved.");

        // Start Loopback API server for browser extension & native messaging handoff
        try
        {
            var server = Services.GetRequiredService<LoopbackServer>();
            server.Start();
            server.DownloadIntercepted += (s, req) =>
            {
                _currentWindow?.DispatcherQueue?.TryEnqueue(async () =>
                {
                    if (_currentWindow is MainWindow mw)
                    {
                        mw.BringToForeground();

                        bool isTorrent = (req.Url != null && req.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) ||
                                         (req.FileName != null && req.FileName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) ||
                                         (req.Url != null && req.Url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase));

                        if (isTorrent)
                        {
                            await mw.HandleTorrentRequestAsync(req.Url ?? string.Empty, req.StartImmediately, req.DestinationPath, silent: !req.ShowDialog);
                        }
                        else if (req.ShowDialog)
                        {
                            await mw.ShowDownloadFileInfoDialogAsync(req);
                        }
                        else
                        {
                            await mw.ReloadDownloadsFromDatabaseAsync();
                            if (req.StartImmediately)
                            {
                                var item = mw.ViewModel.AllDownloads.FirstOrDefault(d =>
                                    string.Equals(d.Id.ToString("N"), req.DownloadId, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(d.Url, req.Url, StringComparison.OrdinalIgnoreCase));
                                if (item != null)
                                {
                                    if (item.JobKind == SwiftGet.Core.Models.JobKind.Media || MainWindow.IsStreamingMediaPlatformUrl(item.Url))
                                    {
                                        _ = mw.StartMediaDownloadAsync(item);
                                    }
                                    else
                                    {
                                        _ = mw.StartHttpDownloadAsync(item);
                                    }
                                }
                            }
                        }
                    }
                });
            };

            window.Closed += (s, e) =>
            {
                _ = server.StopAsync();
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start Loopback API server");
        }

        // Start Single-Instance Named Pipe IPC Server for OS protocol & file handoff
        try
        {
            _ = Services.GetService<ProcessPriorityService>();
            var ipc = Services.GetRequiredService<ISingleInstanceIpc>();
            ipc.MessageReceived += (s, msg) =>
            {
                _currentWindow?.DispatcherQueue?.TryEnqueue(async () =>
                {
                    if (_currentWindow is MainWindow mw)
                    {
                        mw.BringToForeground(msg.WindowLeft, msg.WindowTop, msg.WindowWidth, msg.WindowHeight);

                        if (msg.Action == "AddTorrent")
                        {
                            await mw.HandleTorrentRequestAsync(msg.Argument, msg.StartImmediately, msg.SaveDirectory, msg.Silent);
                        }
                        else if (msg.Action == "AddUrl" || msg.Action == "AddDownload")
                        {
                            if (msg.Silent)
                            {
                                await mw.ReloadDownloadsFromDatabaseAsync();
                            }
                            else
                            {
                                var gatekeeper = AppHost.Services.GetRequiredService<CaptureGatekeeper>();
                                var gateResult = gatekeeper.EvaluateCapture(
                                    url: msg.Argument,
                                    fileName: msg.FileName,
                                    eventId: msg.EventId ?? msg.CorrelationId,
                                    createdAtUtc: msg.CreatedAtUtc,
                                    source: "ipc",
                                    tabId: msg.TabId,
                                    initiator: msg.Initiator,
                                    userGesture: msg.UserGesture,
                                    fileSizeBytes: msg.TotalBytes > 0 ? msg.TotalBytes : null,
                                    enforceStartupTimeGuard: msg.CreatedAtUtc.HasValue,
                                    isExplicitUserAction: msg.ExplicitUserAction);

                                if (!gateResult.Allowed)
                                {
                                    Log.Information("[CaptureGatekeeper] Dropped IPC capture for '{Url}': {Reason}", msg.Argument, gateResult.Reason);
                                    return;
                                }

                                var resolver = AppHost.Services.GetRequiredService<IFileNameResolver>();
                                var fileName = !string.IsNullOrWhiteSpace(msg.FileName)
                                    ? msg.FileName
                                    : resolver.ResolveInitialCandidate(msg.Argument, msg.SaveDirectory);

                                await mw.ShowDownloadFileInfoDialogAsync(new SwiftGet.Engine.Server.InterceptDownloadRequest
                                {
                                    DownloadId = msg.DownloadId,
                                    EventId = gateResult.EventId,
                                    CreatedAtUtc = msg.CreatedAtUtc,
                                    TabId = msg.TabId,
                                    Initiator = msg.Initiator,
                                    UserGesture = msg.UserGesture,
                                    ExplicitUserAction = msg.ExplicitUserAction,
                                    Url = msg.Argument,
                                    FileName = fileName,
                                    DestinationPath = msg.SaveDirectory,
                                    Cookies = msg.Cookies,
                                    Referrer = msg.Referrer,
                                    UserAgent = msg.UserAgent,
                                    StartImmediately = msg.StartImmediately,
                                    ShowDialog = true,
                                    WindowLeft = msg.WindowLeft,
                                    WindowTop = msg.WindowTop,
                                    WindowWidth = msg.WindowWidth,
                                    WindowHeight = msg.WindowHeight,
                                    TotalBytes = msg.TotalBytes
                                });
                            }
                        }
                        else if (msg.Action == "Activate")
                        {
                            mw.BringToForeground(msg.WindowLeft, msg.WindowTop, msg.WindowWidth, msg.WindowHeight);
                        }
                        else
                        {
                            await mw.ReloadDownloadsFromDatabaseAsync();
                        }
                    }
                });
            };
            _ = ipc.StartServerAsync();

            window.Closed += async (s, e) =>
            {
                await ipc.DisposeAsync();
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize SingleInstance IPC listener");
        }

        // Dispatch pending launch command if cold-started with CLI arguments
        if (PendingLaunchCommand != null && !string.IsNullOrWhiteSpace(PendingLaunchCommand.Url))
        {
            var pending = PendingLaunchCommand;
            PendingLaunchCommand = null;

            _currentWindow?.DispatcherQueue?.TryEnqueue(async () =>
            {
                if (_currentWindow is MainWindow mw)
                {
                    if (pending.IsTorrent)
                    {
                        await mw.HandleTorrentRequestAsync(pending.Url!, pending.StartImmediately, pending.DestinationPath, pending.Quiet);
                    }
                    else if (!pending.Quiet)
                    {
                        await mw.ShowDownloadFileInfoDialogAsync(new SwiftGet.Engine.Server.InterceptDownloadRequest
                        {
                            Url = pending.Url!,
                            FileName = pending.FileName,
                            DestinationPath = pending.DestinationPath,
                            StartImmediately = pending.StartImmediately,
                            ShowDialog = true
                        });
                    }
                    else
                    {
                        await mw.ReloadDownloadsFromDatabaseAsync();
                    }
                }
            });
        }

        Log.Information("Calling window.Activate()...");
        window.Activate();
        Log.Information("window.Activate() completed.");
    }
}
