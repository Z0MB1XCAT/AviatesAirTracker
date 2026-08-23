# WinUI 3 Phase 2 — Shell Visual Restoration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `ShellWindow`'s title bar and `NavigationView` rail visually match the Night Flight design system in `wwwroot/css/app.css`, and wire three of its five nav badges to real data.

**Architecture:** Two small XAML resource dictionaries (`Theme/Tokens.xaml` for colors, `Theme/Typography.xaml` for bundled fonts) merged into `App.xaml`, applied directly to `ShellWindow.xaml`'s existing controls. No new windows, no new pages, no MVVM infrastructure — `ShellWindow.xaml.cs` stays the same imperative code-behind style Tasks 10–12 already established, just with three new event subscriptions driving `NavigationViewItem.InfoBadge` values.

**Tech Stack:** WinUI 3 XAML resource dictionaries, `Microsoft.UI.Xaml.Controls.InfoBadge`, TTF fonts bundled as `Content`.

**Spec:** [`docs/superpowers/specs/2026-08-23-winui3-phase2-shell-visual-design.md`](../specs/2026-08-23-winui3-phase2-shell-visual-design.md)

---

## Scope boundary — read before starting any task

This plan touches **only** `ShellWindow.xaml`, `ShellWindow.xaml.cs`, `App.xaml`, and the new `Theme/`/`Assets/Fonts/` files. It does not touch any `.razor` file, `_Host.cshtml`, navigation logic, the login gate, or shutdown — all of that is done and stays untouched. It does not build `Cards.xaml`, `Elevation`, a light theme, or bundle `JetBrains Mono`/`VT323` — the spec explicitly rules all four out as infrastructure with no current consumer. If a task here seems to need one of those, stop and say so rather than building it.

---

## File Structure

### Created

| File | Responsibility |
|---|---|
| `AviatesAirTracker/Assets/Fonts/Syne-Bold.ttf` | Title bar text |
| `AviatesAirTracker/Assets/Fonts/PlusJakartaSans-Medium.ttf` | Nav item labels |
| `AviatesAirTracker/Assets/Fonts/PlusJakartaSans-Bold.ttf` | Section headers, badge text |
| `AviatesAirTracker/Assets/AppIconSmall.png` | Title bar icon tile (64×64, extracted from the existing `.ico`) |
| `AviatesAirTracker/Theme/Tokens.xaml` | Color brushes ported from `app.css`'s `:root` |
| `AviatesAirTracker/Theme/Typography.xaml` | `FontFamily` resources for the two bundled fonts |
| `tests/AviatesAirTracker.Tests/ShellWindowBadgeLogicTests.cs` | Unit tests for the two derived-badge conditions (unsynced-PIREP count, flight-live state) — the only genuinely unit-testable logic this plan adds |

### Modified

| File | Change |
|---|---|
| `AviatesAirTracker/App.xaml` | Merge `Theme/Tokens.xaml` and `Theme/Typography.xaml` into `Application.Resources` |
| `AviatesAirTracker/AviatesAirTracker.csproj` | `Content` items for the font files and the icon PNG, `CopyToOutputDirectory` |
| `AviatesAirTracker/ShellWindow.xaml` | Sidebar background, title bar icon+text, `NavigationViewItem`/`NavigationViewItemHeader` styles, `InfoBadge` bindings |
| `AviatesAirTracker/ShellWindow.xaml.cs` | Subscribe to `FlightSessionManager.SessionStateChanged` and `MessagingService.UnreadCountChanged`; compute the unsynced-PIREP count; push all three into the corresponding `InfoBadge` |

---

## Conventions for every task

