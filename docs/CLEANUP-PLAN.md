# Cleanup plan: preparing Knurl as a product

Review of every file in the repo (knurl.fs 0.4.0, tooling, docs) done while the Onshape API quota was
exhausted. Nothing here has been applied yet: `knurl.fs` cannot be compile-checked or regression-tested
without the API, so all changes wait for the quota reset and land in one pass with the verification
below. The goal is **no behaviour change**: same dialog, same geometry, same messages (except the
wording fixes listed).

## 1. knurl.fs

### Findings

| # | Finding | Where |
|---|---|---|
| F1 | Magic numbers spread through the code: angular/parallel tolerance `1e-6` (6 places), ring sampling `24` points at `[0.5, 0.25, 0.75]`, runout `2 * depth` (revolved, band) and `2 * depth + halfWidth` (planar), clearance `= depth` (3 builders), band path sampling `0.5 mm` / `r / 4` / `1 mm` / `400 points`, concave-fillet limit `2 * (2 * depth + halfWidth)`, minimum grooves `3` / `1`. | throughout |
| F2 | Piece kinds are strings (`"PLANE"`, `"BAND"`, `"CYLINDER"`, `"CONE"`) compared in several places. | body, layouts, builders |
| F3 | The feature body repeats a three-way `if PLANE / BAND / else` dispatch twice (layout, build) next to a separate analyze dispatch. | `knurlFeatureBody` |
| F4 | Duplicated cutter math: the V cutter's top half width is computed in `addGrooveProfile` and again in `buildBandSet`; the clearance policy is repeated in three builders. | geometry layer |
| F5 | Terse or overloaded names: `geo` (the analyzed piece), segment fields `a`, `b`, `c`, `r`, `start`, `sweep`; loop variables `o`, `b` (cut counter), `f`. `start` means a point in one function and an angle in another. | planning, band code |
| F6 | `segmentStart()` finds a segment's arc-length offset by comparing maps in a loop (O(n²) per call, fragile). | band code |
| F7 | Section layout drifted: formatting helpers sit in the "Spec layer", `connectedComponents` sits between the planar and band code, band geometry helpers are mixed with layout. | file structure |
| F8 | Header and comments still use the development phases ("Phase 4"); version should become 1.0.0. | header, `analyzeBand` doc, testgeom.fs |
| F9 | **UI drift:** "Margin from face ends (cylinders, cones)" also applies to bands. The feature description lists only cylindrical, conical and planar faces. | precondition, annotation |
| F10 | No parameter tooltips. Onshape supports a `"Description"` key in parameter annotations (used by std `fillet.fs`, `frame.fs`). | precondition |
| F11 | `bandFrame` has no return on an empty segment list (unreachable today, but implicit). | `bandFrame` |
| F12 | `planarLayout` throws its own groove-limit error with wording that differs from the global check. | `planarLayout` |

### Planned changes

1. **Configuration section at the top** (after the enums): every tunable and tolerance as a named
   constant with a one-line comment, grouped as *parameter bounds*, *tolerances*, *cutter policy*
   (`KNURL_CLEARANCE_FACTOR`, `KNURL_RUNOUT_FACTOR`, `KNURL_CONCAVE_FILLET_FACTOR`), *validation*
   (`KNURL_RING_SAMPLES`, `KNURL_RING_HEIGHTS`, `KNURL_MIN_GROOVES_AROUND`, `KNURL_MIN_GROOVES_PLANAR`),
   *band path sampling* (`KNURL_PATH_ARC_STEP`, `KNURL_PATH_ARC_STEP_PER_RADIUS`, `KNURL_PATH_MAX_AXIAL_STEP`,
   `KNURL_PATH_MAX_POINTS`), *performance* (`KNURL_FACE_WARNING`). (F1)
2. **Internal enum** `KnurlPieceKind { CYLINDER, CONE, PLANE, BAND }` (not exported, so it stays out of
   the UI) replacing the strings. (F2)
