using System.Runtime.InteropServices;

namespace AviatesAirTracker.Platform;

/// <summary>
/// The notification-area (system tray) icon, over Shell_NotifyIcon. WinUI 3 has no tray API and
/// this project avoids extra UI packages, so it's a thin P/Invoke wrapper. The icon's callback
/// messages arrive at the shell window's HWND; <see cref="HandleMessage"/> is fed every message
/// from ShellWindow's existing <see cref="WindowSubclass"/> hook.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = 0x8000 + 0x0A7; // WM_APP + n

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, WM_NULL = 0;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const int CmdOpen = 1, CmdQuit = 2;
    private const uint IconId = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, nint[]? phiconLarge, nint[]? phiconSmall, uint nIcons);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string lpString);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint hMenu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly nint _hwnd;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private string _tooltip;
    private bool _visible;

    /// <summary>Single click, or "Open" in the menu.</summary>
    public event Action? OpenRequested;
    /// <summary>"Quit" in the menu.</summary>
    public event Action? QuitRequested;

    public TrayIcon(nint hwnd, string tooltip)
    {
        _hwnd = hwnd;
        _tooltip = tooltip;
        var small = new nint[1];
        if (Environment.ProcessPath is { } exe && ExtractIconExW(exe, 0, null, small, 1) > 0)
            _icon = small[0];
        // Sent to every top-level window when Explorer restarts; the icon has to be re-added.
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    public bool IsVisible => _visible;

    public void Show()
    {
        if (_visible) return;
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _visible = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    public void Hide()
    {
        if (!_visible) return;
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _visible = false;
    }

    public void SetTooltip(string tooltip)
    {
        tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (tooltip == _tooltip) return;
        _tooltip = tooltip;
        if (!_visible) return;
        var data = Data(NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Called for every message the shell window receives, on the UI thread.</summary>
    public void HandleMessage(int msg, nint lParam)
    {
        if (_taskbarCreated != 0 && msg == (int)_taskbarCreated && _visible)
        {
            _visible = false;
            Show();
            return;
        }
        if (msg != CallbackMessage) return;

        switch ((int)lParam)
        {
            case WM_LBUTTONUP:
                OpenRequested?.Invoke();
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenuW(menu, MF_STRING, CmdOpen, "Open Aviates Air Tracker");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdQuit, "Quit");

            GetCursorPos(out var pt);
            // Without this the menu doesn't close when the pilot clicks elsewhere.
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);

            if (cmd == CmdOpen) OpenRequested?.Invoke();
            else if (cmd == CmdQuit) QuitRequested?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Dispose()
    {
        Hide();
        if (_icon != 0) DestroyIcon(_icon);
    }
}
