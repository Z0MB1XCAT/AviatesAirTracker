# WinUI 3 Migration — Phase 3, ninth page: native Settings — implementation plan

**Date:** 2026-08-25
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Design spec:** `docs/superpowers/specs/2026-08-25-winui3-phase3-settings-native-design.md`
(read this first — this plan assumes it, doesn't repeat it. Read its three "real scope decisions"
section especially closely before Tasks 2 and 4.)

## Before you start

- No new ViewModel — this page reads/writes `SettingsService.Settings` directly (a plain mutable
  object), matching the original's own direct-binding approach. Don't invent a ViewModel wrapper.
- Known, recurring WinUI3-SDK crash traps in this project (hit in every task of every plan so
  far): no `<Style>` element anywhere in any `.xaml` file (silently crashes `XamlCompiler.exe`,
  zero diagnostics); no literal double-hyphen `--` inside any XAML comment (invalid XML, same
  silent crash — use a single hyphen or `──` instead). Run
  `grep -n -- "--" <file>.xaml | grep -v '<!--\|-->'` on every XAML file you touch before
  considering a task done — this has recurred in literally every task of every plan this session,
  including ones written specifically to avoid it.
- `CS8852`: don't bind an init-only `record`/`record struct` to any `ItemsControl` — use a plain
  mutable class with `{ get; set; }` for any display-row wrapper (e.g. the joystick/audio-device
  dropdown items in Task 4).
- `Border`-not-`Button`: no `<Style>` means `Button`'s `CornerRadius` doesn't reliably render on
  this SDK. Use `Border` + `Tapped` for every button in this page — Verify & Sync, Save, Reset,
  Clear (hotkey), Bind Button, Copy, Check for Updates, Download & Install, Start/Stop EFB.
- `ContentDialog` dark-theming pattern (established on `BookingDialog.cs`/`CreateEventDialog.cs`)
  is **not needed here** — this page has no dialogs, just inline panels.
- Live-event subscription pattern (established on `MessagesPage.xaml.cs` for
  `MessagingService.NewMessageReceived`/`UnreadCountChanged`): subscribe in `Loaded`, unsubscribe
  in `Unloaded`, marshal every UI mutation via `DispatcherQueue.TryEnqueue` since
  `UpdateService.DownloadProgressChanged` and `EfbServer.StatusChanged` may not fire on the UI
  thread — copy this exact shape for Task 5, don't re-derive it.
- Read `AviatesAirTracker.LegacyWeb/Pages/Settings.razor` (1058 lines) directly for exact copy,
  field order, and conditional logic per section — this plan summarizes, it doesn't transcribe
  verbatim. Read `AviatesAirTracker/Services/SettingsService.cs` for the full `Settings` object's
  property names (this plan uses the same names `Settings.razor` does, assumed already correct,
  but verify against the actual class before writing code).

## Task 1 — Page skeleton, status banner, Pilot Profile, Appearance, SimBrief

Files: `AviatesAirTracker/Pages/SettingsPage.xaml` (new),
`AviatesAirTracker/Pages/SettingsPage.xaml.cs` (new).

1. `SettingsPage : Page`, `SettingsService` via `App.GetService<SettingsService>()`. `Loaded`
   handler: call a `RefreshNameDisplay()`-equivalent (see below) and any other initial-state sync
   the later tasks' sections need — for THIS task, just enough to render Pilot Profile/Appearance/
   SimBrief correctly on load.
2. Two-column `Grid` (`*`/`*` columns, matching `var(--sp-5)` spacing) hosting the whole page,
   with a status banner (dismissible, success/error variants, reused visual pattern from every
   prior page) above it — build both columns' container structure now even though only 3 of the
   9 panels have real content yet; stub the remaining 6 panel slots as empty `Border`s with
   `// Task N builds this.` comments (matching the exact stub pattern every prior multi-task plan
   this session has used) so later tasks know exactly where to insert their content.