3. **Three dispatchers** so the body reads plan → build → cut → report:
   `analyzePiece(context, faces, spec)`, `layoutGrooveSet(context, piece, spec, grooveSet)`,
   `buildGrooveSet(context, id, piece, spec, grooveSet, layout)`. (F3)
4. **Cutter helpers** `cutterClearance(spec)` and `cutterHalfWidthAtTop(spec, clearance)` used by all
   builders and the band batching. (F4)
5. **Renames:** `geo` → `piece`; segment fields → `startPoint`, `endPoint`, `center`, `radius`,
   `startAngle`, `sweepAngle`, `arcStart`, `length`; loop/counter names spelled out. (F5)
6. **Store `arcStart` on each segment** when the loop is built (after orientation); drop
   `segmentStart()`. (F6)
7. **Section order:** 1 Configuration · 2 Feature (UI + body) · 3 Spec · 4 Planning (selection grouping,
   revolved, planar, band incl. section geometry) · 5 Tool building (revolved, planar, band, profiles) ·
   6 Utilities (formatting, angle wrap, unique). Each section opens with a short comment on what it owns. (F7)
8. **Product header:** purpose, supported geometry, how each kind is built (short), limits, performance
   notes, version 1.0.0, std version. No phase wording. (F8)
9. **UI wording:** margin label "Margin from face ends"; feature description mentions bands; tooltips
   (`"Description"`) on every parameter, e.g. angle "0 = grooves along the axis / reference; measured
   against the surface generator", pitch "distance between grooves measured perpendicular to them",
   reference "planar faces only; default is the face's longest straight edge". (F9, F10)
10. Explicit fallback return in `bandFrame`; one shared groove-limit message. (F11, F12)

Parameter ids, enum values and defaults stay unchanged, so existing Knurl features in documents keep
their values.

## 2. Dev tooling (scripts/)

### Findings

| # | Finding |
|---|---|
| T1 | `dev.ts` (~410 lines) mixes configuration, Feature Studio sync, Part Studio API, case handling, the debug harness and the CLI. |
| T2 | Configuration is scattered: seed names in `dev.ts`, API path in `onshape.ts`, retry counts and the 429 threshold inline, debug feature id inline. |
| T3 | Four copies of "POST featurescript and read notices" (`dev.ts` `evalFS`, `cmdDebug`, `fscheck.ts`, both probes). |
| T4 | Two different implementations of "parameter defaults" (`buildParams` uses `defaultValue`, `cmdDebug` uses `ranges`). |
| T5 | Unclear names: `pid_in`, `ps`, `c`, `r`, `st`, `m`; unused import `existsSync`; unused `Case.featureType`. |
| T6 | The debug harness (defineFeature unwrap) lives in `dev.ts` as an inline string; it belongs with the module rewriter in `fscheck.ts`. |
| T7 | Test fixtures (`testgeom.fs`, `cases.json`) live in `scripts/` although they are data. |
| T8 | No typecheck for the TypeScript; no `package.json` scripts for check/debug/typecheck. |
| T9 | `probe-knurl.ts` only handles cylinders, its name says "knurl"; the probes duplicate ring analysis. |

### Planned layout

```
knurl.fs                      the product
test/geometry.fs              seed feature (was scripts/testgeom.fs)
test/cases.json               regression cases (was scripts/cases.json)
scripts/dev.ts                CLI only: argument parsing and commands
scripts/lib/config.ts         paths, seed names, retry limits, API version, debug ids
scripts/lib/onshape.ts        HTTP client: signing, 409/429 handling
scripts/lib/featurescript.ts  eval call + notices, BTFSValue -> JS, quantity conversion
scripts/lib/fscheck.ts        module -> lambda rewrite, compile diagnostics, debug harness
scripts/lib/studio.ts         Feature Studio push (edit guard, backups) and compile check
scripts/lib/partstudio.ts     features, query resolution, parameters (one defaults helper), upsert, report
scripts/probe-revolved.ts     ring probe for cylinders/cones (was probe-knurl.ts)
scripts/probe-band.ts         ring probe for rounded-rectangle bands
tsconfig.json                 strict; bun types
```

