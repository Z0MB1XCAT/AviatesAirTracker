using Serilog;
using SharpDX.DirectInput;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace AviatesAirTracker.Services;

// ============================================================
// PTT INPUT SERVICE
//
// Two independent input sources, normalized to one PttStateChanged
// event so DispatchRadioService doesn't care which fired:
//
//  - Keyboard: a WH_KEYBOARD_LL global hook, works regardless of
//    which window has focus (MSFS will have focus during flight).
//  - Joystick/HOTAS: SharpDX.DirectInput polling loop, matches the
//    same "works without focus" requirement via a non-exclusive
//    background cooperative level.
//
// Both bindings are unbound by default (VkCode 0 / joystick GUID
// empty) — this service is inert until the pilot sets a binding in
// Settings. Gating "only forward PTT while tuned in" is the
// responsibility of DispatchRadioService, not this service — this
// class only reports raw input state.
//
// Threading notes:
//  - The joystick poll loop owns _joystick/_directInput and disposes
//    them itself in a finally block when it exits, so Dispose() never
//    races the poll loop for those objects — it just cancels and
//    waits for the loop task to finish.
//  - _directInput/_joystick access is additionally serialized with
//    _directInputLock because CaptureNextJoystickButtonAsync (called
//    from the UI thread during binding capture in Settings) and the
//    background poll loop can otherwise touch the same DirectInput
//    COM objects concurrently.
//  - Both input sources write to a single unbounded Channel<bool>
//    instead of raising PttStateChanged directly. A channel write is
//    O(1)/non-blocking, so the WH_KEYBOARD_LL hook thread still
//    returns immediately (a slow hook can be silently uninstalled by
//    the OS or stall system-wide keyboard input) — but one dedicated
//    consumer task (DispatchEventsAsync) drains the channel and
//    invokes PttStateChanged sequentially, in the order events were
//    raised. Firing PttStateChanged from independent Task.Run calls
//    per event gave no ordering guarantee (a rapid press+release
//    could deliver false before true, or run concurrently), which
//    matters because this event drives dispatch-radio transmit state.
// ============================================================