3. **Pilot Profile** panel: avatar circle (initials text over a gradient — matching
   `linear-gradient(135deg, accent, violet)`), a verified-checkmark badge (small circle overlay,
   bottom-right of the avatar, shown only when `_isVerified`), display name text + a "VERIFIED"
   chip (shown only when verified) + an email row (shown only when verified AND a verified email
   is set), Pilot Name/Pilot ID `TextBox`es bound to `Settings.PilotName`/`Settings.PilotId`
   (write-through on `TextChanged`, matching the original's `@oninput`/`@bind` split — Pilot Name
   uses `oninput` because it also drives `UpdateNameDisplay()` live, Pilot ID is a plain two-way
   bind with no side effect).
   - `UpdateNameDisplay()`: recomputes `_displayName` ("Pilot" if blank) and `_initials` (first
     letter of up to 2 space-separated words, uppercased, "?" if blank) from `Settings.PilotName`
     — port verbatim, this exact logic is reused by Task 2's sync flow and the Save/Reset task,
     so make it a clearly-named reusable method, not inlined.
   - `_isVerified`/`_verifiedEmail` are page-local fields, initialized on load: `_isVerified =
     true` if `Settings.AcarsKey` and `Settings.PilotName` are both non-empty (matching
     `OnInitialized`'s exact check — this is a heuristic "looks previously synced" flag, not a
     fresh verification), `_verifiedEmail` stays empty until an actual sync happens (Task 2).
4. **Appearance** panel: 3 theme option cards (System/Light/Dark) in a 3-column grid, each an
   icon (port the 3 SVGs verbatim — monitor/sun/moon) + label, active-state border/background
   (accent when `Settings.Theme == value`). Tapping a card sets `Settings.Theme = value` and
   calls `SettingsService.Save()` — **per the design spec's explicit scope decision, this does
   NOT re-theme the running native shell**, it only persists the setting and updates the active
   card's visual state. Don't build anything beyond that.
5. **SimBrief Integration** panel: username/ID `TextBox` bound to `Settings.SimBriefUsername`, a
   conditional "· synced from account" hint (shown when `_isVerified` and the field is non-empty
   — this hint's visibility will need to re-evaluate after Task 2's sync flow runs, so make sure
   its visibility check reads live state, not a value cached only at page-load), an info banner
   below (static text, port verbatim).
6. Build (`dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`) — 0 errors. Check
   the XAML `--`-in-comment trap.
7. Commit: `feat(settings): native page skeleton, Pilot Profile, Appearance, and SimBrief panels`.

## Task 2 — ACARS Configuration and Friend Code

Files: `AviatesAirTracker/Pages/SettingsPage.xaml`/`.xaml.cs` (modified).

1. **ACARS Configuration** panel: password-masked `PasswordBox` (or a `TextBox` with
   `PasswordChar`-equivalent — check what WinUI3 offers; `PasswordBox` is the standard control)
   bound to `Settings.AcarsKey`, with a Verify & Sync `Border`-button beside it. Button has 3
   visual states matching the original exactly: idle "Verify & Sync" (accent/primary style),
   syncing (`ProgressRing` + "Syncing…"), verified "Synced" (secondary style + checkmark icon).
   Disabled while syncing or while the key field is empty/whitespace.
2. Editing the key field manually clears `_isVerified`/`_verifiedEmail` (matching
   `OnAcarsKeyInput`'s exact side effect — a manual edit invalidates the previous sync until
   re-verified).
3. Conditional info banner: verified copy ("Profile synced — name and SimBrief ID have been
   populated from your AviatesAir account.") vs. unverified copy (the original's exact 2-sentence
   text about where to get a key) — read `Settings.razor` lines 161-179 for exact wording.
4. Auto Position Report Interval: a `ComboBox` with the original's 6 exact options (Off/1/5/10/15/
   30 min), bound to `Settings.AutoPirepIntervalMinutes`, plus the static hint text below it.
5. FMC-shortcut hint strip: a static `Border` with the `Ctrl+Shift+F` key-chip + explanatory text
   — port verbatim, no interactivity needed (this shortcut fires from the ACARS page, not here).
