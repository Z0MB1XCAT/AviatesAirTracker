# WinUI 3 Foundation and Strangler Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the WPF-hosted Blazor Hybrid app into a WinUI 3 app whose native shell hosts the existing Razor UI, with no loss of function and no visual change.

**Architecture:** The existing Razor pages are converted from Blazor **Hybrid** to Blazor **Server**, served by a second in-process Kestrel host bound to `127.0.0.1`, and displayed in a plain `WebView2` inside a WinUI 3 `NavigationView` shell. Both the native shell and the Razor pages resolve services from one shared DI container. This is the scaffold that later phases dismantle one page at a time.

**Tech Stack:** .NET 8, WinUI 3 (Windows App SDK 1.8), ASP.NET Core Blazor Server (via the already-present `Microsoft.AspNetCore.App` FrameworkReference), WebView2, xunit, CommunityToolkit.Mvvm.

**Spec:** [`docs/superpowers/specs/2026-08-13-winui3-migration-design.md`](../specs/2026-08-13-winui3-migration-design.md)

---

## Resequencing note — read before starting

The spec lists Phase 0 (foundation) and Phase 1 (bridge + shell) as separate phases, each ending in a flyable build. **That boundary does not survive contact with the toolchain.**

`Microsoft.AspNetCore.Components.WebView.Wpf` is a WPF-only package. The instant the csproj flips from `<UseWPF>` to `<UseWinUI>`, `BlazorWebView` stops existing and the entire UI is gone. So a standalone Phase 0 would end in a build that compiles but has no user interface — not a flyable build.

This plan therefore merges Phases 0 and 1 and orders the work so the platform flip happens **last**, after the replacement UI host is already proven:

| Part | Platform | App usable? | What it proves |
|---|---|---|---|
| A — Groundwork | WPF | Yes, fully | Platform layer and deletions are safe in isolation |
| B — Blazor Server bridge | WPF | Yes, fully | The Razor pages work over SignalR in a plain WebView2 |
| C — Extract Razor into an RCL | WPF | Yes, fully | Static assets and scoped CSS still resolve from a library |
| D — Flip to WinUI 3 | WinUI 3 | Yes, after Task 13 | The shell hosts what Parts B and C already proved |

Only Part D can break the app, and by then every other variable has been eliminated. If Part D goes wrong, Parts A–C are still valuable and shipped.

---

## File Structure

### Created

| File | Responsibility |
|---|---|
| `tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj` | xunit host for the platform layer |
| `tests/AviatesAirTracker.Tests/Platform/WindowSubclassTests.cs` | Delegate-lifetime contract, headless |
| `tests/AviatesAirTracker.Tests/Platform/WindowSubclassInteropTests.cs` | Real-HWND coverage incl. the GC regression guard |
| `tests/AviatesAirTracker.Tests/Platform/ImmediateUiDispatcherTests.cs` | Dispatcher seam contract |
| `AviatesAirTracker/Platform/IUiDispatcher.cs` | UI-thread marshalling seam |
| `AviatesAirTracker/Platform/ImmediateUiDispatcher.cs` | Inline impl for tests and startup |
| `AviatesAirTracker/Platform/WpfUiDispatcher.cs` | **Transitional**, deleted in Task 9 |
| `AviatesAirTracker/Platform/DispatcherQueueUiDispatcher.cs` | WinUI impl |
| `AviatesAirTracker/Platform/WindowSubclass.cs` | `HwndSource.AddHook` replacement |
| `AviatesAirTracker/Hosting/BlazorHostServer.cs` | Localhost Kestrel + Blazor Server host |
| `AviatesAirTracker/Hosting/_Host.cshtml` | Blazor Server root page (replaces `index.html`) |
| `AviatesAirTracker.LegacyWeb/AviatesAirTracker.LegacyWeb.csproj` | RCL holding the Razor UI; deleted wholesale in Phase 7 |
| `AviatesAirTracker/ShellWindow.xaml` + `.xaml.cs` | WinUI window: title bar, Mica, `NavigationView`, WebView2 |
| `AviatesAirTracker/Navigation/NavDestinations.cs` | Single source of truth for the 12 destinations |

### Modified

| File | Change |
|---|---|
| `AviatesAirTracker/AviatesAirTracker.csproj` | Razor SDK + WPF → `Microsoft.NET.Sdk` + WinUI (Task 9) |
| `AviatesAirTracker/App.xaml.cs` | WPF `Application` → WinUI `Application`; DI unchanged |
| `AviatesAirTracker/ViewModels/MainViewModel.cs` | 7 dispatcher sites → `IUiDispatcher`; dead nav commands removed |
| `AviatesAirTracker/ViewModels/MapViewModel.cs` | 2 dispatcher sites → `IUiDispatcher` |
| `AviatesAirTracker/ViewModels/ReplayViewModel.cs` | `DispatcherTimer` → `DispatcherQueueTimer` |
| `AviatesAirTracker/Core/SimConnect/SimConnectManager.cs` | 2 `DispatcherTimer`s → `DispatcherQueueTimer` |
| `AviatesAirTracker/Services/UpdateService.cs` | 1 dispatcher site → `IUiDispatcher` |
| `AviatesAirTracker/_Imports.razor` | Drop the two OxyPlot usings |
| `AviatesAirTracker.sln` | Add the test and RCL projects |

### Deleted

`Views/` (8 `.xaml` + `ViewCodeBehinds.cs` + `ViewPartials.cs`), `Controls/AviationControls.cs`, `Converters/`, `Resources/Styles/`, `Core/Analytics/FlightProfileCharts.cs`, `ViewModels/LiveFlightViewModel.cs`, `ViewModels/StatisticsViewModel.cs`, `ViewModels/TelemetryViewModel.cs`, `MainWindow.xaml` + `.xaml.cs`, `wwwroot/index.html`.

---

## Conventions for every task

- **Build:** `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
- **Test:** `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
- **Run:** `.\build.ps1 -Run`
- The repo currently builds with **14 pre-existing warnings**. A task is clean if it does not *add* warnings; do not chase the existing 14.
- Commit after every task. Never leave the tree broken between tasks.

---

# Part A — Groundwork (stays on WPF)

## Task 1: Test project and the UI-dispatcher seam

**Files:**
- Create: `tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
- Create: `AviatesAirTracker/Platform/IUiDispatcher.cs`
- Create: `AviatesAirTracker/Platform/ImmediateUiDispatcher.cs`
- Test: `tests/AviatesAirTracker.Tests/Platform/ImmediateUiDispatcherTests.cs`
- Modify: `AviatesAirTracker.sln`

- [ ] **Step 1: Create the test project**

The test project targets plain `net8.0` — **not** `net8.0-windows10.0.19041.0`. It compiles the platform sources by file link rather than referencing the app project, so tests never drag in WPF or the Windows App SDK and run fast.

Create `tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="../../AviatesAirTracker/Platform/IUiDispatcher.cs" Link="Platform/IUiDispatcher.cs" />
    <Compile Include="../../AviatesAirTracker/Platform/ImmediateUiDispatcher.cs" Link="Platform/ImmediateUiDispatcher.cs" />
  </ItemGroup>

</Project>
```

`WindowSubclass.cs` is deliberately **not** listed yet — it does not exist until Task 2, and an `Include` pointing at a missing file fails the build. Task 2 Step 5 adds that line.

- [ ] **Step 2: Write the failing test**

Create `tests/AviatesAirTracker.Tests/Platform/ImmediateUiDispatcherTests.cs`:

```csharp
using AviatesAirTracker.Platform;
using Xunit;

namespace AviatesAirTracker.Tests.Platform;

public class ImmediateUiDispatcherTests
{
    [Fact]
    public void Invoke_RunsSynchronouslyOnTheCallingThread()
    {
        var dispatcher = new ImmediateUiDispatcher();
        var callingThread = Environment.CurrentManagedThreadId;
        var observedThread = 0;

        dispatcher.Invoke(() => observedThread = Environment.CurrentManagedThreadId);

        Assert.Equal(callingThread, observedThread);
    }