public class PttInputService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly SettingsService _settings;
    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _keyIsDown;

    private readonly object _directInputLock = new();
    private readonly DirectInput _directInput = new();
    private Joystick? _joystick;
    private readonly CancellationTokenSource _joystickCts = new();

    // App.OnExit disposes this service explicitly (Step("PttInput", ...)) and then
    // disposes the whole DI ServiceProvider, which disposes every registered singleton
    // again — including this one. Without the guard, the second call hits
    // _joystickCts.Cancel() on an already-disposed CancellationTokenSource and throws
    // ObjectDisposedException, logged to crash.log on every shutdown despite the process
    // exiting cleanly.
    private bool _disposed;
    private readonly Task _joystickPollTask;
    private bool _joystickButtonIsDown;
    private IntPtr _windowHandle;

    // Single sequential dispatch queue for PttStateChanged so press/release
    // transitions from either input source are always delivered in the
    // order they occurred, on one dedicated thread — see the threading
    // notes above.
    private readonly Channel<bool> _eventChannel = Channel.CreateUnbounded<bool>();
    private readonly Task _eventDispatchTask;

    public event EventHandler<bool>? PttStateChanged;

    public PttInputService(SettingsService settings)
    {
        _settings = settings;
        _hookProc = HookCallback;
        InstallKeyboardHook();
        _joystickPollTask = Task.Run(() => JoystickPollLoopAsync(_joystickCts.Token));
        _eventDispatchTask = Task.Run(() => DispatchEventsAsync(_joystickCts.Token));
    }

    public void SetWindowHandle(IntPtr hwnd) => Volatile.Write(ref _windowHandle, hwnd);

    public IReadOnlyList<(Guid InstanceGuid, string ProductName)> GetConnectedJoysticks()
    {
        lock (_directInputLock)
        {
            return _directInput
                .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                .Select(d => (d.InstanceGuid, d.InstanceName))
                .ToList();
        }
    }

    // Polls the given device for up to the token's lifetime, returning the
    // index of the first button pressed, or null on cancellation/timeout.
    // Caller supplies a timeout via the token (see Settings.razor).
    public async Task<int?> CaptureNextJoystickButtonAsync(Guid deviceGuid, CancellationToken token)
    {
        Joystick device;
        lock (_directInputLock)
        {
            device = new Joystick(_directInput, deviceGuid);
            var hwnd = Volatile.Read(ref _windowHandle);
            if (hwnd != IntPtr.Zero)
                device.SetCooperativeLevel(hwnd, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
            device.Acquire();
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                int? pressed = null;
                lock (_directInputLock)
                {
                    device.Poll();
                    var state = device.GetCurrentState();
                    for (int i = 0; i < state.Buttons.Length; i++)
                    {
                        if (state.Buttons[i]) { pressed = i; break; }
                    }
                }

                if (pressed.HasValue) return pressed.Value;
                await Task.Delay(30, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out or cancelled — no button captured
        }
        finally
        {
            lock (_directInputLock)
            {
                device.Dispose();
            }
        }
        return null;
    }

    private void InstallKeyboardHook()
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(curModule?.ModuleName), 0);
        if (_hookHandle == IntPtr.Zero)
            Log.Warning("[PttInput] Failed to install global keyboard hook");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            int boundVk = _settings.Settings.PttHotkeyVkCode;

            if (boundVk != 0 && vkCode == boundVk)
            {
                bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
                bool isUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

                if (isDown && !_keyIsDown)
                {
                    _keyIsDown = true;
                    // Non-blocking write — the hook thread must return
                    // quickly (see threading notes above). Actual delivery
                    // happens on DispatchEventsAsync's dedicated thread.
                    _eventChannel.Writer.TryWrite(true);
                }
                else if (isUp && _keyIsDown)
                {
                    _keyIsDown = false;
                    _eventChannel.Writer.TryWrite(false);
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    // When the bound device can't be opened (unplugged, or a GUID saved on another PC), the
    // loop used to retry every 30 ms and log a full DirectInput stack trace each time --
    // ~126,000 times in one day's log. Now it waits this long between attempts and logs once
    // per outage.
    private static readonly TimeSpan JoystickRetryDelay = TimeSpan.FromSeconds(5);

    private async Task JoystickPollLoopAsync(CancellationToken token)
    {
        var deviceFailing = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var guid = _settings.Settings.PttJoystickGuid;
                    int buttonIndex = _settings.Settings.PttJoystickButton;

                    if (guid == Guid.Empty || buttonIndex < 0)
                    {
                        await Task.Delay(500, token);
                        continue;
                    }

                    bool isDown;
                    lock (_directInputLock)
                    {
                        if (_joystick == null || _joystick.Information.InstanceGuid != guid)
                        {
                            _joystick?.Dispose();
                            _joystick = new Joystick(_directInput, guid);
                            var hwnd = Volatile.Read(ref _windowHandle);
                            if (hwnd != IntPtr.Zero)
                                _joystick.SetCooperativeLevel(hwnd, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
                            _joystick.Acquire();
                        }

                        _joystick.Poll();
                        var state = _joystick.GetCurrentState();
                        isDown = buttonIndex < state.Buttons.Length && state.Buttons[buttonIndex];
                    }

                    if (isDown && !_joystickButtonIsDown)
                    {
                        _joystickButtonIsDown = true;
                        _eventChannel.Writer.TryWrite(true);
                    }
                    else if (!isDown && _joystickButtonIsDown)
                    {
                        _joystickButtonIsDown = false;
                        _eventChannel.Writer.TryWrite(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Propagate to the outer try so shutdown is handled once,
                    // without falling into the generic catch below and logging
                    // a misleading "device unplugged" warning on normal Dispose().
                    throw;
                }
                catch (Exception ex)
                {
                    if (!deviceFailing)
                    {
                        deviceFailing = true;
                        Log.Warning("[PttInput] PTT joystick unavailable ({Message}) -- retrying every {Sec}s",
                            ex.Message, JoystickRetryDelay.TotalSeconds);
                    }
                    lock (_directInputLock)
                    {
                        _joystick?.Dispose();
                        _joystick = null;
                    }
                    await Task.Delay(JoystickRetryDelay, token);
                    continue;
                }

                if (deviceFailing)
                {
                    deviceFailing = false;
                    Log.Information("[PttInput] PTT joystick available again");
                }

                await Task.Delay(30, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via Dispose() — nothing to log.
        }
        finally
        {
            // This loop owns _joystick/_directInput, so it disposes them
            // itself on the way out. Dispose() only cancels and waits for
            // this task, avoiding a race where Dispose() frees these COM
            // objects while the loop is still mid-Poll()/GetCurrentState().
            // Both disposals happen under the same lock so a concurrent
            // GetConnectedJoysticks()/CaptureNextJoystickButtonAsync() call
            // can never observe _directInput disposed while _joystick isn't
            // (or vice versa) mid-teardown.
            lock (_directInputLock)
            {
                _joystick?.Dispose();
                _joystick = null;
                _directInput.Dispose();
            }
            Log.Debug("[PttInput] Joystick poll loop stopped");
        }
    }

    // Drains _eventChannel on one dedicated thread so PttStateChanged is
    // always raised in the exact order press/release transitions occurred,
    // regardless of which input source (keyboard or joystick) produced them.
    private async Task DispatchEventsAsync(CancellationToken token)
    {
        try
        {
            await foreach (var pressed in _eventChannel.Reader.ReadAllAsync(token))
            {
                PttStateChanged?.Invoke(this, pressed);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Dispose — the channel writer is completed and the
            // token cancelled together, so remaining queued events are
            // simply dropped rather than delivered after shutdown.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);

        _eventChannel.Writer.TryComplete();
        _joystickCts.Cancel();

        // Both loops swallow OperationCanceledException internally, so under
        // normal shutdown these tasks complete as RanToCompletion, not
        // Canceled/Faulted — the try/catch below only guards against a
        // genuinely unexpected exception escaping one of the loops.
        try
        {
            if (!_joystickPollTask.Wait(TimeSpan.FromSeconds(1)))
                Log.Warning("[PttInput] Joystick poll task did not stop within timeout during Dispose");
            if (!_eventDispatchTask.Wait(TimeSpan.FromSeconds(1)))
                Log.Warning("[PttInput] Event dispatch task did not stop within timeout during Dispose");
        }
        catch (AggregateException ex)
        {
            Log.Warning(ex, "[PttInput] Unexpected exception while waiting for background tasks to stop during Dispose");
        }
        _joystickCts.Dispose();

        Log.Debug("[PttInput] Disposed");
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
