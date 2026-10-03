# How Knurl works

`knurl.fs` is one file so it can be pasted into a Feature Studio as-is. It is organised in six
sections: **Configuration** (enums, bounds and every tunable value), **Feature** (dialog and the
plan → build → cut → report body), **Spec** (dialog values as a plain map), **Planning** (analyse the
selection, lay out grooves), **Tool building** (sketches, sweeps, extrudes) and **Utilities**.

## Pieces

The selection is split into edge-connected groups. A group of one face is a **cylinder**, **cone** or
**plane**; a larger group is a **band**. Each piece is analysed into a map (`kind` plus the geometry the
builders need), then each groove set (one for single, two for double) gets a **layout**: groove count,
positions where needed, and a summary line for the info message. All layouts are created before
anything is built, so the groove limit is checked up front.

## Cutters

All cutters share one profile model: a groove cross-section in local coordinates `u` (across the groove)
and `v` (away from the material, 0 = original surface), drawn into a sketch through a frame that maps
`u` and `v` to sketch vectors. The cutter reaches one groove depth beyond the surface (the clearance) so
the cut breaks through cleanly. V, square and round profiles are closed polylines, circles or ellipses.

### Cylinders and cones: one twisted sweep per groove set

The profiles of all N grooves sit in one sketch perpendicular to the axis. They are swept along a
straight line on the axis with a **twist**: the sweep turns the profiles about the path, so each groove
follows an exact helix. The twist is `length · tan(angle) / radius`.

- In the transverse section an angled groove is wider than in its normal section, so `u` is stretched
  by `1 / cos(angle)`.
- Cones add the sweep's **scale factor** `r_end / r_start`: the profiles grow with the radius. Groove
  size and pitch are defined at the mid radius; `v` is stretched by `1 / cos(half angle)` so the depth is
  perpendicular to the cone surface. A linear twist cannot keep the helix angle constant on a cone, so
  it is exact at the mid radius.
- Groove end caps lie in planes perpendicular to the axis. At open flat ends the sweep runs two depths
  past the face (a clean runout); at shoulders, chamfers and fillets it stops at the face boundary.
- Bores (internal faces) flip `v`, and the clearance grows by the sagitta over the cutter width.
- If neighbouring profiles overlap, the sketch regions enclose the area around the axis; that region is
  excluded from the sweep.

Why a twisted sweep and not a sweep along a helix: the twist keeps the profile exactly transverse and
radially oriented without lock faces, all grooves of a set come out of one operation, and the end caps
are planar, so no trimming is needed.

### Planar faces: one extrude, trimmed

The profiles sit in one sketch perpendicular to the groove direction (the reference direction turned by
the angle) and are extruded across the face. The cutters are then intersected with a slab, the face
extruded outwards and inwards, using `SUBTRACT_COMPLEMENT`, so grooves stop exactly at the face boundary
and around holes. The default reference direction is the face's longest straight edge.

### Bands: grooves on the developed surface

A band is a closed loop of planes and cylinders that share one direction. Its cross-section is an
ordered loop of line and arc segments, found by walking from face to face through the shared straight
side edges, oriented counterclockwise. Positions on the band are arc lengths along that loop, so the
layout is that of a flat strip of the loop's length: pitch and angle stay exact across fillets.

- Straight grooves: all profiles in one sketch, one extrude along the band direction.
- Angled grooves: each groove is a line `s = s0 + z · tan(angle)` on the developed band. Its path is a
  spline through points sampled on the band (dense enough for the smallest fillet), and the profile is
  swept with `LOCK_DIRECTION` so it stays perpendicular to the band direction.

## Cutting

One subtraction per body and groove set. Crossing sets are cut one after the other: a single boolean
would also have to intersect the crossing cutters with each other above the surface, which made diamond
knurls about ten times slower. Separately swept band grooves that overlap their neighbours make a
boolean fail (`BOOLEAN_INVALID`), so they are cut in alternating batches (0, 2, 4… then 1, 3, 5…); grooves
left over when the count does not divide evenly get their own batches.

## Compatibility rules (from 1.0.0 on)

Users' models keep references to knurled geometry (a chamfer on a knurled edge, a mate on a knurled
face). Onshape names the faces and edges a feature creates or splits after the feature's internal
operation ids, so these are part of the public contract:

- Keep the operation ids in `buildTools`, `cutTools` and the builders stable: `piece<i>set<k>` with the
  `sketch`, `path`, `sweep`, `extrude`, `slab`, `trim`, `groove<k>` sub-ids, `cut<n>` and `cleanup`.
  Renaming them breaks downstream references on update.
- Keep parameter ids and enum values. Add new parameters with defaults that reproduce the old result.
- The order of pieces, sets and batches determines the ids, so keep that order stable too.

## Std library behaviour the feature relies on

- `opSweep` applies a twist only when `hasTwist` is set, as the std Sweep feature does; a bare `angle`
  is silently ignored. Positive twist is counterclockwise about the path direction (right hand).
- `opSweep` scales linearly along the path with `hasScale` and `scaleFactor`.
- `ProfileControlMode` is not re-exported by `common.fs`; `knurl.fs` imports it explicitly.
- `box` and `type` are reserved words in FeatureScript and cannot be used as names or map keys.

## Failure modes

| Situation | Behaviour |
|---|---|
| Face is not a cylinder, cone or plane | error, faces highlighted |
| Cylinder or cone does not go all the way around | error, face highlighted |
| Band faces do not share a direction, do not form a closed loop, or end at different heights | error, faces highlighted |
| Concave band fillet smaller than the groove | error, fillet highlighted |
| Too many grooves | error before anything is built |
| Boolean fails | error with the std error code |
| More than 20,000 faces in the result | warning |
