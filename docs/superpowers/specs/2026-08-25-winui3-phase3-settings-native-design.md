# WinUI 3 Migration — Phase 3, ninth page: native Settings

**Date:** 2026-08-25
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** every prior converted page — reuses established conventions throughout
(`Border`-not-`Button`, the tab-bar-free two-column layout Routes/Fleet/Events already use for
side-by-side cards, `ContentDialog`-free since this page has no modals, direct property
read/write against `SettingsService.Settings` instead of `@bind`).

## Goal

Convert `Settings.razor` (route `/settings`) to a native WinUI 3 `Page` — by far the largest
remaining page (1058 lines, 11 panel-card sections across a two-column layout) and the one every
other converted page's "Go to Settings" stub has been silently pointing at (Messages' ACARS gate,
and likely others once checked). Unlike every prior page, this one pulls in several **OS-level
integrations beyond pure UI porting**: PTT hotkey/joystick capture, audio device enumeration, and
an inline QR code for EFB pairing — three genuine scope questions, resolved below rather than
guessed at implementation time.

## Three real scope decisions, resolved up front

### 1. PTT hotkey + joystick capture — fully portable, native already

`PttInputService` (`Services/PttInputService.cs`) already exposes `GetConnectedJoysticks()` and
`CaptureNextJoystickButtonAsync(Guid, CancellationToken)` as plain C# methods (DirectInput-backed,
no JS/WebView dependency) — confirmed by reading the service directly. The hotkey capture itself
is just a keyboard-focus-and-listen pattern (`PttKeyMap.ToVirtualKey`/`Describe`, also plain C#).
**No gap here** — both port straightforwardly to a native `TextBox`/`KeyDown` handler and a
`ComboBox`+button, no different in kind from the Enter-to-send key handling Messages already
built.

### 2. Microphone/speaker device selection — the mic/speaker *list* is native-portable, but the actual PTT voice pipeline is not

`Settings.razor`'s `LoadAudioDevicesAsync`/`OnMicrophoneChanged`/`OnSpeakerChanged` call
`JS.InvokeAsync("aviatesVoice.getAudioDevices"/"setMicrophone"/"setSpeaker")` — confirmed by
reading `wwwroot/js/voice.js` directly, this is a **JS-only module with no native C# counterpart
anywhere in the codebase** (grepped `Services/`/`Core/` for any `Voice`/`Voip` service — none
exists). The actual PTT audio capture/playback/transmission pipeline this module drives is wired
into the still-Blazor `Acars.razor` page (dispatch radio), which is explicitly out of scope for
this migration phase per `CLAUDE.md` (ACARS/Map are separate, later phases).

**Decision**: the native Settings page builds the microphone/speaker **selector UI** for real —
enumerate real Windows audio devices via `Windows.Devices.Enumeration.DeviceInformation.FindAllAsync`
(`DeviceClass.AudioCapture`/`DeviceClass.AudioRender`, both native WinRT, no JS needed) — and
persists the chosen `PttMicrophoneDeviceId`/`PttSpeakerDeviceId` to `SettingsService.Settings`
exactly like the original. It does **not** call anything equivalent to `aviatesVoice.setMicrophone`/
`setSpeaker` — there is nothing native to call yet; the setting is saved and will take effect once
ACARS's voice pipeline reads it (already the file the current JS module writes/reads from), same
as it does today. This isn't a silently-dropped feature — the actual voice pipeline doesn't run
outside the Blazor-hosted ACARS page in the CURRENT app either, so nothing regresses.

### 3. EFB pairing QR code — port via raw module grid, not the SVG string

The original calls `QrCode.Encode(url, QrEcc.Medium).ToSvg(...)` (`Core/Efb/QrCode.cs`) and injects
the raw SVG string via `MarkupString`. WinUI has no direct way to render an arbitrary inline SVG
string. **`QrCode` already exposes `int Size` and `bool GetModule(int x, int y)`** (confirmed by
reading the file directly) — the native page renders the QR as a `Grid` of black/white `Rectangle`
cells built from `GetModule`, not by fighting SVG string rendering. Same visual result, cleaner
native implementation, no new dependency.

## What's explicitly out of scope

- **App-wide live theme switching.** The original's Appearance panel calls
  `JS.InvokeVoidAsync("aviatesTheme.apply", theme)` to flip a CSS class on the Blazor DOM. Every
  single native page built in this migration so far hardcodes dark-theme hex values directly
  (confirmed — grep any `Pages/*.xaml` for `Background="#0`), none use dynamic `ThemeResource`
  lookups that would respond to a runtime theme change. Making the native Settings page's theme
  picker actually retheme the running native shell would require reworking every previously-built
  page to use dynamic brushes instead of hardcoded hex — a cross-cutting change far beyond this
  page's own scope. **This page builds the picker UI and persists `Settings.Theme`** (System/
  Light/Dark, matching the original's 3 options exactly), but does not implement live re-theming
  of the native shell. Noted plainly here rather than silently shipped as if it worked.
- Does not touch `Settings.razor`, `SettingsService`, `PttInputService`, `EfbServer`,
  `UpdateService`, `AviatesBackendClient`, or `Core/Efb/QrCode.cs` — page-only conversion, same as
  every prior page.
- Does not wire the "Go to Settings" stub buttons on other already-converted native pages
  (Messages' ACARS gate, etc.) to navigate here — that's a small, separate follow-up once this
  page exists, not bundled into this spec silently.

## What gets built

### `AviatesAirTracker/Pages/SettingsPage.xaml`/`.xaml.cs` (new)

No new ViewModel — like Fleet/Dashboard/Pilot Hub, this page reads/writes `SettingsService.Settings`
directly (a plain mutable settings object, not itself a ViewModel), matching the original's own
direct-binding approach. Page-local UI state (capture-in-progress flags, sync/verify status,
update-check state) lives on the page, same placement decision every prior page has made for
`_activeTab`/`_statusMessage`-style fields.

Two-column layout (`Grid` with two `*`-width columns, `ColumnSpacing` matching `var(--sp-5)`),
each column a vertical stack of panel `Border`s:

**Left column:**
- **Pilot Profile**: avatar circle (initials, gradient background, a verified checkmark badge
  when `_isVerified`), display name + "VERIFIED" chip + email row (all conditional), Pilot
  Name/Pilot ID text fields.
- **Appearance**: 3 theme option cards (System/Light/Dark, icon + label, active-state border/bg)
  — persists `Settings.Theme` only, per the scope decision above.
- **SimBrief Integration**: username/ID text field + an inline "synced from account" hint when
  applicable, a small info banner below.

**Right column:**
- **ACARS Configuration**: password-masked key field + a Verify & Sync button (3 visual states:
  idle "Verify & Sync", syncing spinner, verified "Synced"), a conditional info banner (verified
  vs. unverified copy — exact text from the original), an auto-position-report-interval dropdown
  (0/1/5/10/15/30 min), an FMC-shortcut hint strip.
- **Hoppies ACARS**: pilot-code password field, an enable toggle, and 6 category toggles
  (Loadsheet/Timing Reminders/Airborne-Landing Calls/Post-Flight Summary/Weather Advisories/
  Hazard Advisories) that dim + disable when the master toggle is off — exact conditional-opacity
  behavior from the original.
- **Dispatch Radio**: backend URL field, PTT hotkey capture field + Clear button, PTT joystick
  device dropdown + Bind Button (10s capture timeout, matching the original's
  `CancellationTokenSource(TimeSpan.FromSeconds(10))`), microphone/speaker dropdowns (native
  device enumeration per the scope decision above).
- **Preferences**: 4 toggles (Auto-connect to MSFS, Desktop Alerts, Minimize to Tray, Share Live
  Position — the last one calls `BackendClient.SetLiveSharingAsync` immediately on change, not
  deferred to Save, matching the original exactly).
- **Friend Code**: either an amber "set your ACARS key" prompt (no key) or the generated/stored
  code in a large monospace display + a Copy button (2s "Copied!" confirmation, matching the
  original's `Task.Delay(2000)`).
- **About & Updates**: version row + Check for Updates button (3 states), an update-available
  panel (download progress bar while installing, or a Download & Install button) wired to
  `UpdateService.DownloadProgressChanged` (marshaled via `DispatcherQueue.TryEnqueue`, same
  pattern Messages' live-event subscriptions already established — unsubscribe on `Unloaded`).
- **EFB Companion**: running/stopped states (QR code via the `Grid`-of-`Rectangle`s technique
  above + address + pairing code, or a "stopped" banner), Start/Stop button, Enable/Auto-start
  toggles — wired to `EfbServer.StatusChanged` (same `DispatcherQueue.TryEnqueue`/`Unloaded`
  pattern).
- **Save / Reset row**: Save persists everything + regenerates the friend code + shows a success
  status; Reset calls `SettingsService.Reset()` and re-syncs all displayed state.

A status banner (dismissible, success/error variants — reused visual pattern from every prior
page's status banner) sits above the two-column layout, shown for sync results, save
confirmation, and reset confirmation.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"settings"` entry gets `NativePageType: typeof(Pages.SettingsPage)` — one line.

## Verification

- Build clean, no new warnings.
- Visual: native Settings compared against the current Blazor page, section by section — all 9
  panel cards, both column layouts, every toggle/dropdown/field.
- Functional: ACARS sync (needs a working backend + valid key), theme picker persists the setting
  (doesn't re-theme the shell, per the explicit scope decision), Hoppies category-toggle
  dim/enable behavior, PTT hotkey capture + clear, joystick device list + bind, native microphone/
  speaker enumeration, friend code generation/copy, update check (needs network), EFB start/stop
  + QR render + pairing code, Save/Reset. Several of these need a working backend connection
  and/or real hardware (a connected joystick, actual audio devices) to fully exercise — defer
  those specific checks explicitly if unavailable in this environment, rather than skipping
  silently, same as every prior live-dependency check in this migration.
