# WinUI 3 Migration — Phase 3, tenth page: native Support

**Date:** 2026-08-25
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** every prior converted page — reuses established conventions (`Border`-not-`Button`,
the panel-card visual style, icon-as-`Path` reproduction).

## Goal

Convert `Support.razor` (route `/support`) to a native WinUI 3 `Page`. The last real page in this
Phase 3 sweep — small (93 lines), no service dependencies, no ViewModel needed.

## What's real vs. decorative

- **3 feature cards** (Live Chat / Knowledge Base / GitHub Issues) — confirmed by reading the
  markup directly: none of their buttons have an `@onclick` handler at all. They're purely
  decorative in the original today. The native page reproduces them visually (icon, title,
  description, button) but the buttons stay inert, matching the original's actual behavior
  exactly — not a regression, not something to silently "fix" by inventing real actions (opening
  a chat widget, a docs site, or a GitHub URL) that don't exist in the source.
- **FAQ accordion** — the one genuinely interactive piece: 6 fixed Q&A pairs, click to expand/
  collapse, only one open at a time (`OpenFaq` is a single nullable string, not a set).

## What gets built

### `AviatesAirTracker/Pages/SupportPage.xaml`/`.xaml.cs` (new)

No ViewModel — this page has no service dependency of any kind, just static content and local
UI state (`_openFaq`), matching the original's own shape.

- Page header ("Support" / "Get help, report issues, or connect with the AviatesAir community.").
- 3-column feature-card grid, each a `Border` "feature-card": a colored icon chip (blue/violet/
  cyan, icon paths ported verbatim from the original SVGs), title, description, and an inert
  `Border`-styled button (primary style for Live Chat, ghost style for the other two, matching
  the original's `btn-primary`/`btn-ghost` classes) with no `Tapped` handler wired to anything —
  or, if leaving a button with literally no interaction feels wrong for a native control, a
  `Tapped` handler that's a documented no-op, implementer's call, but no invented destination.
- FAQ section: header ("Frequently Asked Questions"), then 6 `Border` rows (question text + a
  rotating "+" glyph that becomes "×"-angled when open — a 45° `RotateTransform`, matching the
  original's CSS `transform: rotate(45deg)`), each toggling an answer panel below it on tap.
  Exactly one FAQ open at a time (`_openFaq` is a single nullable key, not per-row state) —
  tapping the currently-open one closes it, tapping a different one closes the previous and opens
  the new one, matching `ToggleFaq`'s exact logic. The 6 questions/answers are ported verbatim
  from the original's `Faqs` dictionary — including the exact wording, since it references other
  pages by name (Settings, Pilot Hub, Events) that are now real native destinations.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"support"` entry gets `NativePageType: typeof(Pages.SupportPage)` — one line.

## What this spec explicitly does not do

- Does not touch `Support.razor` — left in place, unreferenced.
- Does not wire the 3 feature-card buttons to real actions — they're inert in the original,
  they stay inert here.
- Does not touch ACARS, Map, or News — ACARS/Map are a separate later phase per the project's own
  phase breakdown; News is fully static/decorative with nothing functional to port (confirmed
  during Events' scoping pass) and isn't part of this page's conversion either.

## Verification

- Build clean, no new warnings.
- Visual: native Support compared against the current Blazor page — 3 feature cards, FAQ
  accordion (all 6 questions, expand/collapse, single-open-at-a-time behavior).
- Functional: no live-backend dependency, fully testable without network/hardware — this is the
  first page in the whole migration series that doesn't need anything deferred for a full check.
