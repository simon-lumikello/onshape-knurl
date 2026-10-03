FeatureScript 3083;
import(path : "onshape/std/common.fs", version : "3083.0");
// ProfileControlMode (used to lock band groove profiles) is not re-exported by common.fs.
import(path : "onshape/std/profilecontrolmode.gen.fs", version : "3083.0");

// Knurl 1.0.0: straight, diagonal (helical) and diamond knurling for Onshape.
// MIT License. Copyright (c) 2026 Simon Lumikello.
// Source, documentation and issues: https://github.com/simon-lumikello/onshape-knurl
//
// What it knurls (select faces; edge-connected faces are knurled together as one band):
//   - cylinders and cones, outside or inside (bores, countersinks)
//   - planar faces
//   - closed bands of planes and cylinders that share one direction, e.g. the sides and vertical
//     fillets of a rounded box
//
// How the cutters are built:
//   - Cylinders and cones: all grooves of a set are profiles in one sketch perpendicular to the axis,
//     swept along the axis with a twist (exact helices) and, for cones, a scale factor. Groove ends lie
//     in planes perpendicular to the axis, so they need no trimming.
//   - Planar faces: one sketch perpendicular to the groove direction, one extrude across the face,
//     then the cutters are trimmed to a slab over the face so grooves stop at its boundary.
//   - Bands: grooves are laid out by arc length on the developed (unrolled) band, so pitch and angle
//     stay exact across the fillets. Straight grooves are one extrude; angled grooves are swept one by
//     one with the profile kept perpendicular to the band direction.
//   - Each groove set is subtracted with one boolean per body; crossing sets are cut one after the
//     other, which is far faster than one combined boolean. Swept band grooves that overlap their
//     neighbours are cut in alternating batches, because overlapping tools make a boolean fail.
//
// File layout: 1 Configuration, 2 Feature, 3 Spec, 4 Planning, 5 Tool building, 6 Utilities.

// ===================================== 1. Configuration =====================================
// Dialog enums, parameter bounds, and every tolerance and tuning value used below.

export enum KnurlProfile
{
    annotation { "Name" : "V (triangle)" }
    V,
    annotation { "Name" : "Round" }
    ROUND,
    annotation { "Name" : "Square" }
    SQUARE
}

export enum KnurlDirections
{
    annotation { "Name" : "Single" }
    SINGLE,
    annotation { "Name" : "Double (diamond)" }
    DOUBLE
}

export enum KnurlHand
{
    annotation { "Name" : "Right hand" }
    RIGHT,
    annotation { "Name" : "Left hand" }
    LEFT
}

export enum KnurlSpacing
{
    annotation { "Name" : "Groove count" }
    COUNT,
    annotation { "Name" : "Pitch" }
    PITCH
}

/** What a group of selected faces is knurled as (internal; not shown in the dialog). */
enum KnurlPieceKind
{
    CYLINDER,
    CONE,
    PLANE,
    BAND
}

// Parameter bounds: [minimum, default, maximum] in the first unit, defaults for the other units.
const KNURL_DEPTH_BOUNDS = { (meter) : [1e-6, 0.0004, 0.1], (centimeter) : 0.04, (millimeter) : 0.4, (inch) : 0.016 } as LengthBoundSpec;
const KNURL_CUTTER_SIZE_BOUNDS = { (meter) : [1e-6, 0.0005, 0.1], (centimeter) : 0.05, (millimeter) : 0.5, (inch) : 0.02 } as LengthBoundSpec;
const KNURL_PITCH_BOUNDS = { (meter) : [1e-5, 0.001, 0.1], (centimeter) : 0.1, (millimeter) : 1.0, (inch) : 0.04 } as LengthBoundSpec;
const KNURL_MARGIN_BOUNDS = { (meter) : [0, 0, 1], (centimeter) : 0, (millimeter) : 0, (inch) : 0 } as LengthBoundSpec;
const KNURL_ANGLE_BOUNDS = { (degree) : [0, 30, 75], (radian) : 0.5235987756 } as AngleBoundSpec;
const KNURL_TIP_ANGLE_BOUNDS = { (degree) : [10, 90, 170], (radian) : 1.5707963268 } as AngleBoundSpec;
const KNURL_COUNT_BOUNDS = { (unitless) : [1, 40, 5000] } as IntegerBoundSpec;
const KNURL_MAX_GROOVES_BOUNDS = { (unitless) : [1, 500, 20000] } as IntegerBoundSpec;

// Tolerances.
/** Positions and extents closer than this are treated as equal. */
const KNURL_LENGTH_TOLERANCE = 1e-5 * meter;
/** Sketch radii closer than this give a circle instead of an ellipse. */
const KNURL_SKETCH_TOLERANCE = 1e-9 * meter;
/** Unit vectors count as parallel when |dot| > 1 - this, and as perpendicular when |dot| < this. */
const KNURL_DIRECTION_TOLERANCE = 1e-6;
/** Angles smaller than this are treated as zero (straight grooves). */
const KNURL_ANGLE_TOLERANCE = 1e-6 * degree;

// Cutter policy.
/** The cutter extends this many groove depths beyond the original surface, so the cut breaks through cleanly. */
const KNURL_CLEARANCE_FACTOR = 1;
/** At open flat ends, grooves run out this many groove depths past the face. */
const KNURL_RUNOUT_FACTOR = 2;
/** The trim slab for planar faces extends this many clearances outwards and groove depths inwards. */
const KNURL_TRIM_SLAB_FACTOR = 2;
/** A concave band fillet must be larger than this factor times (2 x depth + groove half width). */
const KNURL_CONCAVE_FILLET_FACTOR = 2;

// Validation.
/** A cylinder or cone counts as complete when one ring of this many points lies on the face... */
const KNURL_RING_SAMPLES = 24;
/** ...at one of these fractions of its height (a ring can be interrupted by a cross hole). */
const KNURL_RING_HEIGHTS = [0.5, 0.25, 0.75];
/** Fewest grooves around a cylinder, cone or band. */
const KNURL_MIN_GROOVES_AROUND = 3;
/** Fewest grooves across a planar face. */
const KNURL_MIN_GROOVES_PLANAR = 1;

// Paths of angled band grooves: splines through points sampled on the developed band.
/** Largest step along the band between path points... */
const KNURL_PATH_ARC_STEP = 0.5 * millimeter;
/** ...and at most this fraction of the smallest fillet radius... */
const KNURL_PATH_ARC_STEP_PER_RADIUS = 0.25;
/** ...and at most this far along the band direction... */
const KNURL_PATH_MAX_AXIAL_STEP = 1 * millimeter;
/** ...with at most this many points per groove. */
const KNURL_PATH_MAX_POINTS = 400;

// Performance.
/** Above this many faces on the knurled bodies, the result is reported as a warning about regeneration time. */
const KNURL_FACE_WARNING = 20000;

// ===================================== 2. Feature =====================================
// The dialog (precondition) and the feature body: plan, build cutters, cut, report.

annotation { "Feature Type Name" : "Knurl",
        "Feature Type Description" : "Cuts straight, diagonal or diamond knurls into cylinders, cones, planar faces and rounded bands." }