    [Fact]
    public void Invoke_PropagatesExceptions()
    {
        var dispatcher = new ImmediateUiDispatcher();

        Assert.Throws<InvalidOperationException>(
            () => dispatcher.Invoke(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void BeginInvoke_RunsTheAction()
    {
        var dispatcher = new ImmediateUiDispatcher();
        var ran = false;

        dispatcher.BeginInvoke(() => ran = true);

        Assert.True(ran);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
Expected: FAIL — compile error, `IUiDispatcher` and `ImmediateUiDispatcher` do not exist.

- [ ] **Step 4: Write the implementation**

Create `AviatesAirTracker/Platform/IUiDispatcher.cs`:

```csharp
namespace AviatesAirTracker.Platform;

/// <summary>
/// Marshals work onto the UI thread. Exists so the ViewModel layer depends on no UI
/// framework type — this replaced direct <c>Application.Current.Dispatcher</c> calls
/// during the WinUI 3 migration, and keeps the 20Hz telemetry path testable.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it.</summary>
    void Invoke(Action action);

    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting.</summary>
    void BeginInvoke(Action action);
}
```

Create `AviatesAirTracker/Platform/ImmediateUiDispatcher.cs`:

```csharp
namespace AviatesAirTracker.Platform;

/// <summary>
/// Runs everything inline on the calling thread. Used by tests, and as a safe fallback
/// before the UI thread's <c>DispatcherQueue</c> exists (e.g. during startup).
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
    public void BeginInvoke(Action action) => action();
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
Expected: PASS — 3 passed.

- [ ] **Step 6: Register the test project in the solution**

Run: `dotnet sln AviatesAirTracker.sln add tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`

- [ ] **Step 7: Commit**

```bash
git add tests/ AviatesAirTracker/Platform/ AviatesAirTracker.sln
git commit -m "test(platform): xunit project and the IUiDispatcher seam"
```

---

## Task 2: Win32 window subclass — the GC regression guard

This replaces `HwndSource.AddHook`, which does not exist in WinUI 3. It is the highest-risk single piece of the migration: if the native callback delegate is not rooted, the process dies on a SimConnect message, mid-flight, non-deterministically.

> **Amended during implementation.** Code review found a second way this class could kill
> the process: `OnSubclassProc` is a reverse P/Invoke, so an exception escaping the
> callback unwinds across the native boundary and fail-fasts — `App.xaml.cs`'s handlers
> cannot stop it. The constructor therefore takes a second argument,
> `Action<Exception>? onError`, and the callback invocation is wrapped in try/catch.
> `WindowSubclass` stays dependency-free (it is compiled into the test project, which has
> no Serilog reference), so the consumer supplies the logging — see Task 10.
>
> Review also established that `_proc` is load-bearing for a **second** reason beyond GC
> safety: `RemoveWindowSubclass` must receive the same delegate instance to match the
> registered pointer, or the unhook silently does nothing.

**Files:**
- Create: `AviatesAirTracker/Platform/WindowSubclass.cs`
- Test: `tests/AviatesAirTracker.Tests/Platform/WindowSubclassTests.cs`
- Test: `tests/AviatesAirTracker.Tests/Platform/WindowSubclassInteropTests.cs`
- Modify: `tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj` (uncomment the `WindowSubclass.cs` link)

- [ ] **Step 1: Write the failing headless test**

Create `tests/AviatesAirTracker.Tests/Platform/WindowSubclassTests.cs`:

```csharp
using AviatesAirTracker.Platform;
using Xunit;

namespace AviatesAirTracker.Tests.Platform;

public class WindowSubclassTests
{
    [Fact]
    public void ForwardsMessagesToTheCallback()
    {
        var received = new List<(nint Hwnd, int Msg)>();
        var subclass = new WindowSubclass((hwnd, msg, _, _) => received.Add((hwnd, msg)));

        subclass.InvokeForTest(42, 0x0402, 0, 0);

        Assert.Single(received);
        Assert.Equal(42, received[0].Hwnd);
        Assert.Equal(0x0402, received[0].Msg);
    }

    [Fact]
    public void StopsForwardingAfterDetach()
    {
        var received = new List<int>();
        var subclass = new WindowSubclass((_, msg, _, _) => received.Add(msg));

        subclass.InvokeForTest(1, 0x0402, 0, 0);
        subclass.Detach();
        subclass.InvokeForTest(1, 0x0402, 0, 0);

        Assert.Single(received);
    }

    [Fact]
    public void DetachIsIdempotent()
    {
        var subclass = new WindowSubclass((_, _, _, _) => { });

        subclass.Detach();
        subclass.Detach();   // must not throw
    }
}
```

- [ ] **Step 2: Write the failing real-HWND test**

This is the test that actually protects the migration. It creates a genuine message-only window, subclasses it, forces a blocking gen-2 collection, then drives a real `SendMessage` through it.

Create `tests/AviatesAirTracker.Tests/Platform/WindowSubclassInteropTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
Expected: FAIL — compile error, `WindowSubclass` does not exist.

- [ ] **Step 4: Write the implementation**

Create `AviatesAirTracker/Platform/WindowSubclass.cs`:

```csharp
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
```

`InvokeForTest` is `internal`, so add to `AviatesAirTracker/Platform/WindowSubclass.cs`'s project an `InternalsVisibleTo` — **not needed here**, because the test project compiles the source file directly by `<Compile Include>` rather than referencing the assembly. Do not add `InternalsVisibleTo`.

- [ ] **Step 5: Add the WindowSubclass link to the test csproj**

In `tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`, add this line to the existing `<Compile Include>` group:

```xml
<Compile Include="../../AviatesAirTracker/Platform/WindowSubclass.cs" Link="Platform/WindowSubclass.cs" />
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
Expected: PASS — 8 passed (3 dispatcher, 3 headless subclass, 2 interop).

- [ ] **Step 7: Prove the regression guard actually bites**

This step verifies the test is real rather than vacuous. Temporarily edit `WindowSubclass.cs` and replace the constructor body's `_proc = OnSubclassProc;` with nothing, passing `OnSubclassProc` directly at both `SetWindowSubclass` and `RemoveWindowSubclass` call sites.

Run: `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
Expected: FAIL or a crashed test host on `NativeThunk_SurvivesGarbageCollection_AndStillDeliversMessages`.

**Revert the edit** and re-run to confirm PASS before committing. Do not commit the broken version.

- [ ] **Step 8: Commit**

```bash
git add AviatesAirTracker/Platform/WindowSubclass.cs tests/
git commit -m "feat(platform): Win32 window subclass for SimConnect messages"
```

---

## Task 3: Delete the dead WPF layer

~4,000 lines with zero live consumers. Deleting now shrinks everything that follows.

**Files:**
- Delete: `AviatesAirTracker/Views/` (all 8 `.xaml`, `ViewCodeBehinds.cs`, `ViewPartials.cs`)
- Delete: `AviatesAirTracker/Controls/AviationControls.cs`
- Delete: `AviatesAirTracker/Converters/ValueConverters.cs`, `AviatesAirTracker/Converters/ExtraConverters.cs`
- Delete: `AviatesAirTracker/Resources/Styles/` (AviatesTheme, Controls, Typography, Animations)

- [ ] **Step 1: Confirm zero consumers before deleting**

Run:

```bash
grep -rn "AviationControls\|CompassRose\|ValueConverters\|ExtraConverters\|Resources/Styles\|Views\." --include=*.cs --include=*.razor --include=*.xaml AviatesAirTracker/ | grep -v "^AviatesAirTracker/Views/\|^AviatesAirTracker/Controls/\|^AviatesAirTracker/Converters/\|^AviatesAirTracker/Resources/"
```

Expected: no output, apart from possible matches inside `App.xaml` merged dictionaries. If `App.xaml` merges `Resources/Styles/*.xaml`, remove those `<ResourceDictionary Source=...>` entries in Step 2.

If anything else appears, **stop** — the assumption that these are dead is wrong. Report it rather than deleting.

- [ ] **Step 2: Delete the files and clean App.xaml**

```bash
git rm -r AviatesAirTracker/Views AviatesAirTracker/Controls AviatesAirTracker/Converters AviatesAirTracker/Resources/Styles
```

Then open `AviatesAirTracker/App.xaml` and remove any `<ResourceDictionary Source="Resources/Styles/...">` entries from the merged dictionaries. If that leaves an empty `<ResourceDictionary.MergedDictionaries>` element, remove the element too.

- [ ] **Step 3: Build to verify nothing referenced them**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: Build succeeded, 14 warnings, 0 errors.

- [ ] **Step 4: Run the app and confirm it is unchanged**

Run: `.\build.ps1 -Run`
Expected: the app launches to the Dashboard exactly as before. Click through Flights, ACARS, Map, Settings — all render.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: delete the dead WPF view layer

Views/, Controls/AviationControls.cs, Converters/ and Resources/Styles/
had zero live consumers — nothing routed to them and they predated ACARS,
Fleet, Routes, Messages, Events and PilotHub entirely."
```

---

## Task 4: Delete OxyPlot and the three orphan ViewModels

No `PlotView` renders anywhere: `/live-flight` and `/telemetry` redirect to `/acars`, `/statistics` redirects to `/pilot-hub`. The charts have no consumer.

**Files:**
- Delete: `AviatesAirTracker/Core/Analytics/FlightProfileCharts.cs`
- Delete: `AviatesAirTracker/ViewModels/LiveFlightViewModel.cs`, `StatisticsViewModel.cs`, `TelemetryViewModel.cs`
- Delete: `AviatesAirTracker/Pages/LiveFlight.razor`, `Statistics.razor`, `TelemetryPage.razor`, `Replay.razor`, `LandingAnalysis.razor`
- Modify: `AviatesAirTracker/AviatesAirTracker.csproj`, `_Imports.razor`, `App.xaml.cs`, `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Preserve the 20Hz batching reference before deleting it**

`LiveFlightViewModel` holds the every-5-samples throttle that CLAUDE.md names as the model for the 20Hz path. Deleting the file would delete the only worked example, which Phase 3 needs.

Read `AviatesAirTracker/ViewModels/LiveFlightViewModel.cs` in full and copy the sample-counter block — the field, the increment, and the modulo guard around the property writes — into a new file `docs/superpowers/notes/20hz-batching-reference.md`, with a one-line header saying where it came from and why it was kept.

This is a documentation file, not code. It exists so Phase 3 does not have to reconstruct the cadence from git history.

- [ ] **Step 2: Delete the files**

```bash
git rm AviatesAirTracker/Core/Analytics/FlightProfileCharts.cs \
       AviatesAirTracker/ViewModels/LiveFlightViewModel.cs \
       AviatesAirTracker/ViewModels/StatisticsViewModel.cs \
       AviatesAirTracker/ViewModels/TelemetryViewModel.cs \
       AviatesAirTracker/Pages/LiveFlight.razor \
       AviatesAirTracker/Pages/Statistics.razor \
       AviatesAirTracker/Pages/TelemetryPage.razor \
       AviatesAirTracker/Pages/Replay.razor \
       AviatesAirTracker/Pages/LandingAnalysis.razor
```

- [ ] **Step 3: Remove the OxyPlot package references**

In `AviatesAirTracker/AviatesAirTracker.csproj`, delete these two lines and the comment above them:

```xml
<!-- Charts — WPF version kept for legacy XAML views; Blazor version for new Razor pages -->
<PackageReference Include="OxyPlot.Wpf" Version="2.1.2" />
<PackageReference Include="OxyPlot.Blazor" Version="2.1.0" />
```

- [ ] **Step 4: Remove the OxyPlot usings**

In `AviatesAirTracker/_Imports.razor`, delete the last two lines:

```razor
@using OxyPlot
@using OxyPlot.Blazor
```

- [ ] **Step 5: Remove the DI registrations**

In `AviatesAirTracker/App.xaml.cs`, delete these three lines from `ConfigureServices` (currently lines 334, 337, 341):

```csharp
services.AddSingleton<LiveFlightViewModel>();
services.AddSingleton<StatisticsViewModel>();
services.AddSingleton<TelemetryViewModel>();
```

- [ ] **Step 6: Remove the MainViewModel properties, constructor parameters and dead nav commands**

In `AviatesAirTracker/ViewModels/MainViewModel.cs`:

Delete these property declarations (lines 26, 29, 32):

```csharp
public LiveFlightViewModel LiveFlight { get; }
public StatisticsViewModel Statistics { get; }
public TelemetryViewModel Telemetry { get; }
```

Change the constructor signature from:

```csharp
        DashboardViewModel dashboard, LiveFlightViewModel liveFlight,
        MapViewModel map, LandingAnalysisViewModel landingAnalysis,
        StatisticsViewModel statistics, SettingsViewModel settingsVm,
        ReplayViewModel replay, TelemetryViewModel telemetry)
```

to:

```csharp
        DashboardViewModel dashboard,
        MapViewModel map, LandingAnalysisViewModel landingAnalysis,
        SettingsViewModel settingsVm,
        ReplayViewModel replay)
```

Change the assignment block from:

```csharp
        Dashboard = dashboard; LiveFlight = liveFlight; Map = map;
        LandingAnalysis = landingAnalysis; Statistics = statistics;
        SettingsVm = settingsVm; Replay = replay; Telemetry = telemetry;
```

to:

```csharp
        Dashboard = dashboard; Map = map;
        LandingAnalysis = landingAnalysis;
        SettingsVm = settingsVm; Replay = replay;
```

Delete these three `[RelayCommand]` methods, which navigated the deleted WPF views:

```csharp
[RelayCommand] public void NavigateToLiveFlight() => Navigate(LiveFlight,      "Live Flight",        ref _isLiveFlightActive);
[RelayCommand] public void NavigateToStats()      => Navigate(Statistics,      "Pilot Statistics",   ref _isStatsActive);
```

and the equivalent `NavigateToTelemetry` if present. Also delete their now-unused backing fields:

```csharp
[ObservableProperty] private bool _isLiveFlightActive;
[ObservableProperty] private bool _isStatsActive;
[ObservableProperty] private bool _isTelemetryActive;
```

- [ ] **Step 7: Build and fix any remaining references**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: Build succeeded, 0 errors.

If errors name a `.razor` page still referencing `LiveFlight`/`Statistics`/`Telemetry` on `MainViewModel`, remove those references — they are dead UI.

- [ ] **Step 8: Run the app and confirm the redirects are gone cleanly**

Run: `.\build.ps1 -Run`
Expected: the app launches to the Dashboard. Navigate to ACARS and PilotHub directly from the sidebar — both render. The `/live-flight`, `/statistics`, `/telemetry`, `/replay` and `/landings` routes now 404 to the "Page Not Found" view, which is correct: nothing in the sidebar links to them.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor(charts): delete OxyPlot and the three orphan ViewModels

No PlotView renders anywhere — /live-flight and /telemetry redirect to
/acars and /statistics to /pilot-hub, so FlightProfileCharts and the three
chart ViewModels had no consumer. Dashboard's activity chart is static
markup, not a charting library."
```

---

## Task 5: Route the ViewModels through IUiDispatcher

Removes the last `using System.Windows` from `ViewModels/`. A transitional WPF-backed implementation keeps the app working; Task 9 swaps it for the WinUI one.

**Files:**
- Create: `AviatesAirTracker/Platform/WpfUiDispatcher.cs` (deleted in Task 9)
- Modify: `AviatesAirTracker/ViewModels/MainViewModel.cs` (7 sites), `MapViewModel.cs` (2), `Services/UpdateService.cs` (1)
- Modify: `AviatesAirTracker/App.xaml.cs` (register `IUiDispatcher`)

- [ ] **Step 1: Create the transitional WPF implementation**

Create `AviatesAirTracker/Platform/WpfUiDispatcher.cs`:

```csharp
using System.Windows;

namespace AviatesAirTracker.Platform;

/// <summary>
/// TRANSITIONAL — exists only while the app is still on WPF during the WinUI 3 migration.
/// Deleted when <c>DispatcherQueueUiDispatcher</c> takes over. Its only job is to let the
/// ViewModel layer move onto <see cref="IUiDispatcher"/> in a separately verifiable step,
/// rather than changing the threading seam and the UI framework at the same time.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => Application.Current.Dispatcher.Invoke(action);
    public void BeginInvoke(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}
```

- [ ] **Step 2: Register it in DI**

In `AviatesAirTracker/App.xaml.cs`, in `ConfigureServices`, add under the `// Infrastructure` comment:

```csharp
services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
```

Add the using at the top of the file:

```csharp
using AviatesAirTracker.Platform;
```

- [ ] **Step 3: Inject and use it in MainViewModel**

In `AviatesAirTracker/ViewModels/MainViewModel.cs`:

Add the field alongside the other readonly fields:

```csharp
private readonly IUiDispatcher _ui;
```

Add `IUiDispatcher ui` as a constructor parameter and assign `_ui = ui;` in the body.

Replace all 7 occurrences:
- `Application.Current.Dispatcher.Invoke(` → `_ui.Invoke(`
- `Application.Current.Dispatcher.BeginInvoke(` → `_ui.BeginInvoke(`

Then delete `using System.Windows;` from line 10 and add:

```csharp
using AviatesAirTracker.Platform;
```

Verify with: `grep -n "System.Windows\|Application.Current" AviatesAirTracker/ViewModels/MainViewModel.cs`
Expected: no output.

- [ ] **Step 4: Do the same for MapViewModel and UpdateService**

Apply the identical transformation to `AviatesAirTracker/ViewModels/MapViewModel.cs` (2 sites) and `AviatesAirTracker/Services/UpdateService.cs` (1 site): add the `IUiDispatcher` constructor parameter and field, replace the dispatcher calls, remove `using System.Windows;`.

- [ ] **Step 5: Confirm the ViewModel layer is free of WPF**

Run: `grep -rn "System.Windows" AviatesAirTracker/ViewModels/`
Expected: no output.

`Microsoft.Win32` in `MainViewModel.cs:7` (the `OpenFileDialog` for imports) is **not** removed here — it is replaced by `FileOpenPicker` in Task 9.

- [ ] **Step 6: Build and run**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: Build succeeded, 0 errors.

Run: `.\build.ps1 -Run`
Expected: app launches normally. Trigger an alert-producing action (start a flight in the sim, or wait for the MSFS-not-running toast) and confirm alerts still appear and auto-dismiss — that path goes through `BeginInvoke`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(platform): route ViewModels through IUiDispatcher

WpfUiDispatcher is transitional and is deleted when the app moves to WinUI.
Changing the threading seam separately from the UI framework keeps each step
independently verifiable."
```

---

# Part B — Blazor Server bridge (still on WPF)

## Task 6: Localhost Blazor Server host

The existing Razor pages start serving over SignalR from an in-process Kestrel host, alongside the still-working Blazor Hybrid UI. Nothing switches over yet — this task only proves the host renders.

**Files:**
- Create: `AviatesAirTracker/Hosting/BlazorHostServer.cs`
- Create: `AviatesAirTracker/Hosting/_Host.cshtml`
- Modify: `AviatesAirTracker/App.xaml.cs`

- [ ] **Step 1: Create the host page**

`_Host.cshtml` is `wwwroot/index.html` with three changes: the Blazor script becomes `blazor.server.js`, the root component is rendered server-side, and `Blazor.start()` is removed (the server script self-starts).

Create `AviatesAirTracker/Hosting/_Host.cshtml`:

```cshtml
@page "/"
@namespace AviatesAirTracker.Hosting
@using Microsoft.AspNetCore.Components.Web
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@{
    Layout = null;
}
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>AviatesAir — Virtual Airline Platform</title>
    <base href="/" />
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Syne:wght@600;700;800&family=Plus+Jakarta+Sans:ital,wght@0,400;0,500;0,600;0,700;1,400&family=JetBrains+Mono:wght@400;500;600&family=VT323&display=swap" rel="stylesheet">
    <script>
        (function () {
            try {
                var t = localStorage.getItem('avt_theme') || 'dark';
                var dark = t === 'dark' || (t === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
                if (dark) document.documentElement.setAttribute('data-theme', 'dark');
            } catch (e) {}
        })();
    </script>
    <link rel="stylesheet" href="css/app.css" />
    <link rel="stylesheet" href="AviatesAirTracker.styles.css" />
    <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
</head>
<body>
    <div id="app">
        <component type="typeof(AviatesAirTracker.BlazorApp)" render-mode="Server" />
    </div>
    <div class="boot-screen" id="bootScreen">
        <div class="boot-logo">
            <img src="images/AviatesairlogoWhite.webp" alt="AviatesAir" class="boot-wordmark" />
        </div>
        <div class="boot-progress"><div class="boot-progress-bar"></div></div>
        <span class="boot-status">Initializing operations center…</span>
    </div>
    <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
    <script src="js/interop.js"></script>
    <script src="js/voice.js"></script>
    <script src="js/performance/aircraft-configs.js"></script>
    <script src="js/performance/density.js"></script>
    <script src="js/performance/airspeed.js"></script>
    <script src="js/performance/flaps.js"></script>
    <script src="js/performance/flex.js"></script>
    <script src="js/performance/index.js"></script>
    <script>
      (function () {
        var boot = document.getElementById('bootScreen');
        if (!boot) return;
        var MIN_MS = 1800, shown = Date.now();
        function dismiss() {
          var wait = Math.max(0, MIN_MS - (Date.now() - shown));
          setTimeout(function () {
            boot.classList.add('boot-screen--hide');
            setTimeout(function () { if (boot && boot.parentNode) boot.remove(); }, 450);
          }, wait);
        }
        var app = document.getElementById('app');
        var done = false;
        var obs = new MutationObserver(function () {
          if (!done && app.childElementCount > 0) { done = true; obs.disconnect(); dismiss(); }
        });
        obs.observe(app, { childList: true });
        setTimeout(function () { if (!done) { done = true; obs.disconnect(); dismiss(); } }, 8000);
      })();
    </script>
    <script src="_framework/blazor.server.js"></script>
</body>
</html>
```

**The CSP meta tag is deliberately dropped.** Blazor Server's SignalR client needs `connect-src` to the host origin and inline-script permissions that the current policy does not grant. The page is served from `127.0.0.1` to a WebView2 the app controls; the CSP was protecting a `file://`-like Hybrid context that no longer applies. Re-adding a correct CSP is tracked as follow-up work, not a blocker.

- [ ] **Step 2: Create the host server**

Create `AviatesAirTracker/Hosting/BlazorHostServer.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Serilog;

namespace AviatesAirTracker.Hosting;

/// <summary>
/// Serves the legacy Razor UI as Blazor Server over loopback, so a plain WebView2 can host
/// it while pages are migrated to native XAML one at a time. Deleted in the final migration
/// phase, together with the Razor UI itself.
/// </summary>
/// <remarks>
/// <para>
/// Bound to <c>127.0.0.1</c> — unlike <see cref="Services.Efb.EfbServer"/>, which binds
/// <c>0.0.0.0</c> for LAN tablets, this host renders the pilot's authenticated desktop UI
/// and must never be reachable off-machine. It therefore needs no pairing or token.
/// </para>
/// <para>
/// The app's existing singletons are handed in and re-registered as instances, so the Razor
/// pages and the native shell share one <c>FlightSessionManager</c>, one
/// <c>SimBriefService</c>, one <c>MessagingService</c>. Building a second container here
/// would give the two halves of a half-migrated app divergent state.
/// </para>
/// </remarks>
public sealed class BlazorHostServer : IAsyncDisposable
{
    private readonly IServiceProvider _appServices;
    private WebApplication? _app;

    public BlazorHostServer(IServiceProvider appServices) => _appServices = appServices;

    /// <summary>Root URL of the running host, e.g. <c>http://127.0.0.1:51734</c>.</summary>
    public string? BaseUrl { get; private set; }

    public async Task<string> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Port 0 asks the OS for any free port; the real one is read back after Start.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();

        ShareAppSingletons(builder.Services);

        _app = builder.Build();

        // wwwroot ships next to the executable as plain content. Serving it from disk keeps
        // every asset path in the Razor pages ("css/app.css", "js/interop.js") unchanged.
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(webRoot),
        });

        _app.UseRouting();
        _app.MapBlazorHub();
        _app.MapFallbackToPage("/_Host");

        await _app.StartAsync();

        BaseUrl = ResolveBoundUrl(_app);
        Log.Information("[BlazorHost] Serving the legacy Razor UI at {Url}", BaseUrl);
        return BaseUrl;
    }

    /// <summary>
    /// Re-registers the app's singletons as instances in the web host's container. Every
    /// type the Razor pages <c>@inject</c> must appear here.
    /// </summary>
    /// <remarks>
    /// The authoritative list is <c>App.ConfigureServices</c> — this mirrors it. If a page
    /// throws "Cannot provide a value for property ... no registered service", the missing
    /// type belongs here. Namespaces below are as written in <c>App.xaml.cs</c>'s using
    /// block; correct any the compiler rejects rather than guessing.
    /// </remarks>
    private void ShareAppSingletons(IServiceCollection services)
    {
        void Share<T>() where T : class
        {
            var instance = _appServices.GetService<T>();
            if (instance != null) services.TryAddSingleton(instance);
        }

        // Data
        Share<Core.Data.IFlightRepository>();
        Share<Core.Data.IPilotRepository>();
        Share<Core.Data.ILandingRepository>();
        Share<Core.Data.IFlightDeletionRepository>();
        Share<Core.Data.IMessageRepository>();
        Share<Core.Data.IFriendRepository>();
        // Infrastructure
        Share<Services.SettingsService>();
        Share<Services.AlertService>();
        Share<Platform.IUiDispatcher>();
        // SimConnect
        Share<Core.SimConnect.SimConnectManager>();
        Share<Core.Analytics.FlightPhaseDetector>();
        Share<Core.SimConnect.TelemetryProcessor>();
        // Analytics
        Share<Core.Analytics.LandingAnalyzer>();
        Share<Core.Analytics.ApproachMonitor>();
        Share<Core.Analytics.FuelAnalyzer>();
        Share<Services.RunwayDetector>();
        // Business
        Share<Core.Backend.AviatesBackendClient>();
        Share<Services.AcarsPositionService>();
        Share<Services.HoppiesAcarsClient>();
        Share<Services.HazardZoneService>();
        Share<Services.WeatherAdvisoryService>();
        Share<Services.HoppiesAcarsOrchestrator>();
        Share<Services.OtherPilotsService>();
        Share<Services.PttInputService>();
        Share<Services.DispatchRadioService>();
        Share<Services.MessagingService>();
        Share<Services.RouteTracker>();
        Share<Services.FlightSessionManager>();
        Share<Services.DiscordPresenceService>();
        Share<Services.SimBriefService>();
        Share<Services.TakeoffPerformanceService>();
        Share<Services.ExportService>();
        Share<Services.PilotStatsService>();
        Share<Services.EventsService>();
        Share<Services.RoutesService>();
        Share<Services.BookingService>();
        Share<Services.FleetService>();
        Share<Services.UpdateService>();
        Share<Services.FlightRecoveryService>();
        // EFB
        Share<Services.Efb.EfbStateService>();
        Share<Services.Efb.EfbChecklistService>();
        Share<Services.Efb.EfbManualsService>();
        Share<Services.Efb.EfbAtisService>();
        Share<Services.Efb.EfbServer>();
        // ViewModels
        Share<ViewModels.DashboardViewModel>();
        Share<ViewModels.MapViewModel>();
        Share<ViewModels.LandingAnalysisViewModel>();
        Share<ViewModels.PilotHubViewModel>();
        Share<ViewModels.SettingsViewModel>();
        Share<ViewModels.ReplayViewModel>();
        Share<ViewModels.MainViewModel>();
        Share<ViewModels.FleetViewModel>();
    }

    private static string ResolveBoundUrl(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;

        var url = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel reported no bound address.");

        return url.Replace("[::]", "127.0.0.1").TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        if (_app == null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }
}
```

- [ ] **Step 3: Make wwwroot copy to the output directory**

Because the host serves `wwwroot` from disk with a `PhysicalFileProvider`, the files must exist next to the executable. Add to `AviatesAirTracker/AviatesAirTracker.csproj`:

```xml
<ItemGroup>
  <Content Include="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 4: Start the host at startup**

In `AviatesAirTracker/App.xaml.cs`, in `OnStartup`, immediately before `_serviceProvider.GetRequiredService<MainWindow>().Show();`:

```csharp
// Legacy Razor UI host — serves the not-yet-migrated pages to a WebView2 over loopback.
var blazorHost = new BlazorHostServer(_serviceProvider);
var blazorUrl = await blazorHost.StartAsync();
Log.Information("[Startup] Legacy UI host ready at {Url}", blazorUrl);
```

`OnStartup` is not `async`. Rather than making it so, start the host synchronously:

```csharp
var blazorHost = new BlazorHostServer(_serviceProvider);
var blazorUrl = blazorHost.StartAsync().GetAwaiter().GetResult();
Log.Information("[Startup] Legacy UI host ready at {Url}", blazorUrl);
_blazorHost = blazorHost;
```

Add the field to the `App` class:

```csharp
private BlazorHostServer? _blazorHost;
```

Add the using:

```csharp
using AviatesAirTracker.Hosting;
```

And dispose it in `OnExit`, before `Log.CloseAndFlush()`:

```csharp
_blazorHost?.DisposeAsync().AsTask().GetAwaiter().GetResult();
```

- [ ] **Step 5: Build and run**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: Build succeeded, 0 errors.

Run: `.\build.ps1 -Run`
Expected: the app launches exactly as before (still Blazor Hybrid). The log now contains a line like:

```
[Startup] Legacy UI host ready at http://127.0.0.1:51734
```

- [ ] **Step 6: Verify the host renders the real UI**

With the app running, open that URL from the log in any browser.

Expected: the full AviatesAir UI renders — sidebar, Dashboard, live stats — served over SignalR. Click through to Flights and Fleet and confirm they render. Data shown must match the app window, proving the shared-singleton wiring.

If the page renders unstyled, `wwwroot` did not copy to the output directory — recheck Step 3.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(hosting): serve the legacy Razor UI as Blazor Server on loopback

BlazorWebView is a WPF-only package with no WinUI 3 equivalent, so the
strangler bridge runs the existing pages over SignalR from an in-process
Kestrel host instead. App singletons are shared into the web host so both
halves of a half-migrated app see identical state."
```

---

## Task 7: Point the WPF window at the Blazor Server host

Swaps `BlazorWebView` for a plain `WebView2`, still inside the WPF window. This isolates "does the UI work over SignalR" from "does WinUI work".

**Files:**
- Modify: `AviatesAirTracker/MainWindow.xaml`, `AviatesAirTracker/MainWindow.xaml.cs`

- [ ] **Step 1: Replace the BlazorWebView with a WebView2**

In `AviatesAirTracker/MainWindow.xaml`, delete the `xmlns:blazor` declaration on the `Window` element and replace the entire `<blazor:BlazorWebView>` element with:

```xml
<wv2:WebView2 Grid.Row="1" x:Name="UiHost" />
```

Add this namespace declaration to the `Window` element:

```xml
xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"
```

- [ ] **Step 2: Navigate it to the host on load**

In `AviatesAirTracker/MainWindow.xaml.cs`, replace the body of `OnLoaded` after the HWND lines with:

```csharp
        try
        {
            await UiHost.EnsureCoreWebView2Async();

            // Voice PTT needs getUserMedia() inside the page's WebView2 context. WebView2
            // blocks microphone access by default; without this handler getUserMedia()
            // hangs waiting on a prompt the user has no way to see or answer.
            UiHost.CoreWebView2.PermissionRequested += OnPermissionRequested;

            UiHost.CoreWebView2.Navigate(App.LegacyUiUrl
                ?? throw new InvalidOperationException("Legacy UI host did not start."));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start the legacy UI host view");
        }
```

- [ ] **Step 3: Expose the host URL from App**

In `AviatesAirTracker/App.xaml.cs`, add a static property and set it where the host starts:

```csharp
/// <summary>Root URL of the loopback Blazor Server host, once started.</summary>
public static string? LegacyUiUrl { get; private set; }
```

and after `var blazorUrl = blazorHost.StartAsync().GetAwaiter().GetResult();`:

```csharp
LegacyUiUrl = blazorUrl;
```

- [ ] **Step 4: Build and run**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: Build succeeded, 0 errors.

Run: `.\build.ps1 -Run`
Expected: the app window looks **identical** to before. The UI is now served over SignalR rather than Hybrid — visually indistinguishable.

- [ ] **Step 5: Verification gate — fly a real flight**

This is the gate that de-risks everything after it. With MSFS running:

1. Launch the app; confirm the Dashboard shows live pilot stats
2. Import a SimBrief plan on the ACARS page; confirm the plan populates
3. Start a flight, take off, and confirm the ACARS banner updates with live altitude and speed
4. Open the Map page and confirm the aircraft marker moves
5. Land and confirm the landing-result modal appears with a score
6. Open the Settings page and toggle the theme; confirm it switches and persists
7. Test dispatch radio PTT and confirm the microphone is captured (this exercises the WebView2 permission handler)

Every item must pass. If any fails, **stop and fix before Part C** — the fault is in the Hybrid→Server conversion, and diagnosing it here is far cheaper than after the WinUI flip.

- [ ] **Step 6: Delete the now-unused Hybrid host page and registration**

```bash
git rm AviatesAirTracker/wwwroot/index.html
```

In `AviatesAirTracker/App.xaml.cs`, delete:

```csharp
// Register Blazor WebView services (required for BlazorWebView in WPF)
services.AddWpfBlazorWebView();
```

and:

```csharp
// Make the service provider available to BlazorWebView via dynamic resource
Resources["services"] = _serviceProvider;
```

Leave the `Microsoft.AspNetCore.Components.WebView.Wpf` package reference in place for now — it is removed with the rest of the WPF stack in Task 9.

- [ ] **Step 7: Rebuild, re-run, and commit**

Run: `.\build.ps1 -Run`
Expected: unchanged behaviour.

```bash
git add -A
git commit -m "feat(hosting): host the UI in a plain WebView2 over loopback

Removes the last BlazorWebView dependency from the running app while still
on WPF, so the Hybrid-to-Server conversion is verified independently of the
WinUI 3 platform flip."
```

---

# Part C — Extract the Razor UI into a library (still on WPF)

## Task 8: Move the Razor UI into AviatesAirTracker.LegacyWeb

The WinUI project cannot use `Microsoft.NET.Sdk.Razor`, so the `.razor` files must live in their own library before the flip. Doing it while still on WPF keeps the failure modes separate.

**Files:**
- Create: `AviatesAirTracker.LegacyWeb/AviatesAirTracker.LegacyWeb.csproj`
- Move: `Pages/*.razor`, `Shared/*.razor`, `Shared/*.razor.css`, `BlazorApp.razor`, `_Imports.razor` → `AviatesAirTracker.LegacyWeb/`
- Modify: `AviatesAirTracker/AviatesAirTracker.csproj`, `AviatesAirTracker/Hosting/_Host.cshtml`, `AviatesAirTracker.sln`

### The reference direction, decided

The Razor pages `@inject` types from the app (`MainViewModel`, `SimBriefService`, …), so **the library must reference the app**. That makes an app→library reference a cycle, which MSBuild rejects outright.

So the dependency is one-way — `AviatesAirTracker.LegacyWeb` → `AviatesAirTracker` — and the app reaches the library **at runtime only**, through an interface the app owns and the library implements:

```
AviatesAirTracker            AviatesAirTracker.LegacyWeb
  ILegacyUiHost  <-------------- BlazorHostServer : ILegacyUiHost
  (interface)      implements    _Host.cshtml
                                 Pages/*.razor, Shared/*.razor
       |                                    ^
       └── Assembly.Load at startup ────────┘
```

The library's build copies its output next to the app's executable, so `Assembly.Load` finds it. Because the library references the app, building the library builds the app first — the ordering is automatic.

- [ ] **Step 1: Create the library project**

Create `AviatesAirTracker.LegacyWeb/AviatesAirTracker.LegacyWeb.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <!--
    TRANSITIONAL. Holds the Blazor UI while pages migrate to native WinUI XAML one at a
    time. Deleted wholesale in the final migration phase — nothing new should be added
    here. It exists because the WinUI app project cannot use Microsoft.NET.Sdk.Razor.

    The reference runs library -> app, never the reverse: the Razor pages @inject app
    types, so the app reaches this assembly at runtime via ILegacyUiHost instead.
  -->
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <Platforms>x64</Platforms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AviatesAirTracker\AviatesAirTracker.csproj" />
  </ItemGroup>

  <!-- Put this assembly next to the app executable so Assembly.Load finds it.
       Runs after this project builds, which is after the app it references. -->
  <Target Name="CopyToAppOutput" AfterTargets="Build">
    <ItemGroup>
      <_LegacyWebOutput Include="$(TargetDir)$(AssemblyName).dll" />
      <_LegacyWebOutput Include="$(TargetDir)$(AssemblyName).pdb" />
      <_LegacyWebScopedCss Include="$(TargetDir)**\$(AssemblyName).bundle.scp.css" />
    </ItemGroup>
    <Copy SourceFiles="@(_LegacyWebOutput)"
          DestinationFolder="$(MSBuildThisFileDirectory)..\AviatesAirTracker\bin\$(Configuration)\$(TargetFramework)\"
          SkipUnchangedFiles="true" />
    <Copy SourceFiles="@(_LegacyWebScopedCss)"
          DestinationFolder="$(MSBuildThisFileDirectory)..\AviatesAirTracker\bin\$(Configuration)\$(TargetFramework)\wwwroot\"
          SkipUnchangedFiles="true"
          Condition="'@(_LegacyWebScopedCss)' != ''" />
  </Target>

</Project>
```

The `TargetFramework` must match the app's (`net8.0-windows10.0.19041.0` after Task 9) — a `net8.0` library cannot reference a `net8.0-windows10.0.19041.0` project. It is set to the target value now so this task does not need revisiting after the flip. If Task 8 runs before Task 9, temporarily set it to `net8.0-windows` to match the app's current TFM, and update it in Task 9 Step 1.

- [ ] **Step 2: Define the runtime seam in the app**

Create `AviatesAirTracker/Hosting/ILegacyUiHost.cs`:

```csharp
namespace AviatesAirTracker.Hosting;

/// <summary>
/// The app's handle on the transitional Blazor UI host, which lives in
/// AviatesAirTracker.LegacyWeb. That assembly references this one, so it cannot be
/// referenced back at compile time — the app loads it by name at startup and finds the
/// single implementation of this interface.
/// </summary>
/// <remarks>Deleted along with the RCL in the final migration phase.</remarks>
public interface ILegacyUiHost : IAsyncDisposable
{
    /// <summary>Starts the loopback host and returns its root URL.</summary>
    Task<string> StartAsync(IServiceProvider appServices);
}
```

Create `AviatesAirTracker/Hosting/LegacyUiHostLoader.cs`:

```csharp
using System.Reflection;
using Serilog;

namespace AviatesAirTracker.Hosting;

/// <summary>Locates and instantiates the <see cref="ILegacyUiHost"/> implementation.</summary>
public static class LegacyUiHostLoader
{
    private const string LegacyAssemblyName = "AviatesAirTracker.LegacyWeb";

    public static ILegacyUiHost Load()
    {
        var assembly = Assembly.Load(LegacyAssemblyName);

        var implementation = assembly.GetTypes().FirstOrDefault(
            t => typeof(ILegacyUiHost).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            ?? throw new InvalidOperationException(
                $"{LegacyAssemblyName} contains no ILegacyUiHost implementation.");

        Log.Information("[Startup] Legacy UI host type: {Type}", implementation.FullName);

        return (ILegacyUiHost)Activator.CreateInstance(implementation)!;
    }
}
```

- [ ] **Step 3: Move the Razor files and the host into the library**

```bash
mkdir -p AviatesAirTracker.LegacyWeb/Pages AviatesAirTracker.LegacyWeb/Shared AviatesAirTracker.LegacyWeb/Hosting
git mv AviatesAirTracker/Pages/*.razor AviatesAirTracker.LegacyWeb/Pages/
git mv AviatesAirTracker/Shared/*.razor AviatesAirTracker.LegacyWeb/Shared/
git mv AviatesAirTracker/Shared/*.razor.css AviatesAirTracker.LegacyWeb/Shared/
git mv AviatesAirTracker/BlazorApp.razor AviatesAirTracker.LegacyWeb/
git mv AviatesAirTracker/_Imports.razor AviatesAirTracker.LegacyWeb/
git mv AviatesAirTracker/Hosting/_Host.cshtml AviatesAirTracker.LegacyWeb/Hosting/
git mv AviatesAirTracker/Hosting/BlazorHostServer.cs AviatesAirTracker.LegacyWeb/Hosting/
```

In both moved `Hosting` files, change the namespace from `AviatesAirTracker.Hosting` to `AviatesAirTracker.LegacyWeb.Hosting`.

- [ ] **Step 4: Make BlazorHostServer implement the interface**

In `AviatesAirTracker.LegacyWeb/Hosting/BlazorHostServer.cs`:

Change the class declaration to:

```csharp
public sealed class BlazorHostServer : AviatesAirTracker.Hosting.ILegacyUiHost
```

Replace the constructor-injected provider with a parameter on `StartAsync`, since the loader activates the type with no arguments:

```csharp
    private IServiceProvider _appServices = null!;
    private WebApplication? _app;

    public string? BaseUrl { get; private set; }

    public async Task<string> StartAsync(IServiceProvider appServices)
    {
        _appServices = appServices;
        // ... rest of the existing method body unchanged
```

Delete the old `public BlazorHostServer(IServiceProvider appServices) => _appServices = appServices;` constructor.

The root component reference in `_Host.cshtml` now resolves normally, because the file lives in the same assembly as `BlazorApp`:

```cshtml
<component type="typeof(AviatesAirTracker.LegacyWeb.BlazorApp)" render-mode="Server" />
```

Add to the top of `_Host.cshtml`:

```cshtml
@using AviatesAirTracker.LegacyWeb
```

Update `@namespace` in that file to `AviatesAirTracker.LegacyWeb.Hosting`.

- [ ] **Step 5: Update App.xaml.cs to load through the seam**

Replace the direct construction added in Task 6 with:

```csharp
            var blazorHost = LegacyUiHostLoader.Load();
            LegacyUiUrl = blazorHost.StartAsync(_serviceProvider).GetAwaiter().GetResult();
            Log.Information("[Startup] Legacy UI host ready at {Url}", LegacyUiUrl);
            _blazorHost = blazorHost;
```

Change the field type:

```csharp
private ILegacyUiHost? _blazorHost;
```

Note that the app project keeps `Microsoft.NET.Sdk.Razor` until Task 9 — with the `.razor` files moved out it simply has nothing to compile, which is harmless.

- [ ] **Step 6: Register the library in the solution and fix the build entry point**

Run: `dotnet sln AviatesAirTracker.sln add AviatesAirTracker.LegacyWeb/AviatesAirTracker.LegacyWeb.csproj`

Building the app project alone no longer produces a runnable app — the library's copy target is what places `AviatesAirTracker.LegacyWeb.dll` beside the executable, and that target only runs when the library builds. **Every build from here on targets the solution.**

In `build.ps1`, change every `dotnet build`/`dotnet publish` invocation that names `AviatesAirTracker/AviatesAirTracker.csproj` to name `AviatesAirTracker.sln` instead. Leave the flags unchanged.

- [ ] **Step 7: Point the host page at the scoped-CSS bundle**

Scoped styles from `LandingResultModal.razor.css` (496 lines) and `FlightRecoveryModal.razor.css` (19 lines) now bundle under the library's name instead of the app's. The library's copy target (Step 1) places that bundle at `wwwroot/AviatesAirTracker.LegacyWeb.bundle.scp.css` in the app output, which the existing `PhysicalFileProvider` already serves.

In `_Host.cshtml`, change:

```html
<link rel="stylesheet" href="AviatesAirTracker.styles.css" />
```

to:

```html
<link rel="stylesheet" href="AviatesAirTracker.LegacyWeb.bundle.scp.css" />
```

Serving the file directly from `wwwroot` avoids depending on the `_content/**` static-web-assets pipeline, which is only wired up automatically for Web SDK host projects — and this host is a WinUI app.

**Do not attempt to inline these stylesheets into `app.css`.** Four of their selectors — `.p-label`, `.p-value`, `.ring-fill`, `.ring-track` — already exist in `app.css` with different rules. Removing the scoping would silently restyle the Dashboard and PilotHub.

- [ ] **Step 8: Build, then confirm both assemblies landed**

Run: `dotnet build AviatesAirTracker.sln -c Debug`
Expected: Build succeeded, 0 errors.

Run: `ls AviatesAirTracker/bin/Debug/net8.0-windows/AviatesAirTracker.LegacyWeb.dll AviatesAirTracker/bin/Debug/net8.0-windows/wwwroot/AviatesAirTracker.LegacyWeb.bundle.scp.css`

Expected: **both files exist.**

If `AviatesAirTracker.LegacyWeb.dll` is missing, the copy target's destination path is wrong — correct the `$(TargetFramework)` segment to match the app's actual output folder name and rebuild.

If the `.bundle.scp.css` is missing, find where the Razor SDK actually emitted it:

```bash
find AviatesAirTracker.LegacyWeb -name "*.scp.css" -o -name "*bundle*css"
```

and correct the `_LegacyWebScopedCss` glob in the library csproj to match. Do not proceed until both files are present — Step 10 will otherwise fail in a way that looks like a CSS bug.

- [ ] **Step 9: Run and confirm the UI is unchanged**

Run: `.\build.ps1 -Run`
Expected: the app is visually identical to Task 7 — sidebar, Dashboard, live stats, all pages navigable.

- [ ] **Step 10: Verify the scoped styles specifically**

Landing-result styling is the thing most likely to break in this task, and it only appears after a landing. Use the built-in debug hook:

Run: `AviatesAirTracker\bin\Debug\net8.0-windows\AviatesAirTracker.exe --debug-landing`

Expected: 12 seconds after launch, the landing-result modal appears **fully styled** — grade badge, score ring, factor bars, flags. An unstyled modal means the bundle path from Step 7 is not resolving.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "refactor(ui): move the Razor UI into AviatesAirTracker.LegacyWeb

The WinUI app project cannot use Microsoft.NET.Sdk.Razor, so the Blazor UI
moves to its own library ahead of the platform flip. The library is
transitional and is deleted wholesale in the final phase."
```

---

# Part D — Flip to WinUI 3

> Everything before this point left the app fully working. This part changes the UI framework, and the app is broken between Task 9 and Task 13. Do not interrupt the sequence.

## Task 9: Convert the project to WinUI 3

**Files:**
- Modify: `AviatesAirTracker/AviatesAirTracker.csproj`, `App.xaml`, `App.xaml.cs`
- Create: `AviatesAirTracker/Platform/DispatcherQueueUiDispatcher.cs`
- Delete: `AviatesAirTracker/Platform/WpfUiDispatcher.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`

- [ ] **Step 1: Rewrite the project properties**

In `AviatesAirTracker/AviatesAirTracker.csproj`, change the SDK attribute on the root element from `Microsoft.NET.Sdk.Razor` to `Microsoft.NET.Sdk`, and replace the `PropertyGroup` head with:

```xml
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>AviatesAirTracker</RootNamespace>
    <AssemblyName>AviatesAirTracker</AssemblyName>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <Version>1.7.1</Version>
    <AssemblyVersion>1.7.1.0</AssemblyVersion>
    <FileVersion>1.7.1.0</FileVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Platforms>x64</Platforms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <DefineConstants>$(DefineConstants);NO_SIMCONNECT</DefineConstants>
  </PropertyGroup>
```

`PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract` and `UseWPF` are all removed — the Windows App SDK does not support single-file publish.

- [ ] **Step 2: Swap the packages**

Remove:

```xml
<PackageReference Include="Microsoft.AspNetCore.Components.WebView.Wpf" Version="8.0.100" />
<PackageReference Include="Mapsui.Wpf" Version="4.1.7" />
```

Add, with the pin comment:

```xml
    <!-- WinUI 3 / Windows App SDK.
         Versions are pinned deliberately — 1.6/1.7 cannot build without Visual Studio
         (MSB4062 in MrtCore.PriGen.targets). -->
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.250907003" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.4654" />
```

Keep `Mapsui.Tiling` and `Mapsui.Nts`; `Mapsui.UI.WinUI` is added in Phase 4, not now.

- [ ] **Step 3: Port the Application class**

In `AviatesAirTracker/App.xaml`, change the root element from `<Application x:Class="AviatesAirTracker.App" xmlns=...winfx/2006/xaml/presentation ...>` to the WinUI form:

```xml
<Application
    x:Class="AviatesAirTracker.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

In `AviatesAirTracker/App.xaml.cs`:

- Replace `using System.Windows;` with `using Microsoft.UI.Xaml;`
- Change `protected override void OnStartup(StartupEventArgs e)` to `protected override void OnLaunched(LaunchActivatedEventArgs args)`
- Delete the `base.OnStartup(e);` call — WinUI's `OnLaunched` has no base implementation to chain
- Replace the `DispatcherUnhandledException` handler with WinUI's:

```csharp
        UnhandledException += (_, ex) =>
        {
            DumpCrash("UnhandledException", ex.Exception);
            ex.Handled = false;
        };
```

- Replace `_serviceProvider.GetRequiredService<MainWindow>().Show();` with:

```csharp
        var shell = _serviceProvider.GetRequiredService<ShellWindow>();
        shell.Activate();
```

- Replace the `Dispatcher.InvokeAsync` call in the `#if DEBUG --debug-landing` block with:

```csharp
                    _serviceProvider.GetRequiredService<Platform.IUiDispatcher>().BeginInvoke(() =>
```

- `OnExit(ExitEventArgs e)` has no WinUI equivalent. Rename it to a public **`Shutdown()`** method on `App`, keeping the body as-is for now; Task 12 finishes it and `ShellWindow.OnClosed` calls it (wired in Task 10). The name must be exactly `Shutdown` — Tasks 10 and 12 both depend on it.

**`UpdateService` also calls into WPF.** `Services/UpdateService.cs:206` runs
`_ui.Invoke(() => Application.Current.Shutdown())` to restart the app after downloading an
update, and the file still carries `using System.Windows;` for it. Task 5 deliberately left
this alone rather than forcing the using removal. `Application.Current.Shutdown()` does not
exist in WinUI 3 — replace it with `Microsoft.UI.Xaml.Application.Current.Exit()` and swap
the using to `Microsoft.UI.Xaml`.

Verify the update-install path still works end to end after the change: this line is what
relaunches the app after an update, so a silent failure here means users never get updates
and nothing in the UI reports it.

- [ ] **Step 4: Add the WinUI dispatcher and delete the WPF one**

Create `AviatesAirTracker/Platform/DispatcherQueueUiDispatcher.cs`:

```csharp
using System.Runtime.ExceptionServices;
using Microsoft.UI.Dispatching;

namespace AviatesAirTracker.Platform;

/// <summary>
/// <see cref="IUiDispatcher"/> backed by WinUI's <see cref="DispatcherQueue"/>.
/// </summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueUiDispatcher(DispatcherQueue queue) => _queue = queue;

    public void Invoke(Action action)
    {
        // Already on the UI thread — run inline. Enqueueing and waiting here would
        // deadlock, because the wait would block the very thread that must run the work.
        if (_queue.HasThreadAccess)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? captured = null;

        if (!_queue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        }))
        {
            // The queue is shutting down (app closing). Dropping the work is correct —
            // throwing here would turn every in-flight telemetry callback into a crash
            // during exit.
            return;
        }

        done.Wait();
        if (captured != null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }

    public void BeginInvoke(Action action) => _queue.TryEnqueue(() => action());
}
```

Delete the transitional one:

```bash
git rm AviatesAirTracker/Platform/WpfUiDispatcher.cs
```

In `App.xaml.cs`, change the registration to build it from the UI thread's queue:

```csharp
services.AddSingleton<IUiDispatcher>(_ =>
    new DispatcherQueueUiDispatcher(DispatcherQueue.GetForCurrentThread()));
```

This registration must be resolved on the UI thread. Because `OnLaunched` runs there and the container is built there, that holds.

#### Three behaviour differences to handle deliberately, not by accident

Surfaced by the Task 5 review, which moved these call sites onto the seam:

1. **`BeginInvoke` after shutdown behaves differently.** WPF's `Dispatcher.BeginInvoke`
   *throws* once the dispatcher is shut down; `DispatcherQueue.TryEnqueue` instead returns
   `false` silently. The one `BeginInvoke` caller — `MainViewModel.cs:232`, the alert
   auto-dismiss fired from a `Task.Delay(...).ContinueWith(...)` continuation — currently
   relies on that exception being swallowed as an unobserved task exception. The
   `DispatcherQueue` version is the better behaviour, but confirm alerts still auto-dismiss
   after the swap rather than assuming it.

2. **`ImmediateUiDispatcher`'s doc comment is currently aspirational.** It claims to be a
   fallback "before the UI thread's `DispatcherQueue` exists (e.g. during startup)", but
   this task registers `DispatcherQueueUiDispatcher` unconditionally and nothing uses the
   immediate one outside tests. Either genuinely use it for the startup window, or amend
   the comment so it stops describing a design that does not exist.

3. **The 20Hz path blocks.** `MainViewModel.cs:208` (`OnTelemetryUpdated`) calls the
   synchronous `Invoke` from the SimConnect thread twenty times a second. That is unchanged
   pre-existing behaviour, not something this migration introduces — but
   `DispatcherQueueUiDispatcher.Invoke` blocks on a `ManualResetEventSlim` when off the UI
   thread, so the characteristic carries over. Do not "fix" it here; changing it is a
   behaviour change on the hottest path in the app. Note it, and revisit only if the live
   sim shows a problem.

- [ ] **Step 5: Convert the remaining WPF timers**

In `AviatesAirTracker/Core/SimConnect/SimConnectManager.cs`:
- Replace `using System.Windows.Threading;` with `using Microsoft.UI.Dispatching;`
- Change both `DispatcherTimer` field declarations (lines 73–74) to `DispatcherQueueTimer?`
- Replace construction at line 653 with:

```csharp
        _pollTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(50);
        _pollTimer.Tick += (_, _) => PollTelemetry();
        _pollTimer.Start();
```

- Apply the same shape to `InitReconnectTimer` at line 784.

In `AviatesAirTracker/ViewModels/ReplayViewModel.cs`, apply the same conversion. **Preserve the `CRIT-01` comment at line 56** — `DispatcherQueueTimer` keeps the UI-thread guarantee that the original fix depends on, and the comment should say so:

```csharp
            // CRIT-01: DispatcherQueueTimer fires on the UI thread — safe for
            // ObservableProperty writes. Was System.Timers.Timer (ThreadPool), which
            // caused cross-thread crashes.
```

- [ ] **Step 6: Replace the WPF file dialog**

`MainViewModel.cs:7` uses `Microsoft.Win32.OpenFileDialog`. Create `AviatesAirTracker/Platform/FilePickerService.cs`:

```csharp
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AviatesAirTracker.Platform;

/// <summary>
/// WinUI file picking. Unlike WPF's dialogs, <see cref="FileOpenPicker"/> must be
/// associated with a window handle in an unpackaged app or it throws at show time.
/// </summary>
public sealed class FilePickerService
{
    private nint _hwnd;

    public void SetWindowHandle(nint hwnd) => _hwnd = hwnd;

    public async Task<string?> PickSingleFileAsync(params string[] fileTypes)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);

        foreach (var type in fileTypes) picker.FileTypeFilter.Add(type);
        if (fileTypes.Length == 0) picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
```

Register it in `ConfigureServices`:

```csharp
services.AddSingleton<FilePickerService>();
```

Update `MainViewModel` to take `FilePickerService` and replace the `OpenFileDialog` usage with `await _filePicker.PickSingleFileAsync(...)`. Remove `using Microsoft.Win32;`.

- [ ] **Step 7: Delete the WPF window**

```bash
git rm AviatesAirTracker/MainWindow.xaml AviatesAirTracker/MainWindow.xaml.cs
```

Remove `services.AddSingleton<MainWindow>();` from `ConfigureServices`.

- [ ] **Step 8: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: **errors about `ShellWindow` not existing** — that is Task 10. All *other* errors must be resolved before moving on. Do not run the app yet; there is no window.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "build: convert the project from WPF to WinUI 3

App does not run until ShellWindow lands in the next commit."
```

---

## Task 10: The WinUI shell window

Custom title bar, Mica, `NavigationView` styled to the current sidebar, and the `WebView2` content host.

**Files:**
- Create: `AviatesAirTracker/Navigation/NavDestinations.cs`
- Create: `AviatesAirTracker/ShellWindow.xaml`, `AviatesAirTracker/ShellWindow.xaml.cs`
- Modify: `AviatesAirTracker/App.xaml.cs`

- [ ] **Step 1: Define the destinations**

The 12 sidebar entries, in the order and wording `MainLayout.razor` uses today.

Create `AviatesAirTracker/Navigation/NavDestinations.cs`:

```csharp
namespace AviatesAirTracker.Navigation;

/// <summary>One entry in the shell's navigation rail.</summary>
/// <param name="Tag">Stable identifier, also the NavigationViewItem tag.</param>
/// <param name="Label">Sidebar text, matching the current Blazor sidebar exactly.</param>
/// <param name="Route">Route on the legacy Blazor host.</param>
/// <param name="Group">Sidebar section heading this entry sits under, or null for none.</param>
public sealed record NavDestination(string Tag, string Label, string Route, string? Group);

/// <summary>
/// Single source of truth for shell navigation. Order, wording and grouping are copied
/// verbatim from <c>Shared/MainLayout.razor</c> lines 68–207, so the native rail is
/// indistinguishable from the sidebar it replaces. As pages migrate to XAML, only the
/// resolution in <c>ShellWindow.Navigate</c> changes — this list does not.
/// </summary>
public static class NavDestinations
{
    public static readonly IReadOnlyList<NavDestination> All =
    [
        new("dashboard", "Dashboard", "/",          "Overview"),
        new("flights",   "Flights",   "/flights",   "Overview"),
        new("routes",    "Routes",    "/routes",    "Overview"),
        new("fleet",     "Fleet",     "/fleet",     "Overview"),
        new("pilot-hub", "Pilot Hub", "/pilot-hub", "Pilot"),
        new("acars",     "ACARS",     "/acars",     "Pilot"),
        new("map",       "Map",       "/map",       "Pilot"),
        new("messages",  "Messages",  "/messages",  "Pilot"),
        new("news",      "News",      "/news",      "Community"),
        new("events",    "Events",    "/events",    "Community"),
        new("support",   "Support",   "/support",   "Community"),
        // Settings sits below the connection-status block in the sidebar footer,
        // outside every section heading.
        new("settings",  "Settings",  "/settings",  null),
    ];
}
```

**Badges are deliberately not modelled yet.** The sidebar carries a dot on Flights, "Live" pills on ACARS and Map, an unread count on Messages, and a "3" on News. Those are live-data concerns that need the theme system, so Phase 2 adds them via `NavigationViewItem.InfoBadge`. Do not stub them here.

- [ ] **Step 2: Create the shell window**

Create `AviatesAirTracker/ShellWindow.xaml`:

```xml
<Window
    x:Class="AviatesAirTracker.ShellWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:Microsoft.UI.Xaml.Controls"
    xmlns:wv2="using:Microsoft.UI.Xaml.Controls">

    <Grid x:Name="RootGrid">
        <Grid.RowDefinitions>
            <RowDefinition Height="36" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <!-- Drag region for the extended title bar. Caption buttons are drawn by the
             system; this Border only supplies the draggable area and the app name. -->
        <Border x:Name="AppTitleBar" Grid.Row="0" Background="Transparent">
            <TextBlock
                Text="Aviates Air Flight Tracker"
                VerticalAlignment="Center"
                Margin="16,0,0,0"
                FontSize="12"
                Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
        </Border>

        <controls:NavigationView
            x:Name="Nav"
            Grid.Row="1"
            PaneDisplayMode="Left"
            IsBackButtonVisible="Visible"
            IsSettingsVisible="False"
            SelectionChanged="OnNavSelectionChanged"
            BackRequested="OnBackRequested">
            <controls:WebView2 x:Name="UiHost" />
        </controls:NavigationView>
    </Grid>
</Window>
```

The rail is deliberately unstyled here — Phase 2 applies the Night Flight theme resources. This task only proves navigation and hosting work.

- [ ] **Step 3: Create the code-behind**

Create `AviatesAirTracker/ShellWindow.xaml.cs`:

```csharp
using AviatesAirTracker.Navigation;
using AviatesAirTracker.Platform;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Serilog;
using Windows.Graphics;
using WinRT.Interop;

namespace AviatesAirTracker;

/// <summary>
/// The application window. Owns the SimConnect message pump, the navigation rail and the
/// WebView2 that hosts not-yet-migrated pages.
/// </summary>
public sealed partial class ShellWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly FilePickerService _filePicker;
    private readonly WindowSubclass _subclass;

    public ShellWindow(MainViewModel viewModel, FilePickerService filePicker)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _filePicker = filePicker;

        // The second argument is not optional in practice. OnSubclassProc is a reverse
        // P/Invoke from comctl32: an exception escaping the callback unwinds across the
        // native boundary and fail-fasts the process. WindowSubclass swallows it to keep
        // the app alive, so if nothing is passed here the fault vanishes silently — and
        // this callback carries 20Hz SimConnect telemetry.
        _subclass = new WindowSubclass(
            (hwnd, msg, w, l) => _viewModel.OnWindowMessage(hwnd, msg, w, l),
            ex => Log.Error(ex, "[Shell] SimConnect message callback threw"));

        Title = "Aviates Air Flight Tracker";

        ConfigureTitleBar();
        ConfigureBackdrop();
        BuildNavItems();

        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    private bool _initialised;

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialised) return;
        _initialised = true;

        var hwnd = WindowNative.GetWindowHandle(this);

        // SimConnect posts its notifications as window message 0x0402 to this HWND.
        _viewModel.SetWindowHandle(hwnd);
        _filePicker.SetWindowHandle(hwnd);
        _subclass.Attach(hwnd);

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new SizeInt32(1440, 900));

        try
        {
            await UiHost.EnsureCoreWebView2Async();

            // Voice PTT needs getUserMedia() inside the page's WebView2 context. WebView2
            // blocks microphone access by default; without this handler getUserMedia()
            // hangs on a prompt the user has no way to answer.
            UiHost.CoreWebView2.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Microphone)
                    e.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
            };

            Navigate(NavDestinations.All[0]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Shell] Failed to initialise the legacy UI host");
        }
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    private void ConfigureBackdrop()
    {
        // Mica Alt gives the window real depth behind the navigation rail. Falls back
        // silently on hardware that cannot render it.
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
    }

    private void BuildNavItems()
    {
        string? currentGroup = null;

        foreach (var destination in NavDestinations.All)
        {
            // Emit a section heading whenever the group changes, mirroring the
            // "Overview" / "Pilot" / "Community" labels in the Blazor sidebar.
            if (destination.Group != null && destination.Group != currentGroup)
            {
                Nav.MenuItems.Add(new NavigationViewItemHeader { Content = destination.Group });
                currentGroup = destination.Group;
            }

            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = destination.Label,
                Tag = destination.Tag,
            });
        }