- **Build:** `dotnet build AviatesAirTracker.sln -c Debug`
- **Test:** `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
- Baseline going into this plan: **0 errors, 10 distinct pre-existing warnings** (`CS0169`×1, `CS0414`×4, `MVVMTK0034`×5). A task is clean if it does not add to that baseline.
- **Do not let the app record a flight during manual verification.** Check `tasklist` for MSFS/`FlightSimulator*` before every launch; if it's running, do not let a flight session start.
- Commit after every task. The app must remain runnable after each one — this plan has no equivalent of Phase 0–1's "intentionally broken" stretch.

---

## Task 1: Font and icon assets

**Files:**
- Create: `AviatesAirTracker/Assets/Fonts/Syne-Bold.ttf`, `AviatesAirTracker/Assets/Fonts/PlusJakartaSans-Medium.ttf`, `AviatesAirTracker/Assets/Fonts/PlusJakartaSans-Bold.ttf`
- Create: `AviatesAirTracker/Assets/AppIconSmall.png`
- Modify: `AviatesAirTracker/AviatesAirTracker.csproj`

- [ ] **Step 1: Download the three font files**

These exact URLs were fetched and verified live during planning — `curl` with a browser `User-Agent` against Google Fonts' `css2` endpoint returns genuine `.ttf` files (not WOFF2), confirmed with `file` on the downloaded bytes (`TrueType Font data, 16 tables`). Use these URLs directly rather than re-deriving them:

```bash
mkdir -p AviatesAirTracker/Assets/Fonts

curl -s -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" \
  "https://fonts.gstatic.com/s/syne/v24/8vIS7w4qzmVxsWxjBZRjr0FKM_3fvj6k.ttf" \
  -o AviatesAirTracker/Assets/Fonts/Syne-Bold.ttf

curl -s -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" \
  "https://fonts.gstatic.com/s/plusjakartasans/v12/LDIbaomQNQcsA88c7O9yZ4KMCoOg4IA6-91aHEjcWuA_m07NSg.ttf" \
  -o AviatesAirTracker/Assets/Fonts/PlusJakartaSans-Medium.ttf

curl -s -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" \
  "https://fonts.gstatic.com/s/plusjakartasans/v12/LDIbaomQNQcsA88c7O9yZ4KMCoOg4IA6-91aHEjcWuA_TknNSg.ttf" \
  -o AviatesAirTracker/Assets/Fonts/PlusJakartaSans-Bold.ttf
```

Verify each is a real font file, not an HTML error page (a broken/rate-limited fetch returns HTML, not TTF bytes — check before trusting the download):

```bash
file AviatesAirTracker/Assets/Fonts/Syne-Bold.ttf
file AviatesAirTracker/Assets/Fonts/PlusJakartaSans-Medium.ttf
file AviatesAirTracker/Assets/Fonts/PlusJakartaSans-Bold.ttf
```

Expected: all three report `TrueType Font data`. If any URL has gone stale (Google Fonts URLs are versioned and can change), fetch the current one from `https://fonts.googleapis.com/css2?family=Syne:wght@700&family=Plus+Jakarta+Sans:wght@500;700&display=swap` with the same `User-Agent` header and use whatever `src: url(...)` it returns instead of guessing.

- [ ] **Step 2: Extract the icon as a PNG**

`Resources/Assets/aviates_icon.ico` is only usable as a Win32 application icon (`ApplicationManifest`) and as an Inno Setup wizard icon today — WinUI's `Image` control needs a standard raster format. Extract it:

```bash
python3 -c "
from PIL import Image
im = Image.open('AviatesAirTracker/Resources/Assets/aviates_icon.ico')
im.save('AviatesAirTracker/Assets/AppIconSmall.png')
print(im.size, im.mode)
"
```

Expected output: `(64, 64) RGBA`. If Pillow isn't available, any equivalent ICO→PNG extraction is fine — the requirement is a 64×64 RGBA PNG with the same visual content as the existing `.ico`, not a specific tool.

**Do not modify or delete `Resources/Assets/aviates_icon.ico`.** It's still the app's real Win32 icon and the installer's wizard icon — this step only adds a second, WinUI-consumable copy of the same artwork.

- [ ] **Step 3: Add the assets to the csproj**

In `AviatesAirTracker/AviatesAirTracker.csproj`, add a new `ItemGroup`:

```xml
<ItemGroup>
  <Content Include="Assets\Fonts\*.ttf" CopyToOutputDirectory="PreserveNewest" />
  <Content Include="Assets\AppIconSmall.png" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 4: Build and confirm the files land in the output directory**

```bash
dotnet build AviatesAirTracker.sln -c Debug
```

Expected: 0 errors, no new warnings beyond the 10-warning baseline.

```bash
ls AviatesAirTracker/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Assets/Fonts/
ls AviatesAirTracker/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Assets/AppIconSmall.png
```

Expected: all four files present.

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/Assets AviatesAirTracker/AviatesAirTracker.csproj
git commit -m "feat(shell): bundle Syne/Plus Jakarta Sans fonts and a WinUI-usable app icon

Only the two font families and weights the shell chrome actually uses --
JetBrains Mono and VT323 have no consumer here, see the Phase 2 spec's
scope boundary. aviates_icon.ico is extracted to a 64x64 PNG because
WinUI's Image control needs a standard raster format; the .ico stays as
the app's real Win32/installer icon, untouched."
```