export const knurl = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Faces to knurl",
                    "Filter" : EntityType.FACE && (GeometryType.CYLINDER || GeometryType.CONE || GeometryType.PLANE) && ConstructionObject.NO && SketchObject.NO,
                    "Description" : "Cylinders, cones and planar faces. Connected faces, such as the sides and vertical fillets of a rounded box, are knurled together as one continuous band." }
        definition.faces is Query;

        annotation { "Group Name" : "Pattern", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Directions", "UIHint" : UIHint.SHOW_LABEL,
                        "Description" : "Single: one set of parallel grooves. Double: two crossing sets (diamond knurl)." }
            definition.directions is KnurlDirections;

            annotation { "Name" : "Angle (0 = straight)",
                        "Description" : "Angle between the grooves and the axis (cylinders, cones), the band direction (bands) or the reference direction (planar faces)." }
            isAngle(definition.angle, KNURL_ANGLE_BOUNDS);

            if (definition.directions == KnurlDirections.SINGLE)
            {
                annotation { "Name" : "Hand", "UIHint" : UIHint.SHOW_LABEL,
                            "Description" : "Right hand turns like a right-hand thread. Has no effect at angle 0." }
                definition.hand is KnurlHand;
            }
            else
            {
                annotation { "Name" : "Independent second angle",
                            "Description" : "Off: the second set mirrors the first (same angle, opposite hand)." }
                definition.hasSecondAngle is boolean;

                if (definition.hasSecondAngle)
                {
                    annotation { "Name" : "Second angle", "Description" : "Angle of the left-hand set." }
                    isAngle(definition.secondAngle, KNURL_ANGLE_BOUNDS);
                }
            }

            annotation { "Name" : "Spacing", "UIHint" : UIHint.SHOW_LABEL,
                        "Description" : "Set the number of grooves, or the distance between them." }
            definition.spacing is KnurlSpacing;

            if (definition.spacing == KnurlSpacing.COUNT)
            {
                annotation { "Name" : "Groove count",
                            "Description" : "Grooves per set: around a cylinder, cone or band, or across a planar face." }
                isInteger(definition.count, KNURL_COUNT_BOUNDS);
            }
            else
            {
                annotation { "Name" : "Pitch (normal to grooves)",
                            "Description" : "Distance between neighbouring grooves, measured perpendicular to them on the surface. Rounded to fit a whole number of grooves around closed faces; on cones it applies at the mid radius." }
                isLength(definition.pitch, KNURL_PITCH_BOUNDS);
            }

            annotation { "Name" : "Reference direction (planar faces)", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1,
                        "Description" : "Edge, axis or plane that the angle on planar faces is measured from. Default: the face's longest straight edge." }
            definition.referenceDirection is Query;
        }

        annotation { "Group Name" : "Cutter", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Profile", "UIHint" : UIHint.SHOW_LABEL, "Description" : "Cross-section of the groove." }
            definition.profile is KnurlProfile;

            annotation { "Name" : "Depth", "Description" : "Groove depth, perpendicular to the surface (at the mid radius on cones)." }
            isLength(definition.depth, KNURL_DEPTH_BOUNDS);

            if (definition.profile == KnurlProfile.V)
            {
                annotation { "Name" : "Included tip angle", "Description" : "Angle between the two flanks of the V. 90 deg is common." }
                isAngle(definition.tipAngle, KNURL_TIP_ANGLE_BOUNDS);
            }
            else if (definition.profile == KnurlProfile.ROUND)
            {
                annotation { "Name" : "Cutter radius", "Description" : "Radius of the round cutter. The depth must be less than its diameter." }
                isLength(definition.cutterRadius, KNURL_CUTTER_SIZE_BOUNDS);
            }
            else if (definition.profile == KnurlProfile.SQUARE)
            {
                annotation { "Name" : "Groove width", "Description" : "Width of the square groove." }
                isLength(definition.width, KNURL_CUTTER_SIZE_BOUNDS);
            }
        }

        annotation { "Group Name" : "Limits", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Margin from face ends",
                        "Description" : "Keeps the grooves this far from the ends of cylinders, cones and bands. At 0, grooves run out past open flat ends." }
            isLength(definition.margin, KNURL_MARGIN_BOUNDS);

            annotation { "Name" : "Maximum groove count",
                        "Description" : "Safety limit for the total number of grooves. Knurls create many faces; raise it only if the regeneration time is acceptable." }
            isInteger(definition.maxGrooves, KNURL_MAX_GROOVES_BOUNDS);
        }
    }
    {
        knurlFeatureBody(context, id, definition);
    });

/** Plans every piece of the selection, builds the cutters, cuts them and reports the result. */
function knurlFeatureBody(context is Context, id is Id, definition is map)
{
    const spec = knurlSpec(context, definition);
    const faces = evaluateQuery(context, definition.faces);
    if (size(faces) == 0)
    {
        throw regenError("Select at least one cylindrical, conical or planar face to knurl.", ["faces"]);
    }

    const plans = planPieces(context, faces, spec, definition.faces);
    const tools = buildTools(context, id, plans, spec);
    const cutBodies = cutTools(context, id, tools.toolsByBody, definition.faces);
    if (size(tools.helpers) > 0)
    {
        opDeleteBodies(context, id + "cleanup", { "entities" : qUnion(tools.helpers) });
    }
    reportResult(context, id, tools.summaries, cutBodies);
}

/**
 * Analyzes each piece of the selection (a single face, or several edge-connected faces forming a band)
 * and lays out its groove sets. Checks the total groove count before anything is built.
 */
function planPieces(context is Context, faces is array, spec is map, selection is Query) returns array
{
    var plans = [];
    var unsupported = [];
    var totalGrooves = 0;
    for (var pieceFaces in connectedComponents(context, faces))
    {
        const piece = analyzePiece(context, pieceFaces, spec);
        if (piece == undefined)
        {
            unsupported = concatenateArrays([unsupported, pieceFaces]);
            continue;
        }
        const minimumGrooves = piece.kind == KnurlPieceKind.PLANE ? KNURL_MIN_GROOVES_PLANAR : KNURL_MIN_GROOVES_AROUND;
        var layouts = [];
        for (var grooveSet in spec.sets)
        {
            const layout = layoutGrooveSet(context, piece, spec, grooveSet);
            if (layout.count < minimumGrooves)
            {
                throw regenError("Pitch is too large for the highlighted faces: it leaves fewer than " ~ minimumGrooves ~ " grooves. Reduce the pitch.",
                    ["pitch"], qUnion(pieceFaces));
            }
            layouts = append(layouts, layout);
            totalGrooves += layout.count;
        }
        plans = append(plans, { "firstFace" : pieceFaces[0], "piece" : piece, "layouts" : layouts });
    }
    if (size(unsupported) > 0)
    {
        throw regenError("Only cylindrical, conical and planar faces are supported. The highlighted faces are not.", ["faces"], qUnion(unsupported));
    }
    if (totalGrooves > spec.maxGrooves)
    {
        throwGrooveLimitError(spec, totalGrooves, selection);
    }
    return plans;
}

/**
 * Builds the cutters of every planned groove set.
 * Returns toolsByBody[body]["<set>_<batch>"] (tool queries cut together in one boolean), the helper
 * bodies to delete afterwards, and one summary line per groove set.
 */
function buildTools(context is Context, id is Id, plans is array, spec is map) returns map
{
    var toolsByBody = {};
    var helpers = [];
    var summaries = [];
    for (var planIndex = 0; planIndex < size(plans); planIndex += 1)
    {
        const plan = plans[planIndex];
        // Evaluated (transient) body query, so pieces on the same body share one map key.
        const body = evaluateQuery(context, qOwnerBody(plan.firstFace))[0];
        var toolGroups = toolsByBody[body] == undefined ? {} : toolsByBody[body];
        for (var setIndex = 0; setIndex < size(spec.sets); setIndex += 1)
        {
            const layout = plan.layouts[setIndex];
            const built = buildGrooveSet(context, id + ("piece" ~ planIndex ~ "set" ~ setIndex), plan.piece, spec, spec.sets[setIndex], layout);
            for (var batchIndex = 0; batchIndex < size(built.batches); batchIndex += 1)
            {
                const key = setIndex ~ "_" ~ batchIndex;
                toolGroups[key] = append(toolGroups[key] == undefined ? [] : toolGroups[key], built.batches[batchIndex]);
            }
            helpers = concatenateArrays([helpers, built.helpers]);
            summaries = append(summaries, layout.summary);
        }
        toolsByBody[body] = toolGroups;
    }
    return { "toolsByBody" : toolsByBody, "helpers" : helpers, "summaries" : summaries };
}