        // Index 0 is the "Overview" header, so the first selectable item is index 1.
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        var destination = NavDestinations.All.FirstOrDefault(d => d.Tag == tag);
        if (destination != null) Navigate(destination);
    }

    /// <summary>
    /// Sends the rail's selection to whichever host owns that destination. Today every
    /// destination lives on the legacy Blazor host; as pages migrate this grows a branch
    /// that shows a native Page instead.
    /// </summary>
    private void Navigate(NavDestination destination)
    {
        if (UiHost.CoreWebView2 == null) return;

        var baseUrl = App.LegacyUiUrl;
        if (baseUrl == null)
        {
            Log.Error("[Shell] Legacy UI host is not running; cannot navigate");
            return;
        }

        UiHost.CoreWebView2.Navigate(baseUrl + destination.Route);
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (UiHost.CoreWebView2?.CanGoBack == true) UiHost.CoreWebView2.GoBack();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _subclass.Detach();
        (Application.Current as App)?.Shutdown();
    }
}
```

- [ ] **Step 4: Hide the Blazor sidebar and header**

The native rail now supplies navigation, so the Blazor sidebar would be a duplicate. In `AviatesAirTracker.LegacyWeb/Shared/MainLayout.razor`, wrap the sidebar and header markup in a check against a query flag, and in `ShellWindow.Navigate` append it:

```csharp
        UiHost.CoreWebView2.Navigate($"{baseUrl}{destination.Route}?shell=native");
