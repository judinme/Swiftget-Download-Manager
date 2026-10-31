using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Views;

public sealed partial class DownloadProgressDialog : Window
{
    private const uint WM_SETICON = 0x0080;
    private const uint ICON_SMALL = 0;
    private const uint ICON_BIG = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private readonly AppWindow? _appWindow;
    private readonly IntPtr _hWnd;
    private bool _isAdjustingSize;

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
                        int minW = (int)Math.Round(480 * scale);
                        int minH = (int)Math.Round(260 * scale);

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

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DownloadDialogViewModel.ShowDetails))
            {
                DispatcherQueue?.TryEnqueue(() => ApplyWindowSize(ViewModel.ShowDetails, center: false));
            }
        };

        ViewModel.Item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DownloadItemViewModel.FileName))
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
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
                    if (ViewModel.CloseOnCompletion)
                    {
                        Close();
                    }
                });
            }
        };
    }

    private void ApplyWindowIcon()
    {
        var iconPath = ResolveAppIconPath();
        if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
        {
            return;
        }

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
                if (hIconSmall != IntPtr.Zero)
                {
                    SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconSmall);
                }

                var hIconBig = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
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

    private void ApplyWindowSize(bool showDetails, bool center = false)
    {
        if (_appWindow == null || _hWnd == IntPtr.Zero)
        {
            return;
        }

        _isAdjustingSize = true;
        try
        {
            uint dpi = GetDpiForWindow(_hWnd);
            double scale = dpi > 0 ? (dpi / 96.0) : 1.0;

            int targetW = (int)Math.Round(520 * scale);
            int targetH = (int)Math.Round((showDetails ? 490 : 340) * scale);

            _appWindow.ResizeClient(new Windows.Graphics.SizeInt32(targetW, targetH));

            if (center)
            {
                var displayArea = DisplayArea.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd),
                    DisplayAreaFallback.Primary);

                if (displayArea != null)
                {
                    int posX = displayArea.WorkArea.X + (displayArea.WorkArea.Width - targetW) / 2;
                    int posY = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - targetH) / 2;
                    _appWindow.Move(new Windows.Graphics.PointInt32(posX, posY));
                }
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
        Close();
    }
}