/**
 * Subtracts the cutters: one boolean per body, groove set and batch. Crossing sets are cut one after
 * the other, because a single boolean would also intersect the crossing cutters with each other above
 * the surface, which is wasted work and the dominant cost of diamond knurls. Returns the cut bodies.
 */
function cutTools(context is Context, id is Id, toolsByBody is map, selection is Query) returns array
{
    var cutIndex = 0;
    var cutBodies = [];
    for (var bodyEntry in toolsByBody)
    {
        for (var groupEntry in bodyEntry.value)
        {
            const toolQuery = qUnion(groupEntry.value);
            if (size(evaluateQuery(context, toolQuery)) == 0)
            {
                throw regenError("Could not build the knurl cutter for the highlighted faces.", ["faces"], selection);
            }
            try
            {
                opBoolean(context, id + ("cut" ~ cutIndex), {
                            "tools" : toolQuery,
                            "targets" : bodyEntry.key,
                            "operationType" : BooleanOperationType.SUBTRACTION
                        });
            }
            catch (error)
            {
                const reason = error is map && error.message != undefined ? " (" ~ toString(error.message) ~ ")" : "";
                throw regenError("Cutting the knurl failed" ~ reason ~ ". Try a smaller depth, a larger pitch, or a different angle.", ["faces"], selection);
            }
            cutIndex += 1;
        }
        cutBodies = append(cutBodies, bodyEntry.key);
    }
    return cutBodies;
}

/** Reports the groove sets and the resulting face count; a warning when the result is heavy. */
function reportResult(context is Context, id is Id, summaries is array, cutBodies is array)
{
    const faceCount = size(evaluateQuery(context, qOwnedByBody(qUnion(cutBodies), EntityType.FACE)));
    const message = "Knurl: " ~ join(uniqueValues(summaries), "; ") ~ ". Result: " ~ faceCount ~ " faces.";
    if (faceCount > KNURL_FACE_WARNING)
    {
        reportFeatureWarning(context, id, message ~ " Heavy model: expect slow regeneration downstream.");
    }
    else
    {
        reportFeatureInfo(context, id, message);
    }
}

function throwGrooveLimitError(spec is map, needed, faces is Query)
{
    throw regenError("This knurl needs " ~ needed ~ " grooves, more than the maximum of " ~ spec.maxGrooves ~
            ". Increase the pitch, reduce the groove count, or raise the limit under Limits (regeneration gets slow).",
        ["maxGrooves"], faces);
}

// ===================================== 3. Spec =====================================
// Turns the dialog definition into a plain spec map that the planning and building code reads.

/**
 * Normalizes the dialog definition. Groove sets carry a signed hand: +1 right, -1 left.
 * Only the cutter size that belongs to the chosen profile is copied.
 */
function knurlSpec(context is Context, definition is map) returns map
{
    var sets;
    if (definition.directions == KnurlDirections.SINGLE)
    {
        sets = [{ "angle" : definition.angle, "hand" : definition.hand == KnurlHand.LEFT ? -1 : 1 }];
    }
    else
    {
        const secondAngle = definition.hasSecondAngle ? definition.secondAngle : definition.angle;
        if (abs(definition.angle) < KNURL_ANGLE_TOLERANCE && abs(secondAngle) < KNURL_ANGLE_TOLERANCE)
        {
            throw regenError("A double (diamond) knurl needs a non-zero angle; with both angles at 0 the two groove sets coincide.", ["angle"]);
        }
        sets = [{ "angle" : definition.angle, "hand" : 1 }, { "angle" : secondAngle, "hand" : -1 }];
    }

    var spec = {
            "sets" : sets,
            "profile" : definition.profile,
            "depth" : definition.depth,
            "spacing" : definition.spacing,
            "count" : definition.count,
            "pitch" : definition.pitch,
            "margin" : definition.margin,
            "maxGrooves" : definition.maxGrooves
        };
    if (definition.profile == KnurlProfile.V)
    {
        spec.tipAngle = definition.tipAngle;
    }
    else if (definition.profile == KnurlProfile.ROUND)
    {
        spec.cutterRadius = definition.cutterRadius;
        if (definition.depth >= 2 * definition.cutterRadius)
        {
            throw regenError("Round cutter: depth must be less than the cutter diameter.", ["depth", "cutterRadius"]);
        }
    }
    else
    {
        spec.width = definition.width;
    }

    // Optional reference direction for planar faces.
    if (definition.referenceDirection != undefined && size(evaluateQuery(context, definition.referenceDirection)) > 0)
    {
        const direction = extractDirection(context, definition.referenceDirection);
        if (direction == undefined)
        {
            throw regenError("Could not get a direction from the reference. Pick a straight edge, an axis or a plane.", ["referenceDirection"]);
        }
        spec.referenceDirection = direction;
    }
    return spec;
}

/** Half width of the groove at the original surface, measured perpendicular to the groove. */
function grooveHalfWidthAtSurface(spec is map) returns ValueWithUnits
{
    if (spec.profile == KnurlProfile.V)
        return spec.depth * tan(spec.tipAngle / 2);
    if (spec.profile == KnurlProfile.ROUND)
    {
        const r = spec.cutterRadius;
        return sqrt(r * r - (r - spec.depth) * (r - spec.depth));
    }
    return spec.width / 2;
}

/** How far the cutter extends beyond the original surface. */
function cutterClearance(spec is map) returns ValueWithUnits
{
    return KNURL_CLEARANCE_FACTOR * spec.depth;
}

/** Half width of the cutter at its outer edge (the clearance line), measured perpendicular to the groove. */
function cutterHalfWidthAtTop(spec is map, clearance is ValueWithUnits) returns ValueWithUnits
{
    if (spec.profile == KnurlProfile.V)
        return (spec.depth + clearance) * tan(spec.tipAngle / 2);
    if (spec.profile == KnurlProfile.ROUND)
        return spec.cutterRadius;
    return spec.width / 2;
}

// ===================================== 4. Planning =====================================
// Analyzes the selected faces into pieces (cylinder, cone, plane, band) and lays out the grooves.
// A piece map always has "kind"; the other fields depend on the kind and are documented by its
// analyze function. A layout map always has "count" and "summary".

/** Splits the selected faces into edge-connected groups. */
function connectedComponents(context is Context, faces is array) returns array
{
    const selection = qUnion(faces);
    var seen = {};
    var components = [];
    for (var seed in faces)
    {
        if (seen[seed] != undefined)
            continue;
        seen[seed] = true;
        var component = [seed];
        for (var i = 0; i < size(component); i += 1)
        {
            const neighbors = qIntersection([qAdjacent(component[i], AdjacencyType.EDGE, EntityType.FACE), selection]);
            for (var neighbor in evaluateQuery(context, neighbors))
            {
                if (seen[neighbor] == undefined)
                {
                    seen[neighbor] = true;
                    component = append(component, neighbor);
                }
            }
        }
        components = append(components, component);
    }
    return components;
}

/** The piece for a group of connected faces, or undefined for a single face of an unsupported type. */
function analyzePiece(context is Context, faces is array, spec is map)
{
    if (size(faces) > 1)
        return analyzeBand(context, faces, spec);
    const surface = evSurfaceDefinition(context, { "face" : faces[0] });
    if (surface is Cylinder || surface is Cone)
        return analyzeRevolved(context, faces[0], surface, spec);
    if (surface is Plane)
        return analyzePlane(context, faces[0], spec);
    return undefined;
}

