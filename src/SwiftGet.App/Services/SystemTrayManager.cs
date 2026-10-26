using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace SwiftGet.App.Services;

public sealed class SystemTrayManager : IDisposable
{
    private const uint WmUser = 0x0400;
    private const uint WmTrayCallback = WmUser + 219;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;

    private const uint NiifInfo = 0x00000001;

    private const uint WmLbuttonUp = 0x0202;
    private const uint WmLbuttonDblClk = 0x0203;
    private const uint WmRbuttonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfDefault = 0x00001000;

    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hWnd;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SubclassProc _subclassProc;
    private IntPtr _hIcon;
    private bool _isCreated;
    private bool _isSubclassed;
    private bool _isDisposed;

    public event Action? RestoreRequested;
    public event Action? AddDownloadRequested;
    public event Action? ToggleDropBoxRequested;
    public event Action? ExitRequested;

    public SystemTrayManager(IntPtr hWnd, DispatcherQueue dispatcherQueue)
    {
        _hWnd = hWnd;
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _subclassProc = OnSubclassMessage;
    }

    public void Initialize()
    {
        if (_isCreated || _hWnd == IntPtr.Zero) return;

        // 1. Subclass window to intercept tray callback messages
        _isSubclassed = SetWindowSubclass(_hWnd, _subclassProc, 1, 0);

        // 2. Load tray icon from assets
        _hIcon = LoadTrayIcon();

        // 3. Create tray icon
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NifMessage | NifTip | (_hIcon != IntPtr.Zero ? NifIcon : 0),
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
            szTip = "SwiftGet Download Manager"
        };

        _isCreated = Shell_NotifyIconW(NimAdd, ref data);

        if (_isCreated)
        {
            var versionData = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = 1,
                uTimeoutOrVersion = NotifyIconVersion4
            };
            Shell_NotifyIconW(NimSetVersion, ref versionData);
        }
    }

    public void RestoreWindow()
    {
        if (_hWnd != IntPtr.Zero)
        {
            ShowWindow(_hWnd, 9); // SW_RESTORE = 9
            SetForegroundWindow(_hWnd);
        }
    }

    public void ShowNotification(string title, string message)
    {
        if (!_isCreated || _hWnd == IntPtr.Zero) return;

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NifInfo,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            szInfo = message.Length > 255 ? message[..255] : message,
            dwInfoFlags = NiifInfo
        };

        Shell_NotifyIconW(NimModify, ref data);
    }

    private IntPtr OnSubclassMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WmTrayCallback)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);

            if (mouseMsg == WmLbuttonUp || mouseMsg == WmLbuttonDblClk)
            {
                _dispatcherQueue.TryEnqueue(() => RestoreRequested?.Invoke());
                return IntPtr.Zero;
            }

            if (mouseMsg == WmRbuttonUp || mouseMsg == WmContextMenu)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var pt)) return;

        SetForegroundWindow(_hWnd);
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(hMenu, MfString | MfDefault, 1001, "Open SwiftGet");
            AppendMenuW(hMenu, MfSeparator, 0, null);
            AppendMenuW(hMenu, MfString, 1002, "Add Download...");
            AppendMenuW(hMenu, MfString, 1003, "Drop Target Window");
            AppendMenuW(hMenu, MfSeparator, 0, null);
            AppendMenuW(hMenu, MfString, 1004, "Exit SwiftGet");

            uint cmd = TrackPopupMenuEx(hMenu, TpmRightButton | TpmReturnCmd, pt.X, pt.Y, _hWnd, IntPtr.Zero);
            PostMessageW(_hWnd, 0, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case 1001:
                    _dispatcherQueue.TryEnqueue(() => RestoreRequested?.Invoke());
                    break;
                case 1002:
                    _dispatcherQueue.TryEnqueue(() => AddDownloadRequested?.Invoke());
                    break;
                case 1003:
                    _dispatcherQueue.TryEnqueue(() => ToggleDropBoxRequested?.Invoke());
                    break;
                case 1004:
                    _dispatcherQueue.TryEnqueue(() => ExitRequested?.Invoke());
                    break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private static IntPtr LoadTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                var hIcon = LoadImageW(IntPtr.Zero, iconPath, 1, 16, 16, 0x0010);
                if (hIcon != IntPtr.Zero)
                {
                    return hIcon;
                }
            }
        }
        catch { }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_isCreated && _hWnd != IntPtr.Zero)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hWnd,
                uID = 1
            };
            Shell_NotifyIconW(NimDelete, ref data);
            _isCreated = false;
        }

        if (_isSubclassed && _hWnd != IntPtr.Zero)
        {
            RemoveWindowSubclass(_hWnd, _subclassProc, 1);
            _isSubclassed = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