---

## Task 2: `Theme/Tokens.xaml` — color resources

**Files:**
- Create: `AviatesAirTracker/Theme/Tokens.xaml`
- Modify: `AviatesAirTracker/App.xaml`

- [ ] **Step 1: Create the token dictionary**

These are direct ports of `app.css`'s `:root` block, dark-theme values only (per the spec: no light theme this phase — the app has no reachable toggle to a native light palette yet). Read `AviatesAirTracker/wwwroot/css/app.css` lines 10–130 yourself and confirm these values before using them; they are transcribed here from that file as read during planning, but re-verify rather than trust blindly, since the CSS could have shifted since.

Create `AviatesAirTracker/Theme/Tokens.xaml`:

```xml
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!--
      Direct port of wwwroot/css/app.css's :root block (dark-theme values — the app
      defaults to dark per _Host.cshtml's anti-flash script, and there is no reachable
      light-theme toggle in the native shell yet). Colors only; only what ShellWindow's
      title bar and NavigationView rail actually consume.
    -->

    <Color x:Key="SidebarBackgroundColor">#0B1628</Color>
    <SolidColorBrush x:Key="SidebarBackgroundBrush" Color="{StaticResource SidebarBackgroundColor}" />

    <Color x:Key="SidebarHoverColor">#FFFFFF</Color>
    <SolidColorBrush x:Key="SidebarHoverBrush" Color="{StaticResource SidebarHoverColor}" Opacity="0.07" />

    <Color x:Key="SidebarActiveColor">#4D9BEC</Color>
    <SolidColorBrush x:Key="SidebarActiveBrush" Color="{StaticResource SidebarActiveColor}" Opacity="0.16" />

    <SolidColorBrush x:Key="SidebarTextBrush" Color="#FFFFFF" Opacity="0.52" />
    <SolidColorBrush x:Key="SidebarTextActiveBrush" Color="#FFFFFF" Opacity="1.0" />
    <SolidColorBrush x:Key="SidebarLabelBrush" Color="#FFFFFF" Opacity="0.28" />

    <Color x:Key="AccentColor">#4D9BEC</Color>
    <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}" />

    <!-- app.css's .nav-item.active::after glow: box-shadow: 0 0 8px rgba(77,155,236,0.6) -->
    <SolidColorBrush x:Key="AccentGlowBrush" Color="{StaticResource AccentColor}" Opacity="0.6" />

</ResourceDictionary>
```

- [ ] **Step 2: Merge it into `App.xaml`**

Current `App.xaml`:
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

Add `Theme/Tokens.xaml` as a second merged dictionary, **after** `XamlControlsResources` so its keys aren't shadowed:

```xml
<Application
    x:Class="AviatesAirTracker.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
                <ResourceDictionary Source="Theme/Tokens.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 3: Build**

```bash
dotnet build AviatesAirTracker.sln -c Debug
```

Expected: 0 errors, baseline warnings only. A malformed resource dictionary or a duplicate key fails the build loudly at this point — good, that's the point of building before wiring anything up to consume it.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Theme/Tokens.xaml AviatesAirTracker/App.xaml
git commit -m "feat(shell): add Theme/Tokens.xaml, a direct port of app.css's color tokens"
```

---

## Task 3: `Theme/Typography.xaml` — font resources

**Files:**
- Create: `AviatesAirTracker/Theme/Typography.xaml`
- Modify: `AviatesAirTracker/App.xaml`

- [ ] **Step 1: Create the typography dictionary**

WinUI's `FontFamily` URI syntax for a bundled font is `/Assets/Fonts/File.ttf#Actual Font Name`. The `#`-suffix must match the font's real internal family name, not the filename — for these three files that's `Syne` and `Plus Jakarta Sans` respectively (both fonts' internal names, confirmed by the `file`/font-metadata tooling used in Task 1, or by opening the `.ttf` in any font viewer if you want a second check).