```

In `MainLayout.razor`, read the flag in `OnInitialized` via `NavigationManager.Uri` and set a `_nativeShell` field; render the sidebar and header only when `!_nativeShell`. Keep the existing markup intact — this is a visibility condition, not a deletion, because Phase 2 needs the sidebar markup as the styling reference.

- [ ] **Step 5: Register the shell**

In `AviatesAirTracker/App.xaml.cs`, in `ConfigureServices`, replace the deleted `MainWindow` registration with:

```csharp
services.AddSingleton<ShellWindow>();
```

- [ ] **Step 6: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Run**

Run: `.\build.ps1 -Run`
Expected: a WinUI window opens with a Mica backdrop, a native title bar with system caption buttons, a left navigation rail listing all 12 destinations, and the Dashboard rendering in the content area **without** the Blazor sidebar. Clicking each rail entry navigates to the corresponding page.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(shell): WinUI 3 window with Mica, NavigationView and WebView2 host"
```

---

## Task 11: Login gate

**Files:**
- Modify: `AviatesAirTracker/ShellWindow.xaml.cs`, `AviatesAirTracker/App.xaml.cs`

- [ ] **Step 1: Understand the existing gate**

The condition already exists and must not be reinvented. `MainLayout.razor:306` redirects when the ACARS key is missing:

```csharp
if (string.IsNullOrWhiteSpace(SettingsService.Settings.AcarsKey))
{
    Nav.NavigateTo("/login");
}
```

and `Login.razor:113` performs the mirror check, skipping the login form when a key is already stored. `Login.razor:153-162` validates the key with `BackendClient.ValidateAcarsKeyAsync(key)` and writes it back to settings on success.

The native gate below uses the **same** predicate. The Blazor redirect stays in place as well — it is the fallback if the shell ever navigates to a page directly.

- [ ] **Step 2: Start on the login route when not logged in**

In `ShellWindow.OnFirstActivated`, replace `Navigate(NavDestinations.All[0]);` with:

```csharp
            if (IsLoggedIn())
            {
                Navigate(NavDestinations.All[0]);
            }
            else
            {
                // Login owns the whole window: no rail, no back button, until it completes.
                Nav.IsPaneVisible = false;
                Nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
                UiHost.CoreWebView2.Navigate($"{App.LegacyUiUrl}/login?shell=native");
            }
```

Add the predicate — the same condition `MainLayout.razor:306` uses, inverted:

```csharp
    /// <summary>
    /// Mirrors the guard in MainLayout.razor:306. A stored ACARS key is what "signed in"
    /// means in this app; Login.razor validates it against the backend before storing it.
    /// </summary>
    private bool IsLoggedIn() =>
        !string.IsNullOrWhiteSpace(_settings.Settings.AcarsKey);
```