/** Lays out one groove set on a piece. */
function layoutGrooveSet(context is Context, piece is map, spec is map, grooveSet is map) returns map
{
    if (piece.kind == KnurlPieceKind.PLANE)
        return planarLayout(context, piece, spec, grooveSet);
    if (piece.kind == KnurlPieceKind.BAND)
        return bandLayout(piece, spec, grooveSet);
    return revolvedLayout(piece, spec, grooveSet);
}

// ---------- Cylinders and cones ----------

/**
 * A cylindrical or conical face. Positions along the axis (zStart, zEnd) are in the surface's own
 * coordinate system; for cones the apex is at z = 0 and radius(z) = z * tan(halfAngle).
 * Fields: kind, origin, axis, xDir, side (+1 outside, -1 bore), halfAngle, zStart, zEnd, rStart, rEnd, rMid.
 * Throws regenErrors that highlight the face for unsupported cases.
 */
function analyzeRevolved(context is Context, face is Query, surface is map, spec is map) returns map
{
    const isCone = surface is Cone;
    const cSys = surface.coordSystem;
    const axis = cSys.zAxis;
    const xDir = cSys.xAxis;
    const yDir = cross(axis, xDir);
    const halfAngle = isCone ? surface.halfAngle : 0 * degree;
    const radiusAt = function(z is ValueWithUnits) returns ValueWithUnits
        {
            return isCone ? z * tan(halfAngle) : surface.radius;
        };

    const extent = evBox3d(context, { "topology" : face, "cSys" : cSys, "tight" : true });
    const zMin = extent.minCorner[2];
    const zMax = extent.maxCorner[2];
    const rMid = radiusAt((zMin + zMax) / 2);

    // On a cone the profile scales with the radius, so this also holds at the small end.
    if (spec.depth >= rMid / 2)
    {
        throw regenError("Knurl depth must be less than half the radius (" ~ formatLength(rMid) ~ ").", ["depth"], face);
    }

    // Outside (material inside) or bore / countersink: face normal against the radial direction.
    const probe = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) });
    const fromAxis = probe.origin - cSys.origin;
    const radial = fromAxis - dot(fromAxis, axis) * axis;
    const side = dot(probe.normal, radial) > 0 * meter ? 1 : -1;

    if (!isCompleteRevolution(context, face, cSys, radiusAt, zMin, zMax))
    {
        throw regenError("Partial cylindrical or conical faces are not supported: the highlighted face does not go all the way around.", ["faces"], face);
    }

    // Groove extent. At an open end (a flat end face pointing away) the grooves run out past the end so
    // the cut is clean. Anywhere else (shoulder, chamfer, fillet) they stop at the face boundary.
    const runout = KNURL_RUNOUT_FACTOR * spec.depth;
    var zStart = zMin + spec.margin;
    var zEnd = zMax - spec.margin;
    if (spec.margin < KNURL_LENGTH_TOLERANCE)
    {
        if (isOpenEnd(context, face, cSys, zMin, false, qNothing()))
            zStart = zMin - runout;
        if (isOpenEnd(context, face, cSys, zMax, true, qNothing()))
            zEnd = zMax + runout;
    }
    if (isCone)
    {
        // Never run out across the apex.
        zStart = max(zStart, zMin / 2);
    }
    if (zEnd - zStart < 2 * KNURL_LENGTH_TOLERANCE)
    {
        throw regenError("Margin is too large: nothing is left to knurl on the highlighted face.", ["margin"], face);
    }

    return {
            "kind" : isCone ? KnurlPieceKind.CONE : KnurlPieceKind.CYLINDER,
            "origin" : cSys.origin,
            "axis" : axis,
            "xDir" : xDir,
            "side" : side,
            "halfAngle" : halfAngle,
            "zStart" : zStart,
            "zEnd" : zEnd,
            "rStart" : radiusAt(zStart),
            "rEnd" : radiusAt(zEnd),
            "rMid" : rMid
        };
}

/** True if a full ring of points around the axis lies on the face at one of the sample heights. */
function isCompleteRevolution(context is Context, face is Query, cSys is CoordSystem, radiusAt is function,
    zMin is ValueWithUnits, zMax is ValueWithUnits) returns boolean
{
    const yDir = cross(cSys.zAxis, cSys.xAxis);
    for (var fraction in KNURL_RING_HEIGHTS)
    {
        const z = zMin + (zMax - zMin) * fraction;
        const radius = radiusAt(z);
        var ringComplete = true;
        for (var k = 0; k < KNURL_RING_SAMPLES; k += 1)
        {
            const phi = (360 * k / KNURL_RING_SAMPLES) * degree;
            const point = cSys.origin + z * cSys.zAxis + radius * (cos(phi) * cSys.xAxis + sin(phi) * yDir);
            if (evDistance(context, { "side0" : face, "side1" : point }).distance > KNURL_LENGTH_TOLERANCE)
            {
                ringComplete = false;
                break;
            }
        }
        if (ringComplete)
            return true;
    }
    return false;
}

/**
 * True if every face touching this end of the face is a flat end face whose normal points away from it.
 * Faces in `ignore` (the other faces of a band) are not considered.
 */
function isOpenEnd(context is Context, face is Query, cSys is CoordSystem, zEnd is ValueWithUnits, isMaxEnd is boolean, ignore is Query) returns boolean
{
    var touching = false;
    for (var neighbor in evaluateQuery(context, qSubtraction(qAdjacent(face, AdjacencyType.EDGE, EntityType.FACE), ignore)))
    {
        const extent = evBox3d(context, { "topology" : neighbor, "cSys" : cSys, "tight" : true });
        if (extent.minCorner[2] > zEnd + KNURL_LENGTH_TOLERANCE || extent.maxCorner[2] < zEnd - KNURL_LENGTH_TOLERANCE)
            continue;
        touching = true;
        if (!(evSurfaceDefinition(context, { "face" : neighbor }) is Plane))
            return false;
        const normal = evFaceTangentPlane(context, { "face" : neighbor, "parameter" : vector(0.5, 0.5) }).normal;
        const alignment = dot(normal, cSys.zAxis);
        if (isMaxEnd ? alignment < 1 - KNURL_DIRECTION_TOLERANCE : alignment > -1 + KNURL_DIRECTION_TOLERANCE)
            return false;
    }
    return touching;
}

/** Groove count around a cylinder or cone. Pitch and count refer to the mid radius. */
function revolvedLayout(piece is map, spec is map, grooveSet is map) returns map
{
    // The angle is measured against the generator (on a cone: the slanted surface line).
    const circumference = 2 * PI * piece.rMid;
    const count = spec.spacing == KnurlSpacing.COUNT ? spec.count : round(circumference * cos(grooveSet.angle) / spec.pitch);
    const normalPitch = count > 0 ? circumference * cos(grooveSet.angle) / count : 0 * meter;
    var summary = count ~ " grooves at " ~ formatAngle(grooveSet) ~ ", pitch " ~ formatLength(normalPitch) ~ " on R" ~ formatLength(piece.rMid);
    if (piece.kind == KnurlPieceKind.CONE)
    {
        summary ~= " (cone mid radius; scales along the cone)";
    }
    return { "count" : count, "summary" : summary };
}

// ---------- Planar faces ----------

/**
 * A planar face. Fields: kind, face, origin (a point on the face), normal (outward), reference
 * (unit direction in the plane that angles are measured from).
 */
