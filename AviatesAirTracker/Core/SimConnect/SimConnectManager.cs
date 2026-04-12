using AviatesAirTracker.Core.SimConnect;
using Serilog;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AviatesAirTracker.Core.SimConnect;

// ============================================================
// SIMCONNECT MANAGER  (Runtime-reflection edition)
//
// SimConnect is loaded at RUNTIME, not at build time.
// This means:
//   - The project always compiles without any SDK installed
//   - If the managed wrapper DLL is present next to the EXE
//     (or in Libs\) it is loaded via Assembly.LoadFrom
//   - If it is absent/native, the app runs in stub mode and
//     all SimConnect calls are silent no-ops
//
// How to enable live telemetry:
//   1. Install the MSFS Developer SDK
//   2. Copy the MANAGED wrapper (NOT SimConnect.dll):
//      From: [MSFS SDK]\SimConnect SDK\lib\managed\
//            Microsoft.FlightSimulator.SimConnect.dll
//      To:   same folder as AviatesAirTracker.exe
//         OR AviatesAirTracker\Libs\  (for development)
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

    // =====================================================
    // RUNTIME STATE
    // =====================================================
    private object?  _simConnect;          // dynamic SimConnect instance
    private Assembly? _scAssembly;          // loaded SDK assembly
    private Type?     _scType;              // SimConnect class type
    private bool      _sdkAvailable;        // true if managed DLL loaded OK
    private bool      _connected;
    private bool      _disposed;
    private IntPtr    _hwnd = IntPtr.Zero;

    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _reconnectTimer;

    private const int    WM_USER_SIMCONNECT = 0x0402;
    private const string APP_NAME           = "AviatesAirTracker";

    // Data definition / request IDs (mirror the enum values)
    private const uint DEF_AIRCRAFT_STATE = 1;
    private const uint DEF_AIRCRAFT_IDENT = 2;
    private const uint REQ_AIRCRAFT_STATE = 1;
    private const uint REQ_AIRCRAFT_IDENT = 2;

    // CRIT-09: Cached reflection values — were resolved via GetType()/Enum.Parse() at 20Hz (144,000 calls/2hr).
    // These types/values never change after SDK load, so cache them once.
    private object?    _cachedSimObjectTypeUser;
    private MethodInfo? _cachedRequestDataMethod;
    // SimConnect methods take Enum for DefineID/RequestID/EventID — raw uint cannot be passed via reflection.
    // We use SIMCONNECT_DATATYPE as the carrier type; Enum.ToObject boxes any uint into a valid enum value.
    private Type?      _cachedEnumCarrierType;

    // Ident is slow-polled: every IDENT_POLL_INTERVAL ticks (~2s) to get aircraft title, callsign, etc.
    private int _identPollCounter;
    private const int IDENT_POLL_INTERVAL = 40;

    // Pre-loads the native SimConnect.dll so Assembly.LoadFrom on the managed wrapper succeeds.
    // The managed wrapper P/Invokes into SimConnect.dll; if it isn't in the Windows search path
    // the CLR throws FileNotFoundException with "The specified module could not be found".
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    private readonly Dispatcher _dispatcher;

    // =====================================================
    // CONSTRUCTION
    // =====================================================

    public SimConnectManager()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        TryLoadSdk();
        // MINOR-01: Reconnect timer is started in SetWindowHandle, not here.
        // Starting it before the HWND is available means it fires needlessly until the window is ready.
    }

    // =====================================================
    // SDK LOADING
    // =====================================================

    private static void PreloadNative(string nativePath)
    {
        if (!File.Exists(nativePath)) return;
        var handle = LoadLibraryW(nativePath);
        if (handle == IntPtr.Zero)
            Log.Warning("[SimConnect] Could not pre-load native {Path} (error {Code}) — " +
                "managed wrapper may still fail", nativePath, Marshal.GetLastWin32Error());
        else
            Log.Information("[SimConnect] Pre-loaded native {Path}", nativePath);
    }

    private void TryLoadSdk()
    {
        // Search order: EXE folder first, then Libs\ subfolder
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory,
                "Microsoft.FlightSimulator.SimConnect.dll"),
            Path.Combine(AppContext.BaseDirectory,
                "Libs", "Microsoft.FlightSimulator.SimConnect.dll"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;

            // The managed wrapper P/Invokes the native SimConnect.dll. If it isn't in the Windows
            // DLL search path, Assembly.LoadFrom throws FileNotFoundException with
            // "The specified module could not be found" even though the managed DLL is right there.
            // Fix: explicitly pre-load SimConnect.dll from the same directory so the OS has it
            // cached before the CLR tries to resolve the P/Invoke dependency.
            var dir = Path.GetDirectoryName(path)!;
            PreloadNative(Path.Combine(dir, "SimConnect.dll"));

            try
            {
                _scAssembly  = Assembly.LoadFrom(path);
                _scType      = _scAssembly.GetType(
                    "Microsoft.FlightSimulator.SimConnect.SimConnect", throwOnError: true);
                _sdkAvailable = true;
                Log.Information("[SimConnect] Managed SDK loaded from: {Path}", path);
                return;
            }
            catch (BadImageFormatException)
            {
                Log.Warning("[SimConnect] {Path} is a native DLL — skipping. " +
                    "You need the MANAGED wrapper from: " +
                    "[MSFS SDK]\\SimConnect SDK\\lib\\managed\\", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SimConnect] Failed to load SDK from: {Path}", path);
            }
        }

        _sdkAvailable = false;
        Log.Warning("[SimConnect] Managed wrapper not found — running in stub mode. " +
                    "Live telemetry disabled.");
    }

    // =====================================================
    // WINDOW HANDLE  (called by MainWindow after load)
    // =====================================================

    public void SetWindowHandle(IntPtr hwnd)
    {
        _hwnd = hwnd;
        // MINOR-01: Start reconnect timer only now that we have the HWND.
        InitReconnectTimer();
        if (_sdkAvailable)
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
        if (!_sdkAvailable || _connected || _hwnd == IntPtr.Zero) return;

        try
        {
            Log.Information("[SimConnect] Connecting to MSFS...");
            UpdateStatus(SimConnectionStatus.Connecting);

            // new SimConnect(name, hwnd, msgId, null, 0)
            _simConnect = Activator.CreateInstance(
                _scType!,
                APP_NAME, _hwnd, (uint)WM_USER_SIMCONNECT, null, (uint)0);

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
            when (tie.InnerException is System.Runtime.InteropServices.COMException)
        {
            Log.Debug("[SimConnect] MSFS not running yet.");
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
        // Enum.ToObject boxes the uint into a valid enum using SIMCONNECT_DATATYPE as the carrier type.
        // The SDK only reads the underlying integer value, so any enum type works.
        object Id(uint v) => Enum.ToObject(dataTypeEnum, (int)v);

        // Helper: AddToDataDefinition(defId, varName, unit, type, epsilon, datumId)
        var addMethod    = _scType!.GetMethod("AddToDataDefinition");
        var regMethod    = _scType.GetMethod("RegisterDataDefineStruct");

        void Add(uint defId, string varName, string? unit)
        {
            addMethod!.Invoke(_simConnect,
                [Id(defId), varName, unit, float64Val, 0f, uint.MaxValue]);
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
        // MAJOR-10: Fields named FuelLeftMainLbs / FuelRightMainLbs were registered as "gallons".
        // Unit corrected to "pounds" to match the field names and downstream usage.
        Add(DEF_AIRCRAFT_STATE, "FUEL LEFT MAIN QUANTITY",    "pounds");
        Add(DEF_AIRCRAFT_STATE, "FUEL RIGHT MAIN QUANTITY",   "pounds");
        Add(DEF_AIRCRAFT_STATE, "ENG FUEL USED SINCE START:1","pounds");
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
        Add(DEF_AIRCRAFT_STATE, "CG PERCENT MAC",         "percent");
        Add(DEF_AIRCRAFT_STATE, "TRANSPONDER CODE:1",     "number");
        Add(DEF_AIRCRAFT_STATE, "GPS IS ACTIVE FLIGHT PLAN","bool");
        Add(DEF_AIRCRAFT_STATE, "GPS WP NEXT DISTANCE",   "nautical miles");
        Add(DEF_AIRCRAFT_STATE, "GPS WP BEARING",         "degrees");
        Add(DEF_AIRCRAFT_STATE, "SIMULATION RATE",        "number");
        Add(DEF_AIRCRAFT_STATE, "LOCAL TIME",             "seconds");
        Add(DEF_AIRCRAFT_STATE, "ZULU TIME",              "seconds");
        Add(DEF_AIRCRAFT_STATE, "PLANE IN PARKING STATE", "bool");

        // Register the state struct
        var regGeneric = regMethod!.MakeGenericMethod(typeof(AircraftState));
        regGeneric.Invoke(_simConnect, [Id(DEF_AIRCRAFT_STATE)]);

        // ---- Aircraft Identification (string fields, slow-polled at ~0.5Hz) ----
        // String types require SIMCONNECT_DATATYPE_STRING* instead of FLOAT64
        var strTypeNames = new[] { "STRING8", "STRING32", "STRING64", "STRING128", "STRING256" };
        var strTypeSizes = new[] { 8,         32,         64,         128,          256 };
        var strTypeObjs  = new object?[strTypeSizes.Length];
        for (int i = 0; i < strTypeNames.Length; i++)
            strTypeObjs[i] = Enum.Parse(dataTypeEnum, strTypeNames[i]);

        object? Str(int size) => strTypeObjs[System.Array.IndexOf(strTypeSizes, size)];

        void AddStr(string varName, int size) =>
            addMethod!.Invoke(_simConnect, [Id(DEF_AIRCRAFT_IDENT), varName, null, Str(size), 0f, uint.MaxValue]);

        AddStr("TITLE",              256);
        AddStr("ATC MODEL",           64);
        AddStr("ATC ID",              64);
        AddStr("ATC AIRLINE",         64);
        AddStr("ATC FLIGHT NUMBER",   64);
        AddStr("GPS WP NEXT ID",      64);

        var regIdent = regMethod.MakeGenericMethod(typeof(AircraftIdentification));
        regIdent.Invoke(_simConnect, [Id(DEF_AIRCRAFT_IDENT)]);

        Log.Information("[SimConnect] Data definitions registered ({Count} vars + ident)", 70);
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

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
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
            // RequestDataOnSimObjectType also requires Enum for reqId/defId — use the cached carrier type.
            var reqState = Enum.ToObject(_cachedEnumCarrierType!, (int)REQ_AIRCRAFT_STATE);
            var defState = Enum.ToObject(_cachedEnumCarrierType!, (int)DEF_AIRCRAFT_STATE);
            _cachedRequestDataMethod!.Invoke(_simConnect,
                [reqState, defState, 0u, _cachedSimObjectTypeUser]);

            // Poll ident at ~0.5Hz (every IDENT_POLL_INTERVAL ticks) — aircraft title, callsign, etc.
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
        Log.Information("[SimConnect] Connected to MSFS");
        _connected = true;
        UpdateStatus(SimConnectionStatus.Connected);
        StartPolling();
    }

    private void OnRecvQuit(object sender, object data)
    {
        Log.Information("[SimConnect] MSFS closing");
        HandleConnectionLost();
        SimStopped?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecvException(object sender, object data)
    {
        try
        {
            var ex   = (uint)data.GetType().GetField("dwException")!.GetValue(data)!;
            var send = (uint)data.GetType().GetField("dwSendID")!.GetValue(data)!;
            Log.Warning("[SimConnect] Exception: {Code} SendID={Send}", ex, send);
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
                case 10: SimStarted?.Invoke(this, EventArgs.Empty);  break;
                case 11: SimStopped?.Invoke(this, EventArgs.Empty);  break;
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
        _reconnectTimer = new DispatcherTimer
            { Interval = TimeSpan.FromSeconds(5) };
        _reconnectTimer.Tick += (_, _) =>
        {
            if (!_connected && _sdkAvailable && _hwnd != IntPtr.Zero)
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
