# Changelog

## 1.0.0

First public release.

- Knurls cylinders, cones, planar faces and closed bands (e.g. rounded boxes): straight, diagonal or
  diamond, with V, round or square profiles, by groove count or pitch.
- Dialog tooltips on every parameter; "Margin from face ends" now says it also applies to bands.
- Code organised into configuration, feature, spec, planning, tool building and utilities; every
  tolerance and tuning value is a named constant. Geometry is unchanged from 0.4.0.
- MIT license, product README, architecture and development guides.
- Dev tooling split into modules under `scripts/lib/`, test fixtures moved to `test/`, TypeScript
  typecheck (`bun run typecheck`), `check` and `help` commands.

## 0.4.0: bands

- Several edge-connected faces are knurled as one band: a closed loop of planes and cylinders sharing one
  direction (e.g. rounded-box sides and vertical fillets). Grooves are laid out on the developed band, so
  pitch and angle stay exact across the fillets.
- Straight band grooves are one extrude; angled grooves are swept one by one with the profile locked
  perpendicular to the band direction. Overlapping neighbours are cut in alternating batches.
- Clear errors for faces not running along the band direction, open strips, faces ending at different
  heights and concave fillets that are too tight. Boolean failures include the std error code.
- Dev loop: rate-limit and quota handling, fewer API calls per run, band probe.

## 0.3.0: cones and planar faces

- Cones, outside and inside: the twisted sweep adds a scale factor, so grooves follow the taper; size and
  pitch are given at the mid radius.
- Planar faces: one extrude per groove set, trimmed to the face boundary (holes included).
- New "Reference direction (planar faces)" parameter; default is the face's longest straight edge.

## 0.2.0: cylinders, single and diamond

- Full cylindrical faces, outside and bores, in any orientation.
- V, round and square profiles; groove count or pitch; angle 0 to 75 deg; single with hand, or double
  (diamond) with an optional independent second angle.
- Margin from face ends; runout past open flat ends, stop at shoulders, chamfers and fillets.
- Groove limit (default 500) and a warning above 20,000 faces.
- Each groove set is one twisted sweep; crossing sets are cut one after the other.
- Dev loop: compile errors with line numbers via eval, debug runs, suppress, scratch case files,
  geometry probe.

## 0.0.1

- Project skeleton, REST API dev loop, seed test geometry, feature stub.