function analyzePlane(context is Context, face is Query, spec is map) returns map
{
    const tangentPlane = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) });
    const normal = tangentPlane.normal;

    var reference;
    if (spec.referenceDirection != undefined)
    {
        reference = spec.referenceDirection - dot(spec.referenceDirection, normal) * normal;
        if (norm(reference) < KNURL_DIRECTION_TOLERANCE)
        {
            throw regenError("The reference direction is perpendicular to the highlighted face. Pick a direction that lies along the face.",
                ["referenceDirection"], face);
        }
        reference = normalize(reference);
    }
    else
    {
        // Default: the longest straight edge of the face; else the plane's own x direction.
        var longest = 0 * meter;
        for (var edge in evaluateQuery(context, qGeometry(qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE), GeometryType.LINE)))
        {
            const length = evLength(context, { "entities" : edge });
            if (length > longest + KNURL_LENGTH_TOLERANCE)
            {
                longest = length;
                reference = evLine(context, { "edge" : edge }).direction;
            }
        }
        if (reference == undefined)
        {
            reference = evPlane(context, { "face" : face }).x;
        }
        reference = normalize(reference - dot(reference, normal) * normal);
    }
    return { "kind" : KnurlPieceKind.PLANE, "face" : face, "origin" : tangentPlane.origin, "normal" : normal, "reference" : reference };
}

/**
 * Groove layout of one set on a planar face: the groove direction (the reference turned by
 * hand * angle about the normal), the face extent along the grooves, and the groove positions across
 * the face. Count mode spreads the grooves across the face width; pitch mode centres them on the face.
 */
function planarLayout(context is Context, piece is map, spec is map, grooveSet is map) returns map
{
    const turn = grooveSet.hand * grooveSet.angle;
    const along = normalize(cos(turn) * piece.reference + sin(turn) * cross(piece.normal, piece.reference));
    const across = cross(piece.normal, along);
    const cSys = coordSystem(piece.origin, along, piece.normal);
    const extent = evBox3d(context, { "topology" : piece.face, "cSys" : cSys, "tight" : true });
    const width = extent.maxCorner[1] - extent.minCorner[1];
    const center = (extent.maxCorner[1] + extent.minCorner[1]) / 2;

    var positions = [];
    var pitch;
    if (spec.spacing == KnurlSpacing.COUNT)
    {
        pitch = width / spec.count;
        for (var k = 0; k < spec.count; k += 1)
        {
            positions = append(positions, center + (k - (spec.count - 1) / 2) * pitch);
        }
    }
    else
    {
        pitch = spec.pitch;
        // Cover the whole width, including grooves that only partly overlap the face edges.
        const reach = width / 2 + grooveHalfWidthAtSurface(spec);
        const half = floor(reach / pitch);
        if (2 * half + 1 > spec.maxGrooves)
        {
            // Checked here already, so a tiny pitch cannot build a huge list before the global check.
            throwGrooveLimitError(spec, 2 * half + 1, piece.face);
        }
        for (var j = -half; j <= half; j += 1)
        {
            positions = append(positions, center + j * pitch);
        }
    }
    return {
            "count" : size(positions),
            "positions" : positions,
            "along" : along,
            "across" : across,
            "alongMin" : extent.minCorner[0],
            "alongMax" : extent.maxCorner[0],
            "summary" : size(positions) ~ " grooves at " ~ formatAngle(grooveSet) ~ ", pitch " ~ formatLength(pitch) ~ " on a planar face"
        };
}

// ---------- Bands ----------

/**
 * A closed band of planes and cylinders that all run along one direction (e.g. the sides and
 * vertical fillets of a rounded box). Its cross-section perpendicular to that direction is an
 * ordered loop of line and arc segments in section coordinates (origin, xDir, yDir), oriented
 * counterclockwise about the direction.
 * Fields: kind, faces, origin, axis (band direction), xDir, yDir, segments, length (loop arc length),
 * side (+1 when the material is inside the loop), zStart, zEnd, minRadius (smallest fillet, or undefined).
 * Segment fields: isArc, face, startPoint, endPoint, length, arcStart (arc length where it begins),
 * and for arcs center, radius, startAngle, sweepAngle (signed; positive = counterclockwise).
 */
function analyzeBand(context is Context, faces is array, spec is map) returns map
{
    const allFaces = qUnion(faces);

    // Band direction and section origin from the first cylinder (fillet).
    var direction;
    var origin;
    for (var face in faces)
    {
        const surface = evSurfaceDefinition(context, { "face" : face });
        if (surface is Cylinder)
        {
            direction = surface.coordSystem.zAxis;
            origin = surface.coordSystem.origin;
            break;
        }
    }
    if (direction == undefined)
    {
        throw regenError("Connected faces are knurled as one band, which must contain at least one cylindrical (fillet) face. " ~
                "To knurl the faces separately, use one Knurl feature per face.", ["faces"], allFaces);
    }
    const xDir = perpendicularVector(direction);
    const yDir = cross(direction, xDir);
    const toSection = function(point is Vector) returns Vector
        {
            const offset = point - origin;
            return vector(dot(offset, xDir), dot(offset, yDir));
        };

    const order = bandFaceOrder(context, faces, direction, allFaces);

    // Cross-section segments, in walk order.
    var segments = [];
    var minRadius;
    for (var step in order)
    {
        const startPoint = toSection(evLine(context, { "edge" : step.entryEdge }).origin);
        const endPoint = toSection(evLine(context, { "edge" : step.exitEdge }).origin);
        if (step.surface is Plane)
        {
            segments = append(segments, { "isArc" : false, "face" : step.face, "startPoint" : startPoint, "endPoint" : endPoint,
                        "length" : norm(endPoint - startPoint) });
        }
        else
        {
            const center = toSection(step.surface.coordSystem.origin);
            const radius = step.surface.radius;
            const midPoint = toSection(evFaceTangentPlane(context, { "face" : step.face, "parameter" : vector(0.5, 0.5) }).origin);
            const startAngle = atan2(startPoint[1] - center[1], startPoint[0] - center[0]);
            const ccwToEnd = wrapAngle(atan2(endPoint[1] - center[1], endPoint[0] - center[0]) - startAngle);
            const ccwToMid = wrapAngle(atan2(midPoint[1] - center[1], midPoint[0] - center[0]) - startAngle);
            // The arc runs the way that passes through the face's middle.
            const sweepAngle = ccwToMid < ccwToEnd ? ccwToEnd : ccwToEnd - 360 * degree;
            segments = append(segments, { "isArc" : true, "face" : step.face, "startPoint" : startPoint, "endPoint" : endPoint,
                        "center" : center, "radius" : radius, "startAngle" : startAngle, "sweepAngle" : sweepAngle,
                        "length" : radius * abs(sweepAngle) / radian });
            minRadius = minRadius == undefined ? radius : min(minRadius, radius);
        }
    }
    if (loopTwiceArea(segments) < 0 * meter * meter)
    {
        segments = reverseLoop(segments);
    }
    var length = 0 * meter;
    var placedSegments = [];
    for (var segment in segments)
    {
        segment.arcStart = length;
        length += segment.length;
        placedSegments = append(placedSegments, segment);
    }
    segments = placedSegments;

    var piece = { "kind" : KnurlPieceKind.BAND, "faces" : allFaces, "origin" : origin, "axis" : direction, "xDir" : xDir, "yDir" : yDir,
        "segments" : segments, "length" : length, "side" : 1, "minRadius" : minRadius };

    // Material side: compare the loop's outward normal with the face normal on the first segment.
    const probe = bandFrame(piece, segments[0].length / 2);
    const faceNormal = evFaceTangentPlane(context, { "face" : segments[0].face, "parameter" : vector(0.5, 0.5) }).normal;
    if (dot(vector(dot(faceNormal, xDir), dot(faceNormal, yDir)), probe.away) < 0)
    {
        piece.side = -1;
    }

    // Concave fillets must be larger than the groove, or the cutter folds over itself.
    const smallestConcaveRadius = KNURL_CONCAVE_FILLET_FACTOR * (2 * spec.depth + grooveHalfWidthAtSurface(spec));
    for (var segment in segments)
    {
        if (!segment.isArc)
            continue;
        const frame = bandFrame(piece, segment.arcStart + segment.length / 2);
        if (dot(frame.away, segment.center - frame.point) > 0 * meter && segment.radius < smallestConcaveRadius)
        {
            throw regenError("The highlighted concave fillet (R" ~ formatLength(segment.radius) ~ ") is too tight for this groove size.", ["depth"], segment.face);
        }
    }

    // Extent along the band direction: all band faces must start and end at the same height.
    const cSys = coordSystem(origin, xDir, direction);
    var zMin;
    var zMax;
    for (var face in faces)
    {
        const extent = evBox3d(context, { "topology" : face, "cSys" : cSys, "tight" : true });
        if (zMin != undefined && (abs(extent.minCorner[2] - zMin) > KNURL_LENGTH_TOLERANCE || abs(extent.maxCorner[2] - zMax) > KNURL_LENGTH_TOLERANCE))
        {
            throw regenError("All faces of a band must start and end at the same height along the band direction. The highlighted face does not.",
                ["faces"], face);
        }
        zMin = extent.minCorner[2];
        zMax = extent.maxCorner[2];
    }
    var zStart = zMin + spec.margin;
    var zEnd = zMax - spec.margin;
    if (spec.margin < KNURL_LENGTH_TOLERANCE)
    {
        var openAtMin = true;
        var openAtMax = true;
        for (var face in faces)
        {
            openAtMin = openAtMin && isOpenEnd(context, face, cSys, zMin, false, allFaces);
            openAtMax = openAtMax && isOpenEnd(context, face, cSys, zMax, true, allFaces);
        }
        if (openAtMin)
            zStart = zMin - KNURL_RUNOUT_FACTOR * spec.depth;
        if (openAtMax)
            zEnd = zMax + KNURL_RUNOUT_FACTOR * spec.depth;
    }
    if (zEnd - zStart < 2 * KNURL_LENGTH_TOLERANCE)
    {
        throw regenError("Margin is too large: nothing is left to knurl on the highlighted faces.", ["margin"], allFaces);
    }
    piece.zStart = zStart;
    piece.zEnd = zEnd;
    return piece;
}

