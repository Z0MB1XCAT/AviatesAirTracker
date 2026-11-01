using AviatesAirTracker.Core.SimConnect;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace AviatesAirTracker.Core.SimConnect;

// ============================================================
// SIMCONNECT MANAGER  (Runtime-reflection, dual-sim edition)
//
// SimConnect is loaded at RUNTIME, not at build time.
// This means:
//   - The project always compiles without any SDK installed
//   - Both MSFS 2020 and MSFS 2024 are supported in a single app
//
// SDK layout (both DLLs required in each folder):
//   managed wrapper  ← Microsoft.FlightSimulator.SimConnect.dll
//   native client    ← SimConnect.dll
//
//   MSFS 2024:  Libs\                   (or next to EXE)
//   MSFS 2020:  Libs\MSFS2020\
//
// Which SDK loads:
//   At connection time the running flight simulator process is
//   detected.  The matching SDK is loaded once per app run.
//   If both sims are installed, only one can be tracked per
//   session — restart the app when switching simulators.
//
// How to set up MSFS 2020 DLLs:
//   1. Enable developer mode in MSFS 2020 and install the SDK
//   2. Copy from [SDK]\SimConnect SDK\lib\ :
//        managed\Microsoft.FlightSimulator.SimConnect.dll
//        SimConnect.dll
//      Into: AviatesAirTracker\Libs\MSFS2020\
// ============================================================

public class SimConnectManager : IDisposable
{
    // =====================================================
    // PUBLIC EVENTS  (always available regardless of mode)
    // =====================================================
    public event EventHandler<TelemetrySnapshot>?      TelemetryReceived;
    public event EventHandler<AircraftIdentification>? AircraftIdentReceived;
    public event EventHandler<SimConnectionStatus>?    ConnectionStatusChanged;
    public event EventHandler? SimStarted;
    public event EventHandler? SimStopped;
    public event EventHandler? CrashDetected;

    public SimConnectionStatus ConnectionStatus { get; private set; } = SimConnectionStatus.Disconnected;
    public bool IsConnected => _connected;

    /// <summary>Human-readable name of the simulator SDK currently loaded ("MSFS 2020", "MSFS 2024", or null).</summary>
    public string? LoadedSimName => _sdkVersionLoaded switch
    {
        SimVersion.MSFS2020 => "MSFS 2020",
        SimVersion.MSFS2024 => "MSFS 2024",
        _                   => null
    };

    // =====================================================
    // RUNTIME STATE
    // =====================================================
    private object?   _simConnect;          // dynamic SimConnect instance
    private Assembly? _scAssembly;          // loaded SDK assembly
    private Type?     _scType;              // SimConnect class type
    private bool      _connected;
    private bool      _disposed;
    private IntPtr    _hwnd = IntPtr.Zero;

    private DispatcherQueueTimer? _pollTimer;
    private DispatcherQueueTimer? _reconnectTimer;

    private const int    WM_USER_SIMCONNECT = 0x0402;
    private const string APP_NAME           = "AviatesAirTracker";

    // Data definition / request IDs (mirror the enum values)
    private const uint DEF_AIRCRAFT_STATE = 1;
    private const uint DEF_AIRCRAFT_IDENT = 2;
    private const uint REQ_AIRCRAFT_STATE = 1;
    private const uint REQ_AIRCRAFT_IDENT = 2;

    // CRIT-09: Cached reflection values — resolved once, not on every 20Hz tick.
    private object?    _cachedSimObjectTypeUser;
    private MethodInfo? _cachedRequestDataMethod;
    private Type?      _cachedEnumCarrierType;

    // Ident is slow-polled: every IDENT_POLL_INTERVAL ticks (~2s).
    private int _identPollCounter;
    private const int IDENT_POLL_INTERVAL = 40;

    // Maps SimConnect SendID → sim variable name for every AddToDataDefinition call.
    // If the sim rejects a variable (SIMCONNECT_EXCEPTION_NAME_UNRECOGNIZED = 7) the entry is
    // silently dropped from the definition and every later struct field receives the wrong
    // variable's value. This map lets OnRecvException name the exact offending variable.
    private readonly Dictionary<uint, string> _defVarBySendId = new();
    private bool _sendIdTrackingWarned;

