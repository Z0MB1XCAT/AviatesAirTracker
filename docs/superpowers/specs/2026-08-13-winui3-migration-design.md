# Blazor Hybrid → WinUI 3 Migration (v2)

**Date:** 2026-08-13
**Status:** Approved, ready for implementation planning
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Supersedes:** `2026-08-12-winui3-migration-design.md` (branch `claude/blazor-winui3-migration-5ab70c`)

## Why this spec replaces the previous one

The 2026-08-12 spec stated:

> **Explicit non-goal:** preserving the current visual design. The app will look like a
> Windows 11 application, not like the current `app.css` "High Altitude Command" theme.

That was chosen in error. **This spec inverts that decision.** The current visual design is
preserved faithfully; WinUI 3 is adopted for native *mechanics*, not a native *appearance*.

The previous branch completed its Phase 0 (foundation) and Phase 1 (shell). The foundation
is visual-design-agnostic and is reused. The shell and its 12 placeholder pages were built
to the wrong brief and are discarded.

## Goal

Replace the Blazor Hybrid UI (Razor pages in a WPF-hosted `BlazorWebView`) with a native
WinUI 3 application that is **visually indistinguishable from today's app**, while fixing
the three things that currently make it feel out of place on Windows.

### Drivers, in priority order

1. **Stop feeling like a webpage in a window.** Native window chrome, native input and
   focus behaviour, and real OS integration.
2. **Native platform mechanics** — real virtualisation, real dialogs, real keyboard
   navigation, system theme awareness.
3. **Lower RAM and CPU** — a welcome consequence, but explicitly *not* a current pain
   point and never a reason to compromise fidelity.

### Fidelity contract

The binding rule for every decision in implementation:

> A pilot who used the app yesterday must not perceive a redesign. Palette, typography,
> spacing, card treatment, page structure, and information architecture are preserved.
> Where WinUI offers a better mechanism at **zero visual cost**, it wins. Where a native
> mechanism would change the appearance, the appearance wins.

