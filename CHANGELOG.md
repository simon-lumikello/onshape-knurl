# Changelog

## 0.4.0 (Phase 4: prismatic bands)
- Selecting several edge-connected faces knurls them as one band: a closed loop of planes and cylinders
  sharing one direction (e.g. rounded-box sides + vertical fillets). Grooves are laid out on the developed
  band, so pitch and angle stay exact across the fillets; count/pitch refer to the band perimeter.
- Straight band grooves: one extrude. Angled: one sweep per groove (profile locked perpendicular to the
  band direction); overlapping neighbours are cut in alternating batches.
- Clear errors: faces not running along the band direction (top fillets, corner blends), open strips,
  faces ending at different heights, concave fillets too tight for the groove.
- Boolean failures now include the underlying error code.
- Dev loop: 429 handling (short waits retried, quota exhaustion reported), fewer API calls per run,
  `scripts/probe-band.ts`.

## 0.3.0 (Phase 3: cones and planar faces)
- Cones (external and internal): twisted sweep with the sweep's scale factor, so grooves follow the
  taper; groove size and pitch are given at the mid radius and scale with the radius. The helix angle
  is exact at the mid radius and varies along the cone (reported in the info message).
- Planar faces: one sketch + one extrude per groove set, trimmed to a slab over the face so grooves stop
  at the face boundary (holes included). Count mode = grooves across the face; pitch mode = centred on the face.
- New parameter "Reference direction (planar faces)": edge, axis or plane; default is the face's longest
  straight edge. Angle and hand are measured from it about the face normal.
- Face filter now accepts cylinders, cones and planes; mixed selections work.

## 0.2.0 (Phases 1+2: cylinders)
- Knurl on full cylindrical faces, external and internal (bores), any orientation.
- Profiles: V (depth + included tip angle), round (depth + cutter radius), square (depth + width).
- Spacing by groove count or by pitch (measured normal to the grooves at the surface).
- Angle 0 to 75 deg; single direction with hand, or double (diamond) with optional independent second angle.
- Margin from face ends; grooves run out past open flat ends and stop at shoulders, chamfers and fillets.
- Maximum groove count (default 500) with a clear error; warning above 20000 faces.
- Each groove set is one twisted sweep along the axis (exact helices); one boolean per body and set.
- Clear errors with highlighted faces: non-cylindrical, partial cylinders, depth too large, margin too large.
- Dev loop: compile errors with line numbers via eval (`scripts/fscheck.ts`), `debug`, `suppress`,
  `--cases`, full parameter sets for API-created features, 409 retry; `scripts/probe-knurl.ts` checks
  groove count and hand on the real geometry.

## 0.0.1 (Phase 0)
- Project skeleton, REST API dev loop (`scripts/dev.ts`), seed test geometry.
- `knurl.fs` stub: Knurl feature with face selection and depth; reports the selection, no geometry yet.
