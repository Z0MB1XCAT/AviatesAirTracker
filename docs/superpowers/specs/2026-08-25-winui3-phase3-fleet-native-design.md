# WinUI 3 Migration — Phase 3, sixth page: native Fleet

**Date:** 2026-08-25
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** Dashboard, Pilot Hub, Flights, the landing-result dialog, and Routes — reuses
established conventions throughout (icon-as-`Path` reproduction, `Border`-not-`Button`, the
`Loaded`-handler refresh pattern, file-path-based photo loading).

## Goal

Convert `Fleet.razor` (route `/fleet`) to a native WinUI 3 `Page`. A type-card grid (subsidiary
filter tabs, stats row) that expands into a registration detail panel when a card is clicked —
no navigation to a separate page, the whole interaction is card → inline panel.

## Decision: reuse the existing `FleetViewModel` as-is

Unlike Flights/Routes, **`AviatesAirTracker.ViewModels.FleetViewModel` already exists**
(`AviatesAirTracker/ViewModels/FleetViewModel.cs`) and is already registered as a DI singleton
(`App.xaml.cs` line 418) — it's what `Fleet.razor` itself injects and drives today. It already
exposes everything the native page needs: `Data` (`FleetData` — `Stats` + `Types`), `FilteredTypes`
(computed, respects `SubsidiaryFilter`), `SelectedType`, `SelectedRegistrations`, `IsLoading`,
`IsLoadingRegs`, `Error`, plus `LoadAsync()`/`SelectTypeAsync(type)`/`ClearSelection()`/
`RefreshAsync()`/`SetFilter(subsidiary)`. This is the same situation Dashboard and Pilot Hub were
in (pre-existing ViewModel, page-only conversion) — **no new ViewModel task**, unlike Flights/Routes.

One inconsistency worth flattening rather than reproducing: `Fleet.razor`'s own `HandleTypeClick`
duplicates `FleetViewModel.SelectTypeAsync`'s logic inline (calling `FleetService` directly)
instead of just calling `SelectTypeAsync` — the native page will simply call
`_viewModel.SelectTypeAsync(type)`, which does the same thing correctly.

## What gets built

### `AviatesAirTracker/Pages/FleetPage.xaml`/`.xaml.cs` (new)

- Page header ("Fleet" / "AviatesAir operates a modern, fuel-efficient fleet across all
  subsidiaries.") with a refresh icon-button (spin animation on `IsLoading` is a nice-to-have, a
  static icon is an acceptable simplification — implementer's call, note which was done).
- Loading state (spinner + "Loading fleet data…") and an error banner (`VM.Error`), matching the
  original's exact two early-return branches.
- Stats row: 4 cards (Total Aircraft / In Service / In Maintenance / On Order), same icon+number
  layout established by Dashboard's own stat cards — reuse that visual pattern, not reinvent it.
- Filter tabs: All / Mainline / Regional / Cargo — same `Border`-active-state tab technique
  already used on Flights/Routes. Selecting a filter calls `VM.SetFilter(name)` (already handles
  clearing an out-of-filter selection internally, don't duplicate that guard).
- Type card grid: reuse Routes' just-built responsive `ItemsWrapGrid` + code-behind `ItemWidth`
  recompute technique (`RouteCardsList_SizeChanged`/`UpdateRouteGridColumns` in
  `RoutesPage.xaml.cs`) rather than a fixed card width — apply that pattern from the start here
  instead of hitting the same "dead gap on the right" bug Routes hit and had to fix after the
  fact. Each card: status chip, type code badge, photo (3 real `wwwroot/images/fleet-*.webp`
  files exist today — `a319`/`a320neo`/`a380`; every other type code falls back to a gradient
  placeholder with the type's own `Emoji` string, exactly matching `Fleet.razor`'s
  `_fleetPhotos` dictionary and fallback), name, active/total or on-order count, a 4-stat mini
  grid (capacity-or-payload, range, active/maintenance/on-order count, subsidiary), and an
  expand/collapse affordance ("Show/Hide registrations"). Selected-card and ordered-card visual
  states (`.fleet-card--selected` accent ring, `.fleet-card--ordered` dashed border + dimmed
  photo) both need reproducing — read `app.css`'s `.fleet-card*` rules directly for exact values
  during implementation rather than guessing.
- Registration detail panel: appears below the grid when `VM.SelectedType != null` (not a
  separate page/dialog — matches the original's inline expand-in-place). Header (type emoji,
  name, aircraft count / category / subsidiary, optional cruise speed / service ceiling), a
  close button (`VM.ClearSelection()`), loading state, empty state ("No registration records
  found."), and a registration-card grid (registration code badge, status chip, MSN/delivery-or-
  expected-date/hub/flights/hours stats — several conditionally shown depending on which fields
  are populated — and an optional notes line). Read `Fleet.razor` lines 253-319 directly for the
  exact conditional field set rather than assuming every registration has every field.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"fleet"` entry gets `NativePageType: typeof(Pages.FleetPage)` — one line, same pattern as every
prior page.

## What this spec explicitly does not do

- Does not touch `Fleet.razor` — left in place, unreferenced.
- Does not modify `FleetViewModel`, `FleetService`, or `FleetModels` — page-only conversion,
  same as Dashboard/Pilot Hub.
- Does not build any drill-down beyond the registration panel — there isn't one in the original.
- Does not touch any other nav destination.

## Verification

- Build clean, no new warnings.
- Visual: native Fleet compared against the current Blazor page — stats row, filter tabs, card
  grid (photos vs. emoji placeholders, selected/ordered states), registration panel (loading/
  empty/populated states).
- Functional: filter switching, card selection toggle (click again to collapse), refresh
  (re-fetches and, if a type was selected, re-selects it afterward — matches `RefreshAsync`'s
  own behavior). Needs a working backend connection to see real data; if unavailable in this
  environment, defer that specific check explicitly rather than skip silently, same as every
  prior live-network-gated functional check in this migration.