Deliberate, approved exceptions to "zero visual cost" are enumerated in
[Approved visual changes](#approved-visual-changes). There are three. Anything not on that
list is a bug.

## Scope

### Ported to native WinUI 3 XAML

12 sidebar destinations plus Login — ~7,340 lines of Razor:

| Page | Razor lines | Route |
|---|---:|---|
| `Pages/Acars.razor` | 1,483 | `/acars` |
| `Pages/Settings.razor` | 1,058 | `/settings` |
| `Pages/Routes.razor` | 729 | `/routes` |
| `Pages/Flights.razor` | 618 | `/flights` |
| `Pages/Fleet.razor` | 574 | `/fleet` |
| `Pages/MapPage.razor` | 570 | `/map` |
| `Pages/Messages.razor` | 515 | `/messages` |
| `Pages/PilotHub.razor` | 509 | `/pilot-hub` |
| `Pages/Dashboard.razor` | 455 | `/` |
| `Pages/Events.razor` | 389 | `/events` |
| `Pages/Login.razor` | 199 | (login gate) |
| `Pages/News.razor` | 144 | `/news` |
| `Pages/Support.razor` | 93 | `/support` |

6 shared components — ~1,950 lines:

| Component | Lines | Becomes |
|---|---:|---|
| `Shared/TakeoffPerformanceModal.razor` | 682 | `ContentDialog` |
| `Shared/MainLayout.razor` | 472 | `NavigationView` shell in the WinUI window |
| `Shared/LandingResultModal.razor` | 366 | `ContentDialog` |
| `Shared/AcarsFmc.razor` | 355 | `UserControl` |
| `Shared/FlightRecoveryModal.razor` | 39 | `ContentDialog` |
| `Shared/LoginLayout.razor` | 32 | Login `Frame` state |

Plus `wwwroot/css/app.css` (3,459 lines) → XAML resource dictionaries, and 2,245 lines of
JavaScript → C# (see [Subsystem rewrites](#subsystem-rewrites)).

The 5 redirect-stub pages (`LiveFlight`, `Statistics`, `Replay`, `TelemetryPage`,
`LandingAnalysis`) contain only `Nav.NavigateTo(...)`. They are deleted; `/live-flight`
and `/telemetry` currently redirect to `/acars`, and `/statistics` to `/pilot-hub`, which
`NavigationView` reaches directly.

### Deleted, not ported

Verified by grep to have zero live consumers:

- `Views/*.xaml` (8 files), `Views/ViewCodeBehinds.cs`, `Views/ViewPartials.cs` — nothing
  routes to them; they predate ACARS, Fleet, Routes, Messages, Events and PilotHub.
- `Controls/AviationControls.cs` (302 lines) — zero references.
- `Converters/ValueConverters.cs`, `Converters/ExtraConverters.cs` — referenced only by the
  dead `Views/*.xaml`. Converters genuinely needed are re-created against
  `Microsoft.UI.Xaml.Data.IValueConverter`.
- `Resources/Styles/*.xaml` — WPF dictionaries consumed only by the dead views.
- `wwwroot/` in its entirety, `BlazorApp.razor`, `_Imports.razor` (in the final phase).

**Charts are deleted, not migrated.** `OxyPlot` is referenced only by
`ViewModels/LiveFlightViewModel.cs`, `ViewModels/StatisticsViewModel.cs`,
`ViewModels/TelemetryViewModel.cs` and `Core/Analytics/FlightProfileCharts.cs` (388 lines).
Those three ViewModels' pages are redirect stubs — no `PlotView` is rendered anywhere in
the live UI. Their only other references are DI registration in `App.xaml.cs:334,337,341`,
property exposure on `MainViewModel` (lines 26, 29, 32, 69–72), and the dead
`Views/ViewCodeBehinds.cs:57`. All are removed, along with the `OxyPlot.Wpf` and
`OxyPlot.Blazor` packages. Dashboard's "Flight Activity" chart is hardcoded `<div>` bars
with inline pixel heights and ports as XAML `Rectangle`s.

**No charting library is added.** The previous spec's LiveCharts2 phase was migrating
charts with no consumer.

`wwwroot/images/flags/*.svg`, the aircraft photography, the AviatesAir wordmark and
`wwwroot/audio/SelCal.wav` are **retained**, relocated to `Assets/` as WinUI content.

### Untouched

- `Core/` except `Core/Analytics/FlightProfileCharts.cs` (deleted) — SimConnect, analytics,
  data, backend client, EFB models.
- `Models/`, and all of `Services/` except the threading changes below.
- **The entire EFB module.** `Services/Efb/EfbServer.cs` is a Kestrel server that serves an
  embedded web frontend to tablets over the LAN. It is not application UI. `EfbWeb/` keeps
  its HTML/CSS/JS and `<EmbeddedResource>` build action, and the
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` is retained — the migration
  in fact depends on it (see below).

ViewModels change only where they reference WPF threading types.

## Architecture

### The strangler bridge

There is **no `BlazorWebView` package for WinUI 3.** Only `.Wpf` and `.WindowsForms`
variants exist; `Microsoft.AspNetCore.Components.WebView.Maui` requires the MAUI app model.
The bridge therefore works differently — and the codebase already proves the pattern, since
`EfbServer.cs:131` runs `WebApplication.CreateBuilder()` in-process today.

A second, **localhost-only** Kestrel host serves the existing Razor pages as **Blazor
Server**, displayed in a plain `WebView2` inside the shell's content `Frame`:

```
WinUI 3 Window  (Mica Alt, ExtendsContentIntoTitleBar)
 └─ NavigationView              ← replaces Shared/MainLayout.razor
     └─ Frame
         ├─ native XAML Page    ← ported destinations
         └─ WebView2 → 127.0.0.1:<port>
              └─ Blazor Server → unchanged .razor pages
```

Both hosts share **one DI container** — the same singleton instances back both the native
pages and the Blazor pages, so a half-migrated app has one `FlightSessionManager`, one
`SimBriefService`, one `MessagingService`. Pages keep their `@inject` declarations and their
`IJSRuntime` interop unchanged; `IJSRuntime` calls are already `async` under Hybrid, so the
move to a SignalR circuit requires no page edits.

`NavigationView` is the single source of truth for navigation. Selecting a not-yet-ported
destination navigates the `WebView2` to that route and sets a CSS flag that hides the Blazor
sidebar and header, so the native shell chrome is the only chrome on screen.

Each page conversion is one commit: delete a route from the Blazor host, add a XAML page.
The final phase deletes `WebView2`, the Blazor host, `wwwroot/` and the Blazor packages.
**The EFB's Kestrel host is untouched at every point.**

#### Why this over the alternatives

| Option | Verdict |
|---|---|
| Blazor Server on in-process Kestrel + `WebView2` | **Chosen.** Proven in-repo pattern, shared DI, no page edits |
| Reparent a hidden WPF `BlazorWebView` HWND via `SetParent` | Rejected — fragile, breaks DPI and focus routing |
| MAUI `BlazorWebView` | Rejected — requires adopting the MAUI app model |
| Big-bang rewrite, no bridge | Rejected — leaves the app unflyable for most of the migration |

The bridge is the only genuinely throwaway work in this migration. It is bought in exchange
for never having a broken build.

### Localhost host binding

The Blazor host binds `127.0.0.1` on a random free port — **not** `0.0.0.0`. Unlike the EFB
server, it is never exposed to the LAN and needs no pairing, because it renders the pilot's
authenticated desktop UI.

## Visual system

`app.css`'s `:root` block is already a disciplined token system, which makes the port
mechanical rather than interpretive.

| CSS | XAML |
|---|---|
| `:root` and `html[data-theme="dark"]` | `Theme/Tokens.xaml`, `ThemeDictionaries` Light/Dark |
| `--r-xs … --r-full` | `CornerRadius` resources |
| `--sp-1 … --sp-16` | `Thickness` and `x:Double` resources |
| `--shadow-xs … --shadow-xl` | `Elevation` attached property applying a composition `DropShadow` per token |
| layered radial gradients | `RadialGradientBrush` (present in WinUI 3) |
| SVG noise/grain filter | one baked tiling PNG as an `ImageBrush` |
| `cubic-bezier(0.34,1.56,0.64,1)` | `KeySpline` with the same control points |
| Google Fonts CDN | Syne, Plus Jakarta Sans, JetBrains Mono, VT323 bundled in `Assets/Fonts` |

**Shadows need the attached property.** WinUI's built-in `ThemeShadow` cannot express the
tinted, multi-layer shadows the CSS uses (`--shadow-md` is two stacked shadows at different
blurs and opacities). An `Elevation` attached property that composes a `DropShadow` per
token is the only way to match them, and it keeps every call site to `theme:Elevation.Level="Md"`.

**Bundling the fonts is a fix, not just a port.** Today, typography silently depends on a
live connection to `fonts.gstatic.com` at startup.

Card, stat-tile, badge and pill styles become XAML `Style`s in `Theme/Cards.xaml`, authored
once during the Dashboard conversion and reused by every later page. This is why Dashboard
is first.

## Native mechanics

These are the "app feel" fixes, grouped by the complaint each addresses.

### Window chrome

- `ExtendsContentIntoTitleBar` with `SetTitleBar` on a drag region inside the existing header
- Mica Alt system backdrop
- Caption buttons recoloured to the palette; snap layouts work by default
- Window size, position and maximised state persisted across sessions
- Follows the system light/dark preference by default, with the existing manual override retained

### Removing webpage-ness

- Native auto-hiding WinUI scrollbars
- No accidental text selection outside fields intended to be selectable
- `FocusVisualPrimaryBrush` set to the accent, replacing browser focus outlines
- Native context menus on list rows (copy flight number, open route, and similar)
- `Alt+←` back; `Ctrl+1…9` for nav destinations; accelerators on primary actions
- Virtualised lists via `ItemsRepeater` — the logbook currently renders every row

### OS integration

- Taskbar overlay icon and progress driven by flight phase
- Windows toast notifications: landing score on touchdown, new pilot message, flight-recovery prompt
- Tray icon with a live flight summary and show/hide

OS integration is isolated in its own late phase so it can be cut without disturbing
anything else.

## Subsystem rewrites

### Map — Leaflet → Mapsui.UI.WinUI

The 21 `aviatesMap.*` / `aviatesMapMini.*` interop calls in `wwwroot/js/interop.js`
(667 lines) become native layers.

**The map keeps its exact current appearance**: `interop.js:626` uses CARTO `dark_all`
tiles, and Mapsui is pointed at that identical URL template with the same attribution.

| JS interop | Native replacement |
|---|---|
| `init`, `destroy` | `MapControl` lifecycle |
| `updatePosition`, `panTo`, `zoomIn`, `zoomOut` | Navigator + aircraft symbol layer |
| `setPlannedRoute` | Line layer from the SimBrief plan |
| `updateFlightPathAltitude` | Line layer with altitude-based colour ramp |
| `addAirportMarker`, `clearAirportMarkers` | Point layer |
| `upsertOtherPilot`, `removeOtherPilot`, `setOtherPilotClickHandler` | Point layer + `MapControl.Info` hit-testing |
| `fitRouteBounds` | `Navigator.ZoomToBox` |

`Mapsui.Tiling` and `Mapsui.Nts` are already referenced and carry over; `Mapsui.Wpf` is
replaced by `Mapsui.UI.WinUI`.

### Takeoff performance — 1,146 lines of JS → C#

`wwwroot/js/performance/` is business logic, not presentation, and has no WinUI equivalent:

| File | Lines |
|---|---:|
| `aircraft-configs.js` | 333 |
| `index.js` | 231 |
| `flex.js` | 204 |
| `airspeed.js` | 156 |
| `density.js` | 118 |
| `flaps.js` | 104 |

**Golden-value tests are captured from the current JS before the port begins**, so the
ported numbers are provably identical rather than plausibly similar.

`Core/Efb/EfbPerformanceCalculator.cs` (276 lines) already performs a similar calculation
for the EFB. **These two are deliberately not unified in this migration.** Merging them
would change the numbers one of the two surfaces produces — a behaviour change dressed as a
refactor, and out of scope. Unification is a candidate for later work once both are in C#.

### Voice — `voice.js` → NAudio + Concentus + a WebM muxer

**The WebM/Opus wire format is mandatory and cannot be changed.**
`DISPATCH-RADIO-WORKER/src/room.js` relays frames opaquely, but a browser-based dispatcher
console (deployed separately, not in this repo) decodes the same relayed frames with the
Web Audio `decodeAudioData()` API, which requires a valid WebM container. Changing the
format breaks dispatch for every operator.

Wire envelope, per `wwwroot/js/voice.js:75-77` (decode) and `:355-356` (encode):

```
[0]           uint8   idLen
[1 .. idLen]  bytes   senderId, UTF-8
[1+idLen ..]  bytes   audio/webm;codecs=opus chunk
```

Text frames are unchanged JSON markers:
`{"type":"voice_start"|"voice_end","from":"<pilotId>"}`.

| `voice.js` concern | Native replacement |
|---|---|
| `getUserMedia`, `enumerateDevices`, `setSinkId` | NAudio WASAPI capture/render + `MMDeviceEnumerator` |
| `MediaRecorder` (`audio/webm;codecs=opus`) | Concentus Opus encoder + a minimal WebM/Matroska muxer |
| `decodeAudioData` on buffered transmission | WebM demuxer + Concentus decoder |
| Web Audio gain graph (`CLEAR_NM` 200, `DEGRADED_NM` 400, `NOISE_GAIN_CEILING` 0.35) | PCM mixing in C# against the same constants |
| Per-sender demultiplexing, `SESSION_TIMEOUT_MS` 2000 lost-`voice_end` timeout | Per-sender playback buffer keyed on the envelope sender id |

Playback keeps the current buffer-then-decode approach (decode on `voice_end`, not
streaming), matching the amendment recorded in the Phase 2 voice spec.

**Hard gate.** This phase is the highest-risk item in the migration, chosen knowingly over
the lower-risk option of keeping a hidden WebView2 for voice alone. Both conditions must
pass before `voice.js` is deleted:

1. A real transmission blob captured from the current Blazor build round-trips byte-exactly
   through the new muxer and demuxer.
2. The new encoder's output plays in a browser via `decodeAudioData()`, proving dispatcher
   compatibility.

If the gate fails, this phase reverts independently and voice ships a release behind the UI.

## Threading

| WPF | WinUI 3 | Files |
|---|---|---|
| `System.Windows.Threading.DispatcherTimer` | `DispatcherQueueTimer` | `SimConnectManager.cs` (poll + reconnect), `ReplayViewModel.cs` |
| `Application.Current.Dispatcher.Invoke` | `DispatcherQueue.TryEnqueue` | `MainViewModel.cs` (7 sites), `MapViewModel.cs` (2), `UpdateService.cs` (1) |

An `IUiDispatcher` abstraction (`Invoke`, `BeginInvoke`) removes the last
`using System.Windows` from `ViewModels/` and makes the 20Hz path testable. This work is
cherry-picked from the previous branch.

`ReplayViewModel.cs:56` carries a comment recording a bug caused by a non-UI-thread timer
writing `[ObservableProperty]` fields. `DispatcherQueueTimer` preserves the UI-thread
guarantee that fix depends on.

### SimConnect HWND hook — highest-risk item in Phase 0

`MainWindow.xaml.cs:23-25` currently does:

```csharp
var hwnd = new WindowInteropHelper(this).Handle;
HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
```

`HwndSource` does not exist in WinUI 3. Replacement:

```csharp
var hwnd = WindowNative.GetWindowHandle(this);
SetWindowSubclass(hwnd, _subclassProc, IdSimConnect, IntPtr.Zero);   // P/Invoke, comctl32
```

The `SUBCLASSPROC` delegate **must be stored in a field** for the window's lifetime. Held
only in a local, the GC collects it and the process crashes on the next SimConnect message —
mid-flight, non-deterministically.

This is already implemented and tested against a real HWND in
`Platform/WindowSubclass.cs` on the previous branch (98 lines), and is cherry-picked rather
than rewritten. It is re-verified against a live sim as a Phase 0 exit criterion.

`SimConnectStub.cs` fallback behaviour and the 5-second auto-reconnect are unchanged.

### 20Hz rendering

The CLAUDE.md rule — *do not update per tick, batch every 3–5 samples* — carries over but
changes mechanism. Instead of throttling `StateHasChanged()`, ViewModels throttle their own
property writes, and pages use `x:Bind` (compiled, `OneWay`) rather than `Binding`.

The every-5-samples cadence in the (now deleted) `LiveFlightViewModel` is the reference
implementation to copy into whichever ViewModels back the live ACARS and map surfaces.
CLAUDE.md's guidance currently cites `LiveFlightViewModel` by name and must be updated in
Phase 7 to point at its replacement.

**During the strangler phase, telemetry-heavy pages are ported early** so the SignalR
circuit is not carrying 20Hz updates for longer than necessary.

## Reused from the previous branch

Cherry-picked — visual-design-agnostic, already written, partly verified (~330 lines):

- `Platform/WindowSubclass.cs`, `Platform/IUiDispatcher.cs`,
  `Platform/DispatcherQueueUiDispatcher.cs`, `Platform/ImmediateUiDispatcher.cs`,
  `Platform/FilePickerService.cs`
- Threading ports in `MainViewModel`, `MapViewModel`, `ReplayViewModel`

  The previous branch also ported threading in `LiveFlightViewModel`,
  `StatisticsViewModel` and `TelemetryViewModel`. Those three are deleted by this spec, so
  their cherry-picked changes are dropped rather than applied.
- `App.xaml.cs` lifecycle port, `Services/PttKeyMap.cs`, `Services/UpdateService.cs` changes
- The verified toolchain pin (below)

Discarded — built to the wrong visual brief:

- The `NavigationView` shell and Mica window (`3643700`, `f79f520`, `ab29ebc`)
- The 12 placeholder pages (`22a0422`)

The previous branch was cut from an older `main`, so cherry-picks are applied onto current
`main` and conflicts resolved against the recovery, booking and flag-gradient work that has
landed since.

## Toolchain and packaging

Windows App SDK is pinned to versions the previous branch **proved build without Visual
Studio** — 1.6 and 1.7 fail with MSB4062 in `MrtCore.PriGen.targets`:

```xml
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.250907003" />
<PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.4654" />
```

| Property | From | To |
|---|---|---|
| SDK | `Microsoft.NET.Sdk.Razor` | `Microsoft.NET.Sdk` |
| `TargetFramework` | `net8.0-windows` | `net8.0-windows10.0.19041.0` |
| — | `<UseWPF>true</UseWPF>` | `<UseWinUI>true</UseWinUI>` |
| — | — | `<WindowsPackageType>None</WindowsPackageType>` |
| `PublishSingleFile` | `true` | removed (unsupported by Windows App SDK) |

**Packages removed:** `Microsoft.AspNetCore.Components.WebView.Wpf` (final phase),
`OxyPlot.Wpf`, `OxyPlot.Blazor`, `Mapsui.Wpf`.
**Added:** `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`,
`Mapsui.UI.WinUI`, `NAudio`, `Concentus`.
**Retained:** `CommunityToolkit.Mvvm`, `Mapsui.Tiling`, `Mapsui.Nts`, `Newtonsoft.Json`,
`RestSharp`, `DiscordRichPresence`, `SharpDX.DirectInput`, Serilog, the
`Microsoft.AspNetCore.App` FrameworkReference, and the runtime SimConnect `<None>` items
with their `Libs\` copy rules.

`PublishSingleFile` is unsupported by the Windows App SDK, so `dist/` becomes a folder:

- `build.ps1` — drop `-p:PublishSingleFile=true`; `-Release` produces a folder
- `installer/AviatesAirTracker_Setup.iss` — bundle folder contents rather than one EXE
- `.github/workflows/build.yml` — same change; release-on-tag flow otherwise unchanged
- `Services/UpdateService.cs` — verify the update/replace path against a folder layout

The app stays **unpackaged**, so `%AppData%\AviatesAirTracker\` paths (SimBrief plan, EFB
wallpaper, settings) are unchanged. MSIX is deferred — it would sandbox file access and
rewrite `UpdateService` entirely, and is not required to ship.

## Phasing

Every phase ends in a build that can record a real flight.

| # | Work | Complete when |
|---|---|---|
| 0 | Cherry-pick `Platform/` + threading ports; csproj conversion; delete dead WPF and OxyPlot; DI port | Telemetry flows from a live sim, EFB serves, Serilog writes, SimConnect subclass verified |
| 1 | Blazor Hybrid → Blazor Server on localhost Kestrel; WinUI shell; `NavigationView` + `WebView2` host; login gate | All 13 destinations reachable; app records a real flight end-to-end |
| 2 | `Theme/Tokens.xaml`, `Typography.xaml`, `Controls.xaml`, `Cards.xaml`; bundled fonts; `Elevation` attached property; custom title bar; Mica | Shell is visually indistinguishable from today's |
| 3 | Pages: Dashboard → Flights → PilotHub → Fleet → News/Support/Events → Messages → Routes → Settings → Login | Each page one commit, each matching the Blazor original |
| 4 | Map → Mapsui.UI.WinUI | Route, aircraft, airport and other-pilot layers native; CARTO tiles identical |
| 5 | Performance JS → C# behind golden tests; then ACARS + `AcarsFmc` + `TakeoffPerformanceModal` + `LandingResultModal` + `FlightRecoveryModal` | Golden values match; ACARS matches the original |
| 6 | Native voice stack, behind the round-trip gate | PTT round-trips against the live Worker and the browser dispatcher console |
| 7 | Delete `WebView2`, Blazor host, `wwwroot/`, Blazor packages; OS integration; `build.ps1`, `.iss`, CI; rewrite `CLAUDE.md` and `docs/` for the WinUI architecture | `.\build.ps1 -Release` produces a working installer; no doc still describes Blazor Hybrid |

**Dashboard is first in Phase 3** deliberately — it establishes the card, stat-tile and
list styles every later page reuses.

**ACARS is last.** At 1,483 lines plus a 355-line FMC and a 682-line performance modal, it
is roughly a third of the UI work and benefits most from a settled style system.

## Risks

| Risk | Mitigation |
|---|---|
| SimConnect subclass delegate collected by GC → random mid-flight crash | Delegate held in a field; implementation cherry-picked from a tested version; re-verified against a live sim as a Phase 0 exit criterion |
| Native voice output rejected by the browser dispatcher console | Byte-exact round-trip plus browser `decodeAudioData()` gate before `voice.js` is deleted; phase reverts independently |
| Ported performance calculator silently produces different numbers | Golden-value tests captured from the current JS before porting begins |
| Blazor Hybrid → Blazor Server conversion breaks page behaviour | Phase 1 exit criterion is a full real flight through the WebView before any page is ported |
| Visual drift accumulating page by page | Fidelity contract is binding; only the three approved visual changes below are permitted |
| Windows App SDK conflicting with the `Microsoft.AspNetCore.App` FrameworkReference used by the EFB Kestrel host | Verified in Phase 0 — EFB server start is a Phase 0 exit criterion |
| Rejected SimConnect vars silently misaligning `AircraftState` (previously observed) | Untouched — `Core/SimConnect/` definitions are not modified |
| 20Hz telemetry over the SignalR circuit costing more than Hybrid during the overlap | Telemetry-heavy pages ported early; existing per-page batching retained |

## Approved visual changes

Exactly two. Anything else that changes appearance is a bug.

1. **Native scrollbars** replace the CSS-styled ones — auto-hiding, WinUI-standard.
2. **Native focus rings** on the accent colour replace browser focus outlines.

> **Superseded 2026-08-23.** This list originally included a third item — the sidebar
> becoming translucent navy over Mica instead of a flat `#0B1628` fill. That approval was
> withdrawn during Phase 2 planning: the sidebar is a byte-exact, fully opaque match to
> `--sb-bg`, held to the same bar as everything else. See
> `2026-08-23-winui3-phase2-shell-visual-design.md` for the decision and the (different)
> place Mica ended up instead — the title bar strip, which is otherwise the only surface in
> the window it could ever be visible on.

## Accepted regressions

1. **No single-file EXE.** `dist/` becomes a folder. Users see no difference; the installer
   handles it.
2. **WebView2 Runtime is still required until Phase 7**, then no longer required by the
   tracker at all. The EFB continues serving its web frontend to tablets and phones
   regardless, which is unaffected.
