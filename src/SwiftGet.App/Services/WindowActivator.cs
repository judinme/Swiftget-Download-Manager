using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.Graphics;

namespace SwiftGet.App.Services;

/// <summary>
/// Unified, robust Win32/WinUI 3 window activator.
/// Honors Windows foreground lock policies, multi-monitor display placement,
/// fullscreen D3D/presentation mode non-intrusive fallbacks, and taskbar flashing.
/// </summary>
public static class WindowActivator
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const uint FLASHW_STOP = 0;
    private const uint FLASHW_CAPTION = 1;
    private const uint FLASHW_TRAY = 2;
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMER = 4;
    private const uint FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

    private static DateTime _lastActivationUtc = DateTime.MinValue;

    /// <summary>
    /// Brings a target Window or dialog reliably into the foreground.
    /// Handles un-minimizing, monitor positioning, foreground lock evasion,
    /// and presentation/game mode non-intrusive fallbacks.
    /// </summary>
    public static void Activate(
        Window window,
        int? browserLeft = null,
        int? browserTop = null,
        int? browserWidth = null,
        int? browserHeight = null)
    {
        if (window == null) return;

        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hWnd == IntPtr.Zero) return;

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            // 1. Position on the browser's active monitor if coordinates provided
            if (appWindow != null && browserLeft.HasValue && browserTop.HasValue)
            {
                PositionOnBrowserMonitor(appWindow, browserLeft.Value, browserTop.Value, browserWidth ?? 1024, browserHeight ?? 768);
            }

            // 2. Coalesce rapid bursts within 400ms
            var now = DateTime.UtcNow;
            bool isBurst = (now - _lastActivationUtc).TotalMilliseconds < 400;
            _lastActivationUtc = now;

            // 3. Inspect OS User Notification State (Respect Fullscreen Games & Presentations)
            if (OperatingSystem.IsWindows())
            {
                int hr = SHQueryUserNotificationState(out var userState);
                if (hr == 0 && (userState == QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY ||
                                userState == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                                userState == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE))
                {
                    Log.Information("[WindowActivator] User in presentation/fullscreen mode ({State}). Using non-activating pulse.", userState);
                    PulseTopmostWithoutFocus(hWnd);
                    FlashTaskbar(hWnd);
                    return;
                }
            }

            // 4. Restore if minimized or in tray
            if (appWindow?.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }
            }
            ShowWindow(hWnd, SW_RESTORE);

            // 5. Attempt foreground activation
            appWindow?.Show(activateWindow: true);
            BringWindowToTop(hWnd);
            bool setFgSuccess = SetForegroundWindow(hWnd);

            // 6. Check if Windows locked out SetForegroundWindow
            var currentFg = GetForegroundWindow();
            if (currentFg != hWnd)
            {
                // Fallback: brief topmost pulse and taskbar flash
                Log.Debug("[WindowActivator] SetForegroundWindow locked by OS. Applying topmost pulse fallback.");
                PulseTopmostWithoutFocus(hWnd);
                if (!isBurst)
                {
                    FlashTaskbar(hWnd);
                }
            }
            else
            {
                Log.Debug("[WindowActivator] Window successfully activated in foreground.");
            }

            window.Activate();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WindowActivator] Activation encountered an unexpected error.");
        }
    }

    private static void PositionOnBrowserMonitor(AppWindow appWindow, int bLeft, int bTop, int bWidth, int bHeight)
    {
        try
        {
            int centerX = bLeft + (bWidth / 2);
            int centerY = bTop + (bHeight / 2);

            var displayArea = DisplayArea.GetFromPoint(new PointInt32(centerX, centerY), DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var work = displayArea.WorkArea;
                int currentW = appWindow.Size.Width;
                int currentH = appWindow.Size.Height;

                if (currentW > 0 && currentH > 0)
                {
                    int targetX = work.X + Math.Max(0, (work.Width - currentW) / 2);
                    int targetY = work.Y + Math.Max(0, (work.Height - currentH) / 2);
                    appWindow.Move(new PointInt32(targetX, targetY));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[WindowActivator] Error centering window on browser monitor.");
        }
    }

    private static void PulseTopmostWithoutFocus(IntPtr hWnd)
    {
        try
        {
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch { }
    }

    private static void FlashTaskbar(IntPtr hWnd)
    {
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hWnd,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 3,
                dwTimeout = 0
            };
            FlashWindowEx(ref info);
        }
        catch { }
    }
}