    private void WarnSendIdTrackingBroken(string reason)
    {
        if (_sendIdTrackingWarned) return;
        _sendIdTrackingWarned = true;
        Log.Warning("[SimConnect] SendID tracking unavailable ({Reason}) — rejected data " +
            "definitions will log an anonymous SendID instead of the variable name", reason);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    private readonly DispatcherQueue _dispatcher;

    // =====================================================
    // DUAL-SIM SDK SUPPORT
    // =====================================================

    private enum SimVersion { None, MSFS2020, MSFS2024 }

    private SimVersion _sdkVersionLoaded = SimVersion.None;

    // Each entry is (managed DLL path, native DLL path, sim version).
    // Populated at startup by scanning Libs\ and Libs\MSFS2020\.
    private readonly List<(string ManagedPath, string NativePath, SimVersion Version)> _availableSdks = new();

    private bool HasAnySdk => _availableSdks.Count > 0;

    // =====================================================
    // CONSTRUCTION
    // =====================================================

    public SimConnectManager()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ScanAvailableSdks();
    }

    // =====================================================
    // SDK SCANNING (startup)
    // =====================================================

    // Pre-loads the native SimConnect.dll so that when the managed wrapper's
    // P/Invoke fires for "SimConnect.dll", the CLR finds the already-loaded
    // module in the process module list.
    private static void PreloadNative(string nativePath)
    {
        if (!File.Exists(nativePath)) return;
        var handle = LoadLibraryW(nativePath);
        if (handle == IntPtr.Zero)
            Log.Warning("[SimConnect] Could not pre-load native {Path} (error {Code})",
                nativePath, Marshal.GetLastWin32Error());
        else
            Log.Information("[SimConnect] Pre-loaded native {Path}", nativePath);
    }

    private void ScanAvailableSdks()
    {
        var baseDir = AppContext.BaseDirectory;

        // MSFS 2024 SDK candidates (existing Libs\ layout)
        foreach (var managedPath in new[]
        {
            Path.Combine(baseDir, "Microsoft.FlightSimulator.SimConnect.dll"),
            Path.Combine(baseDir, "Libs", "Microsoft.FlightSimulator.SimConnect.dll"),
        })
        {
            if (!File.Exists(managedPath)) continue;
            var dir = Path.GetDirectoryName(managedPath)!;
            _availableSdks.Add((managedPath, Path.Combine(dir, "SimConnect.dll"), SimVersion.MSFS2024));
            Log.Information("[SimConnect] MSFS 2024 SDK found: {Path}", managedPath);
            break;
        }

        // MSFS 2020 SDK candidate (user places DLLs in Libs\MSFS2020\)
        var msfs2020Path = Path.Combine(baseDir, "Libs", "MSFS2020",
            "Microsoft.FlightSimulator.SimConnect.dll");
        if (File.Exists(msfs2020Path))
        {
            var dir = Path.GetDirectoryName(msfs2020Path)!;
            _availableSdks.Add((msfs2020Path, Path.Combine(dir, "SimConnect.dll"), SimVersion.MSFS2020));
            Log.Information("[SimConnect] MSFS 2020 SDK found: {Path}", msfs2020Path);
        }

        if (!HasAnySdk)
            Log.Warning("[SimConnect] No SimConnect SDK found — running in stub mode. " +
                "Place MSFS 2024 DLLs in Libs\\ and/or MSFS 2020 DLLs in Libs\\MSFS2020\\.");
    }

    // =====================================================
    // SIM DETECTION
    // =====================================================

    private static SimVersion DetectRunningSim()
    {
        // Check for MSFS 2024 alternate process names first
        foreach (var name in new[] { "FlightSimulator2024", "flightsimulator2024" })
        {
            if (Process.GetProcessesByName(name).Length > 0)
                return SimVersion.MSFS2024;
        }

        // Both MSFS 2020 and 2024 typically use "FlightSimulator" as the process name.
        // Distinguish by checking the executable path: MSFS 2024 installs into a
        // directory whose path contains "2024" (Store: Microsoft.FlightSimulator2024;
        // Steam: "Flight Simulator 2024").
        foreach (var proc in Process.GetProcessesByName("FlightSimulator"))
        {
            try
            {
                using (proc)
                {
                    var exePath = proc.MainModule?.FileName ?? string.Empty;
                    return exePath.Contains("2024", StringComparison.OrdinalIgnoreCase)
                        ? SimVersion.MSFS2024
                        : SimVersion.MSFS2020;
                }
            }
            catch
            {
                proc.Dispose();
                // A Flight Simulator process is running but we can't read its path
                // (e.g. access denied in some Windows security configurations).
                // Fall back to MSFS 2020 — the older, more conservative choice.
                return SimVersion.MSFS2020;
            }
        }

        return SimVersion.None;
    }

    // =====================================================
    // SDK LOADING (deferred until sim is detected)
    // =====================================================