/**
 * Walks around a band through the shared side edges (straight edges along the band direction).
 * Returns one step per face: { face, surface, entryEdge, exitEdge }.
 * Throws when a face does not run along the direction or the faces do not form one closed loop.
 */
function bandFaceOrder(context is Context, faces is array, direction is Vector, allFaces is Query) returns array
{
    var items = [];
    var edgeOwners = {};
    for (var faceIndex = 0; faceIndex < size(faces); faceIndex += 1)
    {
        const face = faces[faceIndex];
        const surface = evSurfaceDefinition(context, { "face" : face });
        const runsAlong = (surface is Cylinder && abs(dot(surface.coordSystem.zAxis, direction)) > 1 - KNURL_DIRECTION_TOLERANCE) ||
            (surface is Plane && abs(dot(surface.normal, direction)) < KNURL_DIRECTION_TOLERANCE);
        if (!runsAlong)
        {
            throw regenError("A band can only contain planes and cylinders that all run along the same direction. " ~
                    "The highlighted face does not (top fillets and corner blends are not supported).", ["faces"], face);
        }
        var sideEdges = [];
        for (var edge in evaluateQuery(context, qGeometry(qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE), GeometryType.LINE)))
        {
            if (abs(dot(evLine(context, { "edge" : edge }).direction, direction)) > 1 - KNURL_DIRECTION_TOLERANCE)
            {
                sideEdges = append(sideEdges, edge);
                edgeOwners[edge] = append(edgeOwners[edge] == undefined ? [] : edgeOwners[edge], faceIndex);
            }
        }
        if (size(sideEdges) != 2)
        {
            throw regenError("The highlighted face is not bounded by two straight edges along the band direction, so it cannot be part of a band.",
                ["faces"], face);
        }
        items = append(items, { "face" : face, "surface" : surface, "sideEdges" : sideEdges });
    }

    var order = [];
    var current = 0;
    var entryEdge = items[0].sideEdges[0];
    for (var step = 0; step < size(items); step += 1)
    {
        const sideEdges = items[current].sideEdges;
        const exitEdge = sideEdges[0] == entryEdge ? sideEdges[1] : sideEdges[0];
        order = append(order, { "face" : items[current].face, "surface" : items[current].surface, "entryEdge" : entryEdge, "exitEdge" : exitEdge });
        var next;
        for (var owner in edgeOwners[exitEdge])
        {
            if (owner != current)
                next = owner;
        }
        if (next == undefined)
        {
            throw regenError("The selected faces do not form a closed band. Select all faces around the part (open strips are not supported).",
                ["faces"], items[current].face);
        }
        current = next;
        entryEdge = exitEdge;
    }
    if (current != 0)
    {
        throw regenError("The selected faces do not form a single closed band.", ["faces"], allFaces);
    }
    return order;
}

/** Twice the signed area enclosed by the segment loop (positive = counterclockwise). */
function loopTwiceArea(segments is array) returns ValueWithUnits
{
    var twiceArea = 0 * meter * meter;
    for (var segment in segments)
    {
        var points = [segment.startPoint, segment.endPoint];
        if (segment.isArc)
        {
            const midAngle = segment.startAngle + segment.sweepAngle / 2;
            points = [segment.startPoint, segment.center + segment.radius * vector(cos(midAngle), sin(midAngle)), segment.endPoint];
        }
        for (var j = 0; j + 1 < size(points); j += 1)
        {
            twiceArea += points[j][0] * points[j + 1][1] - points[j + 1][0] * points[j][1];
        }
    }
    return twiceArea;
}

/** The same loop traversed the other way round. */
function reverseLoop(segments is array) returns array
{
    var reversed = [];
    for (var j = size(segments) - 1; j >= 0; j -= 1)
    {
        var segment = segments[j];
        const startPoint = segment.startPoint;
        segment.startPoint = segment.endPoint;
        segment.endPoint = startPoint;
        if (segment.isArc)
        {
            segment.startAngle = segment.startAngle + segment.sweepAngle;
            segment.sweepAngle = -segment.sweepAngle;
        }
        reversed = append(reversed, segment);
    }
    return reversed;
}

/**
 * Point, unit tangent (counterclockwise) and unit direction away from the material at arc length s
 * on the band section. s wraps around the loop.
 */
function bandFrame(piece is map, s is ValueWithUnits) returns map
{
    const wrapped = s - floor(s / piece.length) * piece.length;
    const last = size(piece.segments) - 1;
    for (var i = 0; i <= last; i += 1)
    {
        const segment = piece.segments[i];
        const local = wrapped - segment.arcStart;
        if (local <= segment.length || i == last)
        {
            const fraction = segment.length > 0 * meter ? min(local / segment.length, 1) : 0;
            var point;
            var tangent;
            if (segment.isArc)
            {
                const theta = segment.startAngle + segment.sweepAngle * fraction;
                point = segment.center + segment.radius * vector(cos(theta), sin(theta));
                tangent = (segment.sweepAngle > 0 * degree ? 1 : -1) * vector(-sin(theta), cos(theta));
            }
            else
            {
                point = segment.startPoint + (segment.endPoint - segment.startPoint) * fraction;
                tangent = normalize(segment.endPoint - segment.startPoint);
            }
            return { "point" : point, "tangent" : tangent, "away" : piece.side * vector(tangent[1], -tangent[0]) };
        }
    }
    throw regenError("Internal error: the band section has no segments.");
}

