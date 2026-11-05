using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SwiftGet.App.ViewModels;
using SwiftGet.Engine.Coordinator;

namespace SwiftGet.App.Views;

public sealed partial class DownloadProgressDialog : Window
{
    private const uint WM_SETICON = 0x0080;
    private const uint ICON_SMALL = 0;
    private const uint ICON_BIG = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOMOVE = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private readonly AppWindow? _appWindow;
    private readonly IntPtr _hWnd;
    private readonly DownloadCoordinator? _coordinator;
    private bool _isAdjustingSize;
    private bool _isClosed;

    public DownloadDialogViewModel ViewModel { get; }

    public DownloadProgressDialog(DownloadDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Title = $"SwiftGet - {ViewModel.Item.FileName}";

        try
        {
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow != null)
            {
                _appWindow.Title = $"SwiftGet - {ViewModel.Item.FileName}";

                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = true;
                    presenter.IsResizable = true;
                }

                ApplyWindowIcon();
                ApplyWindowSize(ViewModel.ShowDetails, center: true);

                _appWindow.Changed += (s, args) =>
                {
                    if (args.DidSizeChange && !_isAdjustingSize)
                    {
                        uint dpi = GetDpiForWindow(_hWnd);
                        double scale = dpi > 0 ? (dpi / 96.0) : 1.0;
                        int minW = (int)Math.Round(460 * scale);
                        int minH = (int)Math.Round(220 * scale);

                        var client = s.ClientSize;
                        if (client.Width > 0 && client.Height > 0)
                        {
                            int clampedW = Math.Max(minW, client.Width);
                            int clampedH = Math.Max(minH, client.Height);
                            if (clampedW != client.Width || clampedH != client.Height)
                            {
                                s.ResizeClient(new Windows.Graphics.SizeInt32(clampedW, clampedH));
                            }
                        }
                    }
                };
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to initialize AppWindow configuration for DownloadProgressDialog");
        }

        Activated += (s, e) =>
        {
            ApplyWindowIcon();
            ApplyWindowSize(ViewModel.ShowDetails, center: false);
        };

