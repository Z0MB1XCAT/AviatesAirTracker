---
format: 1920x1080
message: "Your flight, your data, your career — tracked in real time."
arc: Feature-Benefit Cascade — hook → product intro → feature × 4 → benefits climax → brand outro / CTA
audience: MSFS virtual airline pilots waiting on the pre-release
music: driving, cinematic tech/aviation trailer energy — restrained and pulsing through the hook, building through the feature beats, peaking at the benefits climax, resolving triumphant on the brand close
---

## Video direction

- **Palette** (from `frame.md`, dark-HUD-inverted cobalt-grid): `colors.paper` (#0A0D17) is the ground on every frame — never pure black elsewhere, never a second hue. `colors.ink` (#F1F5F9) carries all type, rules, and the permanent graph-paper grid. `colors.paper-2` (#161D2E) is the only secondary surface — cards/panels sitting on the ground. `colors.ink-soft` (#3B82F6, the app's real accent blue) is reserved as a scarce highlight — one glow/emphasis moment per frame max (a hero number, the wordmark bloom, a locked finale) — never a decorative wash.
- **Motion grammar** — `power3` long-tail smooth settle on every entrance; no `back.out` / `bounce.out` / `elastic.out`, with exactly one sanctioned exception (Frame 7's `ONE APP.` slam, the video's single climax beat). This is a **silent film (no VO)** — the reveal-pacing doctrine still applies, but each frame's own **on-screen text cues** (not spoken words) are the beat reveals key on; nothing dumps at t=0, each further piece (a line, a card, a stat) lands on its own window across the back ~50% of the frame, paced to feel locked to the trailer music's pulse.
- **Rhythm / held-frame allocation** — Frame 2 (Product Intro) and Frame 8 (Brand Outro) are the two deliberate **held, settled** bookend beats: the wordmark assembles once, then holds still (subtle jitter / ambient glow only). Frames 3–6 stay in continuous, purposeful motion (data ticking, cards assembling) since they're the proof beats. Frame 7 is the one deliberately breakneck-tempo beat, offsetting the calm bookends around it.
- **Negative list** — no lazy breathing, no back-half pan/push, no bouncy/elastic entrances outside the one sanctioned climax, no infinite/looping motion, no purple-blue "AI" bokeh gradients, no fake browser chrome or real cursors, no screenshots or real UI captures (none exist — every panel/card/map/badge is an original graphic built inside `frame.md`'s dark-HUD system: graph-paper grid, top/bottom hairlines, 0-radius squareness, flat depth).
- **Caption band** — no narration/captions in this cut, but every frame still keeps the bottom ~17% clear for safe-margin consistency.

## Frame 1 — Hook

- scene: A near-black HUD grid pulses awake; a single altitude/VS readout ticks up like a live instrument coming online
- voiceover:
- duration: 3.5s
- transition_in: cut
- status: outline
- src: compositions/frames/01-hook.html
- type: hook
- persuasion: Pain validation
- beat: anticipation
- blueprint: kinetic-type-beats (Adapt) — reveal cues key on the on-screen line breaks + music hits instead of a spoken line
- focal: (none — pure typography + HUD graphic beat)
- asset_candidates:
- sfx: riser, ui-tick
- onscreen_text: "EVERY FLIGHT / BECOMES DATA."

Adapt: keep the hard-cut word-swap signature; the "spoken cue" becomes the readout's own count-up hitting a round number.
Scene 1 (0.0–1.0s): ground only — the permanent graph-paper grid + top/bottom hairlines on bare `paper`, dead still; one small mono altitude/VS tape reads `000` lower-center — Centered, ~70% negative space.
Scene 2 (1.0–2.2s): the tape's number climbs fast via **value-scaled counter** (`counting-dynamic-scale`); as it passes a round figure, "EVERY FLIGHT" **hard-cuts** in above it (`discrete-text-sequence`) — left-anchored headline, `display-hero` scale.
Scene 3 (2.2–3.5s): "BECOMES DATA." hard-cuts in beneath the first line on the next beat, completing the statement; the tape freezes on its final value. Hold — settled read, at most **subtle jitter** (`sine-wave-loop`, low amplitude) on the tape digits.

narrativeRole: Cold open — plant the promise (live, real telemetry) before the name ever appears.
keyMessage: This isn't a logbook. It's your flight, instrumented.

## Frame 2 — Product Intro

- scene: Scattered HUD glyphs (compass rose, VS tape, grid nodes) collapse and assemble into the Aviates Air Tracker wordmark
- voiceover:
- duration: 5s
- transition_in: zoom-through
- status: outline
- src: compositions/frames/02-product-intro.html
- type: product_intro
- persuasion: Authority by association
- beat: intrigue → confidence
- blueprint: logo-assemble-lockup (Reproduce) — wordless premium sting, elements assemble around the mark
- focal: the wordmark lockup itself
- asset_candidates:
- sfx: whoosh-soft, impact-soft
- onscreen_text: "AVIATES AIR TRACKER / The virtual airline companion for MSFS."

Reproduce: fill the blueprint's assembly slot with scattered instrument glyphs; the signature move is the convergence-into-lockup.
Scene 1 (0.0–1.5s): scattered HUD glyphs (compass rose, VS ticks, grid nodes) fade/scale in at low opacity across the dark canvas via staggered **spring-pop entrance** (`spring-pop-entrance`, smooth long-tail, no overshoot) — layered-depth, no single cluster yet.
Scene 2 (1.5–3.2s): the glyphs converge inward and resolve into the AVIATES AIR TRACKER wordmark, letterforms drawing on stroke-by-stroke via **SVG self-draw** (`svg-path-draw`) — Centered, hero, ~50% of frame.
Scene 3 (3.2–5.0s): the tagline "The virtual airline companion for MSFS" reveals via **per-word staggered reveal** (`dynamic-content-sequencing`) beneath the mark; an **ambient glow** (`ambient-glow-bloom`, `ink-soft`) blooms behind the wordmark and holds. Hold — settled read, subtle jitter only.

narrativeRole: Name the product right after the hook earns curiosity.
keyMessage: One app, built for virtual airline pilots.

## Frame 3 — Feature: Live Tracking + Map

- scene: A live map with a route line drawing itself and an aircraft marker advancing, telemetry readouts (ALT / GS / VS / HDG) ticking beside it at speed
- voiceover:
- duration: 6s
- transition_in: crossfade
- status: outline
- src: compositions/frames/03-live-tracking.html
- type: feature_showcase
- persuasion: Show-don't-tell proof
- beat: control
- blueprint: device-surface-showcase (Adapt) — the "device" is a floating map panel, not an app window
- focal: the map panel
- asset_candidates:
- sfx: ui-tick, data-blip
- onscreen_text: "20Hz FROM SIMCONNECT. / LIVE ON THE MAP."

Adapt: keep the floating-panel-as-hero signature; the "screen cycling through a flow" becomes the route drawing + telemetry ticking live.
Scene 1 (0.0–1.5s): a dark `paper-2` map panel with grid basemap fades/scales in as hero, empty of route — Centered, ~55% of frame, edge glow only.
Scene 2 (1.5–3.5s): a route line self-draws across the panel (**SVG self-draw**, `svg-path-draw`) as an aircraft marker advances along it, camera **pan / focus-lock** gently tracking the marker (`viewport-change`, no back-half push); "20Hz FROM SIMCONNECT." reveals via **per-word staggered reveal** upper-left.
Scene 3 (3.5–5.0s): telemetry readouts (ALT / GS / VS / HDG) tick in one at a time beside the panel via staggered **value-scaled counters** (`counting-dynamic-scale`) — asymmetric 70/30, panel dominant.
Scene 4 (5.0–6.0s): "LIVE ON THE MAP." hard-cuts in beneath the first line. Hold — route, marker, and readouts settled; subtle jitter only.

narrativeRole: First proof beat — the app sees the flight as it happens.
keyMessage: Real-time telemetry, not a post-flight summary.

## Frame 4 — Feature: Landing Analysis

- scene: Six score bars (VS, pitch, bank, speed, crosswind, stability) fill in sequence and resolve into one large landing score number
- voiceover:
- duration: 6s
- transition_in: crossfade
- status: outline
- src: compositions/frames/04-landing-analysis.html
- type: feature_showcase
- persuasion: Statistical proof
- beat: skepticism → confidence
- blueprint: dataviz-countup (Reproduce) — bars count up, camera lands on one hero metric
- focal: the resolved landing-score number
- asset_candidates:
- sfx: impact-soft, riser
- onscreen_text: "SIX POINTS. ONE SCORE. / HOW GOOD WAS THAT LANDING?"

Reproduce: six-metric stat grid pushed-through to one hero number, per the blueprint's own shape.
Scene 1 (0.0–1.0s): "SIX POINTS. ONE SCORE." reveals via **per-word staggered reveal**, centered upper-third; canvas otherwise bare.
Scene 2 (1.0–4.0s): six labeled bars (VS / Pitch / Bank / Speed / Crosswind / Stability) enter left-to-right in a row (asymmetric, ~60% width) and fill one after another via **bars / progress fill** (`stat-bars-and-fills`), staggered — never all at once.
Scene 3 (4.0–5.2s): camera **zoom-to-target** (`coordinate-target-zoom`) collapses the six bars into one large **value-scaled counter** (`counting-dynamic-scale`) counting up to the final landing score, dead-center, with an **ambient glow** (`ambient-glow-bloom`, `ink-soft`) blooming behind it.
Scene 4 (5.2–6.0s): "HOW GOOD WAS THAT LANDING?" hard-cuts in beneath the score. Hold — subtle jitter only.

narrativeRole: Turn a subjective "how'd I land" into a hard number the pilot can chase.
keyMessage: Landing analysis with real scoring, not a guess.

## Frame 5 — Feature: Career Stats + Rank

- scene: An hours counter climbs, stat cards (flights / hours / landings) assemble, and a rank badge advances Cadet → Captain
- voiceover:
- duration: 5s
- transition_in: crossfade
- status: outline
- src: compositions/frames/05-career-stats.html
- type: feature_showcase
- persuasion: Status seeking
- beat: aspiration
- blueprint: grid-card-assemble (Reproduce) — stat cards self-assemble and hold
- focal: the rank badge landing on Captain
- asset_candidates:
- sfx: ui-tick, chime
- onscreen_text: "EVERY HOUR COUNTS. / CADET TO CAPTAIN."

Reproduce: card grid self-assembles per the blueprint; the rank-tier step-through is the frame's own escalation on top of it.
Scene 1 (0.0–1.2s): "EVERY HOUR COUNTS." reveals via **per-word staggered reveal**, upper area; canvas otherwise empty.
Scene 2 (1.2–3.0s): three stat cards (Flights / Hours / Landings) self-assemble via **cluster→outward expansion** (`center-outward-expansion`) into a row; each card's number ticks up via **value-scaled counter**, staggered one after another.
Scene 3 (3.0–4.2s): a rank badge enters lower area and steps through tiers via **in-place token cycle** (`discrete-text-sequence`: Cadet → … → Captain), landing on "CAPTAIN" as "CADET TO CAPTAIN." hard-cuts in beside it.
Scene 4 (4.2–5.0s): Hold on the settled stat row + rank badge; subtle jitter only.

narrativeRole: Give the grind a payoff — progression, not just logging.
keyMessage: A career you can watch grow.

## Frame 6 — Feature: ACARS + Fleet

- scene: A booking card, a SimBrief import chip, and a row of fleet aircraft cards fan in together
- voiceover:
- duration: 6s
- transition_in: crossfade
- status: outline
- src: compositions/frames/06-acars-fleet.html
- type: feature_showcase
- persuasion: Friction reduction
- beat: ease
- blueprint: grid-card-assemble (Reproduce) — feature tiles self-assemble to show breadth at once
- focal: the fleet card row
- asset_candidates:
- sfx: ui-tick, whoosh-soft
- onscreen_text: "BOOK. IMPORT YOUR OFP. FLY. / ACARS, HANDLED."

Reproduce: three clauses of the headline pace three assembling clusters, per the blueprint's enumerate-breadth shape.
Scene 1 (0.0–1.2s): "BOOK." lands via **hard-cut** (`discrete-text-sequence`), upper-third; a booking card **cluster-assembles** (`center-outward-expansion`) left, in step with the word.
Scene 2 (1.2–3.0s): "IMPORT YOUR OFP." hard-cuts in beside the first clause as a SimBrief import chip cluster-assembles center, its icon **SVG-drawing** on (`svg-path-draw`).
Scene 3 (3.0–4.8s): "FLY." hard-cuts in as a row of 3–4 fleet aircraft cards fans in right-to-left via **cluster→outward expansion** — full-width strip, 3 depth layers.
Scene 4 (4.8–6.0s): "ACARS, HANDLED." hard-cuts in beneath the row. Hold on the full assembled set (booking + chip + fleet); subtle jitter only.

narrativeRole: Show the operational side — this runs a virtual airline, not just a tracker.
keyMessage: Booking, dispatch, and fleet in one place.

## Frame 7 — Benefits Climax

- scene: Short value phrases flash center-frame in rapid succession against the HUD grid, tempo increasing
- voiceover:
- duration: 4s
- transition_in: zoom-through
- status: outline
- src: compositions/frames/07-benefits.html
- type: benefit_highlight
- persuasion: Value stacking
- beat: excitement
- blueprint: kinetic-type-beats (Reproduce) — staccato montage of short value phrases at high tempo
- focal: "ONE APP." locked finale
- asset_candidates:
- sfx: impact-hit ×4, riser
- onscreen_text: "LIVE MAP. / LANDING SCORES. / CAREER STATS. / ACARS BOOKING. / ONE APP."

Reproduce: the blueprint's percussive slam array, one phrase per beat, resolving on a locked finale.
Scene 1 (0.0–0.6s): "LIVE MAP." **hard-cut / flash word-swap** (`discrete-text-sequence`), centered, tight.
Scene 2 (0.6–1.2s): "LANDING SCORES." hard-cuts in, replacing the prior line — cut tempo tightens slightly beat to beat, matched to the music hits.
Scene 3 (1.2–1.8s): "CAREER STATS." hard-cuts in.
Scene 4 (1.8–2.4s): "ACARS BOOKING." hard-cuts in.
Scene 5 (2.4–4.0s): **kinetic beat-slam** (`kinetic-beat-slam`) — "ONE APP." slams in as the locked finale via **spring-pop entrance** (`spring-pop-entrance`, this frame's one sanctioned playful overshoot — the video's single climax beat); an **ambient glow** blooms behind it and holds.

narrativeRole: Compress everything just shown into a fast payoff before the close.
keyMessage: It's all in one place.

## Frame 8 — Brand Outro / CTA

- scene: The HUD grid clears from all four edges; the wordmark draws itself in and holds, with a launch tease beneath it
- voiceover:
- duration: 6.5s
- transition_in: zoom-through
- status: outline
- src: compositions/frames/08-brand-outro.html
- type: cta
- persuasion: Scarcity/urgency
- beat: anticipation
- blueprint: logo-assemble-lockup (Reproduce) — feature elements clear the stage, the lockup draws itself in
- focal: the final wordmark lockup
- asset_candidates:
- sfx: impact-soft, chime-resolve
- onscreen_text: "AVIATES AIR TRACKER / COMING SOON"

Reproduce: the stage-clear → lockup-draws-on shape, exactly as the blueprint names it — this frame owns the real exit (no harness transition follows).
Scene 1 (0.0–1.5s): the graph-paper grid + hairlines pull back off all four edges via reverse **cluster→outward expansion** (`center-outward-expansion`), clearing the canvas to bare `paper`.
Scene 2 (1.5–4.0s): the AVIATES AIR TRACKER wordmark draws itself on stroke-by-stroke (**SVG self-draw**, `svg-path-draw`), centered, hero; an **ambient glow** (`ambient-glow-bloom`, `ink-soft`) blooms behind it as it completes, smooth `power3` settle.
Scene 3 (4.0–6.5s): "COMING SOON" reveals via **per-word staggered reveal** beneath the wordmark on its own beat. Final hold — wordmark + tagline settled; subtle jitter only. This is the film's true exit.

narrativeRole: Close on identity and a clean launch tease — no URL yet, this is pre-release.
keyMessage: Remember the name. It's almost here.