Create `AviatesAirTracker/Theme/Typography.xaml`:

```xml
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!--
      Only Syne and Plus Jakarta Sans — JetBrains Mono and VT323 have no consumer in the
      native shell (see the Phase 2 spec's scope boundary). Weight is baked into which .ttf
      file is referenced, not set separately, because each file IS that one weight — there
      is no variable-font axis here.
    -->

    <FontFamily x:Key="DisplayFontBold">/Assets/Fonts/Syne-Bold.ttf#Syne</FontFamily>
    <FontFamily x:Key="BodyFontMedium">/Assets/Fonts/PlusJakartaSans-Medium.ttf#Plus Jakarta Sans</FontFamily>
    <FontFamily x:Key="BodyFontBold">/Assets/Fonts/PlusJakartaSans-Bold.ttf#Plus Jakarta Sans</FontFamily>

</ResourceDictionary>
```

- [ ] **Step 2: Merge it into `App.xaml`**, alongside `Tokens.xaml`:

```xml
<ResourceDictionary.MergedDictionaries>
    <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
    <ResourceDictionary Source="Theme/Tokens.xaml" />
    <ResourceDictionary Source="Theme/Typography.xaml" />
</ResourceDictionary.MergedDictionaries>
```

- [ ] **Step 3: Build and manually confirm the font actually loads**

```bash
dotnet build AviatesAirTracker.sln -c Debug
```

A malformed `FontFamily` URI or a wrong internal name does **not** fail the build — WinUI silently falls back to a default font at runtime instead. This must be checked by actually running the app once `ShellWindow.xaml` references these resources (Tasks 4–5), not assumed to work because the build succeeded. Flag this explicitly in your Task 4/5 verification rather than treating a clean build as proof.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Theme/Typography.xaml AviatesAirTracker/App.xaml
git commit -m "feat(shell): add Theme/Typography.xaml for the two bundled fonts"
```

---

## Task 4: Title bar — icon, text, transparent Mica strip

**Files:**
- Modify: `AviatesAirTracker/ShellWindow.xaml`

- [ ] **Step 1: Read the current title bar markup**

Current `ShellWindow.xaml`'s title bar section (confirm this matches before editing — it may have shifted since planning):

```xml
<Border x:Name="AppTitleBar" Grid.Row="0" Background="Transparent">
    <TextBlock
        Text="Aviates Air Flight Tracker"
        VerticalAlignment="Center"
        Margin="16,0,0,0"
        FontSize="12"
        Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
</Border>
```

- [ ] **Step 2: Replace the content with icon + styled text**

The `Background="Transparent"` stays exactly as it is — that's the deliberate decision from the spec, not something to "fix". Only the content inside changes:

```xml
<Border x:Name="AppTitleBar" Grid.Row="0" Background="Transparent">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center" Margin="16,0,0,0" Spacing="9">
        <Image Source="/Assets/AppIconSmall.png" Width="18" Height="18" />
        <TextBlock
            Text="Aviates Air Flight Tracker"
            VerticalAlignment="Center"
            FontFamily="{StaticResource DisplayFontBold}"
            FontSize="12.5"
            Foreground="#B8FFFFFF" />
    </StackPanel>
</Border>
```

`Foreground="#B8FFFFFF"` is `rgba(255,255,255,0.72)` expressed as WinUI's `#AARRGGBB` hex (`0.72 × 255 ≈ 184 = 0xB8`) — matching the spec's `rgba(255,255,255,0.72)` value exactly rather than approximating with a named brush.

- [ ] **Step 3: Build and run — this is where the font resource actually gets exercised for the first time**

```bash
dotnet build AviatesAirTracker.sln -c Debug
```

Check `tasklist` for MSFS before launching. Then:

```bash
.\build.ps1 -Run
```

Look at the title bar specifically. Expected: the app icon tile at the left, then "Aviates Air Flight Tracker" in a visibly different typeface from the WinUI default (Syne is a distinctive geometric/display face — if the text looks like the default Segoe UI, the `FontFamily` resource did not resolve and Step 1 of Task 3 needs re-checking, most likely the `#`-suffix family name). Confirm the title bar strip shows a hint of the desktop/window content behind it through the Mica backdrop — if it's fully opaque black or fully see-through with no tint at all, `ConfigureBackdrop()`'s `MicaBackdrop` may not be applying; that method is untouched by this plan, so a regression there is unexpected and worth flagging rather than silently working around.