Add `SettingsService settings` to the `ShellWindow` constructor and store it as `_settings`. The DI container already registers it as a singleton, so no registration change is needed.

- [ ] **Step 3: Reveal the rail when login completes**

Watch for the WebView leaving the login route:

```csharp
        UiHost.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            var url = UiHost.CoreWebView2.Source ?? string.Empty;
            var onLogin = url.Contains("/login", StringComparison.OrdinalIgnoreCase);

            Nav.IsPaneVisible = !onLogin;
            Nav.IsBackButtonVisible = onLogin
                ? NavigationViewBackButtonVisible.Collapsed
                : NavigationViewBackButtonVisible.Visible;
        };
```

- [ ] **Step 4: Run and verify both paths**

Run: `.\build.ps1 -Run`

With a saved ACARS key: expected to open straight to the Dashboard with the rail visible.

Then clear the key (Settings → remove the ACARS key → restart) and run again: expected to open to the login page **full-window with no rail**. Complete a login and confirm the rail appears and the Dashboard loads.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(shell): login gate hides the navigation rail until signed in"
```

---

## Task 12: Restore correct shutdown

WinUI has no `OnExit`. Without this, the EFB server, SimConnect and Discord presence leak on close.

**Files:**
- Modify: `AviatesAirTracker/App.xaml.cs`

- [ ] **Step 1: Convert OnExit into an explicit Shutdown**

In `AviatesAirTracker/App.xaml.cs`, replace the `OnExit` override with:

```csharp
    private bool _shutdownRan;

    /// <summary>
    /// Ordered teardown. WinUI has no <c>OnExit</c>, so <see cref="ShellWindow"/> calls
    /// this from its <c>Closed</c> handler. Guarded because a window close and a process
    /// exit can both reach it.
    /// </summary>
    public void Shutdown()
    {
        if (_shutdownRan) return;
        _shutdownRan = true;

        _blazorHost?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _serviceProvider.GetService<EfbServer>()?.Dispose();
        _serviceProvider.GetService<SimConnectManager>()?.Disconnect();
        _serviceProvider.GetService<DiscordPresenceService>()?.Dispose();
        _serviceProvider.GetService<AcarsPositionService>()?.Dispose();
        _serviceProvider.GetService<HoppiesAcarsOrchestrator>()?.Dispose();
        _serviceProvider.GetService<PttInputService>()?.Dispose();
        _serviceProvider.GetService<DispatchRadioService>()?.Dispose();
        _serviceProvider.GetService<UpdateService>()?.Dispose();
        _serviceProvider.GetService<TakeoffPerformanceService>()?.Dispose();
        Log.CloseAndFlush();
        _serviceProvider.Dispose();
    }
