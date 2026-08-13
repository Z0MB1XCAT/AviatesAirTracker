using System.Runtime.InteropServices;
using AviatesAirTracker.Platform;
using Xunit;

namespace AviatesAirTracker.Tests.Platform;

/// <summary>
/// Skips instead of failing when the test run is not on Windows. Dependency-free
/// stand-in for xunit.skippablefact.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows: creates a real Win32 message-only window.";
    }
}

/// <summary>
/// Real-HWND coverage for <see cref="WindowSubclass"/>. These tests create an actual
/// message-only window, subclass it, and drive genuine window messages through
/// <c>SendMessage</c>, so the native side of the contract is exercised rather than
/// simulated.
/// </summary>
public class WindowSubclassInteropTests
{
    private const int WM_USER_SIMCONNECT = 0x0402;
    private static readonly nint HWND_MESSAGE = -3;

    /// <summary>
    /// THE regression guard for the migration's worst failure mode.
    ///
    /// <para>
    /// <c>SetWindowSubclass</c> stores a raw function pointer to the marshalling thunk
    /// generated for the managed <c>SUBCLASSPROC</c>. The CLR does not know Windows holds
    /// that pointer. If the delegate is not rooted by the <see cref="WindowSubclass"/>
    /// instance, a collection frees it and the next SimConnect message dispatches through
    /// a dead thunk — the process dies in flight, non-deterministically.
    /// </para>
    ///
    /// <para>
    /// Verified to bite: rebuilding the delegate into a discarded local instead of the
    /// <c>_proc</c> field makes this test fail (the callback never fires).
    /// </para>
    /// </summary>
    [WindowsFact]
    public void NativeThunk_SurvivesGarbageCollection_AndStillDeliversMessages()
    {
        var received = new List<int>();
        MessageOnlyWindow? window = null;
        WindowSubclass? subclass = null;

        try
        {
            window = MessageOnlyWindow.Create();
            subclass = new WindowSubclass((_, msg, _, _) => received.Add(msg));
            subclass.Attach(window.Handle);

            // Nothing managed references the SUBCLASSPROC except the instance field.
            // If that field did not exist, these collections would free the thunk.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            SendMessageW(window.Handle, WM_USER_SIMCONNECT, 0, 0);

            Assert.Contains(WM_USER_SIMCONNECT, received);
        }
        finally
        {
            subclass?.Detach();
            window?.Dispose();
        }
    }

    /// <summary>
    /// After <see cref="WindowSubclass.Detach"/> the subclass is removed from the real
    /// window, so further messages must not reach the callback.
    /// </summary>
    [WindowsFact]
    public void Detach_RemovesSubclassFromRealWindow()
    {
        var received = new List<int>();
        MessageOnlyWindow? window = null;

        try
        {
            window = MessageOnlyWindow.Create();
            var subclass = new WindowSubclass((_, msg, _, _) => received.Add(msg));
            subclass.Attach(window.Handle);

            SendMessageW(window.Handle, WM_USER_SIMCONNECT, 0, 0);
            Assert.Contains(WM_USER_SIMCONNECT, received);

            received.Clear();
            subclass.Detach();

            SendMessageW(window.Handle, WM_USER_SIMCONNECT, 0, 0);
            Assert.Empty(received);
        }
        finally
        {
            window?.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // Minimal message-only window host
    // ---------------------------------------------------------------------

    private sealed class MessageOnlyWindow : IDisposable
    {
        // Rooted for the process lifetime: Windows holds a pointer to this thunk too,
        // and collecting it would produce the very crash these tests exist to catch.
        private static readonly WNDPROC s_wndProc = DefWindowProcW;

        private readonly string _className;
        private nint _hwnd;
        private ushort _atom;

        public nint Handle => _hwnd;

        private MessageOnlyWindow(string className, nint hwnd, ushort atom)
        {
            _className = className;
            _hwnd = hwnd;
            _atom = atom;
        }

        public static MessageOnlyWindow Create()
        {
            var className = "AviatesSubclassTest_" + Guid.NewGuid().ToString("N");

            var wc = new WNDCLASSW
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                lpszClassName = className,
            };

            var atom = RegisterClassW(ref wc);
            if (atom == 0)
                throw new InvalidOperationException(
                    $"RegisterClassW failed (Win32 error {Marshal.GetLastWin32Error()})");

            var hwnd = CreateWindowExW(
                0, className, string.Empty, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);

            if (hwnd == 0)
            {
                var err = Marshal.GetLastWin32Error();
                UnregisterClassW(className, 0);
                throw new InvalidOperationException($"CreateWindowExW failed (Win32 error {err})");
            }

            return new MessageOnlyWindow(className, hwnd, atom);
        }

        public void Dispose()
        {
            if (_hwnd != 0)
            {
                DestroyWindow(_hwnd);
                _hwnd = 0;
            }

            if (_atom != 0)
            {
                UnregisterClassW(_className, 0);
                _atom = 0;
            }
        }
    }

    private delegate nint WNDPROC(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