If you cannot get a visual screenshot in this environment, say so plainly and describe what you verified instead (build success, no runtime exceptions in the log, `Assets/AppIconSmall.png` and the font files present in the output directory).

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/ShellWindow.xaml
git commit -m "feat(shell): brand the title bar with the app icon and Syne-set text"
```

---

## Task 5: Sidebar — flat navy, nav item and section header styling

**Files:**
- Modify: `AviatesAirTracker/ShellWindow.xaml`

- [ ] **Step 1: Set the flat navy background**

In `ShellWindow.xaml`, the `NavigationView` currently has no explicit background set (it inherits WinUI's default). Add:

```xml
<controls:NavigationView
    x:Name="Nav"
    Grid.Row="1"
    PaneDisplayMode="Left"
    IsPaneVisible="False"
    IsBackButtonVisible="Collapsed"
    IsSettingsVisible="False"
    PaneBackground="{StaticResource SidebarBackgroundBrush}"
    SelectionChanged="OnNavSelectionChanged"
    BackRequested="OnBackRequested">
```

Only the `PaneBackground="{StaticResource SidebarBackgroundBrush}"` line is new — `IsPaneVisible="False"` and `IsBackButtonVisible="Collapsed"` are already there from Task 11's login gate and must not change.

- [ ] **Step 2: Style `NavigationViewItem` and `NavigationViewItemHeader`**

Add these two implicit styles inside the `Window.Resources` (create a `<Window.Resources>` block if `ShellWindow.xaml` doesn't already have one — check first) so they apply to every item without per-item styling:

```xml
<Window.Resources>
    <Style TargetType="controls:NavigationViewItem">
        <Setter Property="FontFamily" Value="{StaticResource BodyFontMedium}" />
        <Setter Property="FontSize" Value="13.5" />
        <Setter Property="Foreground" Value="{StaticResource SidebarTextBrush}" />
        <Setter Property="Margin" Value="10,2" />
        <Setter Property="CornerRadius" Value="12" />
    </Style>

    <Style TargetType="controls:NavigationViewItemHeader">
        <Setter Property="FontFamily" Value="{StaticResource BodyFontBold}" />
        <Setter Property="FontSize" Value="9" />
        <Setter Property="Foreground" Value="{StaticResource SidebarLabelBrush}" />
        <Setter Property="Margin" Value="20,16,20,8" />
    </Style>
</Window.Resources>
```

`app.css`'s `.nav-section-label` uses `letter-spacing: 0.12em` and `text-transform: uppercase`. XAML `TextBlock`/`NavigationViewItemHeader` has no direct `letter-spacing` equivalent property pre-WinUI-1.5-ish — **do not attempt to fake letter-spacing with padded Unicode characters or per-character spacing hacks.** If `CharacterSpacing` (the WinUI property, measured in 1/1000 em, so `0.12em` ≈ `120`) is available on the Windows App SDK version this project targets (1.8.250907003 — check the actual API surface rather than assuming), set `CharacterSpacing="120"` on both styles above. If it is not available on this SDK version, leave letter-spacing off entirely and note it in your report — it's a minor visual delta, not worth blocking the task over, and should not be faked. For uppercase, either the section names can be typed in caps directly in `NavDestinations.cs` (do NOT do this — it changes a shared data file for a purely visual concern) or `NavigationViewItemHeader`'s content can be set via a converter/direct uppercase string at the point it's constructed in `BuildNavItems()` — prefer the latter if `CharacterSpacing` works out, since it keeps `NavDestinations.cs` as plain-cased source data.

**The active-item accent bar** (`app.css`'s `.nav-item.active::after` — a 3px-wide, 18px-tall bar with an accent glow, positioned at the item's leading edge) has no direct `NavigationViewItem` style equivalent; WinUI's own default selection indicator is visually different (a pill/underline depending on `PaneDisplayMode`). Do not attempt to fully replicate the exact `::after` pseudo-element positioning in this task — that's a level of custom `ControlTemplate` work disproportionate to what's been scoped. Instead, set the selected-state background to `{StaticResource SidebarActiveBrush}` and the selected-state foreground to `{StaticResource SidebarTextActiveBrush}` via the style's `VisualStateManager` overrides if straightforward, or accept WinUI's default selection visual with the correct colors if a full custom template turns out to be non-trivial. **Report which of these two you landed on** — this is a real, acknowledged fidelity gap either way, and the plan is not asking you to solve it silently.

- [ ] **Step 3: Build and run**

```bash
dotnet build AviatesAirTracker.sln -c Debug
```

Check `tasklist` for MSFS, then `.\build.ps1 -Run`. Compare against the real Blazor sidebar (reachable by navigating the `WebView2` to any route without `?shell=native`, or simply looking at the app before this plan's changes via `git stash`/a side-by-side if that's easier). Confirm: navy background, correct-looking fonts on item labels and section headers, hover state visibly lighter than the base navy when you mouse over an item, no crash.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/ShellWindow.xaml
git commit -m "feat(shell): style the nav rail to match app.css's sidebar treatment"
```

