using System.Runtime.InteropServices;

namespace AviatesAirTracker.Platform;

/// <summary>
/// Win32 window subclass, replacing WPF's <c>HwndSource.AddHook</c>. SimConnect delivers
/// its notifications as a window message (0x0402) to the HWND registered with it, so the
/// app must see that HWND's message stream — something WinUI 3 exposes no managed API for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The delegate must stay rooted.</b> <c>SetWindowSubclass</c> stores a raw function
/// pointer; the CLR has no knowledge of that reference. If the <see cref="SUBCLASSPROC"/>
/// is only held in a local, the GC is free to collect it while Windows still holds the
/// pointer, and the process dies on the next SimConnect message — in flight, and only
/// sometimes. <see cref="_proc"/> exists solely to prevent that.
/// </para>
/// <para>
/// <b>Do not refactor <see cref="_proc"/> into a local or a per-call method group.</b>
/// Passing <c>OnSubclassProc</c> directly to <c>SetWindowSubclass</c> allocates a fresh
/// delegate that nothing roots, and the process then terminates with
/// <c>"A callback was made on a garbage collected delegate"</c>. This is verified by
/// <c>WindowSubclassInteropTests.NativeThunk_SurvivesGarbageCollection_AndStillDeliversMessages</c>,
/// which subclasses a real message-only HWND, forces a blocking gen-2 collection, and
/// then sends message 0x0402 through it — that test has been confirmed to crash the test
/// host when the field is removed.
/// </para>
/// </remarks>
public sealed class WindowSubclass : IDisposable
{
    private delegate nint SUBCLASSPROC(
        nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    private const nuint SubclassId = 0xA71A7E5;   // arbitrary, unique to this app

    /// <summary>Rooted for the object's lifetime. See the remarks on the type.</summary>
    private readonly SUBCLASSPROC _proc;
    private readonly Action<nint, int, nint, nint> _onMessage;
    private nint _hwnd;
    private bool _attached;
    private bool _detached;

    public WindowSubclass(Action<nint, int, nint, nint> onMessage)
    {
        _onMessage = onMessage;
        _proc = OnSubclassProc;
    }

    /// <summary>Begins forwarding <paramref name="hwnd"/>'s messages to the callback.</summary>
    public void Attach(nint hwnd)
    {
        if (_attached) return;
        _hwnd = hwnd;
        _attached = SetWindowSubclass(hwnd, _proc, SubclassId, 0);
        if (!_attached)
            throw new InvalidOperationException(
                $"SetWindowSubclass failed (Win32 error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>Stops forwarding messages. Safe to call more than once.</summary>
    public void Detach()
    {
        _detached = true;
        if (!_attached) return;
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        _attached = false;
    }

    private nint OnSubclassProc(
        nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        _onMessage(hWnd, (int)uMsg, wParam, lParam);
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// Drives the callback without a real HWND, so the delegate-lifetime guarantee can be
    /// asserted in a headless test.
    /// </summary>
    internal void InvokeForTest(nint hwnd, int msg, nint wParam, nint lParam)
    {
        if (_detached) return;
        _onMessage(hwnd, msg, wParam, lParam);
    }

    public void Dispose() => Detach();
}