        // Wire live connection telemetry directly from DownloadCoordinator
        try
        {
            _coordinator = App.Services.GetService<DownloadCoordinator>();
            if (_coordinator != null)
            {
                _coordinator.ConnectionsUpdated += OnConnectionsUpdated;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to bind DownloadCoordinator to DownloadProgressDialog");
        }

        Closed += (s, e) =>
        {
            _isClosed = true;

            try
            {
                if (_coordinator != null)
                {
                    _coordinator.ConnectionsUpdated -= OnConnectionsUpdated;
                }
            }
            catch { }

            try
            {
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                ViewModel.Item.PropertyChanged -= OnItemPropertyChanged;
            }
            catch { }

            try
            {
                Bindings?.StopTracking();
            }
            catch { }
        };

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Item.PropertyChanged += OnItemPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isClosed) return;
        if (e.PropertyName == nameof(DownloadDialogViewModel.ShowDetails))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isClosed) return;
                ApplyWindowSize(ViewModel.ShowDetails, center: false);
            });
        }
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isClosed) return;
        if (e.PropertyName == nameof(DownloadItemViewModel.FileName))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isClosed) return;
                Title = $"SwiftGet - {ViewModel.Item.FileName}";
                if (_appWindow != null)
                {
                    _appWindow.Title = $"SwiftGet - {ViewModel.Item.FileName}";
                }
            });
        }
        else if (e.PropertyName == nameof(DownloadItemViewModel.Status) && ViewModel.Item.Status == Core.Models.DownloadStatus.Completed)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isClosed) return;
                if (ViewModel.CloseOnCompletion)
                {
                    Close();
                }
            });
        }
    }

    private void OnConnectionsUpdated(object? sender, (string DownloadId, IReadOnlyList<SwiftGet.Engine.Turbo.ConnectionSnapshot> Connections) args)
    {
        if (_isClosed) return;

        var currentId = ViewModel.Item.Id.ToString("N");
        var targetNorm = (args.DownloadId ?? string.Empty).Replace("-", string.Empty);

        if (!string.Equals(currentId, targetNorm, StringComparison.OrdinalIgnoreCase) &&
            !(Guid.TryParse(args.DownloadId, out var parsedGuid) && ViewModel.Item.Id == parsedGuid))
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isClosed) return;

            var conns = args.Connections;
            if (conns == null || conns.Count == 0) return;

            for (int i = 0; i < conns.Count; i++)
            {
                var conn = conns[i];
                if (i < ViewModel.Item.Segments.Count)
                {
                    var seg = ViewModel.Item.Segments[i];
                    seg.Index = conn.Index;
                    seg.Status = conn.Status;
                    seg.StartOffset = conn.StartOffset;
                    seg.EndOffset = conn.EndOffset;
                    seg.Update(conn.DownloadedBytes, conn.SpeedBytesPerSec);
                    seg.UpdateMetrics(ViewModel.Item.TotalBytes);
                }
                else
                {
                    var seg = new SegmentProgressViewModel(conn.Index, conn.StartOffset, conn.EndOffset)
                    {
                        Status = conn.Status
                    };
                    seg.Update(conn.DownloadedBytes, conn.SpeedBytesPerSec);
                    seg.UpdateMetrics(ViewModel.Item.TotalBytes);
                    ViewModel.Item.Segments.Add(seg);
                }
            }

            while (ViewModel.Item.Segments.Count > conns.Count)
            {
                ViewModel.Item.Segments.RemoveAt(ViewModel.Item.Segments.Count - 1);
            }
        });
    }

    private void ApplyWindowIcon()
    {
        var iconPath = ResolveAppIconPath();
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            try
            {
                _appWindow?.SetIcon(iconPath);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to set AppWindow icon on DownloadProgressDialog");
            }

            if (_hWnd != IntPtr.Zero)
            {
                try
                {
                    var hIconSmall = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                    if (hIconSmall == IntPtr.Zero)
                    {
                        hIconSmall = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                    }
                    if (hIconSmall != IntPtr.Zero)
                    {
                        SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconSmall);
                    }

                    var hIconBig = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                    if (hIconBig == IntPtr.Zero)
                    {
                        hIconBig = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                    }
                    if (hIconBig != IntPtr.Zero)
                    {
                        SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_BIG, hIconBig);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Failed to set Win32 WM_SETICON on DownloadProgressDialog");
                }
            }
        }

        // Always fallback to process executable icon if AppWindow.SetIcon didn't catch
        if (_hWnd != IntPtr.Zero)
        {
            try
            {
                var procPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(procPath) && File.Exists(procPath))
                {
                    var hIcon = ExtractIconW(IntPtr.Zero, procPath, 0);
                    if (hIcon != IntPtr.Zero)
                    {
                        SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_SMALL, hIcon);
                        SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_BIG, hIcon);
                    }
                }
            }
            catch { }
        }
    }

    private void ApplyWindowSize(bool showDetails, bool center = false)
    {
        if (_isClosed || _appWindow == null || _hWnd == IntPtr.Zero)
        {
            return;
        }

        if (_appWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized)
        {
            return;
        }

        _isAdjustingSize = true;
        try
        {
            uint dpi = GetDpiForWindow(_hWnd);
            double scale = dpi > 0 ? (dpi / 96.0) : 1.0;

            int targetW = (int)Math.Round(480 * scale);
            int targetH = (int)Math.Round((showDetails ? 385 : 215) * scale);

            if (targetW > 0 && targetH > 0)
            {
                _appWindow.ResizeClient(new Windows.Graphics.SizeInt32(targetW, targetH));
            }

            if (center)
            {
                var displayArea = DisplayArea.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd),
                    DisplayAreaFallback.Primary);

                if (displayArea != null)
                {
                    int posX = displayArea.WorkArea.X + (displayArea.WorkArea.Width - targetW) / 2;
                    int posY = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - targetH) / 2;
                    SetWindowPos(_hWnd, IntPtr.Zero, posX, posY, targetW, targetH, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
            }
            else
            {
                SetWindowPos(_hWnd, IntPtr.Zero, 0, 0, targetW, targetH, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE | SWP_SHOWWINDOW);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to resize DownloadProgressDialog");
        }
        finally
        {
            _isAdjustingSize = false;
        }
    }

    private static string? ResolveAppIconPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "assets", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "installer", "assets", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "app.ico"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCommand.Execute(null);
        Close();
    }
}