---

## Task 6: Live badges — Flights, ACARS, Map, Messages

**Files:**
- Modify: `AviatesAirTracker/ShellWindow.xaml.cs`
- Test: `tests/AviatesAirTracker.Tests/ShellWindowBadgeLogicTests.cs`

- [ ] **Step 1: Extract the two derived-badge conditions as pure, testable functions first**

Rather than burying the unsynced-PIREP count and the flight-live check inside event handlers where they can't be unit tested, write them as small static methods first, following this repo's established TDD pattern from Phase 0–1 (Task 1's `ImmediateUiDispatcherTests`, Task 2's `WindowSubclassTests`).

Add to `AviatesAirTracker/ShellWindow.xaml.cs` (or a new small internal static class in the same file if that reads better — your call, keep it simple):

```csharp
internal static class ShellBadgeLogic
{
    /// <summary>
    /// Mirrors FlightSessionManager.cs:470's existing convention — Idle and Complete are
    /// both "nothing active" for every other purpose in this codebase, so the ACARS/Map
    /// "Live" badge follows that same rule rather than inventing a new one.
    /// </summary>
    public static bool IsFlightLive(FlightSessionState state) =>
        state is not (FlightSessionState.Idle or FlightSessionState.Complete);

    /// <summary>
    /// The Flights nav-rail dot: true when at least one completed flight hasn't synced to
    /// the backend yet. Same predicate as App.xaml.cs's RetryPendingPirepsAsync — reused,
    /// not reinvented.
    /// </summary>
    public static bool HasUnsyncedPireps(IEnumerable<FlightRecord> flights) =>
        flights.Any(f => !f.SyncedToBackend && f.Status == FlightStatus.Completed);
}
```

Confirm `FlightRecord`, `FlightStatus` and `FlightSessionState`'s actual namespaces before adding usings — they were referenced as `AviatesAirTracker.Models`/`AviatesAirTracker.Services` elsewhere in this codebase during Phase 0–1, but verify against the current file rather than assume.

- [ ] **Step 2: Write the failing tests**

Create `tests/AviatesAirTracker.Tests/ShellWindowBadgeLogicTests.cs`. This test file needs `ShellWindow.xaml.cs`'s `ShellBadgeLogic` class, `FlightRecord`/`FlightStatus`/`FlightSessionState` — check whether these compile cleanly under the test project's plain `net8.0` target (no WinUI/WPF dependency) the same way `Platform/IUiDispatcher.cs` etc. do via `<Compile Include>` file-links. If `ShellBadgeLogic` references any WinUI type transitively, it will **not** link cleanly into the test project — if that happens, move `ShellBadgeLogic` to a new `AviatesAirTracker/Shell/ShellBadgeLogic.cs` file with no WinUI usings at all (just the plain model/service types), and link **that** file instead. Do not skip writing these tests because of this friction — resolve it, don't route around it.

