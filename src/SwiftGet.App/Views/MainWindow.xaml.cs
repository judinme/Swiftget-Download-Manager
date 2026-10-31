using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.IO;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Coordinator;
using SwiftGet.Engine.Clipboard;
using SwiftGet.Engine.Diagnostics;
using SwiftGet.Engine.Notifications;
using SwiftGet.Engine.Probing;
using SwiftGet.Engine.Queues;
using SwiftGet.Engine.Serialization;
using SwiftGet.Engine.Torrent;
using SwiftGet.Media.Recorder;
using Windows.ApplicationModel.DataTransfer;

namespace SwiftGet.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WinUI 3 Window lifecycle is managed via Closed event")]
public sealed partial class MainWindow : Window
{
    private DropBoxWindow? _dropBoxWindow;
    private Services.SystemTrayManager? _trayManager;
    private Microsoft.UI.Windowing.AppWindow? _appWindow;
    private bool _isExplicitExit;
    private bool _isSnailMode;
    private bool _isHighPriority = true;
    private bool _isDragOver;
    private string _currentSortMode = "Newest";
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeDownloads = new();
    private readonly SemaphoreSlim _contentDialogSemaphore = new(1, 1);
    private readonly DispatcherTimer _telemetryTimer = new();
    public MainViewModel ViewModel { get; }
    public DownloadDetailsViewModel DetailsViewModel { get; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const int SwRestore = 9;

    public async Task<ContentDialogResult> ShowContentDialogSafeAsync(ContentDialog dialog)
    {
        await _contentDialogSemaphore.WaitAsync();
        try
        {
            if (Content?.XamlRoot == null)
            {
                Serilog.Log.Warning("Cannot show ContentDialog: Content or XamlRoot is null.");
                return ContentDialogResult.None;
            }

            dialog.XamlRoot = Content.XamlRoot;
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to show ContentDialog of type {Type}", dialog.GetType().Name);
            return ContentDialogResult.None;
        }
        finally
        {
            _contentDialogSemaphore.Release();
        }
    }

    private async void DownloadListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
            UpdateCategoryBadges();
            UpdateInspectorDetails(ViewModel.SelectedItem);
            e.Handled = true;
        }
    }

    public async Task ReloadDownloadsFromDatabaseAsync()
    {
        var db = App.Services.GetRequiredService<ISwiftGetDatabase>();
        var items = await db.GetAllDownloadsAsync();
        var existingActiveVms = ViewModel.AllDownloads
            .Where(vm => _activeDownloads.ContainsKey(vm.Id))
            .ToDictionary(vm => vm.Id);

        ViewModel.ClearAll();
        var vms = items.Select(d =>
        {
            var gid = Guid.TryParse(d.Id, out var parsed) ? parsed : Guid.NewGuid();
            if (existingActiveVms.TryGetValue(gid, out var activeVm))
            {
                return activeVm;
            }

            var pct = d.Status == DownloadStatus.Completed
                ? 100.0
                : Math.Round(d.ProgressPercentage, 1);

            return new DownloadItemViewModel
            {
                Id = gid,
                JobKind = d.JobKind,
                InfoHash = d.InfoHash ?? string.Empty,
                FileName = d.FileName,
                Url = d.Url,
                SavePath = d.DestinationPath,
                TotalBytes = d.TotalBytes,
                DownloadedBytes = d.DownloadedBytes,
                ProgressPercentage = pct,
                Status = d.Status,
                Category = d.CategoryId ?? "all",
                QueueName = d.QueueId ?? "default"
            };
        });
        ViewModel.AddDownloads(vms);
        UpdateCategoryBadges();
        if (ViewModel.SelectedItem != null && !ViewModel.AllDownloads.Contains(ViewModel.SelectedItem))
        {
            ViewModel.SelectedItem = null;
        }
        UpdateInspectorDetails(ViewModel.SelectedItem);
        BuildChunkVisualizer(ViewModel.SelectedItem);

        // Restore active/paused torrents in engine if not already registered
        var torrentEngine = App.Services.GetService<ITorrentEngine>();
        if (torrentEngine != null)
        {
            foreach (var d in items.Where(x => x.JobKind == JobKind.Torrent))
            {
                if (!torrentEngine.HasJob(d.Id))
                {
                    _ = RestoreTorrentAsync(torrentEngine, db, d);
                }
            }
        }
    }

    private async Task RestoreTorrentAsync(ITorrentEngine torrentEngine, ISwiftGetDatabase db, DownloadItem d)
    {
        try
        {
            var saveDir = !string.IsNullOrWhiteSpace(d.DestinationPath)
                ? (Directory.Exists(d.DestinationPath) ? d.DestinationPath : Path.GetDirectoryName(d.DestinationPath) ?? d.DestinationPath)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            bool startImmediately = d.Status is DownloadStatus.Downloading or DownloadStatus.Seeding;

            var addOptions = new TorrentAddOptions
            {
                DownloadId = d.Id,
                SaveDirectory = saveDir,
                StartImmediately = startImmediately
            };

            var torrentRecord = await db.GetTorrentAsync(d.Id).ConfigureAwait(false);
            var source = !string.IsNullOrWhiteSpace(torrentRecord?.MagnetUri) ? torrentRecord.MagnetUri : d.Url;

            if (!string.IsNullOrWhiteSpace(source))
            {
                if (source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
                {
                    await torrentEngine.AddMagnetAsync(source, addOptions).ConfigureAwait(false);
                }
                else if (File.Exists(source))
                {
                    var bytes = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
                    await torrentEngine.AddTorrentFileAsync(bytes, addOptions).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to restore torrent {Id} in engine", d.Id);
        }
    }

    public MainWindow(MainViewModel viewModel, DownloadDetailsViewModel detailsViewModel)
    {
        Serilog.Log.Information("MainWindow constructor starting...");
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DetailsViewModel = detailsViewModel ?? throw new ArgumentNullException(nameof(detailsViewModel));
        InitializeComponent();
        Serilog.Log.Information("MainWindow InitializeComponent completed.");
        Title = "SwiftGet Download Manager";

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Custom title bar integration skipped");
        }

        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (_appWindow != null)
            {
                _appWindow.Title = "SwiftGet Download Manager";

                // DPI-aware sizing: Calculate scaling factor based on the window's monitor DPI.
                // Standard default compact client dimensions: 680 DIPs width x 460 DIPs height.
                // On 125% DPI (120 dpi), target client size = 850 x 575 physical pixels, matching UI 2 perfectly.
                uint dpi = GetDpiForWindow(hWnd);
                double scale = dpi > 0 ? (dpi / 96.0) : 1.25;

                int targetClientW = Math.Max(840, (int)Math.Round(680 * scale));
                int targetClientH = Math.Max(560, (int)Math.Round(460 * scale));

                _appWindow.ResizeClient(new Windows.Graphics.SizeInt32(targetClientW, targetClientH));
                _appWindow.Changed += (s, args) =>
                {
                    if (args.DidSizeChange)
                    {
                        var clientSize = s.ClientSize;
                        if (clientSize.Width <= 0 || clientSize.Height <= 0)
                        {
                            return;
                        }

                        int minClientW = Math.Max(640, (int)Math.Round(580 * scale));
                        int minClientH = Math.Max(420, (int)Math.Round(380 * scale));
                        int clampedW = Math.Max(minClientW, clientSize.Width);
                        int clampedH = Math.Max(minClientH, clientSize.Height);
                        if (clampedW != clientSize.Width || clampedH != clientSize.Height)
                        {
                            s.ResizeClient(new Windows.Graphics.SizeInt32(clampedW, clampedH));
                        }
                    }
                };
                if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    var titleBar = _appWindow.TitleBar;
                    titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                    titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                    titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 204, 204, 204);
                    titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 44, 44, 44);
                    titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                }
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(iconPath))
                {
                    _appWindow.SetIcon(iconPath);
                }

                _appWindow.Closing += (sender, args) =>
                {
                    if (!_isExplicitExit)
                    {
                        args.Cancel = true;
                        _appWindow.Hide();
                        _trayManager?.ShowNotification("SwiftGet", "SwiftGet is minimized to the system tray and continuing in the background.");
                    }
                };
            }

            _trayManager = new Services.SystemTrayManager(hWnd, DispatcherQueue);
            _trayManager.RestoreRequested += RestoreMainWindow;
            _trayManager.AddDownloadRequested += () => AddDownload_Click(this, new RoutedEventArgs());
            _trayManager.ToggleDropBoxRequested += () => DropTarget_Click(this, new RoutedEventArgs());
            _trayManager.ExitRequested += ExitApplication;
            _trayManager.Initialize();

            Closed += (s, e) =>
            {
                foreach (var kvp in _activeDownloads)
                {
                    try { kvp.Value.Cancel(); kvp.Value.Dispose(); } catch { }
                }
                _activeDownloads.Clear();
                _telemetryTimer.Stop();
                _trayManager?.Dispose();
            };

            Serilog.Log.Information("MainWindow AppWindow icon/title and SystemTrayManager initialized.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to initialize AppWindow or SystemTrayManager");
        }

        ViewModel.ResumeHandler = async (item) =>
        {
            if (item.JobKind == JobKind.Torrent)
            {
                var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                var db = App.Services.GetRequiredService<ISwiftGetDatabase>();
                string jobId = item.Id.ToString("N");
                if (!torrentEngine.HasJob(jobId))
                {
                    var dbItem = await db.GetDownloadAsync(jobId);
                    if (dbItem != null)
                    {
                        await RestoreTorrentAsync(torrentEngine, db, dbItem);
                    }
                }
                await torrentEngine.StartAsync(jobId);
                item.Status = DownloadStatus.Downloading;
            }
            else if (item.JobKind == JobKind.Media || IsStreamingMediaPlatformUrl(item.Url))
            {
                await StartMediaDownloadAsync(item);
            }
            else
            {
                await StartHttpDownloadAsync(item);
            }
            UpdateCategoryBadges();
            UpdateInspectorDetails(item);
        };

        ViewModel.StopHandler = (item) =>
        {
            if (item.JobKind == JobKind.Torrent)
            {
                var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                _ = torrentEngine.StopAsync(item.Id.ToString("N"));
                item.Status = DownloadStatus.Paused;
                item.SpeedBytesPerSec = 0;
                item.UploadSpeedBytesPerSec = 0;
            }
            else
            {
                if (_activeDownloads.TryRemove(item.Id, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                item.Status = DownloadStatus.Paused;
                item.SpeedBytesPerSec = 0;
                item.RemainingTime = TimeSpan.Zero;
            }
            UpdateCategoryBadges();
            UpdateInspectorDetails(item);
        };

        ViewModel.StopAllHandler = () =>
        {
            foreach (var kvp in _activeDownloads)
            {
                try { kvp.Value.Cancel(); kvp.Value.Dispose(); } catch { }
            }
            _activeDownloads.Clear();

            try
            {
                var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                foreach (var d in ViewModel.AllDownloads.Where(x => x.JobKind == JobKind.Torrent))
                {
                    _ = torrentEngine.StopAsync(d.Id.ToString("N"));
                }
            }
            catch { }
            UpdateCategoryBadges();
        };

        ViewModel.DeleteHandler = async (item) =>
        {
            if (item.JobKind == JobKind.Torrent)
            {
                try
                {
                    var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                    await torrentEngine.RemoveAsync(item.Id.ToString("N"), deleteFiles: false);
                }
                catch { }
            }
            else
            {
                if (_activeDownloads.TryRemove(item.Id, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
            UpdateCategoryBadges();
        };

        try
        {
            var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
            torrentEngine.MetadataReceived += (sender, args) =>
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    var item = ViewModel.AllDownloads.FirstOrDefault(d =>
                        d.Id.ToString("N").Equals(args.JobId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.InfoHash, args.InfoHash, StringComparison.OrdinalIgnoreCase));

                    if (item != null)
                    {
                        if (!string.IsNullOrWhiteSpace(args.Name))
                        {
                            item.FileName = args.Name;
                        }
                        if (args.TotalBytes > 0)
                        {
                            item.TotalBytes = args.TotalBytes;
                        }
                        item.Status = DownloadStatus.Downloading;

                        try
                        {
                            var db = App.Services.GetRequiredService<ISwiftGetDatabase>();
                            var dbItem = await db.GetDownloadAsync(item.Id.ToString("N"));
                            if (dbItem != null)
                            {
                                dbItem.FileName = item.FileName;
                                dbItem.TotalBytes = item.TotalBytes;
                                dbItem.Status = DownloadStatus.Downloading;
                                await db.SaveDownloadAsync(dbItem);
                            }

                            var torrentRecord = await db.GetTorrentAsync(item.Id.ToString("N"));
                            if (torrentRecord != null)
                            {
                                torrentRecord.Name = item.FileName;
                                torrentRecord.TotalBytes = item.TotalBytes;
                                await db.SaveTorrentAsync(torrentRecord);
                            }

                            if (args.Files != null && args.Files.Count > 0)
                            {
                                var fileItems = args.Files.Select(f => new TorrentFileItem
                                {
                                    Id = Guid.NewGuid().ToString("N"),
                                    TorrentId = item.Id.ToString("N"),
                                    FileIndex = f.FileIndex,
                                    RelativePath = f.RelativePath,
                                    FileSizeBytes = f.FileSizeBytes,
                                    DownloadedBytes = f.DownloadedBytes,
                                    Priority = f.Priority,
                                    IsSelected = f.IsSelected
                                }).ToList();
                                await db.SaveTorrentFilesAsync(item.Id.ToString("N"), fileItems);
                            }
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "Failed to persist updated torrent metadata for {Id}", item.Id);
                        }

                        if (DetailsViewModel.IsOpen && ViewModel.SelectedItem == item)
                        {
                            _ = DetailsViewModel.RefreshSnapshotAsync(item, forceRefresh: true);
                        }
                    }
                });
            };

            torrentEngine.StateChanged += (sender, args) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var item = ViewModel.AllDownloads.FirstOrDefault(d =>
                        d.Id.ToString("N").Equals(args.JobId, StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                    {
                        item.Status = args.NewState;
                    }
                });
            };

            var coordinator = App.Services.GetService<DownloadCoordinator>();
            if (coordinator != null)
            {
                coordinator.FileNameUpdated += (sender, args) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var item = ViewModel.AllDownloads.FirstOrDefault(d =>
                            d.Id.ToString("N").Equals(args.DownloadId, StringComparison.OrdinalIgnoreCase));
                        if (item != null)
                        {
                            item.FileName = args.NewFileName;
                            item.SavePath = args.NewDestinationPath;
                            UpdateCategoryBadges();
                            if (ViewModel.SelectedItem == item)
                            {
                                UpdateInspectorDetails(item);
                                if (DetailsViewModel.IsOpen)
                                {
                                    _ = DetailsViewModel.RefreshSnapshotAsync(item, forceRefresh: true);
                                }
                            }
                        }
                    });
                };

                coordinator.ConnectionsUpdated += (sender, args) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var item = ViewModel.AllDownloads.FirstOrDefault(d =>
                            d.Id.ToString("N").Equals(args.DownloadId, StringComparison.OrdinalIgnoreCase));
                        if (item != null && args.Connections.Count > 0)
                        {
                            for (int i = 0; i < args.Connections.Count; i++)
                            {
                                var conn = args.Connections[i];
                                if (i < item.Segments.Count)
                                {
                                    var seg = item.Segments[i];
                                    seg.Index = conn.Index;
                                    seg.Status = conn.Status;
                                    seg.StartOffset = conn.StartOffset;
                                    seg.EndOffset = conn.EndOffset;
                                    seg.Update(conn.DownloadedBytes, conn.SpeedBytesPerSec);
                                    seg.UpdateMetrics(item.TotalBytes);
                                }
                                else
                                {
                                    var seg = new SegmentProgressViewModel(conn.Index, conn.StartOffset, conn.EndOffset)
                                    {
                                        Status = conn.Status
                                    };
                                    seg.Update(conn.DownloadedBytes, conn.SpeedBytesPerSec);
                                    seg.UpdateMetrics(item.TotalBytes);
                                    item.Segments.Add(seg);
                                }
                            }

                            while (item.Segments.Count > args.Connections.Count && item.Segments.Count > 1)
                            {
                                item.Segments.RemoveAt(item.Segments.Count - 1);
                            }
                        }
                    });
                };
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to wire engine events in MainWindow");
        }

        if (Content is FrameworkElement fe)
        {
            fe.Loaded += async (s, e) =>
            {
                Serilog.Log.Information("MainWindow FrameworkElement Loaded event triggered.");
                await ReloadDownloadsFromDatabaseAsync();
                UpdateCategoryBadges();
                BuildChunkVisualizer();
                Serilog.Log.Information("MainWindow ReloadDownloadsFromDatabaseAsync completed.");
            };
        }

        BuildChunkVisualizer();

        _telemetryTimer.Interval = TimeSpan.FromMilliseconds(500);
        _telemetryTimer.Tick += TelemetryTimer_Tick;
        _telemetryTimer.Start();

        InitializeClipboardMonitoring();

        Serilog.Log.Information("MainWindow constructor completed.");
    }

    private void InitializeClipboardMonitoring()
    {
        try
        {
            var clipboardMonitor = App.Services.GetService<IClipboardMonitor>();
            if (clipboardMonitor != null)
            {
                clipboardMonitor.UrlDetected += (s, args) =>
                {
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (args.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || args.Url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                        {
                            await HandleTorrentRequestAsync(args.Url);
                        }
                    });
                };

                try
                {
                    Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += (s, e) =>
                    {
                        try
                        {
                            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var text = await dataPackage.GetTextAsync();
                                        clipboardMonitor.ProcessClipboardText(text);
                                    }
                                    catch { }
                                });
                            }
                        }
                        catch { }
                    };
                }
                catch { }
            }
        }
        catch { }
    }

    public void UpdateCategoryBadges()
    {
        int allCount = ViewModel.AllDownloads.Count;
        int videoCount = ViewModel.AllDownloads.Count(d => string.Equals(d.Category, "video", StringComparison.OrdinalIgnoreCase) || d.JobKind == JobKind.Media);
        int compressedCount = ViewModel.AllDownloads.Count(d => string.Equals(d.Category, "compressed", StringComparison.OrdinalIgnoreCase));
        int programsCount = ViewModel.AllDownloads.Count(d => string.Equals(d.Category, "programs", StringComparison.OrdinalIgnoreCase));
        int documentsCount = ViewModel.AllDownloads.Count(d => string.Equals(d.Category, "documents", StringComparison.OrdinalIgnoreCase));
        int musicCount = ViewModel.AllDownloads.Count(d => string.Equals(d.Category, "music", StringComparison.OrdinalIgnoreCase));
        int torrentsCount = ViewModel.AllDownloads.Count(d => d.JobKind == JobKind.Torrent);
        int unfinishedCount = ViewModel.AllDownloads.Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.FetchingMetadata or DownloadStatus.Checking or DownloadStatus.Failed);
        int completedCount = ViewModel.AllDownloads.Count(d => d.Status is DownloadStatus.Completed or DownloadStatus.Seeding);

        if (FilterItemAll != null) FilterItemAll.Text = allCount > 0 ? $"All Downloads ({allCount})" : "All Downloads";
        if (FilterItemVideo != null) FilterItemVideo.Text = videoCount > 0 ? $"Video / Media ({videoCount})" : "Video / Media";
        if (FilterItemCompressed != null) FilterItemCompressed.Text = compressedCount > 0 ? $"Compressed ({compressedCount})" : "Compressed";
        if (FilterItemPrograms != null) FilterItemPrograms.Text = programsCount > 0 ? $"Programs ({programsCount})" : "Programs";
        if (FilterItemDocuments != null) FilterItemDocuments.Text = documentsCount > 0 ? $"Documents ({documentsCount})" : "Documents";
        if (FilterItemMusic != null) FilterItemMusic.Text = musicCount > 0 ? $"Music ({musicCount})" : "Music";
        if (FilterItemTorrents != null) FilterItemTorrents.Text = torrentsCount > 0 ? $"Torrents ({torrentsCount})" : "Torrents";
        if (FilterItemUnfinished != null) FilterItemUnfinished.Text = unfinishedCount > 0 ? $"Unfinished ({unfinishedCount})" : "Unfinished";
        if (FilterItemFinished != null) FilterItemFinished.Text = completedCount > 0 ? $"Finished ({completedCount})" : "Finished";

        UpdateEmptyStateVisibility();
    }

    private void UpdateEmptyStateVisibility()
    {
        if (EmptyStateOverlay == null || WorkspaceDashedBorder == null || EmptyStateText == null || EmptyStateIcon == null)
        {
            return;
        }

        if (_isDragOver)
        {
            EmptyStateOverlay.Visibility = Visibility.Visible;
            EmptyStateText.Text = "Drop URL or torrent file to download";
            var accentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x59, 0xB9, 0xF7));
            EmptyStateText.Foreground = accentBrush;
            EmptyStateIcon.Foreground = accentBrush;
            WorkspaceDashedBorder.Stroke = accentBrush;
        }
        else
        {
            bool isEmpty = ViewModel.FilteredDownloads.Count == 0;
            EmptyStateOverlay.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = "Drag-and-drop URL or torrent file";
            var mutedBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x60, 0x60, 0x60));
            EmptyStateText.Foreground = mutedBrush;
            EmptyStateIcon.Foreground = mutedBrush;
            WorkspaceDashedBorder.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2E, 0x2E, 0x2E));
        }
    }

    private void BuildChunkVisualizer(DownloadItemViewModel? item = null)
    {
    }

    private void UpdateInspectorDetails(DownloadItemViewModel? item)
    {
        if (item == null)
        {
            if (FooterSelectedTicker != null)
            {
                FooterSelectedTicker.Text = string.Empty;
                FooterSelectedTicker.Visibility = Visibility.Collapsed;
            }
            if (FooterSelectedSeparator != null)
            {
                FooterSelectedSeparator.Visibility = Visibility.Collapsed;
            }
            DetailsViewModel.SetSelectionState(null, 0);
            return;
        }

        if (FooterSelectedTicker != null)
        {
            FooterSelectedTicker.Text = item.FileName;
            FooterSelectedTicker.Visibility = Visibility.Visible;
        }
        if (FooterSelectedSeparator != null)
        {
            FooterSelectedSeparator.Visibility = Visibility.Visible;
        }

        int selectedCount = ViewModel.FilteredDownloads.Count(d => d.IsSelected);
        if (selectedCount == 0) selectedCount = 1;

        DetailsViewModel.SetSelectionState(item, selectedCount);
        if (DetailsViewModel.IsOpen)
        {
            _ = DetailsViewModel.RefreshSnapshotAsync(item);
        }
    }

    private void TelemetryTimer_Tick(object? sender, object e)
    {
        if (ViewModel.SelectedItem != null && !ViewModel.AllDownloads.Contains(ViewModel.SelectedItem))
        {
            ViewModel.SelectedItem = null;
            UpdateInspectorDetails(null);
        }
        else if (ViewModel.AllDownloads.Count == 0 && (FooterSelectedTicker?.Visibility == Visibility.Visible || FooterSelectedTicker?.Text?.Length > 0))
        {
            ViewModel.SelectedItem = null;
            UpdateInspectorDetails(null);
        }

        UpdateActiveTorrentTelemetry();
        ViewModel.UpdateMetrics();
        if (DetailsViewModel.IsOpen && ViewModel.SelectedItem != null)
        {
            _ = DetailsViewModel.RefreshSnapshotAsync(ViewModel.SelectedItem);
        }
        UpdateFooterTelemetry();
        UpdateEmptyStateVisibility();
    }

    private void UpdateFooterTelemetry()
    {
        double downSpeed = ViewModel.AllDownloads
            .Where(d => d.Status is DownloadStatus.Downloading)
            .Sum(d => d.SpeedBytesPerSec);

        double upSpeed = ViewModel.AllDownloads
            .Where(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Seeding)
            .Sum(d => d.UploadSpeedBytesPerSec);

        if (FooterDownSpeed != null)
        {
            FooterDownSpeed.Text = DownloadItemViewModel.FormatSpeed(downSpeed);
        }

        if (FooterUpSpeed != null)
        {
            FooterUpSpeed.Text = DownloadItemViewModel.FormatSpeed(upSpeed);
        }
    }

    private void UpdateActiveTorrentTelemetry()
    {
        var activeTorrents = ViewModel.AllDownloads.Where(d => d.JobKind == JobKind.Torrent &&
            (d.Status is DownloadStatus.Downloading or DownloadStatus.Seeding or DownloadStatus.FetchingMetadata or DownloadStatus.Checking)).ToList();

        if (activeTorrents.Count == 0) return;

        try
        {
            var engine = App.Services.GetService<ITorrentEngine>();
            if (engine == null) return;

            foreach (var t in activeTorrents)
            {
                var stats = engine.GetJobStats(t.Id.ToString("N")) ?? engine.GetJobStats(t.InfoHash);
                if (stats != null)
                {
                    long total = stats.TotalBytes > 0 ? stats.TotalBytes : t.TotalBytes;
                    t.UpdateTorrentProgress(
                        stats.DownloadedBytes,
                        total,
                        stats.DownloadSpeedBytesPerSec,
                        stats.UploadSpeedBytesPerSec,
                        stats.UploadedBytes,
                        stats.ConnectedSeeds,
                        stats.ConnectedPeers,
                        stats.State);

                    if (stats.HasMetadata && stats.TotalBytes > 0 && (t.Status == DownloadStatus.FetchingMetadata || t.TotalBytes <= 0 || t.FileName == "Fetching metadata..." || t.FileName.StartsWith("Torrent_", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!string.IsNullOrWhiteSpace(stats.Name)) t.FileName = stats.Name;
                        t.TotalBytes = stats.TotalBytes;
                        if (t.Status == DownloadStatus.FetchingMetadata)
                        {
                            t.Status = DownloadStatus.Downloading;
                            var audio = App.Services.GetService<IAudioNotificationService>();
                            audio?.PlaySound(SoundNotificationType.DownloadComplete);
                        }
                    }

                    if (stats.State == DownloadStatus.Seeding)
                    {
                        double ratio = stats.DownloadedBytes > 0 ? (double)stats.UploadedBytes / stats.DownloadedBytes : 0;
                        if (engine.Options.DefaultSeedRatio > 0 && ratio >= engine.Options.DefaultSeedRatio)
                        {
                            _ = engine.StopAsync(t.Id.ToString("N"));
                            t.Status = DownloadStatus.Completed;
                            t.SpeedBytesPerSec = 0;
                            t.UploadSpeedBytesPerSec = 0;
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void UpdateConnectionTelemetry(DownloadItemViewModel? item)
    {
    }

    private void DownloadsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DownloadsListView.SelectedItem is DownloadItemViewModel item)
        {
            ViewModel.SelectedItem = item;
            DetailsViewModel.IsOpen = true;
            UpdateInspectorDetails(item);
        }
        else
        {
            ViewModel.SelectedItem = null;
            UpdateInspectorDetails(null);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.SearchFilter = sender.Text;
        ApplyCurrentSort();
        UpdateCategoryBadges();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list && list.SelectedItem is ListViewItem item && item.Tag is string tag)
        {
            ApplyCategorySelection(tag);
        }
    }

    private void QuickCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ApplyCategorySelection(tag);
        }
    }

    private void FilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string tag)
        {
            ApplyCategorySelection(tag);
        }
    }

    private void ApplyCategorySelection(string tag)
    {
        ViewModel.SelectCategory(tag);
        ApplyCurrentSort();
        if (FilterButtonLabel != null)
        {
            FilterButtonLabel.Text = tag.ToLowerInvariant() switch
            {
                "all" => "All files",
                "video" => "Video / Media",
                "compressed" => "Compressed",
                "programs" => "Programs",
                "documents" => "Documents",
                "music" => "Music",
                "torrents" => "Torrents",
                "unfinished" => "Unfinished",
                "finished" => "Finished",
                "queue_main" => "Main Queue (Q1)",
                "grabber" => "Grabber projects",
                _ => "All files"
            };
        }
        UpdateCategoryBadges();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Content?.ToString() is string sortMode)
        {
            _currentSortMode = sortMode;
            if (SortButtonLabel != null) SortButtonLabel.Text = sortMode;
            ApplyCurrentSort();
        }
    }

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string sortMode)
        {
            _currentSortMode = sortMode;
            if (SortButtonLabel != null) SortButtonLabel.Text = sortMode;
            ApplyCurrentSort();
        }
    }

    private void ApplyCurrentSort()
    {
        if (ViewModel == null) return;
        var current = ViewModel.FilteredDownloads.ToList();
        switch (_currentSortMode)
        {
            case "Largest Size":
                current = current.OrderByDescending(d => d.TotalBytes).ToList();
                break;
            case "Status":
                current = current.OrderBy(d => d.Status.ToString()).ToList();
                break;
            case "File Name":
                current = current.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).ToList();
                break;
            case "Newest":
            default:
                current = current.OrderByDescending(d => d.LastTryDate).ToList();
                break;
        }
        ViewModel.FilteredDownloads = new ObservableCollection<DownloadItemViewModel>(current);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.FilteredDownloads)
        {
            item.IsSelected = true;
        }
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.AllDownloads)
        {
            item.IsSelected = false;
        }
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.FilteredDownloads)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    private async void StartAllResume_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null && ViewModel.SelectedItem.Status is DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.Failed)
        {
            await ViewModel.ResumeSelectedCommand.ExecuteAsync(null);
            UpdateInspectorDetails(ViewModel.SelectedItem);
            return;
        }

        var resumable = ViewModel.AllDownloads
            .Where(d => d.Status is DownloadStatus.Paused or DownloadStatus.Queued)
            .ToList();
        foreach (var item in resumable)
        {
            ViewModel.SelectedItem = item;
            await ViewModel.ResumeSelectedCommand.ExecuteAsync(null);
        }

        StartQueue_Click(sender, e);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        // Sidebar consolidated into FilterDropDown in compact mode
    }

    private void ShowBottomInspector(bool show)
    {
        DetailsViewModel.IsOpen = show;
        if (ShowInspectorMenuItem != null)
        {
            ShowInspectorMenuItem.IsChecked = show;
        }
        if (show && ViewModel.SelectedItem != null)
        {
            _ = DetailsViewModel.RefreshSnapshotAsync(ViewModel.SelectedItem);
        }
    }

    private void ToggleInspector_Click(object sender, RoutedEventArgs e)
    {
        ShowBottomInspector(!DetailsViewModel.IsOpen);
    }

    private void InspectorTab_Checked(object sender, RoutedEventArgs e)
    {
    }

    private async void ResumeSelected_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResumeSelectedCommand.ExecuteAsync(null);
        if (ViewModel.SelectedItem != null) UpdateInspectorDetails(ViewModel.SelectedItem);
    }

    private void PauseSelected_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StopSelectedCommand.Execute(null);
        if (ViewModel.SelectedItem != null) UpdateInspectorDetails(ViewModel.SelectedItem);
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StopAllCommand.Execute(null);
        if (ViewModel.SelectedItem != null) UpdateInspectorDetails(ViewModel.SelectedItem);
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var checkedItems = ViewModel.AllDownloads.Where(d => d.IsSelected).ToList();
        if (checkedItems.Count > 0)
        {
            foreach (var item in checkedItems)
            {
                ViewModel.SelectedItem = item;
                await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
            }
        }
        else
        {
            await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
        }
        UpdateCategoryBadges();
        UpdateInspectorDetails(ViewModel.SelectedItem);
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteCompletedCommand.ExecuteAsync(null);
        UpdateCategoryBadges();
        UpdateInspectorDetails(ViewModel.SelectedItem);
    }

    private static DownloadItemViewModel? ResolveRowItem(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is DownloadItemViewModel dcItem)
                return dcItem;
            if (fe.Tag is DownloadItemViewModel tagItem)
                return tagItem;
        }
        return null;
    }

    private async void RowResumePause_Click(object sender, RoutedEventArgs e)
    {
        var item = ResolveRowItem(sender);
        if (item != null)
        {
            ViewModel.SelectedItem = item;
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.FetchingMetadata or DownloadStatus.Checking or DownloadStatus.Seeding)
            {
                ViewModel.StopSelectedCommand.Execute(null);
            }
            else
            {
                await ViewModel.ResumeSelectedCommand.ExecuteAsync(null);
            }
            UpdateInspectorDetails(item);
        }
    }

    private void RowStop_Click(object sender, RoutedEventArgs e)
    {
        var item = ResolveRowItem(sender);
        if (item != null)
        {
            ViewModel.SelectedItem = item;
            ViewModel.StopSelectedCommand.Execute(null);
            UpdateInspectorDetails(item);
        }
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        var item = ResolveRowItem(sender);
        if (item != null)
        {
            ViewModel.SelectedItem = item;
            await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
            UpdateCategoryBadges();
            UpdateInspectorDetails(ViewModel.SelectedItem);
        }
    }

    private void RowInspector_Click(object sender, RoutedEventArgs e)
    {
        var item = ResolveRowItem(sender);
        if (item != null)
        {
            bool wasSameItem = ReferenceEquals(ViewModel.SelectedItem, item);
            ViewModel.SelectedItem = item;
            UpdateInspectorDetails(item);

            bool currentlyOpen = DetailsViewModel.IsOpen;
            ShowBottomInspector(!currentlyOpen || !wasSameItem);
        }
    }

    private async void SnailModeBtn_Click(object sender, RoutedEventArgs e)
    {
        _isSnailMode = !_isSnailMode;
        var torrentEngine = App.Services.GetService<ITorrentEngine>();
        long downLimit = 0;
        long upLimit = 0;

        if (_isSnailMode)
        {
            downLimit = torrentEngine?.Options.AltDownloadRateBytesPerSec > 0
                ? torrentEngine.Options.AltDownloadRateBytesPerSec
                : 512 * 1024;
            upLimit = torrentEngine?.Options.AltUploadRateBytesPerSec > 0
                ? torrentEngine.Options.AltUploadRateBytesPerSec
                : 128 * 1024;
        }

        try
        {
            if (torrentEngine != null)
            {
                await torrentEngine.SetGlobalRateLimitsAsync(downLimit, upLimit);
            }
        }
        catch { }

        if (_isSnailMode)
        {
            SnailModeText.Text = "ON";
            SnailModeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x59, 0xB9, 0xF7));
            ViewModel.StatusMessage = $"Snail Mode active: Bandwidth limited to {downLimit / 1024} KB/s";
        }
        else
        {
            SnailModeText.Text = "OFF";
            SnailModeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9A, 0x9A, 0x9A));
            ViewModel.StatusMessage = "Snail Mode disabled: Full bandwidth restored";
        }
    }

    private void PriorityBadge_Click(object sender, RoutedEventArgs e)
    {
        _isHighPriority = !_isHighPriority;
        if (PriorityBadgeText != null && PriorityBadgeBtn != null)
        {
            if (_isHighPriority)
            {
                PriorityBadgeText.Text = "📈 HIGH";
                PriorityBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2C, 0xCB, 0x70));
                PriorityBadgeBtn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x13, 0x2E, 0x21));
            }
            else
            {
                PriorityBadgeText.Text = "⚖ AUTO";
                PriorityBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x59, 0xB9, 0xF7));
                PriorityBadgeBtn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x14, 0x26, 0x34));
            }
        }
    }

    private void DropZone_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        AddDownload_Click(sender, new RoutedEventArgs());
    }

    private void InspectorSource_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        CopyUrl_Click(sender, new RoutedEventArgs());
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        _isDragOver = true;
        UpdateEmptyStateVisibility();
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        _isDragOver = false;
        UpdateEmptyStateVisibility();
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        _isDragOver = false;
        UpdateEmptyStateVisibility();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var storageItem in items)
                {
                    if (storageItem is Windows.Storage.StorageFile file && file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleTorrentRequestAsync(file.Path);
                        return;
                    }
                }
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                var text = (await e.DataView.GetTextAsync())?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleTorrentRequestAsync(text);
                        return;
                    }
                    else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                    {
                        HttpProbeService probe = App.Services.GetRequiredService<HttpProbeService>();
                        var resolver = App.Services.GetRequiredService<IFileNameResolver>();
                        AddUrlViewModel addVm = new(probe, resolver) { Url = uri.AbsoluteUri };
                        var dialog = new DownloadFileInfoDialog(addVm);
                        if (await ShowContentDialogSafeAsync(dialog) == ContentDialogResult.Primary)
                        {
                            var fileName = string.IsNullOrWhiteSpace(addVm.FileName) ? "download.bin" : addVm.FileName;
                            var destPath = addVm.FullSavePath;
                            var newItem = new DownloadItemViewModel
                            {
                                Id = Guid.NewGuid(),
                                Url = addVm.Url,
                                FileName = fileName,
                                IsFileNameUserLocked = true,
                                SavePath = destPath,
                                Category = addVm.SelectedCategory,
                                QueueName = addVm.QueueName,
                                TotalBytes = addVm.FileSize > 0 ? addVm.FileSize : 0,
                                Status = DownloadStatus.Downloading,
                                LastTryDate = DateTime.Now
                            };
                            ViewModel.AddDownload(newItem);
                            _ = StartHttpDownloadAsync(newItem);
                        }
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to process dropped item in DropZone");
        }

        AddDownload_Click(sender, new RoutedEventArgs());
    }

    private async void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        HttpProbeService probe = App.Services.GetRequiredService<HttpProbeService>();
        var resolver = App.Services.GetRequiredService<IFileNameResolver>();
        AddUrlViewModel addVm = new(probe, resolver);

        // Check if clipboard contains a valid URL and auto-populate
        try
        {
            DataPackageView clipboardContent = Clipboard.GetContent();
            if (clipboardContent.Contains(StandardDataFormats.Text))
            {
                string text = await clipboardContent.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text) && Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    if (IsStreamingMediaPlatformUrl(uri.AbsoluteUri))
                    {
                        MediaDownload_Click(sender, e);
                        return;
                    }
                    addVm.Url = uri.AbsoluteUri;
                    _ = addVm.ProbeAsync();
                }
            }
        }
        catch
        {
            // Clipboard access might fail if locked by another app
        }

        DownloadFileInfoDialog dialog = new(addVm);
        ContentDialogResult result = await ShowContentDialogSafeAsync(dialog);
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            var fileName = string.IsNullOrWhiteSpace(addVm.FileName) ? "download.bin" : addVm.FileName;
            var destPath = addVm.FullSavePath;
            var id = Guid.NewGuid();
            var isLocked = true; // Confirmed by user in Download File Info dialog

            DownloadItemViewModel newItem = new()
            {
                Id = id,
                Url = addVm.Url,
                FileName = fileName,
                IsFileNameUserLocked = isLocked,
                SavePath = destPath,
                Category = addVm.SelectedCategory,
                QueueName = addVm.QueueName,
                TotalBytes = addVm.FileSize > 0 ? addVm.FileSize : 0,
                Status = result == ContentDialogResult.Primary ? DownloadStatus.Downloading : DownloadStatus.Queued,
                LastTryDate = DateTime.Now,
                Description = string.IsNullOrWhiteSpace(addVm.Description) ? "Added manually via SwiftGet" : addVm.Description
            };

            newItem.Segments.Add(new SegmentProgressViewModel
            {
                Index = 1,
                StartOffset = 0,
                EndOffset = Math.Max(0, newItem.TotalBytes - 1),
                DownloadedBytes = 0,
                Status = "Ready"
            });
            newItem.Segments[0].UpdateMetrics(newItem.TotalBytes);

            var db = App.Services.GetRequiredService<ISwiftGetDatabase>();
            var downloadItem = new DownloadItem
            {
                Id = id.ToString("N"),
                JobKind = JobKind.Http,
                Url = newItem.Url,
                FileName = newItem.FileName,
                IsFileNameUserLocked = isLocked,
                DestinationPath = newItem.SavePath,
                CategoryId = newItem.Category,
                QueueId = newItem.QueueName,
                TotalBytes = newItem.TotalBytes,
                Status = newItem.Status,
                CreatedAtUtc = DateTime.UtcNow
            };
            await db.SaveDownloadAsync(downloadItem);

            ViewModel.AddDownload(newItem);
            UpdateCategoryBadges();

            if (result == ContentDialogResult.Primary)
            {
                _ = StartHttpDownloadAsync(newItem);
                OpenDownloadDetails(newItem);
            }
        }
    }

    public async Task ShowDownloadCompleteDialogAsync(string url, string savePath, long totalBytes)
    {
        try
        {
            _trayManager?.ShowNotification("Download complete", $"Finished downloading {Path.GetFileName(savePath)}");

            var dialog = new DownloadCompleteDialog(url, savePath, totalBytes);
            await ShowContentDialogSafeAsync(dialog);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to show DownloadCompleteDialog");
        }
    }

    public static bool IsStreamingMediaPlatformUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        return host.Contains("youtube.com") ||
               host.Contains("youtu.be") ||
               host.Contains("vimeo.com") ||
               host.Contains("dailymotion.com") ||
               host.Contains("tiktok.com") ||
               host.Contains("twitch.tv") ||
               host.Contains("bilibili.com") ||
               host.Contains("soundcloud.com") ||
               (host.Contains("facebook.com") && uri.AbsolutePath.Contains("/videos/")) ||
               (host.Contains("instagram.com") && (uri.AbsolutePath.Contains("/reel/") || uri.AbsolutePath.Contains("/p/")));
    }

    public async Task ShowDownloadFileInfoDialogAsync(SwiftGet.Engine.Server.InterceptDownloadRequest req)
    {
        BringToForeground();
        try
        {
            if (IsStreamingMediaPlatformUrl(req.Url))
            {
                var mediaVm = App.Services.GetRequiredService<MediaDownloadViewModel>();
                mediaVm.ExistingDownloadId = req.DownloadId;
                mediaVm.MediaUrl = req.Url;
                _ = mediaVm.ProbeMediaCommand.ExecuteAsync(null);

                var mediaDialog = new MediaDownloadDialog(mediaVm);
                var mediaResult = await ShowContentDialogSafeAsync(mediaDialog);
                if (mediaResult == ContentDialogResult.Primary || mediaResult == ContentDialogResult.Secondary)
                {
                    await ReloadDownloadsFromDatabaseAsync();
                    if (mediaResult == ContentDialogResult.Primary && mediaVm.LastEnqueuedItem != null)
                    {
                        var enqueuedVm = ViewModel.AllDownloads.FirstOrDefault(d => d.Id.ToString("N") == mediaVm.LastEnqueuedItem.Id);
                        if (enqueuedVm != null)
                        {
                            _ = StartMediaDownloadAsync(enqueuedVm, formatId: mediaVm.LastSelectedFormatSelector);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(req.DownloadId))
                {
                    mediaVm.ExistingDownloadId = null;
                    var mediaDb = App.Services.GetRequiredService<ISwiftGetDatabase>();
                    await mediaDb.DeleteDownloadAsync(req.DownloadId);
                    await ReloadDownloadsFromDatabaseAsync();
                }
                return;
            }

            var resolver = App.Services.GetRequiredService<IFileNameResolver>();
            var initialFileName = !string.IsNullOrWhiteSpace(req.FileName)
                ? req.FileName
                : resolver.ResolveInitialCandidate(req.Url, req.DestinationPath);

            HttpProbeService probe = App.Services.GetRequiredService<HttpProbeService>();
            AddUrlViewModel addVm = new(probe, resolver)
            {
                Url = req.Url,
                FileName = initialFileName ?? string.Empty,
                CookiesJson = req.Cookies,
                Referrer = req.Referrer,
                UserAgent = req.UserAgent
            };

            if (!string.IsNullOrWhiteSpace(req.DestinationPath))
            {
                var dir = Path.GetDirectoryName(req.DestinationPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    addVm.SaveDirectory = dir;
                }
            }

            if (!string.IsNullOrWhiteSpace(req.CategoryId))
            {
                addVm.SelectedCategory = req.CategoryId;
            }

            _ = addVm.ProbeAsync();

            var dialog = new DownloadFileInfoDialog(addVm);
            var result = await ShowContentDialogSafeAsync(dialog);
            var db = App.Services.GetRequiredService<ISwiftGetDatabase>();

            if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
            {
                var fileName = string.IsNullOrWhiteSpace(addVm.FileName) ? "download.bin" : addVm.FileName;
                addVm.FileName = fileName;
                var destPath = addVm.FullSavePath;
                var downloadId = !string.IsNullOrWhiteSpace(req.DownloadId) ? req.DownloadId : Guid.NewGuid().ToString("N");
                var idGuid = Guid.TryParse(downloadId, out var g) ? g : Guid.NewGuid();

                var isLocked = true; // Confirmed by user in Download File Info dialog

                var newItem = new DownloadItemViewModel
                {
                    Id = idGuid,
                    Url = addVm.Url,
                    FileName = fileName,
                    IsFileNameUserLocked = isLocked,
                    SavePath = destPath,
                    Category = addVm.SelectedCategory,
                    QueueName = addVm.QueueName,
                    TotalBytes = addVm.FileSize > 0 ? addVm.FileSize : 0,
                    Status = result == ContentDialogResult.Primary ? DownloadStatus.Downloading : DownloadStatus.Queued,
                    LastTryDate = DateTime.Now,
                    Description = addVm.Description
                };

                newItem.Segments.Add(new SegmentProgressViewModel
                {
                    Index = 1,
                    StartOffset = 0,
                    EndOffset = Math.Max(0, newItem.TotalBytes - 1),
                    DownloadedBytes = 0,
                    Status = "Ready"
                });
                newItem.Segments[0].UpdateMetrics(newItem.TotalBytes);

                var downloadItem = new DownloadItem
                {
                    Id = downloadId,
                    JobKind = JobKind.Http,
                    Url = newItem.Url,
                    FileName = newItem.FileName,
                    IsFileNameUserLocked = isLocked,
                    DestinationPath = newItem.SavePath,
                    CategoryId = newItem.Category,
                    QueueId = newItem.QueueName,
                    TotalBytes = newItem.TotalBytes,
                    CookiesJson = addVm.CookiesJson,
                    Referrer = addVm.Referrer,
                    UserAgent = addVm.UserAgent,
                    Status = newItem.Status,
                    CreatedAtUtc = DateTime.UtcNow
                };
                await db.SaveDownloadAsync(downloadItem);

                ViewModel.AddDownload(newItem);
                UpdateCategoryBadges();

                if (result == ContentDialogResult.Primary)
                {
                    _ = StartHttpDownloadAsync(newItem);
                    OpenDownloadDetails(newItem);
                }
            }
            else if (!string.IsNullOrWhiteSpace(req.DownloadId))
            {
                await db.DeleteDownloadAsync(req.DownloadId);
                await ReloadDownloadsFromDatabaseAsync();
            }
            else if (!string.IsNullOrWhiteSpace(req.DownloadId))
            {
                await db.DeleteDownloadAsync(req.DownloadId);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to show DownloadFileInfoDialog");
        }
    }

    private async void AddTorrent_Click(object sender, RoutedEventArgs e)
    {
        var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
        var addVm = new AddTorrentViewModel(torrentEngine);

        try
        {
            var clipboardContent = Clipboard.GetContent();
            if (clipboardContent.Contains(StandardDataFormats.Text))
            {
                string text = await clipboardContent.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var trimmed = text.Trim().Trim('"');
                    if (trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
                        (trimmed.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed)))
                    {
                        await addVm.InitializeFromSourceAsync(trimmed);
                    }
                }
            }
        }
        catch
        {
            // Clipboard access might fail if locked
        }

        var dialog = new AddTorrentDialog(addVm);
        var result = await ShowContentDialogSafeAsync(dialog);
        bool isConfirmed = result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary;
        addVm.CancelProbing(isConfirmed: isConfirmed);
        if (isConfirmed)
        {
            await HandleTorrentAddConfirmedAsync(addVm, startImmediately: result == ContentDialogResult.Primary);
        }
    }

    private async void CreateTorrent_Click(object sender, RoutedEventArgs e)
    {
        var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
        var createVm = new CreateTorrentViewModel(torrentEngine);
        var dialog = new CreateTorrentDialog(createVm)
        {
            XamlRoot = this.Content.XamlRoot
        };
        await ShowContentDialogSafeAsync(dialog);
    }

    public async Task HandleTorrentRequestAsync(string source, bool startImmediately = true, string? saveDir = null, bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        if (!silent)
        {
            BringToForeground();
        }

        var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
        var addVm = new AddTorrentViewModel(torrentEngine);
        if (!string.IsNullOrWhiteSpace(saveDir))
        {
            addVm.SaveDirectory = saveDir;
        }
        addVm.StartImmediately = startImmediately;

        await addVm.InitializeFromSourceAsync(source);

        if (silent)
        {
            await HandleTorrentAddConfirmedAsync(addVm, startImmediately);
            return;
        }

        var dialog = new AddTorrentDialog(addVm);
        var result = await ShowContentDialogSafeAsync(dialog);
        bool isConfirmed = result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary;
        addVm.CancelProbing(isConfirmed: isConfirmed);
        if (isConfirmed)
        {
            await HandleTorrentAddConfirmedAsync(addVm, startImmediately: result == ContentDialogResult.Primary);
        }
    }

    private async Task HandleTorrentAddConfirmedAsync(AddTorrentViewModel addVm, bool startImmediately)
    {
        var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
        var db = App.Services.GetRequiredService<ISwiftGetDatabase>();

        var existing = ViewModel.AllDownloads.FirstOrDefault(d =>
            d.JobKind == JobKind.Torrent &&
            !string.IsNullOrWhiteSpace(addVm.InfoHash) &&
            string.Equals(d.InfoHash, addVm.InfoHash, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            if (startImmediately)
            {
                await torrentEngine.StartAsync(existing.Id.ToString("N")).ConfigureAwait(false);
                existing.Status = DownloadStatus.Downloading;
            }
            return;
        }

        var jobId = Guid.NewGuid().ToString("N");
        var selectedIndices = addVm.Files.Where(f => f.IsSelected).Select(f => f.FileIndex).ToList();
        var priorities = addVm.Files.ToDictionary(f => f.FileIndex, f => f.Priority);

        var addOptions = new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = addVm.SaveDirectory,
            StartImmediately = startImmediately,
            SequentialDownload = addVm.SequentialMode,
            SelectedFileIndices = selectedIndices,
            FilePriorities = priorities
        };

        try
        {
            if (addVm.Source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            {
                await torrentEngine.AddMagnetAsync(addVm.Source, addOptions).ConfigureAwait(false);
            }
            else if (File.Exists(addVm.Source))
            {
                var bytes = await File.ReadAllBytesAsync(addVm.Source).ConfigureAwait(false);
                await torrentEngine.AddTorrentFileAsync(bytes, addOptions).ConfigureAwait(false);
            }
            else if (addVm.Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || addVm.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var httpClient = App.Services.GetRequiredService<HttpClient>();
                var bytes = await httpClient.GetByteArrayAsync(addVm.Source).ConfigureAwait(false);
                await torrentEngine.AddTorrentFileAsync(bytes, addOptions).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to register torrent {Source} in MonoTorrentEngine", addVm.Source);
            return;
        }

        var initialStatus = addVm.TotalBytes > 0
            ? (startImmediately ? DownloadStatus.Downloading : DownloadStatus.Queued)
            : (startImmediately ? DownloadStatus.FetchingMetadata : DownloadStatus.Queued);

        var downloadItem = new DownloadItem
        {
            Id = jobId,
            JobKind = JobKind.Torrent,
            InfoHash = addVm.InfoHash,
            Url = addVm.Source,
            FileName = addVm.TorrentName,
            DestinationPath = Path.Combine(addVm.SaveDirectory, addVm.TorrentName),
            CategoryId = addVm.SelectedCategory,
            QueueId = addVm.QueueName,
            TotalBytes = addVm.TotalBytes,
            Status = initialStatus,
            CreatedAtUtc = DateTime.UtcNow
        };
        await db.SaveDownloadAsync(downloadItem).ConfigureAwait(false);

        var torrentRecord = new TorrentItem
        {
            Id = jobId,
            InfoHash = addVm.InfoHash,
            Name = addVm.TorrentName,
            TotalBytes = addVm.TotalBytes,
            PieceCount = addVm.PieceCount,
            PieceLength = (int)addVm.PieceLength,
            MagnetUri = addVm.Source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ? addVm.Source : null
        };
        await db.SaveTorrentAsync(torrentRecord).ConfigureAwait(false);

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            var newItem = new DownloadItemViewModel
            {
                Id = Guid.TryParse(jobId, out var gid) ? gid : Guid.NewGuid(),
                JobKind = JobKind.Torrent,
                InfoHash = addVm.InfoHash,
                FileName = addVm.TorrentName,
                Url = addVm.Source,
                SavePath = Path.Combine(addVm.SaveDirectory, addVm.TorrentName),
                Category = addVm.SelectedCategory,
                QueueName = addVm.QueueName,
                TotalBytes = addVm.TotalBytes,
                Status = initialStatus,
                LastTryDate = DateTime.Now,
                Description = "BitTorrent transfer",
                IsSequential = addVm.SequentialMode
            };
            ViewModel.AddDownload(newItem);
            UpdateCategoryBadges();
        });
    }

    private void DownloadListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: DownloadItemViewModel tappedItem })
        {
            ViewModel.SelectedItem = tappedItem;
        }

        if (ViewModel.SelectedItem != null)
        {
            UpdateInspectorDetails(ViewModel.SelectedItem);
            ShowBottomInspector(true);
        }
    }

    private void DownloadListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is DownloadItemViewModel item)
        {
            ViewModel.SelectedItem = item;
            UpdateInspectorDetails(item);
        }
    }

    private void OpenDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null)
        {
            OpenDownloadDetails(ViewModel.SelectedItem);
        }
    }

    private async void OpenDownloadDetails(DownloadItemViewModel item)
    {
        if (item.JobKind == JobKind.Torrent)
        {
            var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
            var db = App.Services.GetRequiredService<ISwiftGetDatabase>();
            var detailsVm = new TorrentDetailsViewModel(item, torrentEngine, db);
            await detailsVm.InitializeAsync();
            var dialog = new TorrentDetailsDialog(detailsVm);
            await ShowContentDialogSafeAsync(dialog);
        }
        else
        {
            DownloadDialogViewModel dialogVm = new(item);
            DownloadProgressDialog dialog = new(dialogVm);
            dialog.Activate();
        }
    }

    private async void ResumeContext_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResumeSelectedCommand.ExecuteAsync(null);
        if (ViewModel.SelectedItem != null) UpdateInspectorDetails(ViewModel.SelectedItem);
    }

    private void StopContext_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StopSelectedCommand.Execute(null);
        if (ViewModel.SelectedItem != null) UpdateInspectorDetails(ViewModel.SelectedItem);
    }

    private async void ForceRecheck_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null && ViewModel.SelectedItem.JobKind == JobKind.Torrent)
        {
            try
            {
                var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                await torrentEngine.ForceRecheckAsync(ViewModel.SelectedItem.Id.ToString("N"));
                ViewModel.SelectedItem.Status = DownloadStatus.Checking;
                ShowInfoDialog("Force Recheck", $"Started checking pieces for '{ViewModel.SelectedItem.FileName}'.");
            }
            catch (Exception ex)
            {
                ShowInfoDialog("Force Recheck Failed", ex.Message);
            }
        }
    }

    private async void ForceAnnounce_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null && ViewModel.SelectedItem.JobKind == JobKind.Torrent)
        {
            try
            {
                var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                await torrentEngine.ForceAnnounceAsync(ViewModel.SelectedItem.Id.ToString("N"));
                ShowInfoDialog("Force Announce", $"Announce request dispatched to all active trackers for '{ViewModel.SelectedItem.FileName}'.");
            }
            catch (Exception ex)
            {
                ShowInfoDialog("Force Announce Failed", ex.Message);
            }
        }
    }

    private async void MoveStorage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null && ViewModel.SelectedItem.JobKind == JobKind.Torrent)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                try
                {
                    var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                    await torrentEngine.MoveStorageAsync(ViewModel.SelectedItem.Id.ToString("N"), folder.Path);
                    ViewModel.SelectedItem.SavePath = folder.Path;
                    var db = App.Services.GetRequiredService<ISwiftGetDatabase>();
                    var dbItem = await db.GetDownloadAsync(ViewModel.SelectedItem.Id.ToString("N"));
                    if (dbItem != null)
                    {
                        dbItem.DestinationPath = folder.Path;
                        await db.SaveDownloadAsync(dbItem);
                    }
                    ShowInfoDialog("Storage Moved", $"Files moved to '{folder.Path}'.");
                }
                catch (Exception ex)
                {
                    ShowInfoDialog("Move Storage Failed", ex.Message);
                }
            }
        }
    }

    private void CopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem == null) return;

        string copyText = string.Empty;
        if (!string.IsNullOrWhiteSpace(ViewModel.SelectedItem.Url) &&
            ViewModel.SelectedItem.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            copyText = ViewModel.SelectedItem.Url;
        }
        else if (!string.IsNullOrWhiteSpace(ViewModel.SelectedItem.InfoHash))
        {
            copyText = $"magnet:?xt=urn:btih:{ViewModel.SelectedItem.InfoHash}&dn={Uri.EscapeDataString(ViewModel.SelectedItem.FileName)}";
        }
        else if (!string.IsNullOrWhiteSpace(ViewModel.SelectedItem.Url))
        {
            copyText = ViewModel.SelectedItem.Url;
        }

        if (!string.IsNullOrEmpty(copyText))
        {
            DataPackage package = new();
            package.SetText(copyText);
            Clipboard.SetContent(package);
            ShowInfoDialog("Copied", "Magnet URI copied to clipboard.");
        }
    }

    private async void ToggleSequential_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null && ViewModel.SelectedItem.JobKind == JobKind.Torrent)
        {
            ViewModel.SelectedItem.IsSequential = !ViewModel.SelectedItem.IsSequential;
            try
            {
                var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                await torrentEngine.SetSequentialDownloadAsync(ViewModel.SelectedItem.Id.ToString("N"), ViewModel.SelectedItem.IsSequential);
            }
            catch { }
            ShowInfoDialog("Sequential Download", $"Sequential download for '{ViewModel.SelectedItem.FileName}' is now {(ViewModel.SelectedItem.IsSequential ? "enabled" : "disabled")}.");
        }
    }

    private async void DeleteContext_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
        UpdateCategoryBadges();
        UpdateInspectorDetails(ViewModel.SelectedItem);
    }

    private async void DeleteWithFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null)
        {
            var item = ViewModel.SelectedItem;
            var dialog = new ContentDialog
            {
                Title = "Delete Download and Files?",
                Content = $"Are you sure you want to remove '{item.FileName}' and PERMANENTLY DELETE all downloaded files from disk?",
                PrimaryButtonText = "Delete Files",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            if (await ShowContentDialogSafeAsync(dialog) == ContentDialogResult.Primary)
            {
                if (item.JobKind == JobKind.Torrent)
                {
                    try
                    {
                        var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
                        await torrentEngine.RemoveAsync(item.Id.ToString("N"), deleteFiles: true);
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        if (_activeDownloads.TryRemove(item.Id, out var cts))
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                        if (File.Exists(item.SavePath))
                        {
                            File.Delete(item.SavePath);
                        }
                        else if (Directory.Exists(item.SavePath))
                        {
                            Directory.Delete(item.SavePath, true);
                        }
                    }
                    catch { }
                }

                ViewModel.RemoveDownload(item);
                var db = App.Services.GetRequiredService<ISwiftGetDatabase>();
                await db.DeleteDownloadAsync(item.Id.ToString("N"));
                UpdateCategoryBadges();
                UpdateInspectorDetails(ViewModel.SelectedItem);
            }
        }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null && !string.IsNullOrEmpty(ViewModel.SelectedItem.Url))
        {
            DataPackage package = new();
            package.SetText(ViewModel.SelectedItem.Url);
            Clipboard.SetContent(package);
            ShowInfoDialog("Copied", "URL copied to clipboard.");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.SelectedItem?.SavePath ?? DetailsViewModel?.FullSavePath;
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                string dir = Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{dir}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    Directory.CreateDirectory(defaultDir);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{defaultDir}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to open folder");
            }
        }
    }

    private async void MediaDownload_Click(object sender, RoutedEventArgs e)
    {
        var mediaVm = App.Services.GetRequiredService<MediaDownloadViewModel>();

        try
        {
            DataPackageView clipboardContent = Clipboard.GetContent();
            if (clipboardContent.Contains(StandardDataFormats.Text))
            {
                string text = await clipboardContent.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text) && Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    mediaVm.MediaUrl = uri.AbsoluteUri;
                    _ = mediaVm.ProbeMediaCommand.ExecuteAsync(null);
                }
            }
        }
        catch
        {
            // Clipboard access might fail if locked
        }

        var dialog = new MediaDownloadDialog(mediaVm);

        var result = await ShowContentDialogSafeAsync(dialog);
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            await ReloadDownloadsFromDatabaseAsync();
            if (result == ContentDialogResult.Primary && mediaVm.LastEnqueuedItem != null)
            {
                var enqueuedVm = ViewModel.AllDownloads.FirstOrDefault(d => d.Id.ToString("N") == mediaVm.LastEnqueuedItem.Id);
                if (enqueuedVm != null)
                {
                    _ = StartMediaDownloadAsync(enqueuedVm, formatId: mediaVm.LastSelectedFormatSelector);
                }
            }
        }
    }

    private async void BatchDownload_Click(object sender, RoutedEventArgs e)
    {
        var batchVm = App.Services.GetRequiredService<BatchDownloadViewModel>();
        var dialog = new BatchDownloadDialog(batchVm)
        {
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
        await ReloadDownloadsFromDatabaseAsync();
    }

    private async void Grabber_Click(object sender, RoutedEventArgs e)
    {
        var grabberVm = App.Services.GetRequiredService<GrabberWizardViewModel>();
        var dialog = new GrabberWizardDialog(grabberVm)
        {
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
        await ReloadDownloadsFromDatabaseAsync();
    }

    private void DropTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_dropBoxWindow == null)
        {
            _dropBoxWindow = App.Services.GetRequiredService<DropBoxWindow>();
            _dropBoxWindow.Closed += (s, args) =>
            {
                _dropBoxWindow = null;
                if (DropTargetMenuItem != null)
                {
                    DropTargetMenuItem.IsChecked = false;
                }
            };
            _dropBoxWindow.Activate();
            if (DropTargetMenuItem != null)
            {
                DropTargetMenuItem.IsChecked = true;
            }
        }
        else
        {
            _dropBoxWindow.Close();
            _dropBoxWindow = null;
            if (DropTargetMenuItem != null)
            {
                DropTargetMenuItem.IsChecked = false;
            }
        }
    }

    private async void Options_Click(object sender, RoutedEventArgs e)
    {
        var optionsVm = App.Services.GetRequiredService<OptionsViewModel>();
        await optionsVm.InitializeAsync();
        var dialog = new OptionsDialog(optionsVm);
        await ShowContentDialogSafeAsync(dialog);
    }

    private async void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        var schedVm = App.Services.GetRequiredService<SchedulerViewModel>();
        await schedVm.InitializeAsync();
        var dialog = new SchedulerDialog(schedVm);
        await ShowContentDialogSafeAsync(dialog);
    }

    private async void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        var queueManager = App.Services.GetRequiredService<IQueueManager>();
        ViewModel.StatusMessage = "Starting main queue...";
        try
        {
            await queueManager.StartQueueAsync("default");
            ViewModel.StatusMessage = "Main queue started";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Failed to start queue: {ex.Message}";
        }
    }

    private async void StopQueue_Click(object sender, RoutedEventArgs e)
    {
        var queueManager = App.Services.GetRequiredService<IQueueManager>();
        ViewModel.StatusMessage = "Stopping main queue...";
        try
        {
            await queueManager.StopQueueAsync("default");
            ViewModel.StatusMessage = "Main queue stopped";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Failed to stop queue: {ex.Message}";
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exportService = App.Services.GetRequiredService<IExportService>();
            var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloadsDir);
            var exportPath = Path.Combine(downloadsDir, $"swiftget_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");

            await exportService.ExportToJsonFileAsync(exportPath);
            ShowInfoDialog("Export Successful", $"Complete state exported to:\n{exportPath}");
        }
        catch (Exception ex)
        {
            ShowInfoDialog("Export Failed", ex.Message);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var importService = App.Services.GetRequiredService<IImportService>();
            var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            var jsonFiles = Directory.Exists(downloadsDir)
                ? Directory.GetFiles(downloadsDir, "swiftget_export*.json")
                : [];
            var txtFiles = Directory.Exists(downloadsDir)
                ? Directory.GetFiles(downloadsDir, "swiftget_urls*.txt")
                : [];

            if (jsonFiles.Length > 0)
            {
                Array.Sort(jsonFiles);
                var latest = jsonFiles[^1];
                var count = await importService.ImportFromJsonFileAsync(latest);
                await ReloadDownloadsFromDatabaseAsync();
                ShowInfoDialog("Import Complete", $"Imported JSON package from {Path.GetFileName(latest)}:\nRestored {count} items.");
            }
            else if (txtFiles.Length > 0)
            {
                Array.Sort(txtFiles);
                var latest = txtFiles[^1];
                var count = await importService.ImportFromTextUrlListAsync(latest);
                await ReloadDownloadsFromDatabaseAsync();
                ShowInfoDialog("Import Complete", $"Imported {count} URLs from {Path.GetFileName(latest)}.");
            }
            else
            {
                ShowInfoDialog("Import", $"No swiftget_export*.json or swiftget_urls*.txt found in:\n{downloadsDir}");
            }
        }
        catch (Exception ex)
        {
            ShowInfoDialog("Import Failed", ex.Message);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        ShowInfoDialog("About SwiftGet", "SwiftGet Download Manager v1.0.0\nHigh-performance IDM-class download accelerator for Windows 10/11.");
    }

    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        ShowInfoDialog("Check for Updates", "You are using the latest version of SwiftGet Download Manager.");
    }

    private void Docs_Click(object sender, RoutedEventArgs e)
    {
        ShowInfoDialog("Documentation", "Please visit https://github.com/swiftget for user manuals and API documentation.");
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var diagService = App.Services.GetRequiredService<IDiagnosticService>();
            var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloadsDir);
            var exportPath = Path.Combine(downloadsDir, $"swiftget_diagnostics_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");

            await diagService.ExportDiagnosticBundleAsync(exportPath);
            ShowInfoDialog("Diagnostic Report Exported", $"Sanitized diagnostic report saved to:\n{exportPath}");
        }
        catch (Exception ex)
        {
            ShowInfoDialog("Diagnostics Export Failed", ex.Message);
        }
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "logs");
            Directory.CreateDirectory(logsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = logsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowInfoDialog("Open Logs Folder", $"Could not open logs folder: {ex.Message}");
        }
    }

    private void ViewTorrentLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "logs");
            Directory.CreateDirectory(logsDir);
            var torrentLog = Directory.GetFiles(logsDir, "torrent-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            var targetFile = torrentLog ?? Directory.GetFiles(logsDir, "swiftget-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

            if (targetFile != null && File.Exists(targetFile))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetFile,
                    UseShellExecute = true
                });
            }
            else
            {
                OpenLogsFolder_Click(sender, e);
            }
        }
        catch (Exception ex)
        {
            ShowInfoDialog("View Torrent Logs", $"Could not open torrent log file: {ex.Message}");
        }
    }

    private async void ExportTorrentDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
            var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloadsDir)) Directory.CreateDirectory(downloadsDir);

            var destPath = Path.Combine(downloadsDir, $"SwiftGet_Torrent_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await torrentEngine.ExportLogsAsync(destPath).ConfigureAwait(false);

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = destPath,
                    UseShellExecute = true
                });
            });
        }
        catch (Exception ex)
        {
            ShowInfoDialog("Export Torrent Diagnostics", $"Could not export torrent diagnostics: {ex.Message}");
        }
    }

    public void BringToForeground()
    {
        if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(BringToForeground);
            return;
        }

        try
        {
            _appWindow?.Show();

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SwRestore);

                var foregroundHwnd = GetForegroundWindow();
                uint foregroundThreadId = foregroundHwnd != IntPtr.Zero ? GetWindowThreadProcessId(foregroundHwnd, out _) : 0;
                uint currentThreadId = GetCurrentThreadId();

                bool attached = false;
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }

                try
                {
                    BringWindowToTop(hWnd);
                    SetWindowPos(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                    SetWindowPos(hWnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                    SetForegroundWindow(hWnd);
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThreadId, foregroundThreadId, false);
                    }
                }
            }

            Activate();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to force BringToForeground");
        }
    }

    public void RestoreMainWindow()
    {
        BringToForeground();
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        _trayManager?.Dispose();
        Close();
        Application.Current.Exit();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        _appWindow?.Hide();
        _trayManager?.ShowNotification("SwiftGet", "SwiftGet is running in the system tray.");
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private async void ShowInfoDialog(string title, string content)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    public Task StartHttpDownloadAsync(DownloadItemViewModel vm, CancellationToken externalCt = default)
    {
        if (_activeDownloads.ContainsKey(vm.Id)) return Task.CompletedTask;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        if (!_activeDownloads.TryAdd(vm.Id, cts))
        {
            cts.Dispose();
            return Task.CompletedTask;
        }

        vm.Status = DownloadStatus.Downloading;
        var coordinator = App.Services.GetRequiredService<DownloadCoordinator>();
        var db = App.Services.GetRequiredService<ISwiftGetDatabase>();

        _ = Task.Run(async () =>
        {
            try
            {
                var dbItem = await db.GetDownloadAsync(vm.Id.ToString("N")) ?? new DownloadItem
                {
                    Id = vm.Id.ToString("N"),
                    JobKind = JobKind.Http,
                    Url = vm.Url,
                    FileName = vm.FileName,
                    DestinationPath = vm.SavePath,
                    CategoryId = vm.Category,
                    QueueId = vm.QueueName,
                    TotalBytes = vm.TotalBytes,
                    DownloadedBytes = vm.DownloadedBytes,
                    Status = DownloadStatus.Downloading,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var destDir = Path.GetDirectoryName(dbItem.DestinationPath);
                if (!string.IsNullOrWhiteSpace(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                DateTime lastTime = DateTime.UtcNow;
                long lastBytes = vm.DownloadedBytes;
                double smoothedSpeed = vm.SpeedBytesPerSec;

                var progress = new Progress<long>(downloaded =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var now = DateTime.UtcNow;
                        var elapsed = (now - lastTime).TotalSeconds;
                        if (elapsed >= 0.4)
                        {
                            if (downloaded > lastBytes)
                            {
                                double instantSpeed = (downloaded - lastBytes) / elapsed;
                                smoothedSpeed = smoothedSpeed > 0
                                    ? (smoothedSpeed * 0.6) + (instantSpeed * 0.4)
                                    : instantSpeed;
                                lastBytes = downloaded;
                            }
                            lastTime = now;
                        }

                        var total = dbItem.TotalBytes > 0 ? dbItem.TotalBytes : vm.TotalBytes;
                        double effectiveSpeed = smoothedSpeed > 0 ? smoothedSpeed : vm.SpeedBytesPerSec;
                        vm.UpdateProgress(downloaded, total, effectiveSpeed);
                        ViewModel.UpdateMetrics();
                        UpdateFooterTelemetry();
                        if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                    });
                });

                await coordinator.StartAsync(dbItem, progress: progress, ct: cts.Token).ConfigureAwait(false);

                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (!string.IsNullOrWhiteSpace(dbItem.FileName))
                    {
                        vm.FileName = dbItem.FileName;
                    }
                    if (!string.IsNullOrWhiteSpace(dbItem.DestinationPath))
                    {
                        vm.SavePath = dbItem.DestinationPath;
                    }
                    vm.Status = DownloadStatus.Completed;
                    var total = dbItem.TotalBytes > 0 ? dbItem.TotalBytes : (vm.TotalBytes > 0 ? vm.TotalBytes : dbItem.DownloadedBytes);
                    vm.UpdateProgress(total, total, 0);
                    vm.ProgressPercentage = 100.0;
                    ViewModel.UpdateMetrics();
                    UpdateFooterTelemetry();
                    UpdateCategoryBadges();
                    if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                    await ShowDownloadCompleteDialogAsync(vm.Url, vm.SavePath, total);
                });
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    vm.Status = DownloadStatus.Paused;
                    vm.SpeedBytesPerSec = 0;
                    vm.RemainingTime = TimeSpan.Zero;
                    ViewModel.UpdateMetrics();
                    UpdateFooterTelemetry();
                    UpdateCategoryBadges();
                    if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "HTTP download {Id} failed", vm.Id);
                try
                {
                    await db.UpdateDownloadStatusAsync(vm.Id.ToString("N"), DownloadStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                DispatcherQueue.TryEnqueue(() =>
                {
                    vm.Status = DownloadStatus.Failed;
                    vm.SpeedBytesPerSec = 0;
                    vm.RemainingTime = TimeSpan.Zero;
                    ViewModel.UpdateMetrics();
                    UpdateFooterTelemetry();
                    UpdateCategoryBadges();
                    if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                });
            }
            finally
            {
                _activeDownloads.TryRemove(vm.Id, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StartMediaDownloadAsync(DownloadItemViewModel vm, string? formatId = null, CancellationToken externalCt = default)
    {
        if (_activeDownloads.ContainsKey(vm.Id)) return Task.CompletedTask;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        if (!_activeDownloads.TryAdd(vm.Id, cts))
        {
            cts.Dispose();
            return Task.CompletedTask;
        }

        vm.Status = DownloadStatus.Downloading;
        var recorder = App.Services.GetRequiredService<StreamRecorder>();
        var db = App.Services.GetRequiredService<ISwiftGetDatabase>();

        _ = Task.Run(async () =>
        {
            try
            {
                var dbItem = await db.GetDownloadAsync(vm.Id.ToString("N")) ?? new DownloadItem
                {
                    Id = vm.Id.ToString("N"),
                    JobKind = JobKind.Media,
                    Url = vm.Url,
                    FileName = vm.FileName,
                    DestinationPath = vm.SavePath,
                    CategoryId = vm.Category,
                    QueueId = vm.QueueName,
                    TotalBytes = vm.TotalBytes,
                    DownloadedBytes = vm.DownloadedBytes,
                    Status = DownloadStatus.Downloading,
                    CreatedAtUtc = DateTime.UtcNow
                };

                dbItem.Status = DownloadStatus.Downloading;
                await db.SaveDownloadAsync(dbItem, CancellationToken.None).ConfigureAwait(false);

                DateTime lastTime = DateTime.UtcNow;
                long lastBytes = vm.DownloadedBytes;
                double smoothedSpeed = vm.SpeedBytesPerSec;

                void OnDetailedProgress(object? sender, StreamProgressEventArgs args)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var total = args.TotalBytes > 0 ? args.TotalBytes : (dbItem.TotalBytes > 0 ? dbItem.TotalBytes : vm.TotalBytes);
                        var current = args.DownloadedBytes > 0 ? args.DownloadedBytes : (total > 0 ? (long)(total * (args.ProgressPercentage / 100.0)) : 0);

                        double speed = args.SpeedBytesPerSec;
                        if (speed > 0)
                        {
                            smoothedSpeed = smoothedSpeed > 0 ? (smoothedSpeed * 0.6) + (speed * 0.4) : speed;
                        }
                        else
                        {
                            var now = DateTime.UtcNow;
                            var elapsed = (now - lastTime).TotalSeconds;
                            if (elapsed >= 0.4 && current > lastBytes)
                            {
                                double instantSpeed = (current - lastBytes) / elapsed;
                                smoothedSpeed = smoothedSpeed > 0 ? (smoothedSpeed * 0.6) + (instantSpeed * 0.4) : instantSpeed;
                                lastTime = now;
                                lastBytes = current;
                            }
                        }

                        double effectiveSpeed = smoothedSpeed > 0 ? smoothedSpeed : speed;
                        vm.UpdateProgress(current, total, effectiveSpeed);
                        vm.ProgressPercentage = Math.Round(Math.Clamp(args.ProgressPercentage, 0.0, 100.0), 1);

                        if (args.RemainingTime.HasValue && args.RemainingTime.Value > TimeSpan.Zero)
                        {
                            vm.RemainingTime = args.RemainingTime.Value;
                        }

                        ViewModel.UpdateMetrics();
                        UpdateFooterTelemetry();
                        if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                    });
                }

                string actualPath = dbItem.DestinationPath;
                recorder.DetailedProgressChanged += OnDetailedProgress;
                try
                {
                    actualPath = await recorder.RecordStreamAsync(
                        streamUrl: dbItem.Url,
                        destinationPath: dbItem.DestinationPath,
                        formatId: formatId,
                        ct: cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    recorder.DetailedProgressChanged -= OnDetailedProgress;
                }

                var verifiedPath = !string.IsNullOrWhiteSpace(actualPath) && File.Exists(actualPath)
                    ? actualPath
                    : dbItem.DestinationPath;

                long finalSize = 0;
                if (File.Exists(verifiedPath))
                {
                    finalSize = new FileInfo(verifiedPath).Length;
                }
                if (finalSize > 0)
                {
                    dbItem.DestinationPath = verifiedPath;
                    dbItem.FileName = Path.GetFileName(verifiedPath);
                    dbItem.TotalBytes = finalSize;
                    dbItem.DownloadedBytes = finalSize;
                    dbItem.Status = DownloadStatus.Completed;
                    dbItem.ErrorMessage = null;
                    dbItem.CompletedAtUtc = DateTime.UtcNow;
                    await db.SaveDownloadAsync(dbItem, CancellationToken.None).ConfigureAwait(false);

                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        vm.SavePath = verifiedPath;
                        vm.FileName = dbItem.FileName;
                        vm.Status = DownloadStatus.Completed;
                        vm.UpdateProgress(finalSize, finalSize, 0);
                        vm.ProgressPercentage = 100.0;
                        ViewModel.UpdateMetrics();
                        UpdateFooterTelemetry();
                        UpdateCategoryBadges();
                        if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                        await ShowDownloadCompleteDialogAsync(vm.Url, vm.SavePath, finalSize);
                    });
                }
                else
                {
                    dbItem.Status = DownloadStatus.Failed;
                    dbItem.ErrorMessage = "Download verification failed: destination file is missing or empty.";
                    await db.SaveDownloadAsync(dbItem, CancellationToken.None).ConfigureAwait(false);

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        vm.Status = DownloadStatus.Failed;
                        ViewModel.UpdateMetrics();
                        UpdateFooterTelemetry();
                        UpdateCategoryBadges();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    vm.Status = DownloadStatus.Paused;
                    vm.SpeedBytesPerSec = 0;
                    vm.RemainingTime = TimeSpan.Zero;
                    ViewModel.UpdateMetrics();
                    UpdateFooterTelemetry();
                    UpdateCategoryBadges();
                    if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Media download {Id} failed", vm.Id);
                try
                {
                    await db.UpdateDownloadStatusAsync(vm.Id.ToString("N"), DownloadStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                DispatcherQueue.TryEnqueue(() =>
                {
                    vm.Status = DownloadStatus.Failed;
                    vm.SpeedBytesPerSec = 0;
                    vm.RemainingTime = TimeSpan.Zero;
                    ViewModel.UpdateMetrics();
                    UpdateFooterTelemetry();
                    UpdateCategoryBadges();
                    if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                });
            }
            finally
            {
                _activeDownloads.TryRemove(vm.Id, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }
}
