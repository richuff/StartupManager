using System.IO;
using System.Runtime.InteropServices;

namespace StartupManager.Gui;

/// <summary>
/// Minimal system-tray icon implemented with Win32 Shell_NotifyIcon, so the project
/// does not need System.Windows.Forms (which would add ~30 MB to a self-contained
/// publish and inject conflicting implicit usings into WPF).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int WM_USER = 0x0400;

    // Callback message posted to the window when the user interacts with the icon.
    public const int TrayCallbackMessage = WM_USER + 1;

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type,
        int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    private const int ID_SHOW = 1;
    private const int ID_EXIT = 2;

    private readonly IntPtr _hWnd;
    private NOTIFYICONDATA _data;
    private bool _added;
    private IntPtr _hIcon;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    /// <param name="hWnd">Window handle that receives the tray callback messages.</param>
    /// <param name="iconPath">Path to the .ico used for the tray icon.</param>
    public TrayIcon(IntPtr hWnd, string? iconPath)
    {
        _hWnd = hWnd;

        _hIcon = IntPtr.Zero;
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            // LR_LOADFROMFILE = 0x0010, IMAGE_ICON = 1
            _hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x0010);
        }
        if (_hIcon == IntPtr.Zero)
        {
            // IDI_APPLICATION = 32512
            _hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _hIcon,
            szTip = "启动项管理器",
        };

        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public bool IsAvailable => _added;

    /// <summary>Show a balloon tip (best effort).</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        try
        {
            var d = _data;
            d.uFlags = NIF_INFO;
            d.szInfoTitle = title;
            d.szInfo = text;
            d.dwInfoFlags = 1; // NIIF_INFO
            d.uTimeoutOrVersion = 2000;
            Shell_NotifyIcon(NIM_MODIFY, ref d);
        }
        catch { /* best effort */ }
    }

    /// <summary>Call from the window's WndProc when TrayCallbackMessage arrives.</summary>
    public void HandleCallback(IntPtr lParam)
    {
        var msg = (int)lParam & 0xFFFF;
        if (msg == WM_LBUTTONDBLCLK)
        {
            ShowRequested?.Invoke();
        }
        else if (msg == WM_RBUTTONUP)
        {
            ShowContextMenu();
        }
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, new IntPtr(ID_SHOW), "显示主窗口");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, "");
            AppendMenu(menu, MF_STRING, new IntPtr(ID_EXIT), "退出");

            GetCursorPos(out var p);
            SetForegroundWindow(_hWnd);
            var cmd = TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RETURNCMD | TPM_NONOTIFY,
                p.X, p.Y, 0, _hWnd, IntPtr.Zero);

            if (cmd == ID_SHOW) ShowRequested?.Invoke();
            else if (cmd == ID_EXIT) ExitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = _data;
            Shell_NotifyIcon(NIM_DELETE, ref d);
            _added = false;
        }
        // Only destroy icons we loaded from a file, not shared system icons.
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
