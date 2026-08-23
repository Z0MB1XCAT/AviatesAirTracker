# WinUI 3 Migration — Phase 2: Shell Visual Restoration

**Date:** 2026-08-23
**Status:** Approved, ready for implementation planning
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** `2026-08-13-winui3-migration-design.md` (Phases 0–1, complete) and
`2026-08-13-winui3-foundation-and-bridge.md` (the 13-task implementation plan, complete)

## Goal

`ShellWindow` currently renders with default WinUI styling — no brand colors, no custom
fonts, a bare title bar, an unstyled `NavigationView` rail. This phase makes it visually
match the Night Flight design system in `wwwroot/css/app.css`, so a pilot who used the
Blazor Hybrid app yesterday does not perceive a redesign.

**Scope boundary, stated precisely:** Phase 2 styles exactly what exists as native XAML
today — `ShellWindow`'s title bar and `NavigationView` rail. Every page destination still
renders through the Blazor Server bridge inside the `WebView2`, and that content already
has the correct Night Flight look via `app.css`. Phase 2 does not touch a single `.razor`
file. Card, stat-tile and `Elevation`-shadow infrastructure is explicitly **not** built in
this phase — nothing in `ShellWindow` needs it yet, and building it speculatively ahead of
Phase 3 (native pages) would be building against a consumer that doesn't exist. Phase 3
adds `Cards.xaml` and the `Elevation` attached property when it has an actual card to style.

## Decisions, made against real mockups using real assets

Three visual questions were resolved by publishing an artifact using this app's actual
token values (`--sb-bg`, `--accent`, font stack) and actual asset files (`aviates_icon.ico`,
the cropped wordmark), not placeholders.

### 1. Sidebar: flat opaque navy, not translucent Mica

The original Phase 0–1 spec listed "the sidebar becomes translucent navy over Mica" as
one of three explicitly pre-approved visual deviations. **That approval is withdrawn.**
The sidebar is a **byte-exact match** to `--sb-bg: #0B1628` — a plain, fully opaque
`SolidColorBrush`, not a semi-transparent brush over the window's backdrop. No approved
visual deviation applies to the sidebar; it is held to the same "must not perceive a
redesign" bar as everything else.

### 2. Title bar: transparent background, icon + text

- `ExtendsContentIntoTitleBar` stays as Task 10 built it, and the title bar `Border`'s
  background stays **transparent** — deliberately, not by omission. Because the sidebar
  (below the title bar) and the `WebView2` content area are both fully opaque, the title
  bar strip is the **only** surface in the entire window where the `MicaBackdrop` set in
  `ConfigureBackdrop()` is ever visible. Keeping it transparent is what gives that Mica
  code a purpose; making it opaque would leave `SystemBackdrop = new MicaBackdrop(...)`
  doing nothing, anywhere, ever. This is the Windows 11 File Explorer / Settings pattern —
  a translucent title bar strip with content floating on top of it.
- Content **on top of** that transparent strip: the `aviates_icon.ico` app icon (already
  shipped, already referenced in `AviatesAirTracker.csproj`'s `ApplicationManifest` and the
  installer) rendered as an 18–20px tile, followed by the window title text set in Syne at
  roughly 12.5px, weight 700, `-0.01em` letter-spacing, `rgba(255,255,255,0.72)` — matching
  the restraint of `app.css`'s own uppercase/tracked label treatment rather than shouting.
- The full wordmark image was considered and rejected: the shipped
  `wwwroot/images/AviatesairlogoWhite.webp` is a 1920×1080 canvas with the visible glyphs
  occupying a `162,366 → 1760,714` region (1598×348 once cropped) — usable, but it requires
  producing and shipping a new cropped asset for no gain in a title bar this size, versus
  the icon tile which is already exactly the right shape and already exists.

### 3. Nav rail badges: real signals where they exist, faithful stasis where they don't

The current Blazor sidebar's five badges were read directly from
`AviatesAirTracker.LegacyWeb/Shared/MainLayout.razor` before any decision was made, because
assuming they were data-driven would have been wrong for four of the five:

| Badge | Current Blazor markup | Backing data | Phase 2 treatment |
|---|---|---|---|
| Flights — dot | `<span class="nav-badge-dot">` (line 89), unconditional | None — purely decorative | **Made genuinely live**: unsynced-PIREP count. Reuses the exact query already in `App.xaml.cs`'s `RetryPendingPirepsAsync` (`!f.SyncedToBackend && f.Status == FlightStatus.Completed`) rather than inventing a new one |
| ACARS — "Live" pill | `<span class="nav-badge">Live</span>` (line 131), unconditional | None | **Made genuinely live**: shown when `State is not (FlightSessionState.Idle or FlightSessionState.Complete)`, updated on the existing `SessionStateChanged` event. Not a bare `!= Idle` — `FlightSessionManager.cs:470` already treats `Idle` and `Complete` as the same "nothing active" category (`if (State is FlightSessionState.Idle or FlightSessionState.Complete) return;`), so the badge follows the codebase's own existing convention rather than a new one |
| Map — "Live" pill | Line 142, unconditional | None | Same condition as ACARS — one flight session, one "is a flight in progress" signal, shared |
| Messages — count | `@if (_unreadCount > 0)` (line 152–154), shows `count` or `"99+"` | `MessagingService.UnreadCountChanged`, already wired | **Ported as-is** — this one was already live; `ShellWindow` subscribes to the same event `MainLayout.razor` does |
| News — `3` | `<span class="nav-badge">3</span>` (line 167), unconditional | **None — no `NewsService`, no data model, no `@code` block behind `News.razor` at all** | **Left static**, matching current behavior exactly. Building a real news/read-tracking system was explicitly considered and rejected as disproportionate to a shell reskin — it is a distinct feature, not a styling task, and gets its own future spec if wanted |