6. `SyncProfileAsync()`: trims the key, writes the trimmed value back, calls
   `AviatesBackendClient.ValidateAcarsKeyAsync(key)` (inject `AviatesBackendClient` via
   `App.GetService<AviatesBackendClient>()`). On success (`IsValid: true`): populate
   `Settings.PilotName` from the result (call Task 1's `UpdateNameDisplay()` after), populate
   `Settings.SimBriefUsername` only if the backend returned a non-blank value (and refresh the
   SimBrief panel's "synced from account" hint), populate `Settings.PilotId` from the backend or
   derive one deterministically via SHA256 if the backend didn't return one AND no `PilotId` is
   already set (port the exact hash-derivation logic — `"avt_" + first-12-hex-chars-of-
   SHA256("AVIATES_PID_" + key)`), set `_isVerified = true`/`_verifiedEmail = result.Email`, call
   `SettingsService.RefreshFriendCode()` (so the Friend Code panel updates too), show a success
   status ("Profile synced — welcome, {name}!"). On failure or exception: clear
   `_isVerified`/`_verifiedEmail`, show the appropriate error status (invalid-key vs.
   network-error copy — both exact strings from the original).
7. **Friend Code** panel: an amber "Set your ACARS key and save to generate your Friend Code."
   banner when `Settings.AcarsKey` is empty; otherwise a large monospace code display (generated
   via `SettingsService.GenerateFriendCode(key)` if `Settings.FriendCode` is empty, else the
   stored value directly — matching the original's exact fallback) + a Copy `Border`-button.
   Copying uses `Windows.ApplicationModel.DataTransfer.Clipboard.SetContent` (the native
   clipboard API — NOT a JS interop call, this page has no WebView), shows "Copied!" for 2
   seconds (matching `Task.Delay(2000)`) then reverts.
8. Build — 0 errors, compare warnings against baseline (15 pre-existing, none referencing
   Settings).
9. Commit: `feat(settings): ACARS Configuration and Friend Code panels`.

## Task 3 — Hoppies ACARS and App Preferences

Files: `AviatesAirTracker/Pages/SettingsPage.xaml`/`.xaml.cs` (modified).

1. **Hoppies ACARS** panel: pilot-code `PasswordBox` bound to `Settings.HoppiesPilotCode`
   (trimmed on write, matching `OnHoppiesPilotCodeInput`), a master enable `ToggleSwitch`
   (`Settings.HoppiesEnabled`, calls `SettingsService.Save()` immediately on change — not
   deferred to the page's Save button, matching the original), and 6 category toggles
   (Loadsheet/Timing Reminders/Airborne-Landing Calls/Post-Flight Summary/Weather Advisories/
   Hazard Advisories) each bound to its own `Settings.HoppiesSend*` property, each disabled AND
   visually dimmed (Opacity per the original's `opacity: HoppiesEnabled ? 1 : 0.5` with a
   transition) when the master toggle is off, each saving immediately on change (matching
   `OnCategoryToggle`'s exact `setter(value); SettingsService.Save();` pattern).
2. **App Preferences** panel: 4 toggles — Auto-connect to MSFS (`Settings.AutoConnectSimConnect`),
   Desktop Alerts (`Settings.ShowApproachAlerts`), Minimize to Tray (`Settings.MinimizeToTray`,
   all three are plain `@bind`-equivalent with no immediate-save side effect in the original — do
   NOT call `SettingsService.Save()` on these 3, they're only persisted by the page's Save button,
   matching the original exactly), and Share My Live Position (`Settings.ShareLivePosition`,
   which DOES save immediately AND calls `AviatesBackendClient.SetLiveSharingAsync(enabled,
   Settings.AcarsKey.Trim())` if a key is set — matching `OnShareLivePositionChanged`'s exact
   immediate-effect behavior, explicitly different from the other 3 toggles in this same panel).
   This 3-vs-1 immediate-save distinction within one panel is easy to get wrong — double check
   against the original before considering this task done.
3. Build — 0 errors, compare warnings against baseline.
4. Commit: `feat(settings): Hoppies ACARS and App Preferences panels`.

## Task 4 — Dispatch Radio (PTT hotkey, joystick, native audio device enumeration)

Files: `AviatesAirTracker/Pages/SettingsPage.xaml`/`.xaml.cs` (modified).

1. Dispatch Backend URL `TextBox` bound to `Settings.DispatchBackendUrl`, plus the static hint
   banner below it (port verbatim).
2. **PTT Hotkey** capture: a read-only `TextBox` showing either "Press a key…" (while capturing)
   or the current binding label (`Settings.PttHotkeyLabel`, or "Unbound — click and press a key"
   if `Settings.PttHotkeyVkCode == 0`) + a Clear `Border`-button. Focusing the field starts
   capture (`_capturingHotkey = true`, clear any rejection message); losing focus stops it. While
   capturing, a `KeyDown` handler reads the key via `PttKeyMap.ToVirtualKey(...)` (inject
   `PttInputService` via `App.GetService<PttInputService>()` if `PttKeyMap` is a static helper
   class reachable without DI — check its actual location/accessibility) — on an unmappable key,
   show a rejection message ("\"{key}\" can't be used as a PTT hotkey — try a different key.") and
   stop capturing without saving; on a valid key, save
   `Settings.PttHotkeyVkCode`/`Settings.PttHotkeyLabel` via `PttKeyMap.ToVirtualKey`/`Describe`,
   call `SettingsService.Save()`, stop capturing. Clear button zeroes both fields and saves.
3. **PTT Joystick Button**: a `ComboBox` populated from `PttInputService.GetConnectedJoysticks()`
   (called once on load — the original doesn't live-refresh this list, matching that), a
   placeholder "-- No joystick --" first entry, a Bind Button `Border`-button (disabled when no
   joystick is selected or a capture is already in flight) that calls
   `PttInputService.CaptureNextJoystickButtonAsync(guid, cancellationToken)` with a 10-second
   timeout (`CancellationTokenSource(TimeSpan.FromSeconds(10))`, matching the original exactly),
   saving `Settings.PttJoystickGuid`/`Settings.PttJoystickButton` on a non-null result. Shows
   "Bound: button {n+1}" below when `Settings.PttJoystickButton >= 0` (1-indexed display of a
   0-indexed value, matching the original's `+ 1`).
4. **Microphone/Speaker** dropdowns: per the design spec's explicit scope decision, enumerate real
   Windows audio devices natively via `Windows.Devices.Enumeration.DeviceInformation.FindAllAsync`
   with `DeviceClass.AudioCapture` (microphones) and `DeviceClass.AudioRender` (speakers) — NOT
   the original's `JS.InvokeAsync("aviatesVoice.getAudioDevices")`, there is no JS runtime to call
   into from this native page. Each `ComboBox` gets a "System default" first entry (empty device
   id) plus the enumerated devices (id + friendly name). On selection change, save
   `Settings.PttMicrophoneDeviceId`/`Settings.PttSpeakerDeviceId` via `SettingsService.Save()` —
   do **NOT** attempt to call anything equivalent to `aviatesVoice.setMicrophone`/`setSpeaker`,
   there is no native audio pipeline to receive it yet (the spec explains why this isn't a
   regression — the actual voice pipeline only runs inside the still-Blazor ACARS page today
   regardless). Just persist the choice.
5. Build — 0 errors, compare warnings against baseline. If `Windows.Devices.Enumeration` requires
   a capability declaration in the app manifest that isn't already present, note this explicitly
   in your report rather than silently working around it — check `Package.appxmanifest` (or
   equivalent, this project's exact manifest setup) for whether a microphone capability is
   already declared (likely yes, if any other part of the app already touches audio) before
   assuming you need to add one.
6. Commit: `feat(settings): Dispatch Radio panel (PTT hotkey, joystick, audio devices)`.

## Task 5 — About & Updates, EFB Companion

Files: `AviatesAirTracker/Pages/SettingsPage.xaml`/`.xaml.cs` (modified).

1. **About & Updates** panel: version row (`v{major}.{minor}.{build}` from
   `Assembly.GetExecutingAssembly().GetName().Version`, matching the original's exact format) +
   Check for Updates `Border`-button (3 states: idle, checking with `ProgressRing`, and — after a
   completed check — nothing changes on the button itself, the result renders below). Inject
   `UpdateService` via `App.GetService<UpdateService>()`.
   - Below the button: if `UpdateService.AvailableUpdate` is non-null, show an update-available
     panel (version number, then either a download-progress bar while `_isInstallingUpdate`
     — bound to `UpdateService.DownloadProgressChanged`, marshaled via `DispatcherQueue.TryEnqueue`
     matching Messages' established live-subscription pattern — or a Download & Install button);
     else if a check has completed and found nothing, show a green "You're on the latest version."
     strip; else show nothing (matches the original's 3-way `@if`/`else if`/implicit-else-nothing).
2. **EFB Companion** panel: inject `EfbServer` via `App.GetService<EfbServer>()`. Two top-level
   states based on `EfbServer.IsRunning`:
   - Running: a QR code (build via `Core.Efb.QrCode.Encode(EfbServer.PrimaryUrl, QrEcc.Medium)`,
     then render as a `Grid` of `Rectangle` cells from `qr.Size`/`qr.GetModule(x, y)` — per the
     design spec's explicit resolution of this, NOT an SVG string — sized to roughly match the
     original's 150×150px card with an 8px white padding border) + a live status dot/label
     ("Running · port {N}"), the display address (`EfbServer.PrimaryUrl.Split('#')[0]`), and the
     pairing code (`EfbServer.PairingCode`) in large monospace, plus a Stop EFB Server button.
   - Stopped: an info banner ("The EFB server is stopped. Start it to pair a device.") + a Start
     EFB Server button (busy state: "Starting…" while `_efbBusy`).
   - Both buttons disabled while `_efbBusy`; `StartEfbAsync`/`StopEfbAsync` call
     `EfbServer.StartAsync()`/`StopAsync()` and toggle `_efbBusy` around the call.
   - Subscribe to `EfbServer.StatusChanged` (marshaled via `DispatcherQueue.TryEnqueue`,
     unsubscribed on `Unloaded` — same pattern as `UpdateService.DownloadProgressChanged` above
     and Messages' `MessagingService` subscriptions) to invalidate the cached QR (URL/port may
     have changed) and rebuild this panel.
   - Enable EFB Companion toggle (`Settings.EfbEnabled`, saves immediately — and if turned off
     while the server is running, calls `StopEfbAsync()`, matching `OnEfbEnabledChanged` exactly)
     and Start Automatically toggle (`Settings.EfbAutoStart`, plain deferred-to-Save bind, no
     immediate side effect, matching the original).
3. Build — 0 errors, compare warnings against baseline.
4. Commit: `feat(settings): About/Updates and EFB Companion panels`.

## Task 6 — Save/Reset row, nav wiring, final verification

Files: `AviatesAirTracker/Pages/SettingsPage.xaml`/`.xaml.cs` (modified),
`AviatesAirTracker/Navigation/NavDestinations.cs` (modified).

1. **Save/Reset row**: a Save `Border`-button (primary style) calling
   `SettingsService.RefreshFriendCode()`, Task 1's `UpdateNameDisplay()`, `SettingsService.Save()`,
   then a success status ("Settings saved successfully."); a Reset `Border`-button (secondary
   style) calling `SettingsService.Reset()`, clearing `_isVerified`/`_verifiedEmail`, re-running
   `UpdateNameDisplay()`, showing a "Settings reset to defaults." status — and, critically,
   **re-syncing every panel's displayed state** from the now-reset `Settings` object (theme card
   active-state, all toggles, all text fields, the Friend Code panel, the Dispatch Radio panel's
   hotkey/joystick display) — a full page rebuild after Reset is the simplest way to guarantee
   this, don't try to patch individual controls piecemeal and risk missing one.
2. **Nav wiring**: `AviatesAirTracker/Navigation/NavDestinations.cs`'s `"settings"` entry gets
   `NativePageType: typeof(Pages.SettingsPage)` — one line, matching every prior page's pattern.
3. Build — 0 errors, compare warnings against baseline (15 pre-existing, none referencing
   Settings/NavDestinations) and flag anything new.
4. Verification pass (read-only code review, no computer-use/live-app testing — that's reserved
   for the orchestrating session): re-read the full diff across all 5 prior tasks against the
   design spec. Confirm: the theme picker persists but doesn't attempt to re-theme the shell; the
   microphone/speaker selectors enumerate real native devices and save the choice but don't call
   anything JS-shaped; the QR code renders via the module-grid technique, not an SVG hack; the
   Hoppies category toggles' dim/disable state and the App Preferences panel's 3-deferred-vs-1-
   immediate save distinction are both correct; `EfbServer.StatusChanged`/
   `UpdateService.DownloadProgressChanged` subscriptions are unsubscribed on `Unloaded`. Note
   explicitly which functional checks need a working backend connection, a configured ACARS key,
   or real hardware (joystick, audio devices) that may not be available in this environment —
   defer those explicitly rather than skipping silently or claiming they passed.
5. Commit: `feat(settings): Save/Reset row and native SettingsPage navigation wiring`.

## Verification (after all six tasks land)

- Build clean, no new warnings.
- Visual: compare native Settings against the current Blazor page, section by section.
- Functional: theme picker persists (doesn't re-theme, as designed); ACARS sync populates name/
  SimBrief/PilotId and regenerates the friend code (needs backend + valid key); Hoppies toggle
  cascade; PTT hotkey capture/clear; joystick enumeration + bind (needs a connected joystick);
  native mic/speaker enumeration (needs real audio devices) + persisted selection; friend code
  generation/copy; update check (needs network) + install progress; EFB start/stop + QR render +
  pairing code; Save persists everything; Reset clears everything and the whole page reflects it.