/** Groove count around a band. Pitch is measured perpendicular to the grooves along the band surface. */
function bandLayout(piece is map, spec is map, grooveSet is map) returns map
{
    const developedLength = piece.length * cos(grooveSet.angle);
    const count = spec.spacing == KnurlSpacing.COUNT ? spec.count : round(developedLength / spec.pitch);
    const normalPitch = count > 0 ? developedLength / count : 0 * meter;
    return {
            "count" : count,
            "summary" : count ~ " grooves at " ~ formatAngle(grooveSet) ~ ", pitch " ~ formatLength(normalPitch) ~
                " around a band of " ~ formatLength(piece.length)
        };
}

// ===================================== 5. Tool building =====================================
// Builds the cutter bodies for one groove set. Every builder returns
// { "batches" : [query, ...], "helpers" : [query, ...] }: batches of tool bodies that may be
// subtracted together, and construction bodies (sketches, paths, slabs) to delete afterwards.

function buildGrooveSet(context is Context, id is Id, piece is map, spec is map, grooveSet is map, layout is map) returns map
{
    if (piece.kind == KnurlPieceKind.PLANE)
        return buildPlanarSet(context, id, piece, spec, layout);
    if (piece.kind == KnurlPieceKind.BAND)
        return buildBandSet(context, id, piece, spec, grooveSet, layout);
    return buildRevolvedSet(context, id, piece, spec, grooveSet, layout);
}

/** One groove set on a cylinder or cone: a single twisted (and for cones, scaled) sweep along the axis. */
function buildRevolvedSet(context is Context, id is Id, piece is map, spec is map, grooveSet is map, layout is map) returns map
{
    const count = layout.count;
    const cosAngle = cos(grooveSet.angle);
    const cosHalfAngle = cos(piece.halfAngle);
    const start = piece.origin + piece.zStart * piece.axis;
    const end = piece.origin + piece.zEnd * piece.axis;
    // Groove size is specified at the mid radius; the sketch sits at the start radius.
    const scaleAtStart = piece.rStart / piece.rMid;

    // In a bore the surface curves towards the cutter, so the clearance also covers the sagitta over
    // the transverse cutter width (estimated from the surface width plus two depths).
    var clearance = cutterClearance(spec);
    if (piece.side < 0)
    {
        const transverseHalfWidth = (grooveHalfWidthAtSurface(spec) + 2 * spec.depth) / cosAngle;
        clearance += 2 * transverseHalfWidth * transverseHalfWidth / piece.rMid;
    }

    // Transverse profiles in a sketch perpendicular to the axis (sketch x = surface x axis).
    // u (across the groove) is stretched by 1/cos(angle), since the transverse section of an angled
    // groove is wider than its normal section; v (radial) is stretched by 1/cos(halfAngle), so the
    // depth is measured perpendicular to a cone's surface.
    const sketchId = id + "sketch";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(start, piece.axis, piece.xDir) });
    for (var k = 0; k < count; k += 1)
    {
        const phi = (360 * k / count) * degree;
        const radial = vector(cos(phi), sin(phi));
        const tangent = vector(-sin(phi), cos(phi));
        addGrooveProfile(sketch, "groove" ~ k, spec, clearance, {
                    "base" : piece.rStart * radial,
                    "u" : tangent * (scaleAtStart / cosAngle),
                    "v" : radial * (piece.side * scaleAtStart / cosHalfAngle)
                });
    }
    skSolve(sketch);
    // If neighbouring grooves overlap all around, their outlines enclose the region around the axis. Exclude it.
    const regions = qSubtraction(qSketchRegion(sketchId), qContainsPoint(qSketchRegion(sketchId), start));

    // Path: a straight line on the axis. The twist turns the profiles about it, giving helices.
    const pathId = id + "path";
    opFitSpline(context, pathId, { "points" : [start, end] });

    // Twist so the grooves meet the generators at the set angle at the mid radius
    // (exact everywhere on a cylinder; on a cone the angle varies with the radius).
    const twist = grooveSet.hand * (piece.zEnd - piece.zStart) * tan(grooveSet.angle) / (piece.rMid * cosHalfAngle) * radian;
    var sweepDefinition = { "profiles" : regions, "path" : qCreatedBy(pathId, EntityType.EDGE) };
    if (abs(twist) > KNURL_ANGLE_TOLERANCE)
    {
        // As in the std Sweep feature: hasTwist plus a signed "angle" (without hasTwist the angle is
        // silently ignored). Positive = counterclockwise about the path direction = right hand.
        sweepDefinition.hasTwist = true;
        sweepDefinition.angle = twist;
    }
    if (piece.kind == KnurlPieceKind.CONE)
    {
        // As in the std Sweep feature's Scale option: linear scale along the path.
        sweepDefinition.hasScale = true;
        sweepDefinition.scaleFactor = piece.rEnd / piece.rStart;
    }
    const sweepId = id + "sweep";
    opSweep(context, sweepId, sweepDefinition);

    return {
            "batches" : [qBodyType(qCreatedBy(sweepId, EntityType.BODY), BodyType.SOLID)],
            "helpers" : [qCreatedBy(sketchId, EntityType.BODY), qCreatedBy(pathId, EntityType.BODY)]
        };
}

/**
 * One groove set on a planar face: all profiles in one sketch perpendicular to the groove direction,
 * one extrude across the face, then the cutters are trimmed to a slab over the face.
 */
function buildPlanarSet(context is Context, id is Id, piece is map, spec is map, layout is map) returns map
{
    const clearance = cutterClearance(spec);
    const runout = KNURL_RUNOUT_FACTOR * spec.depth + grooveHalfWidthAtSurface(spec);
    // Sketch plane: normal = groove direction, sketch x = across the grooves, sketch y = face normal.
    const sketchOrigin = piece.origin + (layout.alongMin - runout) * layout.along;
    const sketchId = id + "sketch";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(sketchOrigin, layout.along, layout.across) });
    for (var k = 0; k < layout.count; k += 1)
    {
        addGrooveProfile(sketch, "groove" ~ k, spec, clearance, {
                    "base" : vector(layout.positions[k], 0 * meter),
                    "u" : vector(1, 0),
                    "v" : vector(0, 1)
                });
    }
    skSolve(sketch);

    const extrudeId = id + "extrude";
    opExtrude(context, extrudeId, {
                "entities" : qSketchRegion(sketchId),
                "direction" : layout.along,
                "endBound" : BoundingType.BLIND,
                "endDepth" : layout.alongMax - layout.alongMin + 2 * runout
            });
    const tools = qBodyType(qCreatedBy(extrudeId, EntityType.BODY), BodyType.SOLID);

    // Trim to the face: the slab is the face extruded outwards past the clearance and inwards past the depth.
    const slabId = id + "slab";
    opExtrude(context, slabId, {
                "entities" : piece.face,
                "direction" : piece.normal,
                "endBound" : BoundingType.BLIND,
                "endDepth" : KNURL_TRIM_SLAB_FACTOR * clearance,
                "startBound" : BoundingType.BLIND,
                "startDepth" : KNURL_TRIM_SLAB_FACTOR * spec.depth
            });
    opBoolean(context, id + "trim", {
                "tools" : qCreatedBy(slabId, EntityType.BODY),
                "targets" : tools,
                "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT,
                "keepTools" : true
            });

    return {
            "batches" : [tools],
            "helpers" : [qCreatedBy(sketchId, EntityType.BODY), qCreatedBy(slabId, EntityType.BODY)]
        };
}

/**
 * One groove set on a band. Profiles sit in a sketch perpendicular to the band direction, normal to the
 * band section.
 *   Straight (0 deg): all profiles in one sketch, one extrude along the band direction.
 *   Angled: one sweep per groove along a path sampled from the developed band (s = s0 + z tan(angle));
 *   the profile is locked perpendicular to the band direction, so it stays a transverse section.
 * Groove ends lie in planes perpendicular to the band direction, so they need no trimming.
 */