`InfoBadge`/dot styling (color, size, position) matches `app.css`'s `.nav-badge` /
`.nav-badge-dot` rules exactly — accent-colored pill, white text, `10px`/`700` weight for
the count badges; a plain small dot for Flights.

**Unverified assumption, to confirm during implementation, not at spec time:**
`NavigationViewItem.InfoBadge` is expected to support a dot-only style with no visible
value (WinUI ships `AttentionDotInfoBadgeStyle` for exactly this) for the Flights indicator,
distinct from the numeric/text style used for Messages and News. If that style resource
does not exist or does not behave this way, the fallback is a small custom dot `Ellipse`
positioned the same way `.nav-badge-dot` is — not a blocker either way, but the plan should
verify the built-in style first rather than assume it.

## What gets built

### `Theme/Tokens.xaml`

Direct translation of `app.css`'s `:root` block — colors only, the ones the shell chrome
actually consumes:

```
--sb-bg: #0B1628            --sb-hover: rgba(255,255,255,0.07)
--sb-active: rgba(77,155,236,0.16)      --sb-border: rgba(255,255,255,0.06)
--sb-text: rgba(255,255,255,0.52)       --sb-text-active: #FFFFFF
--sb-label: rgba(255,255,255,0.28)      --accent: #4d9bec (dark-theme value; `_Host.cshtml`'s
                                                             anti-flash script defaults to
                                                             dark when no theme is stored —
                                                             `localStorage.getItem('avt_theme')
                                                             || 'dark'`)
--r-md: 12px (nav-item radius)
```

No light-theme token set is built in this phase. `app.css` has one, but nothing in the
current app exposes a light/dark toggle to the native shell yet — that arrives with
`SettingsService` integration when Settings itself becomes a native page (Phase 3). Building
an unreachable light palette now would be speculative.

### `Theme/Typography.xaml` + bundled fonts

**Correction made during plan-writing, after the spec above was approved:** only **Syne**
and **Plus Jakarta Sans** are bundled in this phase, not all four fonts. Checked directly
against `app.css`: `JetBrains Mono` appears only on `.login-input` and other data-entry/
readout elements, `VT323` only on `.fmc-display` (the ACARS FMC's terminal-green readout) —
neither is used anywhere in `.sidebar`, `.top-header`, `.nav-item` or `.nav-section-label`.
Bundling them now would be the same mistake the scope boundary above already rules out for
`Cards.xaml`/`Elevation` — infrastructure with no current consumer. They get bundled by
whichever later phase actually converts the FMC display or the login page.

Shipped as `Assets/Fonts/*.ttf`, referenced via `FontFamily="/Assets/Fonts/....ttf#Syne"`-
style URIs, not the Google Fonts CDN link `_Host.cshtml` still uses for the Blazor-rendered
pages — native XAML text cannot reference a remote stylesheet the way HTML can. Source URLs
verified live and confirmed to serve genuine `.ttf` files (not WOFF2) via
`fonts.googleapis.com/css2?...&display=swap` with a browser `User-Agent` header; see the
implementation plan for the exact, tested URLs. This also happens to fix a real latent issue
no task in Phases 0–1 touched: today, even the Blazor-rendered pages' typography silently
depends on a live connection to `fonts.gstatic.com` at first paint.

### `ShellWindow.xaml` changes

- `NavigationView`'s pane background bound to `{ThemeResource SidebarBackgroundBrush}`
  (backed by `--sb-bg`)
- `NavigationViewItem` style: padding, margin, radius and hover/active states matching
  `app.css`'s `.nav-item` rules line-for-line, including the `::after`-equivalent active
  indicator (a 3px accent bar, 18px tall, with the same `rgba(77,155,236,0.6)` glow,
  positioned at the item's leading edge)
- `NavigationViewItemHeader` (the "Overview" / "Pilot" / "Community" labels) styled to
  `.nav-section-label`: 9px, 700 weight, `0.12em` tracking, `--sb-label` color, uppercase
- Title bar `Border` background stays `Transparent`; icon `Image` + `TextBlock` added as
  its content, per the decisions above
- `InfoBadge` bound per-item to the live/static values from the table above

### `ShellWindow.xaml.cs` changes

- Subscribe to `FlightSessionManager.SessionStateChanged` and `MessagingService
  .UnreadCountChanged` (both already exist; `ShellWindow` currently subscribes to neither)
- Compute the unsynced-PIREP count once at startup and on `FlightCompleted`/sync events,
  reusing `IFlightRepository`, not duplicating the query logic already in `App.xaml.cs`
- Push all three live values into the corresponding `NavigationViewItem.InfoBadge`

## What this phase explicitly does not do

- No `Cards.xaml`, no `Elevation` attached property, no shadow system — nothing in
  `ShellWindow` consumes them yet
- No `JetBrains Mono` or `VT323` bundled — neither is used anywhere in the sidebar or title
  bar; they belong to whichever later phase converts the FMC display or the login page
- No light theme — no reachable toggle exists yet
- No News data model — the badge stays static, faithfully
- No changes to any `.razor` file, `_Host.cshtml`, or the Blazor Server bridge
- No changes to navigation logic, the login gate, or shutdown — all untouched from Phases 0–1

## Verification

Visual fidelity is judged by direct comparison against the running Blazor sidebar (still
reachable at any time by navigating the `WebView2` without the `?shell=native` flag) and
against the approved artifact mockups. The three live badges are verified functionally: a
flight in progress shows "Live" on ACARS and Map and clears when it ends; an unsynced PIREP
shows the Flights dot and clears once `RetryPendingPirepsAsync` succeeds; Messages matches
whatever `MainLayout.razor`'s own badge currently shows, side by side.