```csharp
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using Xunit;

namespace AviatesAirTracker.Tests;

public class ShellBadgeLogicTests
{
    [Theory]
    [InlineData(FlightSessionState.Idle, false)]
    [InlineData(FlightSessionState.Complete, false)]
    [InlineData(FlightSessionState.PreFlight, true)]
    [InlineData(FlightSessionState.Taxiing, true)]
    [InlineData(FlightSessionState.Airborne, true)]
    [InlineData(FlightSessionState.OnApproach, true)]
    [InlineData(FlightSessionState.Landed, true)]
    public void IsFlightLive_MatchesFlightSessionManagersOwnIdleOrCompleteConvention(
        FlightSessionState state, bool expectedLive)
    {
        Assert.Equal(expectedLive, ShellBadgeLogic.IsFlightLive(state));
    }

    [Fact]
    public void HasUnsyncedPireps_TrueWhenACompletedFlightIsNotSynced()
    {
        var flights = new[]
        {
            new FlightRecord { Status = FlightStatus.Completed, SyncedToBackend = false },
        };

        Assert.True(ShellBadgeLogic.HasUnsyncedPireps(flights));
    }

    [Fact]
    public void HasUnsyncedPireps_FalseWhenEverythingIsSynced()
    {
        var flights = new[]
        {
            new FlightRecord { Status = FlightStatus.Completed, SyncedToBackend = true },
        };

        Assert.False(ShellBadgeLogic.HasUnsyncedPireps(flights));
    }

    [Fact]
    public void HasUnsyncedPireps_IgnoresUnsyncedFlightsThatArentCompletedYet()
    {
        // An in-progress flight is trivially "not synced" — that must not count. Only a
        // completed-but-unsynced flight should show the dot.
        var flights = new[]
        {
            new FlightRecord { Status = FlightStatus.InProgress, SyncedToBackend = false },
        };

        Assert.False(ShellBadgeLogic.HasUnsyncedPireps(flights));
    }

    [Fact]
    public void HasUnsyncedPireps_FalseOnEmptyHistory()
    {
        Assert.False(ShellBadgeLogic.HasUnsyncedPireps(Array.Empty<FlightRecord>()));
    }
}
```