function buildBandSet(context is Context, id is Id, piece is map, spec is map, grooveSet is map, layout is map) returns map
{
    const count = layout.count;
    const cosAngle = cos(grooveSet.angle);
    const spacing = piece.length / count;
    const start = piece.origin + piece.zStart * piece.axis;
    const height = piece.zEnd - piece.zStart;
    const sketchPlane = plane(start, piece.axis, piece.xDir); // sketch x = xDir, sketch y = axis x xDir = yDir
    const clearance = cutterClearance(spec);
    const profileFrameAt = function(s is ValueWithUnits) returns map
        {
            const frame = bandFrame(piece, s);
            return { "base" : frame.point, "u" : frame.tangent / cosAngle, "v" : frame.away };
        };

    if (abs(grooveSet.angle) < KNURL_ANGLE_TOLERANCE)
    {
        const sketchId = id + "sketch";
        const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
        for (var k = 0; k < count; k += 1)
        {
            addGrooveProfile(sketch, "groove" ~ k, spec, clearance, profileFrameAt(k * spacing));
        }
        skSolve(sketch);
        // If neighbouring grooves overlap all around, their outlines enclose the band's inside. Exclude it.
        const regions = qSubtraction(qSketchRegion(sketchId), qContainsPoint(qSketchRegion(sketchId), start));
        const extrudeId = id + "extrude";
        opExtrude(context, extrudeId, {
                    "entities" : regions,
                    "direction" : piece.axis,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : height
                });
        return {
                "batches" : [qBodyType(qCreatedBy(extrudeId, EntityType.BODY), BodyType.SOLID)],
                "helpers" : [qCreatedBy(sketchId, EntityType.BODY)]
            };
    }

    // Path sampling, dense enough to follow the fillets.
    const slope = grooveSet.hand * tan(grooveSet.angle);
    var arcStep = KNURL_PATH_ARC_STEP;
    if (piece.minRadius != undefined)
        arcStep = min(arcStep, KNURL_PATH_ARC_STEP_PER_RADIUS * piece.minRadius);
    const axialStep = min(KNURL_PATH_MAX_AXIAL_STEP, arcStep / abs(slope));
    const pointCount = min(KNURL_PATH_MAX_POINTS, ceil(height / axialStep) + 1);

    // Each groove is its own swept body, and a boolean fails (BOOLEAN_INVALID) when tools in it overlap.
    // Neighbouring grooves overlap whenever the cutter is wider than the spacing, so they are cut in
    // batches: groove k goes to batch k % batchCount. Grooves left over when the count does not divide
    // evenly each get their own batch, so the last groove never shares a batch with the first.
    const halfTop = cutterHalfWidthAtTop(spec, clearance);
    const batchCount = min(count, floor(2 * halfTop / cosAngle / spacing) + 1);
    const evenCount = count - count % batchCount;
    var batches = makeArray(batchCount + count % batchCount, []);

    var helpers = [];
    for (var k = 0; k < count; k += 1)
    {
        const s0 = k * spacing;
        const grooveId = id + ("groove" ~ k);
        const sketchId = grooveId + "sketch";
        const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
        addGrooveProfile(sketch, "profile", spec, clearance, profileFrameAt(s0));
        skSolve(sketch);

        var points = [];
        for (var j = 0; j < pointCount; j += 1)
        {
            const z = height * j / (pointCount - 1);
            const p = bandFrame(piece, s0 + slope * z).point;
            points = append(points, start + p[0] * piece.xDir + p[1] * piece.yDir + z * piece.axis);
        }
        const pathId = grooveId + "path";
        opFitSpline(context, pathId, { "points" : points });

        const sweepId = grooveId + "sweep";
        opSweep(context, sweepId, {
                    "profiles" : qSketchRegion(sketchId),
                    "path" : qCreatedBy(pathId, EntityType.EDGE),
                    "profileControl" : ProfileControlMode.LOCK_DIRECTION,
                    "lockDirection" : piece.axis
                });
        const batch = k < evenCount ? k % batchCount : batchCount + (k - evenCount);
        batches[batch] = append(batches[batch], qCreatedBy(sweepId, EntityType.BODY));
        helpers = concatenateArrays([helpers, [qCreatedBy(sketchId, EntityType.BODY), qCreatedBy(pathId, EntityType.BODY)]]);
    }
    var batchQueries = [];
    for (var batch in batches)
    {
        batchQueries = append(batchQueries, qBodyType(qUnion(batch), BodyType.SOLID));
    }
    return { "batches" : batchQueries, "helpers" : helpers };
}

/**
 * Adds one groove profile to a sketch. Profile coordinates: u across the groove, v away from the
 * material (0 = original surface). frame.base is the sketch point of (0, 0); frame.u and frame.v map
 * unit u and v to sketch vectors (they may be scaled, but must stay perpendicular).
 */
function addGrooveProfile(sketch is Sketch, profileId is string, spec is map, clearance is ValueWithUnits, frame is map)
{
    const toSketch = function(u is ValueWithUnits, v is ValueWithUnits) returns Vector
        {
            return frame.base + u * frame.u + v * frame.v;
        };
    const depth = spec.depth;

    if (spec.profile == KnurlProfile.V)
    {
        const halfTop = cutterHalfWidthAtTop(spec, clearance);
        skPolyline(sketch, profileId, { "points" : [
                        toSketch(-halfTop, clearance),
                        toSketch(halfTop, clearance),
                        toSketch(0 * meter, -depth),
                        toSketch(-halfTop, clearance)
                    ] });
    }
    else if (spec.profile == KnurlProfile.SQUARE)
    {
        const half = spec.width / 2;
        skPolyline(sketch, profileId, { "points" : [
                        toSketch(-half, -depth),
                        toSketch(half, -depth),
                        toSketch(half, clearance),
                        toSketch(-half, clearance),
                        toSketch(-half, -depth)
                    ] });
    }
    else
    {
        // Round cutter: a circle, or an ellipse where the frame stretches u and v differently.
        const r = spec.cutterRadius;
        const center = toSketch(0 * meter, r - depth);
        const radiusU = r * norm(frame.u);
        const radiusV = r * norm(frame.v);
        if (abs(radiusU - radiusV) < KNURL_SKETCH_TOLERANCE)
        {
            skCircle(sketch, profileId, { "center" : center, "radius" : radiusU });
        }
        else
        {
            const uIsMajor = radiusU > radiusV;
            skEllipse(sketch, profileId, {
                        "center" : center,
                        "majorRadius" : uIsMajor ? radiusU : radiusV,
                        "minorRadius" : uIsMajor ? radiusV : radiusU,
                        "majorAxis" : normalize(uIsMajor ? frame.u : frame.v)
                    });
        }
    }
}

// ===================================== 6. Utilities =====================================

function formatLength(value is ValueWithUnits) returns string
{
    return roundToPrecision(value / millimeter, 3) ~ " mm";
}

/** "30 deg RH", or "0 deg" for straight grooves (no hand). */
function formatAngle(grooveSet is map) returns string
{
    var text = roundToPrecision(grooveSet.angle / degree, 2) ~ " deg";
    if (abs(grooveSet.angle) > KNURL_ANGLE_TOLERANCE)
    {
        text ~= (grooveSet.hand > 0 ? " RH" : " LH");
    }
    return text;
}

/** The angle wrapped into [0, 360) degrees. */
function wrapAngle(angle is ValueWithUnits) returns ValueWithUnits
{
    return angle - floor(angle / (360 * degree)) * 360 * degree;
}

/** The values in their original order, without repeats. */
function uniqueValues(values is array) returns array
{
    var seen = {};
    var result = [];
    for (var value in values)
    {
        if (seen[value] == undefined)
        {
            seen[value] = true;
            result = append(result, value);
        }
    }
    return result;
}
