# WinUI 3 Migration — Phase 3, eleventh page: native News

**Date:** 2026-08-25
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** every prior converted page — reuses established conventions (`Border`-not-`Button`,
panel-card visual style, icon-as-`Path` reproduction, file-path-based photo loading established by
Dashboard's `hero.webp`/Fleet's `fleet-*.webp`).

## Goal

Convert `News.razor` (route `/news`) to a native WinUI 3 `Page`. Fully static/decorative content
— confirmed by reading the file directly (no `@code` block at all, no ViewModel, no service
dependency): 5 tabs with only "All News" ever active and no `@onclick` on any of them, and 6 fixed
news cards whose "Read" links all point to `#` (dead). This is a like-for-like visual port, not a
feature port — there is no logic to reproduce because none exists in the original.

## What gets built

### `AviatesAirTracker/Pages/NewsPage.xaml`/`.xaml.cs` (new)

No ViewModel, no DI service injection.

- Page header ("News & Announcements" / "Stay up to date with AviatesAir operations, fleet
  updates, and community news.").
- Tab bar: 5 static tabs (All News / Operations / Fleet / Community / Events), same
  `Border`-active-state visual technique used everywhere else in this migration — but per the
  original, only "All News" is ever active and none of them are tappable/functional. A documented
  no-op `Tapped` handler is fine (matching the same choice made for Support's inert feature-card
  buttons), or the tabs can be built with no `Tapped` wiring at all — implementer's call, note
  which.
- 6-card grid (3-column, reuse the responsive `ItemsWrapGrid`/`SizeChanged` fill technique already
  established on Routes/Fleet rather than a fixed card width, to avoid the same "dead gap on
  resize" bug that shipped once and had to be fixed): each card has a banner (2 of the 6 use a
  real photo — `wwwroot/images/news-cityhopper.webp` and `news-acars-update.webp`, both already
  present in `wwwroot/images/` — loaded via the same file-path-based technique Dashboard/Fleet
  already use; the other 4 use a colored gradient background with a large low-opacity emoji glyph
  centered on it, port each exact color/emoji pair), a tag chip + date in the banner's corner, a
  title, an excerpt, and a footer (avatar-initials circle + author name + a dead "Read" link with
  a chevron icon — inert, matching the original's `href="#"`, no real navigation).
- All 6 cards' exact copy (tag, date, title, excerpt, author initials/name) ported verbatim from
  `News.razor` — this is marketing/flavor copy, get it exact, don't paraphrase.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"news"` entry gets `NativePageType: typeof(Pages.NewsPage)` — one line.

## What this spec explicitly does not do

- Does not touch `News.razor` — left in place, unreferenced.
- Does not make the tabs functional or the "Read" links navigate anywhere — none of that exists
  in the original, this is a faithful visual port of static content, not a feature addition.
- Does not add a real news/CMS backend — the 6 articles stay hardcoded, matching the original.

## Verification

- Build clean, no new warnings.
- Visual: native News compared against the current Blazor page — tab bar, all 6 cards (2 photo
  banners + 4 gradient/emoji banners), footer author rows.
- Functional: none needed — no live-backend or hardware dependency, same as Support, fully
  testable without deferring anything.
