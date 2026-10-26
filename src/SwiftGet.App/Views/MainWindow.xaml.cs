using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Coordinator;
using SwiftGet.Engine.Diagnostics;
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
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeDownloads = new();
    private readonly SemaphoreSlim _contentDialogSemaphore = new(1, 1);
    private readonly DispatcherTimer _telemetryTimer = new();
    public MainViewModel ViewModel { get; }

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
            e.Handled = true;
        }
    }

    public async Task ReloadDownloadsFromDatabaseAsync()
    {
        var db = App.Services.GetRequiredService<ISwiftGetDatabase>();
        var items = await db.GetAllDownloadsAsync();
        ViewModel.ClearAll();
        var vms = items.Select(d => new DownloadItemViewModel
        {
            Id = Guid.TryParse(d.Id, out var gid) ? gid : Guid.NewGuid(),
            JobKind = d.JobKind,
            InfoHash = d.InfoHash ?? string.Empty,
            FileName = d.FileName,
            Url = d.Url,
            SavePath = d.DestinationPath,
            TotalBytes = d.TotalBytes,
            DownloadedBytes = d.DownloadedBytes,
            Status = d.Status,
            Category = d.CategoryId ?? "all",
            QueueName = d.QueueId ?? "default"
        });
        ViewModel.AddDownloads(vms);
        UpdateCategoryBadges();
        BuildChunkVisualizer(ViewModel.SelectedItem);
    }

    public MainWindow(MainViewModel viewModel)
    {
        Serilog.Log.Information("MainWindow constructor starting...");
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Serilog.Log.Information("MainWindow InitializeComponent completed.");
        Title = "SwiftGet Download Manager";

        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (_appWindow != null)
            {
                _appWindow.Title = "SwiftGet Download Manager";
                _appWindow.Resize(new Windows.Graphics.SizeInt32(1200, 750));
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
                await torrentEngine.StartAsync(item.Id.ToString("N"));
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

        Serilog.Log.Information("MainWindow constructor completed.");
    }

    public void UpdateCategoryBadges()
    {
        if (BadgeAll != null) BadgeAll.Value = ViewModel.AllDownloads.Count;
        if (BadgeVideo != null) BadgeVideo.Value = ViewModel.AllDownloads.Count(d => d.Category == "video" || d.JobKind == JobKind.Media);
        if (BadgeCompressed != null) BadgeCompressed.Value = ViewModel.AllDownloads.Count(d => d.Category == "compressed");
        if (BadgePrograms != null) BadgePrograms.Value = ViewModel.AllDownloads.Count(d => d.Category == "programs");
        if (BadgeDocuments != null) BadgeDocuments.Value = ViewModel.AllDownloads.Count(d => d.Category == "documents");
        if (BadgeMusic != null) BadgeMusic.Value = ViewModel.AllDownloads.Count(d => d.Category == "music");
        if (BadgeTorrents != null) BadgeTorrents.Value = ViewModel.AllDownloads.Count(d => d.JobKind == JobKind.Torrent);
        if (BadgeUnfinished != null) BadgeUnfinished.Value = ViewModel.AllDownloads.Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.FetchingMetadata or DownloadStatus.Checking);
        if (BadgeCompleted != null) BadgeCompleted.Value = ViewModel.AllDownloads.Count(d => d.Status is DownloadStatus.Completed or DownloadStatus.Seeding);
    }

    private void BuildChunkVisualizer(DownloadItemViewModel? item = null)
    {
        if (ChunksGrid == null) return;
        ChunksGrid.Items.Clear();

        double progress = item?.ProgressPercentage ?? 0;
        bool isDownloading = item?.Status == DownloadStatus.Downloading;
        int completedThreshold = (int)Math.Round((progress / 100.0) * 32);

        for (int i = 0; i < 32; i++)
        {
            Windows.UI.Color color;
            if (i < completedThreshold)
            {
                color = Windows.UI.Color.FromArgb(255, 56, 189, 248); // Blue Completed
            }
            else if (isDownloading && i == completedThreshold)
            {
                color = Windows.UI.Color.FromArgb(255, 74, 222, 128); // Green/Cyan streaming
            }
            else
            {
                color = Windows.UI.Color.FromArgb(255, 38, 41, 52); // Dark queued
            }

            ChunksGrid.Items.Add(new Border
            {
                Width = 28,
                Height = 14,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color)
            });
        }
    }

    private void UpdateInspectorDetails(DownloadItemViewModel item)
    {
        if (item == null) return;
        InspectorTitle.Text = item.FileName;
        InspectorFolder.Text = string.IsNullOrWhiteSpace(item.SavePath) ? @"D:\Downloads" : item.SavePath;
        InspectorDownloaded.Text = $"{item.FormattedDownloaded} of {item.FormattedSize} ({item.ProgressPercentage:0.0}%)";
        InspectorUploaded.Text = item.UploadedBytes > 0
            ? $"{item.UploadedBytes / (1024.0 * 1024.0):0.##} MB (ratio: {item.ShareRatio:0.00})"
            : "0 B";
        InspectorAdded.Text = item.FormattedLastTryDate;
        InspectorPriority.Text = item.JobKind == JobKind.Torrent ? "Torrent Swarm" : "Multi-Source (Auto-Boost)";
        InspectorSourceText.Text = string.IsNullOrWhiteSpace(item.Url) ? item.FileName : item.Url;
        FooterSelectedTicker.Text = item.FileName;
        BuildChunkVisualizer(item);
        UpdateConnectionTelemetry(item);
    }

    private void TelemetryTimer_Tick(object? sender, object e)
    {
        UpdateActiveTorrentTelemetry();
        UpdateConnectionTelemetry(ViewModel.SelectedItem);
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
                var stats = engine.GetJobStats(t.InfoHash);
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
                }
            }
        }
        catch { }
    }

    private void UpdateConnectionTelemetry(DownloadItemViewModel? item)
    {
        if (InspectorThreadConnections == null || InspectorProtocol == null || InspectorChunkAllocationHeader == null) return;

        if (item == null || ViewModel.AllDownloads.Count == 0)
        {
            InspectorThreadConnections.Text = "0 Active";
            InspectorProtocol.Text = "Idle";
            InspectorChunkAllocationHeader.Text = "Multi-Threaded Segment Allocation (0 Chunks)";
            return;
        }

        if (item.JobKind == JobKind.Torrent)
        {
            InspectorThreadConnections.Text = $"{item.PeersConnected} Connected ({item.SeedsConnected} Seeds)";
            InspectorProtocol.Text = "BitTorrent (P2P Swarm)";
            InspectorChunkAllocationHeader.Text = $"Piece Distribution Map ({item.FormattedSize})";
        }
        else if (item.Status == DownloadStatus.Downloading)
        {
            int active = item.Segments.Count(s => s.Status == "Downloading" || s.Status == "Active");
            int count = item.Segments.Count > 0 ? item.Segments.Count : 1;
            InspectorThreadConnections.Text = $"{Math.Max(1, active)} Active ({count} Chunks)";
            InspectorProtocol.Text = "HTTP/1.1 (Multi-Source)";
            InspectorChunkAllocationHeader.Text = $"Multi-Threaded Segment Allocation ({count} Chunks)";
        }
        else
        {
            InspectorThreadConnections.Text = "0 Active";
            InspectorProtocol.Text = item.JobKind == JobKind.Media ? "Media Stream" : "HTTP/1.1";
            InspectorChunkAllocationHeader.Text = $"Multi-Threaded Segment Allocation ({item.Segments.Count} Chunks)";
        }
    }

    private void DownloadsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DownloadsListView.SelectedItem is DownloadItemViewModel item)
        {
            ViewModel.SelectedItem = item;
            UpdateInspectorDetails(item);
        }
        else
        {
            UpdateConnectionTelemetry(null);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.SearchFilter = sender.Text;
        UpdateCategoryBadges();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is ListViewItem item && item.Tag is string tag)
        {
            ViewModel.SelectCategory(tag);
            UpdateCategoryBadges();
        }
    }

    private void QuickCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QuickCategoryCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ViewModel.SelectCategory(tag);
            UpdateCategoryBadges();
        }
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo?.SelectedItem is ComboBoxItem item && ViewModel != null)
        {
            var tag = item.Content?.ToString();
            var current = ViewModel.FilteredDownloads.ToList();
            switch (tag)
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
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
        SidebarToggleBtn.IsChecked = MainSplitView.IsPaneOpen;
    }

    private void ToggleInspector_Click(object sender, RoutedEventArgs e)
    {
        bool visible = BottomInspectorPanel.Visibility == Visibility.Visible;
        BottomInspectorPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        InspectorRow.Height = new GridLength(visible ? 0 : 225);
    }

    private void InspectorTab_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralTabView == null || ProgressTabView == null || ConnectionsTabView == null) return;
        GeneralTabView.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProgressTabView.Visibility = TabProgress.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsTabView.Visibility = TabConnections.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
        await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
        UpdateCategoryBadges();
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteCompletedCommand.ExecuteAsync(null);
        UpdateCategoryBadges();
    }

    private void SnailModeBtn_Click(object sender, RoutedEventArgs e)
    {
        _isSnailMode = !_isSnailMode;
        if (_isSnailMode)
        {
            SnailModeText.Text = "SNAIL ON";
            SnailModeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248));
            ViewModel.StatusMessage = "Snail Mode active: Bandwidth limited to 25%";
        }
        else
        {
            SnailModeText.Text = "SNAIL OFF";
            SnailModeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175));
            ViewModel.StatusMessage = "Snail Mode disabled: Full bandwidth restored";
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
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
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
                        AddUrlViewModel addVm = new(probe) { Url = uri.AbsoluteUri };
                        var dialog = new DownloadFileInfoDialog(addVm);
                        if (await ShowContentDialogSafeAsync(dialog) == ContentDialogResult.Primary)
                        {
                            var fileName = string.IsNullOrWhiteSpace(addVm.FileName) ? "download.bin" : addVm.FileName;
                            var destPath = Path.Combine(addVm.SaveDirectory, addVm.SelectedCategory, fileName);
                            var newItem = new DownloadItemViewModel
                            {
                                Id = Guid.NewGuid(),
                                Url = addVm.Url,
                                FileName = fileName,
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
        AddUrlViewModel addVm = new(probe);

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
            var destPath = Path.Combine(addVm.SaveDirectory, addVm.SelectedCategory, fileName);
            var id = Guid.NewGuid();

            DownloadItemViewModel newItem = new()
            {
                Id = id,
                Url = addVm.Url,
                FileName = fileName,
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
        try
        {
            if (IsStreamingMediaPlatformUrl(req.Url))
            {
                var mediaVm = App.Services.GetRequiredService<MediaDownloadViewModel>();
                mediaVm.MediaUrl = req.Url;
                _ = mediaVm.ProbeMediaCommand.ExecuteAsync(null);

                var mediaDialog = new MediaDownloadDialog(mediaVm)
                {
                    XamlRoot = Content.XamlRoot
                };
                var mediaResult = await mediaDialog.ShowAsync();
                if (mediaResult == ContentDialogResult.Primary || mediaResult == ContentDialogResult.Secondary)
                {
                    await ReloadDownloadsFromDatabaseAsync();
                    if (mediaResult == ContentDialogResult.Primary && mediaVm.LastEnqueuedItem != null)
                    {
                        var enqueuedVm = ViewModel.AllDownloads.FirstOrDefault(d => d.Id.ToString("N") == mediaVm.LastEnqueuedItem.Id);
                        if (enqueuedVm != null)
                        {
                            _ = StartMediaDownloadAsync(enqueuedVm);
                        }
                    }
                }
                return;
            }

            var initialFileName = req.FileName;
            if (string.IsNullOrWhiteSpace(initialFileName) && !string.IsNullOrWhiteSpace(req.DestinationPath) && Path.HasExtension(req.DestinationPath))
            {
                initialFileName = Path.GetFileName(req.DestinationPath);
            }

            HttpProbeService probe = App.Services.GetRequiredService<HttpProbeService>();
            AddUrlViewModel addVm = new(probe)
            {
                Url = req.Url,
                FileName = initialFileName ?? string.Empty
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

            var dialog = new DownloadFileInfoDialog(addVm);
            var result = await ShowContentDialogSafeAsync(dialog);
            var db = App.Services.GetRequiredService<ISwiftGetDatabase>();

            if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
            {
                var fileName = string.IsNullOrWhiteSpace(addVm.FileName) ? "download.bin" : addVm.FileName;
                var destPath = Path.Combine(addVm.SaveDirectory, addVm.SelectedCategory, fileName);
                var downloadId = !string.IsNullOrWhiteSpace(req.DownloadId) ? req.DownloadId : Guid.NewGuid().ToString("N");
                var idGuid = Guid.TryParse(downloadId, out var g) ? g : Guid.NewGuid();

                var newItem = new DownloadItemViewModel
                {
                    Id = idGuid,
                    Url = addVm.Url,
                    FileName = fileName,
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
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            await HandleTorrentAddConfirmedAsync(addVm, startImmediately: result == ContentDialogResult.Primary);
        }
    }

    public async Task HandleTorrentRequestAsync(string source, bool startImmediately = true, string? saveDir = null, bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(source)) return;

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
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            await HandleTorrentAddConfirmedAsync(addVm, startImmediately: result == ContentDialogResult.Primary);
        }
    }

    private async Task HandleTorrentAddConfirmedAsync(AddTorrentViewModel addVm, bool startImmediately)
    {
        var torrentEngine = App.Services.GetRequiredService<ITorrentEngine>();
        var db = App.Services.GetRequiredService<ISwiftGetDatabase>();

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
            Status = startImmediately ? DownloadStatus.Downloading : DownloadStatus.Queued,
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
                Status = startImmediately ? DownloadStatus.Downloading : DownloadStatus.Queued,
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
        if (ViewModel.SelectedItem != null)
        {
            OpenDownloadDetails(ViewModel.SelectedItem);
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

    private void ToggleSequential_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null && ViewModel.SelectedItem.JobKind == JobKind.Torrent)
        {
            ViewModel.SelectedItem.IsSequential = !ViewModel.SelectedItem.IsSequential;
            ShowInfoDialog("Sequential Download", $"Sequential download for '{ViewModel.SelectedItem.FileName}' is now {(ViewModel.SelectedItem.IsSequential ? "enabled" : "disabled")}.");
        }
    }

    private async void DeleteContext_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
        UpdateCategoryBadges();
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
        var path = ViewModel.SelectedItem?.SavePath ?? InspectorFolder?.Text;
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

        var dialog = new MediaDownloadDialog(mediaVm)
        {
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            await ReloadDownloadsFromDatabaseAsync();
            if (result == ContentDialogResult.Primary && mediaVm.LastEnqueuedItem != null)
            {
                var enqueuedVm = ViewModel.AllDownloads.FirstOrDefault(d => d.Id.ToString("N") == mediaVm.LastEnqueuedItem.Id);
                if (enqueuedVm != null)
                {
                    _ = StartMediaDownloadAsync(enqueuedVm);
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

    public void RestoreMainWindow()
    {
        _appWindow?.Show();
        _trayManager?.RestoreWindow();
        Activate();
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

                var progress = new Progress<long>(downloaded =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var now = DateTime.UtcNow;
                        var elapsed = (now - lastTime).TotalSeconds;
                        double speed = 0;
                        if (elapsed >= 0.5)
                        {
                            speed = Math.Max(0, (downloaded - lastBytes) / elapsed);
                            lastTime = now;
                            lastBytes = downloaded;
                        }

                        var total = dbItem.TotalBytes > 0 ? dbItem.TotalBytes : vm.TotalBytes;
                        vm.UpdateProgress(downloaded, total, speed);
                        ViewModel.UpdateMetrics();
                        if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                    });
                });

                await coordinator.StartAsync(dbItem, progress: progress, ct: cts.Token).ConfigureAwait(false);

                DispatcherQueue.TryEnqueue(async () =>
                {
                    vm.Status = DownloadStatus.Completed;
                    var total = dbItem.TotalBytes > 0 ? dbItem.TotalBytes : vm.TotalBytes;
                    vm.UpdateProgress(total, total, 0);
                    ViewModel.UpdateMetrics();
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
                    UpdateCategoryBadges();
                    if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "HTTP download {Id} failed", vm.Id);
                DispatcherQueue.TryEnqueue(() =>
                {
                    vm.Status = DownloadStatus.Failed;
                    vm.SpeedBytesPerSec = 0;
                    vm.RemainingTime = TimeSpan.Zero;
                    ViewModel.UpdateMetrics();
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

    public Task StartMediaDownloadAsync(DownloadItemViewModel vm, CancellationToken externalCt = default)
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

                void OnProgress(object? sender, double percent)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var total = dbItem.TotalBytes > 0 ? dbItem.TotalBytes : vm.TotalBytes;
                        long current = total > 0 ? (long)(total * (percent / 100.0)) : 0;
                        var now = DateTime.UtcNow;
                        var elapsed = (now - lastTime).TotalSeconds;
                        double speed = 0;
                        if (elapsed >= 0.5 && current > lastBytes)
                        {
                            speed = Math.Max(0, (current - lastBytes) / elapsed);
                            lastTime = now;
                            lastBytes = current;
                        }

                        vm.ProgressPercentage = percent;
                        vm.UpdateProgress(current, total, speed);
                        ViewModel.UpdateMetrics();
                        if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                    });
                }

                recorder.ProgressChanged += OnProgress;
                try
                {
                    await recorder.RecordStreamAsync(
                        streamUrl: dbItem.Url,
                        destinationPath: dbItem.DestinationPath,
                        ct: cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    recorder.ProgressChanged -= OnProgress;
                }

                long finalSize = 0;
                if (File.Exists(dbItem.DestinationPath))
                {
                    finalSize = new FileInfo(dbItem.DestinationPath).Length;
                }
                if (finalSize > 0)
                {
                    dbItem.TotalBytes = finalSize;
                    dbItem.DownloadedBytes = finalSize;
                    dbItem.Status = DownloadStatus.Completed;
                    dbItem.CompletedAtUtc = DateTime.UtcNow;
                    await db.SaveDownloadAsync(dbItem, CancellationToken.None).ConfigureAwait(false);

                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        vm.Status = DownloadStatus.Completed;
                        vm.UpdateProgress(finalSize, finalSize, 0);
                        ViewModel.UpdateMetrics();
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
                    UpdateCategoryBadges();
                    if (ViewModel.SelectedItem == vm) UpdateInspectorDetails(vm);
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Media download {Id} failed", vm.Id);
                DispatcherQueue.TryEnqueue(() =>
                {
                    vm.Status = DownloadStatus.Failed;
                    vm.SpeedBytesPerSec = 0;
                    vm.RemainingTime = TimeSpan.Zero;
                    ViewModel.UpdateMetrics();
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
