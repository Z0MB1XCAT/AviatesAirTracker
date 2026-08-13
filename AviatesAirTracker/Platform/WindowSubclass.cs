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
/// <para>
/// A single delegate instance is also required so that <c>RemoveWindowSubclass</c> can
/// match the pointer that was registered by <c>SetWindowSubclass</c>. A fresh delegate per
/// call produces a different native thunk, so the "unhook" call would silently do nothing
/// — no exception, no error code, just a subclass that is never actually removed.
/// </para>
/// <para>
/// <b>Thread affinity:</b> <see cref="Attach"/> and <see cref="Detach"/> must be called on
/// the thread that owns the window. comctl32's subclass chain is per-HWND state tied to
/// that window's owning thread, the same way <c>HwndSource.AddHook</c> was.
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
    private readonly Action<Exception>? _onError;
    private nint _hwnd;
    private bool _attached;
    private bool _detached;

    /// <param name="onMessage">Receives every window message sent to the attached HWND.</param>
    /// <param name="onError">
    /// Invoked if <paramref name="onMessage"/> throws. Optional, but a caller that passes
    /// nothing is choosing to discard the exception silently — see the remarks on why it
    /// cannot be allowed to propagate.
    /// </param>
    public WindowSubclass(
        Action<nint, int, nint, nint> onMessage,
        Action<Exception>? onError = null)
    {
        _onMessage = onMessage;
        _onError = onError;
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
        // This method is invoked by comctl32 as a reverse P/Invoke. An exception escaping
        // here unwinds across the native boundary, which .NET does not support — the
        // process fail-fasts, and the handlers in App.xaml.cs cannot stop it. Swallowing
        // a telemetry-callback fault is strictly better than killing the app mid-flight.
        try
        {
            _onMessage(hWnd, (int)uMsg, wParam, lParam);
        }
        catch (Exception ex)
        {
            try { _onError?.Invoke(ex); } catch { /* the error reporter must never throw */ }
        }

        // Always falls through: the window must keep processing messages regardless.
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// Drives the callback without a real HWND, so the delegate-lifetime guarantee can be
    /// asserted in a headless test.
    /// </summary>
    internal void InvokeForTest(nint hwnd, int msg, nint wParam, nint lParam)
    {
        if (_detached) return;

        try
        {
            _onMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            try { _onError?.Invoke(ex); } catch { /* the error reporter must never throw */ }
        }
    }

    public void Dispose() => Detach();
}