```

`ShellWindow.OnClosed` already calls it (Task 10, Step 3).

- [ ] **Step 2: Verify teardown actually runs**

Run: `.\build.ps1 -Run`, then close the window with the title-bar X.

Expected: the process disappears from Task Manager within ~2 seconds. Open the newest log under `%AppData%\AviatesAirTracker\logs\` and confirm it ends cleanly with no unhandled exception.

Run a second time and confirm the EFB port is free — if the previous process leaked, `EfbServer` would fail to bind and log a port-in-use error.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "fix(shell): explicit ordered shutdown, replacing WPF OnExit"
```

---

## Task 13: Full verification against a live sim

No code. This is the gate that decides whether Part D succeeded.

- [ ] **Step 1: Confirm the automated suite still passes**

Run: `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
Expected: PASS — 8 passed, 0 failed.

- [ ] **Step 2: Confirm a clean release build**

Run: `.\build.ps1 -Release`
Expected: succeeds, producing a **folder** in `dist/` rather than a single EXE. Confirm `dist/AviatesAirTracker.exe` exists alongside its dependencies.

The installer script is not yet updated for folder layout — that is Phase 7. Do not fix it here.

- [ ] **Step 3: Fly a complete flight**

With MSFS running, work through every item. Each must pass:

1. App launches to the Dashboard; live pilot stats populate
2. Serilog writes to `%AppData%\AviatesAirTracker\logs\`
3. SimConnect connects — the header shows the connected state, not "MSFS Not Running"
4. **Telemetry flows for at least 10 continuous minutes with no crash.** This is the window in which a GC-collected subclass delegate would surface.
5. Import a SimBrief plan on ACARS; the plan populates
6. Take off; the ACARS banner tracks altitude and speed live
7. Open the Map; the aircraft marker moves and the planned route draws
8. Dispatch radio PTT captures the microphone and transmits
9. Land; the landing-result modal appears, fully styled, with a score
10. The flight appears in the Logbook with correct times and fuel
11. The EFB server starts and a phone or tablet on the LAN can pair and see live telemetry
12. Close the window; the process exits cleanly

- [ ] **Step 4: Record the results**

Append a short results section to this plan file listing each item and its outcome. If anything failed, record the symptom and the log excerpt — do not mark the phase complete.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/plans/2026-08-13-winui3-foundation-and-bridge.md
git commit -m "docs(plan): record foundation and bridge verification results"
```

---

## Definition of done

- `dotnet test` passes, including the real-HWND GC regression guard
- The app runs as a WinUI 3 process with Mica, a native title bar and a native navigation rail
- All 12 destinations render the existing Razor UI inside the shell, visually unchanged
- A complete flight records end to end, including landing analysis and the EFB
- No WPF assembly remains referenced by `AviatesAirTracker.csproj`
- The tree is committed and the app is usable

## What this plan deliberately does not do

- **No visual theming.** The rail and title bar are unstyled WinUI defaults. Phase 2 applies the Night Flight tokens; judging the look before then is judging a scaffold.
- **No native pages.** Every destination still renders Razor. Phase 3 starts converting them.
- **No map, voice, or performance work.** Those are Phases 4–6.
- **No installer or CI changes.** `dist/` becomes a folder here, but `build.ps1 -Release` and the `.iss` are only fully reworked in Phase 7.
