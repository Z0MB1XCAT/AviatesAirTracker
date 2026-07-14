using Serilog;
using SharpDX.DirectInput;
using System.Runtime.InteropServices;

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
//  - PttStateChanged is raised via Task.Run so a slow subscriber
//    (e.g. DispatchRadioService) can never stall the WH_KEYBOARD_LL
//    hook thread — a slow low-level keyboard hook can be silently
//    uninstalled by the OS or stall system-wide keyboard input.
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
    private readonly Task _joystickPollTask;
    private bool _joystickButtonIsDown;
    private IntPtr _windowHandle;

    public event EventHandler<bool>? PttStateChanged;

    public PttInputService(SettingsService settings)
    {
        _settings = settings;
        _hookProc = HookCallback;
        InstallKeyboardHook();
        _joystickPollTask = Task.Run(() => JoystickPollLoopAsync(_joystickCts.Token));
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
                    // Dispatched off the hook thread — a low-level keyboard
                    // hook must return quickly or the OS can silently
                    // uninstall it / stall system-wide keyboard input.
                    _ = Task.Run(() => PttStateChanged?.Invoke(this, true));
                }
                else if (isUp && _keyIsDown)
                {
                    _keyIsDown = false;
                    _ = Task.Run(() => PttStateChanged?.Invoke(this, false));
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private async Task JoystickPollLoopAsync(CancellationToken token)
    {
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
                        _ = Task.Run(() => PttStateChanged?.Invoke(this, true));
                    }
                    else if (!isDown && _joystickButtonIsDown)
                    {
                        _joystickButtonIsDown = false;
                        _ = Task.Run(() => PttStateChanged?.Invoke(this, false));
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
                    Log.Debug(ex, "[PttInput] Joystick poll error (device may have been unplugged)");
                    lock (_directInputLock)
                    {
                        _joystick?.Dispose();
                        _joystick = null;
                    }
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
            lock (_directInputLock)
            {
                _joystick?.Dispose();
                _joystick = null;
            }
            _directInput.Dispose();
            Log.Debug("[PttInput] Joystick poll loop stopped");
        }
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);

        _joystickCts.Cancel();
        try
        {
            _joystickPollTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Expected: the poll loop task completes via OperationCanceledException.
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