    private bool TryLoadSdkFor(SimVersion version)
    {
        var sdk = _availableSdks.FirstOrDefault(s => s.Version == version);
        if (sdk.ManagedPath is null)
        {
            Log.Warning("[SimConnect] No {Version} SDK files available in Libs\\. " +
                "MSFS 2024 → Libs\\  |  MSFS 2020 → Libs\\MSFS2020\\", version);
            return false;
        }

        // Pre-load the native DLL from the same directory as the managed wrapper.
        // This must happen before Assembly.LoadFrom so the CLR's P/Invoke resolver
        // finds the correct native SimConnect.dll for this sim version.
        PreloadNative(sdk.NativePath);

        try
        {
            _scAssembly       = Assembly.LoadFrom(sdk.ManagedPath);
            _scType           = _scAssembly.GetType(
                "Microsoft.FlightSimulator.SimConnect.SimConnect", throwOnError: true);
            _sdkVersionLoaded = version;
            Log.Information("[SimConnect] Loaded {Version} SDK from: {Path}", version, sdk.ManagedPath);
            return true;
        }
        catch (BadImageFormatException)
        {
            Log.Warning("[SimConnect] {Path} is a native DLL — need the MANAGED wrapper from " +
                "[MSFS SDK]\\SimConnect SDK\\lib\\managed\\", sdk.ManagedPath);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SimConnect] Failed to load {Version} SDK from {Path}",
                version, sdk.ManagedPath);
            return false;
        }
    }

    // =====================================================
    // WINDOW HANDLE  (called by MainWindow after load)
    // =====================================================

    /// <summary>Settings' "Auto-connect to MSFS". Off: no connection attempts at launch or on the
    /// 5 s retry -- only when the pilot presses Connect.</summary>
    public bool AutoConnect { get; set; } = true;

    public void SetWindowHandle(IntPtr hwnd)
    {
        _hwnd = hwnd;
        InitReconnectTimer();
        if (HasAnySdk && AutoConnect)
            TryConnect();
        else
            Log.Information("[SimConnect] Stub mode — SetWindowHandle ignored.");
    }

    public void HandleSimConnectMessage()
    {
        if (_simConnect == null) return;
        try
        {
            _scType!.GetMethod("ReceiveMessage")!.Invoke(_simConnect, null);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[SimConnect] ReceiveMessage error");
            HandleConnectionLost();
        }
    }

    // =====================================================
    // CONNECTION
    // =====================================================

    public void TryConnect()
    {
        if (!HasAnySdk || _connected || _hwnd == IntPtr.Zero) return;

        // On the first connection attempt, detect which sim is running and load its SDK.
        // Once a native SimConnect.dll is loaded into the process it cannot be swapped —
        // this runs only once per app lifetime.
        if (_sdkVersionLoaded == SimVersion.None)
        {
            var running = DetectRunningSim();
            if (running == SimVersion.None)
            {
                Log.Debug("[SimConnect] No flight simulator process detected — waiting.");
                return;
            }

            if (!TryLoadSdkFor(running))
            {
                // The right SDK isn't installed; try the other version as a best-effort fallback.
                var fallback = _availableSdks.FirstOrDefault(s => s.Version != running);
                if (fallback.ManagedPath is null || !TryLoadSdkFor(fallback.Version))
                {
                    Log.Warning("[SimConnect] {Sim} is running but no matching SDK was found. " +
                        "Place MSFS 2024 DLLs in Libs\\ or MSFS 2020 DLLs in Libs\\MSFS2020\\.", running);
                    return;
                }
                Log.Warning("[SimConnect] Loaded {Loaded} SDK but {Running} is running — " +
                    "connection may fail. Restart the app after adding the correct SDK DLLs.",
                    _sdkVersionLoaded, running);
            }

            if (_sdkVersionLoaded == SimVersion.None) return;
        }
        else if (DetectRunningSim() == SimVersion.None)
        {
            // Once an SDK is loaded, every 5 s reconnect tick after the sim closed used to flip
            // the status Connecting → Disconnected (ACARS banner flicker, two status events, a
            // COMException) just to learn the sim still isn't running.
            return;
        }

        try
        {
            Log.Information("[SimConnect] Connecting to {Sim}...", _sdkVersionLoaded);
            // A previous attempt whose open handshake never arrived is replaced, not leaked.
            if (_simConnect != null)
            {
                try { (_simConnect as IDisposable)?.Dispose(); } catch { }
                _simConnect = null;
            }
            UpdateStatus(SimConnectionStatus.Connecting);

            // new SimConnect(name, hwnd, msgId, null, 0)
            _simConnect = Activator.CreateInstance(
                _scType!, APP_NAME, _hwnd, (uint)WM_USER_SIMCONNECT, null, (uint)0);

            if (_simConnect == null)
            {
                UpdateStatus(SimConnectionStatus.Error);
                return;
            }

            WireEvents();
            RegisterDataDefinitions();
            SubscribeSystemEvents();
        }
        catch (TargetInvocationException tie)
            when (tie.InnerException is COMException)
        {
            Log.Debug("[SimConnect] {Sim} not running yet.", _sdkVersionLoaded);
            _simConnect = null;
            UpdateStatus(SimConnectionStatus.Disconnected);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SimConnect] Connection failed");
            _simConnect = null;
            UpdateStatus(SimConnectionStatus.Error);
        }
    }

    public void Disconnect()
    {
        StopPolling();
        if (_simConnect != null)
        {
            try { (_simConnect as IDisposable)?.Dispose(); } catch { }
            _simConnect = null;
        }
        _connected = false;
        UpdateStatus(SimConnectionStatus.Disconnected);
    }

    // =====================================================
    // EVENT WIRING  (via reflection)
    // =====================================================

    private void WireEvents()
    {
        if (_simConnect == null || _scType == null) return;

        BindEvent("OnRecvOpen",                 nameof(OnRecvOpen));
        BindEvent("OnRecvQuit",                 nameof(OnRecvQuit));
        BindEvent("OnRecvException",            nameof(OnRecvException));
        BindEvent("OnRecvSimobjectDataBytype",  nameof(OnRecvSimobjectData));
        BindEvent("OnRecvEvent",                nameof(OnRecvSystemEvent));
    }

    private void BindEvent(string eventName, string handlerName)
    {
        try
        {
            var ev      = _scType!.GetEvent(eventName);
            var handler = GetType().GetMethod(handlerName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (ev == null || handler == null) return;

            var del = Delegate.CreateDelegate(ev.EventHandlerType!, this, handler);
            ev.AddEventHandler(_simConnect, del);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[SimConnect] Could not bind event {Event}", eventName);
        }
    }

    // =====================================================
    // DATA DEFINITIONS  (via reflection)
    // =====================================================

    private void RegisterDataDefinitions()
    {
        if (_simConnect == null) return;

        // Resolve enum types from the SDK assembly
        var dataTypeEnum = _scAssembly!.GetType(
            "Microsoft.FlightSimulator.SimConnect.SIMCONNECT_DATATYPE")!;
        var float64Val   = Enum.Parse(dataTypeEnum, "FLOAT64");

        // SimConnect methods require Enum for DefineID — raw uint fails via reflection.
        // Enum.ToObject boxes the uint into a valid enum using SIMCONNECT_DATATYPE as carrier.
        object Id(uint v) => Enum.ToObject(dataTypeEnum, (int)v);

        var addMethod = _scType!.GetMethod("AddToDataDefinition");
        var regMethod = _scType.GetMethod("RegisterDataDefineStruct");
        var lastSentMethod = _scType.GetMethod("GetLastSentPacketID");

        _defVarBySendId.Clear();

        void TrackSendId(string varName)
        {
            if (lastSentMethod == null)
            {
                WarnSendIdTrackingBroken("GetLastSentPacketID not found on SimConnect type");
                return;
            }
            try
            {
                uint sendId;
                if (lastSentMethod.GetParameters().Length == 0)
                {
                    // MSFS 2024 wrapper: uint GetLastSentPacketID()
                    sendId = (uint)lastSentMethod.Invoke(_simConnect, null)!;
                }
                else
                {
                    // FSX-era / MSFS 2020 wrapper: void GetLastSentPacketID(out uint dwSendID)
                    var args = new object[] { 0u };
                    lastSentMethod.Invoke(_simConnect, args);
                    sendId = (uint)args[0];
                }
                _defVarBySendId[sendId] = varName;
            }
            catch (Exception ex)
            {
                // Diagnostics only — never block registration. But don't fail silently either:
                // without this map a rejected variable logs as an anonymous SendID.
                WarnSendIdTrackingBroken(ex.Message);
            }
        }

        void Add(uint defId, string varName, string? unit)
        {
            addMethod!.Invoke(_simConnect,
                [Id(defId), varName, unit, float64Val, 0f, uint.MaxValue]);
            TrackSendId(varName);
        }

        // ---- Position ----
        Add(DEF_AIRCRAFT_STATE, "PLANE LATITUDE",            "degrees");
        Add(DEF_AIRCRAFT_STATE, "PLANE LONGITUDE",           "degrees");
        Add(DEF_AIRCRAFT_STATE, "PLANE ALTITUDE",            "feet");
        Add(DEF_AIRCRAFT_STATE, "PLANE ALT ABOVE GROUND",    "feet");
        Add(DEF_AIRCRAFT_STATE, "PRESSURE ALTITUDE",         "feet");
        // ---- Orientation ----
        Add(DEF_AIRCRAFT_STATE, "PLANE HEADING DEGREES TRUE",     "degrees");
        Add(DEF_AIRCRAFT_STATE, "PLANE HEADING DEGREES MAGNETIC", "degrees");
        Add(DEF_AIRCRAFT_STATE, "GPS GROUND TRUE TRACK",          "degrees");
        Add(DEF_AIRCRAFT_STATE, "PLANE PITCH DEGREES",            "degrees");
        Add(DEF_AIRCRAFT_STATE, "PLANE BANK DEGREES",             "degrees");
        // ---- Speeds ----
        Add(DEF_AIRCRAFT_STATE, "GPS GROUND SPEED",       "knots");
        Add(DEF_AIRCRAFT_STATE, "AIRSPEED TRUE",          "knots");
        Add(DEF_AIRCRAFT_STATE, "AIRSPEED INDICATED",     "knots");
        Add(DEF_AIRCRAFT_STATE, "AIRSPEED MACH",          "mach");
        Add(DEF_AIRCRAFT_STATE, "VERTICAL SPEED",         "feet/minute");
        Add(DEF_AIRCRAFT_STATE, "AMBIENT WIND VELOCITY",  "knots");
        Add(DEF_AIRCRAFT_STATE, "AMBIENT WIND DIRECTION", "degrees");
        // ---- Throttle ----
        Add(DEF_AIRCRAFT_STATE, "GENERAL ENG THROTTLE LEVER POSITION:1", "percent");
        Add(DEF_AIRCRAFT_STATE, "GENERAL ENG THROTTLE LEVER POSITION:2", "percent");
        Add(DEF_AIRCRAFT_STATE, "GENERAL ENG THROTTLE LEVER POSITION:3", "percent");
        Add(DEF_AIRCRAFT_STATE, "GENERAL ENG THROTTLE LEVER POSITION:4", "percent");
        // ---- Engines ----
        Add(DEF_AIRCRAFT_STATE, "TURB ENG N1:1", "percent");
        Add(DEF_AIRCRAFT_STATE, "TURB ENG N1:2", "percent");
        Add(DEF_AIRCRAFT_STATE, "TURB ENG N1:3", "percent");
        Add(DEF_AIRCRAFT_STATE, "TURB ENG N1:4", "percent");
        Add(DEF_AIRCRAFT_STATE, "TURB ENG N2:1", "percent");
        Add(DEF_AIRCRAFT_STATE, "TURB ENG N2:2", "percent");
        Add(DEF_AIRCRAFT_STATE, "ENG FUEL FLOW PPH:1", "pounds per hour");
        Add(DEF_AIRCRAFT_STATE, "ENG FUEL FLOW PPH:2", "pounds per hour");
        Add(DEF_AIRCRAFT_STATE, "ENG FUEL FLOW PPH:3", "pounds per hour");
        Add(DEF_AIRCRAFT_STATE, "ENG FUEL FLOW PPH:4", "pounds per hour");
        Add(DEF_AIRCRAFT_STATE, "ENG COMBUSTION:1", "bool");
        Add(DEF_AIRCRAFT_STATE, "ENG COMBUSTION:2", "bool");
        Add(DEF_AIRCRAFT_STATE, "ENG COMBUSTION:3", "bool");
        Add(DEF_AIRCRAFT_STATE, "ENG COMBUSTION:4", "bool");
        // ---- Fuel ----
        Add(DEF_AIRCRAFT_STATE, "FUEL TOTAL QUANTITY WEIGHT", "pounds");
        Add(DEF_AIRCRAFT_STATE, "FUEL TOTAL QUANTITY",        "gallons");
        // The per-tank sim vars are named "FUEL TANK <tank> QUANTITY" — "FUEL LEFT MAIN QUANTITY"
        // does not exist and is rejected with SIMCONNECT_EXCEPTION_NAME_UNRECOGNIZED. A rejected
        // entry is silently dropped, shifting every later struct field (SimOnGround ended up
        // reading the wrong variable, killing all landing detection). Volume vars: gallons only.
        Add(DEF_AIRCRAFT_STATE, "FUEL TANK LEFT MAIN QUANTITY",  "gallons");
        Add(DEF_AIRCRAFT_STATE, "FUEL TANK RIGHT MAIN QUANTITY", "gallons");
        Add(DEF_AIRCRAFT_STATE, "GENERAL ENG FUEL USED SINCE START:1", "pounds");
        // ---- Controls ----
        Add(DEF_AIRCRAFT_STATE, "FLAPS HANDLE INDEX",             "number");
        Add(DEF_AIRCRAFT_STATE, "TRAILING EDGE FLAPS LEFT PERCENT","percent");
        Add(DEF_AIRCRAFT_STATE, "SPOILERS HANDLE POSITION",       "percent");
        Add(DEF_AIRCRAFT_STATE, "GEAR HANDLE POSITION",           "bool");
        Add(DEF_AIRCRAFT_STATE, "GEAR CENTER POSITION",           "percent");
        Add(DEF_AIRCRAFT_STATE, "GEAR LEFT POSITION",             "percent");
        Add(DEF_AIRCRAFT_STATE, "GEAR RIGHT POSITION",            "percent");
        Add(DEF_AIRCRAFT_STATE, "BRAKE LEFT POSITION EX1",        "position");
        Add(DEF_AIRCRAFT_STATE, "BRAKE RIGHT POSITION EX1",       "position");
        // ---- Autopilot ----
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT MASTER",            "bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT ALTITUDE LOCK",     "bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT ALTITUDE LOCK VAR", "feet");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT HEADING LOCK",      "bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT HEADING LOCK DIR",  "degrees");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT AIRSPEED HOLD",     "bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT AIRSPEED HOLD VAR", "knots");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT VERTICAL HOLD",     "bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT VERTICAL HOLD VAR", "feet/minute");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT NAV1 LOCK",         "bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT APPROACH HOLD",     "bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT APPROACH CAPTURED", "bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT FLIGHT LEVEL CHANGE","bool");
        Add(DEF_AIRCRAFT_STATE, "AUTOPILOT THROTTLE ARM",      "bool");
        // ---- Nav ----
        Add(DEF_AIRCRAFT_STATE, "NAV ACTIVE FREQUENCY:1", "MHz");
        Add(DEF_AIRCRAFT_STATE, "NAV ACTIVE FREQUENCY:2", "MHz");
        Add(DEF_AIRCRAFT_STATE, "COM ACTIVE FREQUENCY:1", "MHz");
        Add(DEF_AIRCRAFT_STATE, "NAV CDI:1",               "number");
        Add(DEF_AIRCRAFT_STATE, "NAV GSI:1",               "number");
        Add(DEF_AIRCRAFT_STATE, "NAV LOCALIZER:1",         "bool");
        Add(DEF_AIRCRAFT_STATE, "NAV GS FLAG:1",           "bool");
        Add(DEF_AIRCRAFT_STATE, "NAV DME:1",               "nautical miles");
        // ---- Ambient ----
        Add(DEF_AIRCRAFT_STATE, "AMBIENT TEMPERATURE",  "celsius");
        Add(DEF_AIRCRAFT_STATE, "AMBIENT PRESSURE",     "millibars");
        Add(DEF_AIRCRAFT_STATE, "SEA LEVEL PRESSURE",   "millibars");
        Add(DEF_AIRCRAFT_STATE, "TOTAL AIR TEMPERATURE","celsius");
        Add(DEF_AIRCRAFT_STATE, "AMBIENT VISIBILITY",   "meters");
        // ---- Ground/misc ----
        Add(DEF_AIRCRAFT_STATE, "SIM ON GROUND",          "bool");
        Add(DEF_AIRCRAFT_STATE, "ON ANY RUNWAY",          "bool");
        Add(DEF_AIRCRAFT_STATE, "BRAKE PARKING INDICATOR",  "bool");
        Add(DEF_AIRCRAFT_STATE, "STALL WARNING",          "bool");
        Add(DEF_AIRCRAFT_STATE, "OVERSPEED WARNING",      "bool");
        Add(DEF_AIRCRAFT_STATE, "TOTAL WEIGHT",           "pounds");
        Add(DEF_AIRCRAFT_STATE, "EMPTY WEIGHT",           "pounds");
        Add(DEF_AIRCRAFT_STATE, "MAX GROSS WEIGHT",       "pounds");
        Add(DEF_AIRCRAFT_STATE, "CG PERCENT",             "percent");
        Add(DEF_AIRCRAFT_STATE, "TRANSPONDER CODE:1",     "number");
        Add(DEF_AIRCRAFT_STATE, "GPS IS ACTIVE FLIGHT PLAN","bool");
        Add(DEF_AIRCRAFT_STATE, "GPS WP DISTANCE",        "nautical miles");
        Add(DEF_AIRCRAFT_STATE, "GPS WP BEARING",         "degrees");
        Add(DEF_AIRCRAFT_STATE, "SIMULATION RATE",        "number");
        Add(DEF_AIRCRAFT_STATE, "LOCAL TIME",             "seconds");
        Add(DEF_AIRCRAFT_STATE, "ZULU TIME",              "seconds");
        Add(DEF_AIRCRAFT_STATE, "PLANE IN PARKING STATE", "bool");

        // Register the state struct
        var regGeneric = regMethod!.MakeGenericMethod(typeof(AircraftState));
        regGeneric.Invoke(_simConnect, [Id(DEF_AIRCRAFT_STATE)]);

        // ---- Aircraft Identification (string fields, slow-polled at ~0.5Hz) ----
        var strTypeNames = new[] { "STRING8", "STRING32", "STRING64", "STRING128", "STRING256" };
        var strTypeSizes = new[] { 8,         32,         64,         128,          256 };
        var strTypeObjs  = new object?[strTypeSizes.Length];
        for (int i = 0; i < strTypeNames.Length; i++)
            strTypeObjs[i] = Enum.Parse(dataTypeEnum, strTypeNames[i]);

        object? Str(int size) => strTypeObjs[System.Array.IndexOf(strTypeSizes, size)];

        void AddStr(string varName, int size)
        {
            addMethod!.Invoke(_simConnect, [Id(DEF_AIRCRAFT_IDENT), varName, null, Str(size), 0f, uint.MaxValue]);
            TrackSendId(varName);
        }

        AddStr("TITLE",              256);
        AddStr("ATC MODEL",           64);
        AddStr("ATC ID",              64);
        AddStr("ATC AIRLINE",         64);
        AddStr("ATC FLIGHT NUMBER",   64);
        AddStr("GPS WP NEXT ID",      64);

        var regIdent = regMethod.MakeGenericMethod(typeof(AircraftIdentification));
        regIdent.Invoke(_simConnect, [Id(DEF_AIRCRAFT_IDENT)]);

        Log.Information("[SimConnect] Data definitions registered ({Count} vars + ident)", 93);
    }

    private void SubscribeSystemEvents()
    {
        if (_simConnect == null) return;
        var sub        = _scType!.GetMethod("SubscribeToSystemEvent");
        var subEnumType = _scAssembly!.GetType(
            "Microsoft.FlightSimulator.SimConnect.SIMCONNECT_DATATYPE")!;
        void Subs(uint id, string name) =>
            sub!.Invoke(_simConnect, [Enum.ToObject(subEnumType, (int)id), name]);

        Subs(10, "SimStart");
        Subs(11, "SimStop");
        Subs(12, "Pause");
        Subs(14, "Crashed");
    }

    // =====================================================
    // POLLING  (20Hz)
    // =====================================================

    private void StartPolling()
    {
        // CRIT-09: Cache the reflection lookups that were previously done every 50ms in PollTelemetry.
        if (_scAssembly != null && _scType != null)
        {
            var simObjType = _scAssembly.GetType(
                "Microsoft.FlightSimulator.SimConnect.SIMCONNECT_SIMOBJECT_TYPE")!;
            _cachedSimObjectTypeUser  = Enum.Parse(simObjType, "USER");
            _cachedRequestDataMethod  = _scType.GetMethod("RequestDataOnSimObjectType");
            _cachedEnumCarrierType    = _scAssembly.GetType(
                "Microsoft.FlightSimulator.SimConnect.SIMCONNECT_DATATYPE")!;
        }

        _pollTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(50);
        _pollTimer.Tick += (_, _) => PollTelemetry();
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void PollTelemetry()
    {
        if (_simConnect == null) return;
        try
        {
            // CRIT-09: Use cached reflection values instead of resolving at 20Hz.
            var reqState = Enum.ToObject(_cachedEnumCarrierType!, (int)REQ_AIRCRAFT_STATE);
            var defState = Enum.ToObject(_cachedEnumCarrierType!, (int)DEF_AIRCRAFT_STATE);
            _cachedRequestDataMethod!.Invoke(_simConnect,
                [reqState, defState, 0u, _cachedSimObjectTypeUser]);

            // Poll ident at ~0.5Hz (every IDENT_POLL_INTERVAL ticks)
            if (++_identPollCounter >= IDENT_POLL_INTERVAL)
            {
                _identPollCounter = 0;
                var reqIdent = Enum.ToObject(_cachedEnumCarrierType!, (int)REQ_AIRCRAFT_IDENT);
                var defIdent = Enum.ToObject(_cachedEnumCarrierType!, (int)DEF_AIRCRAFT_IDENT);
                _cachedRequestDataMethod.Invoke(_simConnect,
                    [reqIdent, defIdent, 0u, _cachedSimObjectTypeUser]);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SimConnect] Poll error");
            HandleConnectionLost();
        }
    }

    // =====================================================
    // EVENT CALLBACKS  (called by reflected delegates)
    // =====================================================

    private void OnRecvOpen(object sender, object data)
    {
        Log.Information("[SimConnect] Connected to {Sim}", _sdkVersionLoaded);
        _connected = true;
        UpdateStatus(SimConnectionStatus.Connected);
        StartPolling();
    }

    private void OnRecvQuit(object sender, object data)
    {
        Log.Information("[SimConnect] Simulator closing");
        HandleConnectionLost();
        SimStopped?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecvException(object sender, object data)
    {
        try
        {
            var ex   = (uint)data.GetType().GetField("dwException")!.GetValue(data)!;
            var send = (uint)data.GetType().GetField("dwSendID")!.GetValue(data)!;

            if (_defVarBySendId.TryGetValue(send, out var varName))
            {
                // A rejected data-definition entry is silently dropped by the sim, which shifts
                // every later AircraftState field onto the wrong variable. This corrupted
                // SimOnGround (→ landing detection dead) until the bad entries were fixed —
                // treat any recurrence as a serious error, not a warning.
                Log.Error("[SimConnect] Sim REJECTED data definition '{Var}' (exception {Code}). " +
                    "All telemetry fields after this variable are now misaligned — " +
                    "fix the variable name/unit in RegisterDataDefinitions!", varName, ex);
            }
            else
            {
                Log.Warning("[SimConnect] Exception: {Code} SendID={Send}", ex, send);
            }
        }
        catch { /* field names changed — ignore */ }
    }

    private void OnRecvSimobjectData(object sender, object data)
    {
        try
        {
            var reqId  = (uint)data.GetType().GetField("dwRequestID")!.GetValue(data)!;
            var dwData = (object[])data.GetType().GetField("dwData")!.GetValue(data)!;

            if (reqId == REQ_AIRCRAFT_STATE && dwData.Length > 0 &&
                dwData[0] is AircraftState state)
            {
                var snapshot = new TelemetrySnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    Raw       = state
                };
                TelemetryReceived?.Invoke(this, snapshot);
            }
            else if (reqId == REQ_AIRCRAFT_IDENT && dwData.Length > 0 &&
                     dwData[0] is AircraftIdentification ident)
            {
                AircraftIdentReceived?.Invoke(this, ident);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SimConnect] Data callback error");
        }
    }

    private void OnRecvSystemEvent(object sender, object data)
    {
        try
        {
            var eventId = (uint)data.GetType().GetField("uEventID")!.GetValue(data)!;
            switch (eventId)
            {
                case 10: SimStarted?.Invoke(this, EventArgs.Empty);   break;
                case 11: SimStopped?.Invoke(this, EventArgs.Empty);   break;
                case 14: CrashDetected?.Invoke(this, EventArgs.Empty); break;
            }
        }
        catch { /* ignore */ }
    }

    // =====================================================
    // AUTO-RECONNECT
    // =====================================================

    private void InitReconnectTimer()
    {
        _reconnectTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _reconnectTimer.Interval = TimeSpan.FromSeconds(5);
        _reconnectTimer.Tick += (_, _) =>
        {
            if (!_connected && AutoConnect && HasAnySdk && _hwnd != IntPtr.Zero)
                TryConnect();
        };
        _reconnectTimer.Start();
    }

    private void HandleConnectionLost()
    {
        StopPolling();
        try { (_simConnect as IDisposable)?.Dispose(); } catch { }
        _simConnect = null;
        _connected  = false;
        UpdateStatus(SimConnectionStatus.Disconnected);
    }

    private void UpdateStatus(SimConnectionStatus status)
    {
        if (ConnectionStatus == status) return;
        ConnectionStatus = status;
        ConnectionStatusChanged?.Invoke(this, status);
    }

    // =====================================================
    // DISPOSE
    // =====================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reconnectTimer?.Stop();
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