Add dev dependencies `typescript` and `@types/bun` (via `bun add -d`) and scripts
`typecheck`, `check` (fscheck), `debug`. Keep the CLI commands and flags exactly as documented.

## 3. Documentation

| # | Finding |
|---|---|
| D1 | README is only a dev-loop manual; it does not say how to **use** the feature: install, parameters, supported geometry, limits, performance, troubleshooting. |
| D2 | README seed list is outdated (missing tube, stepped shaft, rounded box). |
| D3 | README says `fscheck.ts` needs "no push"; it still needs the API (eval endpoint). |
| D4 | CHANGELOG headings use development phases; 1.0.0 entry needed. |
| D5 | `.env.example` has no per-variable comments. |
| D6 | No LICENSE (needs the owner's choice). |

### Planned documents

- **README.md** (product): what it does, screenshots placeholder, install (link the public Feature Studio
  or paste `knurl.fs`), quick start, parameter reference table (all 16 parameters, defaults, bounds),
  supported geometry and how pieces are detected (single face vs band), limits and error messages,
  performance guidance (groove limit, face warning, diamond cost), versioning. Short "Development"
  section linking to the dev guide.
- **docs/DEVELOPMENT.md**: setup (account, API key scopes, `.env`), the dev loop and every command,
  how push/compile check/debug/cases work, API quota, manual fallback.
- **docs/ARCHITECTURE.md**: the algorithms (twisted/scaled sweep, planar slab trim, developed band,
  batching, sequential set booleans), why each was chosen, the std behaviours we rely on
  (`hasTwist` required, defaults not filled for API-created features, `ProfileControlMode` import),
  and known failure modes.
- **CHANGELOG.md**: 1.0.0 entry summarising the product; earlier entries kept, headings reworded
  without phases.
- **.env.example**: one comment per variable.

## 4. Verification after the quota resets

API budget for the whole pass: about 60 calls.

1. `bun run typecheck` (offline) and `bun scripts/fscheck.ts knurl.fs`.
2. `bun run dev`: all cases must report INFO with the same face counts as the baseline below.
3. Probes on cylinder, bore, cone, plate and band: same results as recorded in the CHANGELOG checks.
4. Still open from 0.4.0: band diamond crossing count between coincidence heights (z = 6.75, 12.75)
   and band hand direction.
5. Open the dialog in Onshape and check labels and tooltips.

### Baseline (0.4.0, before the cleanup)

| Case | Summary | Faces |
|---|---|---|
| Knurl cylinder | 30 grooves at 0 deg, pitch 2.094 mm on R10 mm | 92 |
| Knurl tilted helical | 36 grooves at 30 deg RH, pitch 1.511 mm on R10 mm | 74 |
| Knurl tube bore | 24 grooves at 20 deg LH, pitch 1.968 mm on R8 mm | 99 |
| Knurl shaft diamond | 33 + 33 grooves at 30 deg RH/LH, pitch 0.989 mm on R6 mm | 3469 |
| Knurl collar margin | 40 at 30 deg RH, pitch 1.36 mm; 40 at 45 deg LH, pitch 1.111 mm on R10 mm | 6150 |
| Knurl cone helical | 41 grooves at 30 deg RH, pitch 1.493 mm on R11.25 mm (cone mid radius) | 125 |
| Knurl plate diamond | 35 + 35 grooves at 45 deg RH/LH, pitch 2 mm on a planar face | 3261 |
| Knurl cube top straight | 10 grooves at 0 deg, pitch 2 mm on a planar face | 86 |
| Knurl band diamond | 63 + 63 grooves at 30 deg RH/LH, pitch 1.508 mm around a band of 109.699 mm | 3214 |

## 5. Decisions needed from the owner

- **License** (e.g. MIT, or proprietary/all rights reserved).
- **Distribution**: publish the Feature Studio in a public document for others to add to their
  toolbar, or keep it private? This decides the README install section.
- **Version**: 1.0.0 after the cleanup, or 0.5.0 until it has been used on real parts.
