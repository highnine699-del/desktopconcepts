using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopConcepts.UI;

/// <summary>
/// System-tray icon implemented directly via Shell_NotifyIcon Win32 API.
/// Zero NuGet dependencies — works on any Windows 10/11 machine without extra packages.
///
/// Provides:
///   - Left-click  → toggle widget visibility
///   - Right-click → context menu: Open/Hide · Open Settings · Quit
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ── Win32 constants ──────────────────────────────────────────────────────
    private const int WM_USER               = 0x0400;
    private const int WM_TRAYICON           = WM_USER + 1;
    private const int WM_LBUTTONUP          = 0x0202;
    private const int WM_RBUTTONUP          = 0x0205;
    private const int NIM_ADD               = 0x00;
    private const int NIM_DELETE            = 0x02;
    private const int NIM_SETVERSION        = 0x04;
    private const int NIF_MESSAGE           = 0x01;
    private const int NIF_ICON              = 0x02;
    private const int NIF_TIP              = 0x04;
    private const int NIIF_INFO             = 0x01;
    private const int NOTIFYICON_VERSION_4  = 4;

    // ── Win32 context menu ───────────────────────────────────────────────────
    private const int MF_STRING    = 0x00000000;
    private const int MF_SEPARATOR = 0x00000800;
    private const int TPM_RETURNCMD = 0x0100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint  cbSize;
        public IntPtr hWnd;
        public uint  uID;
        public uint  uFlags;
        public uint  uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint  dwState;
        public uint  dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint  uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint  dwInfoFlags;
        public Guid  guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // ── Menu item IDs ────────────────────────────────────────────────────────
    private const uint CMD_TOGGLE   = 1001;
    private const uint CMD_SETTINGS = 1002;
    private const uint CMD_QUIT     = 1003;

    // ── State ────────────────────────────────────────────────────────────────
    private readonly Window  _window;
    private readonly IntPtr  _hwnd;
    private NOTIFYICONDATA   _nid;
    private bool             _disposed;
    private HwndSource?      _hwndSource;

    public event Action? ToggleRequested;
    public event Action? OpenSettingsRequested;
    public event Action? QuitRequested;

    public TrayIcon(Window window)
    {
        _window = window;
        _hwnd   = new WindowInteropHelper(window).Handle;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        _nid = new NOTIFYICONDATA
        {
            cbSize          = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd            = _hwnd,
            uID             = 1,
            uFlags          = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon           = LoadIcon(IntPtr.Zero, (IntPtr)32512), // IDI_APPLICATION
            szTip           = "DesktopConcepts — daily tech concepts",
        };

        Shell_NotifyIcon(NIM_ADD, ref _nid);

        // Set NOTIFYICON_VERSION_4 for proper taskbar positioning
        _nid.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref _nid);
    }

    // ── Win32 message pump hook ───────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAYICON) return IntPtr.Zero;

        var mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);

        if (mouseMsg == WM_LBUTTONUP)
        {
            handled = true;
            ToggleRequested?.Invoke();
        }
        else if (mouseMsg == WM_RBUTTONUP)
        {
            handled = true;
            ShowContextMenu();
        }

        return IntPtr.Zero;
    }

    // ── Tray context menu ─────────────────────────────────────────────────────

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            var label = _window.IsVisible ? "Hide widget" : "Show widget";
            AppendMenu(menu, MF_STRING,    CMD_TOGGLE,   label);
            AppendMenu(menu, MF_STRING,    CMD_SETTINGS, "AI Settings…");
            AppendMenu(menu, MF_SEPARATOR, 0,            string.Empty);
            AppendMenu(menu, MF_STRING,    CMD_QUIT,     "Quit DesktopConcepts");

            // Must set foreground window so the menu dismisses on click-outside
            SetForegroundWindow(_hwnd);
            GetCursorPos(out var pt);

            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            switch (cmd)
            {
                case CMD_TOGGLE:   ToggleRequested?.Invoke();       break;
                case CMD_SETTINGS: OpenSettingsRequested?.Invoke(); break;
                case CMD_QUIT:     QuitRequested?.Invoke();         break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIcon(NIM_DELETE, ref _nid);
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }
}