Check the actual `FlightStatus` enum for the correct "in progress" member name before using it — it may not be literally `InProgress`; read `Models/FlightModels.cs` (or wherever it's actually defined) rather than guessing.

- [ ] **Step 3: Run and confirm the tests fail**

```bash
dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj
```

Expected: compile failure or test failures referencing `ShellBadgeLogic` — confirms the tests are exercising real, not-yet-written logic.

- [ ] **Step 4: Confirm the tests pass**

```bash
dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj
```

Expected: all `ShellBadgeLogicTests` pass, plus the pre-existing 11.

- [ ] **Step 5: Wire the three live badges in `ShellWindow.xaml.cs`**

Add `FlightSessionManager`, `MessagingService`, and `IFlightRepository` as constructor parameters (all already DI-registered singletons — no `App.xaml.cs` registration change needed). Store references to the `NavigationViewItem`s that need live badges — `BuildNavItems()` currently discards them after adding to `Nav.MenuItems`; change it to also keep named references (e.g. `private NavigationViewItem? _flightsItem, _acarsItem, _mapItem, _messagesItem;`, assigned by matching `destination.Tag` during the existing loop).

Subscribe in the constructor or `OnFirstActivated` (constructor is fine — these events don't need a window handle, unlike `SetWindowHandle`):

```csharp
_sessionManager.SessionStateChanged += (_, state) =>
{
    var isLive = ShellBadgeLogic.IsFlightLive(state);
    _acarsItem!.InfoBadge = isLive ? new InfoBadge { Value = 0, Style = LiveBadgeStyle() } : null;
    _mapItem!.InfoBadge = isLive ? new InfoBadge { Value = 0, Style = LiveBadgeStyle() } : null;
};

_messagingService.UnreadCountChanged += (_, count) =>
{
    _messagesItem!.InfoBadge = count > 0
        ? new InfoBadge { Value = count > 99 ? 99 : count }
        : null;
};
```

The "Live" pill is text, not a number — `InfoBadge.Value` is numeric-only in the base control. Set `InfoBadge`'s content via a custom `Style`/template carrying a `TextBlock` reading "Live", or fall back to a plain `TextBlock`/`Border` positioned where the badge would go if templating `InfoBadge` for text content turns out to be more involved than this task warrants. **Report which approach you took** — this is exactly the "unverified assumption" the spec flagged about `InfoBadge`'s dot-style support; resolve it here and record what you found, don't guess further upstream.

For the Flights dot, at startup (not on every event — read once, and again on `FlightSessionManager.FlightCompleted` since that's when sync state can change) query `IFlightRepository.GetAllAsync()` and apply `ShellBadgeLogic.HasUnsyncedPireps`:

```csharp
private async Task RefreshFlightsBadgeAsync()
{
    var flights = await _flightRepository.GetAllAsync();
    _flightsItem!.InfoBadge = ShellBadgeLogic.HasUnsyncedPireps(flights)
        ? new InfoBadge { /* dot style, no value */ }
        : null;
}
```

Call this once during startup and subscribe it to whatever event fires when a PIREP sync completes (check `AviatesBackendClient`/`FlightSessionManager` for the right hook — `App.xaml.cs`'s `RetryPendingPirepsAsync` is the existing consumer of this same data and is a reasonable place to look for what event, if any, signals "sync state changed").

- [ ] **Step 6: Build and run**

```bash
dotnet build AviatesAirTracker.sln -c Debug
```

Check `tasklist` for MSFS, then `.\build.ps1 -Run`. Confirm no crash on startup (a null `_flightsItem`/`_acarsItem`/etc. from a tag-matching bug would null-ref the moment an event fires). If MSFS happens to be available for a real test, starting a flight should flip the ACARS/Map badges; do not force this check if it means recording an unwanted flight — a static code read of the wiring plus a clean startup is sufficient verification for this task if live testing isn't safe to do right now.

- [ ] **Step 7: Commit**

```bash
git add AviatesAirTracker/ShellWindow.xaml.cs tests/AviatesAirTracker.Tests/
git commit -m "feat(shell): wire the Flights, ACARS, Map and Messages nav badges to real data

News stays a static \"3\", matching current behavior -- it has no backing
data model and building one is out of scope for a shell reskin, per the
Phase 2 spec."
```

---

## Task 7: Final verification

No code. This is the task that decides whether Phase 2 succeeded.

- [ ] **Step 1: Confirm the automated suite**

```bash
dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj
```

Expected: 11 pre-existing + however many `ShellBadgeLogicTests` Task 6 added, all passing.

- [ ] **Step 2: Confirm a clean build with no new warnings**

```bash
dotnet build AviatesAirTracker.sln -c Debug
```

Expected: 0 errors, still the 10-warning baseline (`CS0169`×1, `CS0414`×4, `MVVMTK0034`×5) — nothing this plan added should introduce a new warning code.

- [ ] **Step 3: Visual comparison**

Check `tasklist` for MSFS before launching. Run the app, and compare the native shell against the Blazor sidebar side by side (navigate the `WebView2` to a route without `?shell=native` for the comparison, or reference the earlier approved mockup artifact). Confirm: sidebar is the correct flat navy, title bar shows the icon and Syne-set text with a visible Mica tint, section headers and item labels use the correct fonts, hover/active states work.

- [ ] **Step 4: Functional badge check, if it's safe to do**

If MSFS is available and it's safe to fly without creating an unwanted flight record, confirm the ACARS/Map "Live" badges appear once a flight session goes active and clear once it ends. Otherwise, this is deferred to whenever the next live-sim session happens — note that explicitly rather than skipping the check silently.

- [ ] **Step 5: Record results and commit**

Append a short results section to this plan (pass/fail per step above, any deviations found — e.g. which `CharacterSpacing`/`InfoBadge` fallback got used) and commit:

```bash
git add docs/superpowers/plans/2026-08-23-winui3-phase2-shell-visual.md
git commit -m "docs(plan): record Phase 2 shell visual verification results"
```

---

## Definition of done

- `dotnet test` passes, including the new badge-logic tests
- The shell's title bar and sidebar are visually indistinguishable from the approved mockup and the real Blazor sidebar, modulo the two acknowledged, reported fidelity gaps (the active-item accent bar's exact shape; letter-spacing, if `CharacterSpacing` isn't available on this SDK)
- Flights, ACARS, Map and Messages badges reflect real state; News stays static
- No `.razor` file, navigation logic, login gate, or shutdown code was touched
- The tree is committed and the app is usable after every single task
