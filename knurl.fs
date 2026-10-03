FeatureScript 3083;
import(path : "onshape/std/common.fs", version : "3083.0");
// ProfileControlMode (sweep lock direction) is not re-exported by common.fs.
import(path : "onshape/std/profilecontrolmode.gen.fs", version : "3083.0");

// Knurl: Onshape FeatureScript custom feature
// Version: 0.4.0 (Phase 4: closed prismatic bands; plus cylinders, cones and planar faces)
//
// Structure:
//   1. UI layer: enums, bounds, feature precondition
//   2. Spec layer: knurlSpec() turns the dialog definition into a plain spec map
//   3. Planning: analyze every face, lay out the grooves, check the groove budget
//   4. Geometry layer: tool building for revolved faces (cylinder, cone) and planar faces
//
// Revolved faces: every groove set is ONE twisted sweep. The transverse groove profiles (N shapes
// in a sketch perpendicular to the axis) are swept along a straight line on the axis with a twist,
// which turns them about the axis: each groove follows an exact helix and its end caps lie in planes
// perpendicular to the axis. Cones add the sweep's scale factor (r_end / r_start), so the profiles
// grow with the radius; groove size and pitch are taken at the mid radius and scale along the cone.
//
// Planar faces: the groove profiles of one set sit in one sketch perpendicular to the groove
// direction and are extruded across the face in one go; the tools are then trimmed to a slab over
// the face so the grooves stop exactly at the face boundary.
//
// Bands (several edge-connected faces): a closed loop of planes and cylinders that all run along one
// direction, e.g. the sides plus vertical fillets of a rounded box. The cross-section is an ordered
// loop of lines and arcs; grooves are laid out by arc length on the developed band, so pitch and angle
// are exact across the fillets. Straight grooves: one sketch + one extrude. Angled grooves: one sweep per
// groove along a sampled path, with the profile locked perpendicular to the band direction.
//
// Each groove set is subtracted with one boolean per body; crossing sets go one after the other
// (one combined boolean was ~10x slower for diamond knurls). Separately swept band grooves that overlap
// are cut in alternating batches, since overlapping tools in one boolean fail (BOOLEAN_INVALID).
//
// Limits: open strips, bands with top fillets/corner blends, and partial cylinders/cones are rejected
// with a highlighted error. Regen cost grows with grooves x crossings; see "Maximum groove count".

// ===================================== 1. UI layer =====================================

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

const KNURL_DEPTH_BOUNDS = { (meter) : [1e-6, 0.0004, 0.1], (centimeter) : 0.04, (millimeter) : 0.4, (inch) : 0.016 } as LengthBoundSpec;
const KNURL_CUTTER_SIZE_BOUNDS = { (meter) : [1e-6, 0.0005, 0.1], (centimeter) : 0.05, (millimeter) : 0.5, (inch) : 0.02 } as LengthBoundSpec;
const KNURL_PITCH_BOUNDS = { (meter) : [1e-5, 0.001, 0.1], (centimeter) : 0.1, (millimeter) : 1.0, (inch) : 0.04 } as LengthBoundSpec;
const KNURL_MARGIN_BOUNDS = { (meter) : [0, 0, 1], (centimeter) : 0, (millimeter) : 0, (inch) : 0 } as LengthBoundSpec;
const KNURL_ANGLE_BOUNDS = { (degree) : [0, 30, 75], (radian) : 0.5235987756 } as AngleBoundSpec;
const KNURL_TIP_ANGLE_BOUNDS = { (degree) : [10, 90, 170], (radian) : 1.5707963268 } as AngleBoundSpec;
const KNURL_COUNT_BOUNDS = { (unitless) : [1, 40, 5000] } as IntegerBoundSpec;
const KNURL_MAX_GROOVES_BOUNDS = { (unitless) : [1, 500, 20000] } as IntegerBoundSpec;

/** Above this many faces on the knurled bodies the feature reports a regen-time warning. */
const KNURL_FACE_WARNING = 20000;
const KNURL_TOLERANCE = 1e-5 * meter;

annotation { "Feature Type Name" : "Knurl",
        "Feature Type Description" : "Cuts a straight, diagonal or diamond knurl into cylindrical, conical or planar faces." }
export const knurl = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Faces to knurl",
                    "Filter" : EntityType.FACE && (GeometryType.CYLINDER || GeometryType.CONE || GeometryType.PLANE) && ConstructionObject.NO && SketchObject.NO }
        definition.faces is Query;

        annotation { "Group Name" : "Pattern", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Directions", "UIHint" : UIHint.SHOW_LABEL }
            definition.directions is KnurlDirections;

            annotation { "Name" : "Angle (0 = straight)" }
            isAngle(definition.angle, KNURL_ANGLE_BOUNDS);

            if (definition.directions == KnurlDirections.SINGLE)
            {
                annotation { "Name" : "Hand", "UIHint" : UIHint.SHOW_LABEL }
                definition.hand is KnurlHand;
            }
            else
            {
                annotation { "Name" : "Independent second angle" }
                definition.hasSecondAngle is boolean;

                if (definition.hasSecondAngle)
                {
                    annotation { "Name" : "Second angle" }
                    isAngle(definition.secondAngle, KNURL_ANGLE_BOUNDS);
                }
            }

            annotation { "Name" : "Spacing", "UIHint" : UIHint.SHOW_LABEL }
            definition.spacing is KnurlSpacing;

            if (definition.spacing == KnurlSpacing.COUNT)
            {
                annotation { "Name" : "Groove count" }
                isInteger(definition.count, KNURL_COUNT_BOUNDS);
            }
            else
            {
                annotation { "Name" : "Pitch (normal to grooves)" }
                isLength(definition.pitch, KNURL_PITCH_BOUNDS);
            }

            annotation { "Name" : "Reference direction (planar faces)", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1 }
            definition.referenceDirection is Query;
        }

        annotation { "Group Name" : "Cutter", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Profile", "UIHint" : UIHint.SHOW_LABEL }
            definition.profile is KnurlProfile;

            annotation { "Name" : "Depth" }
            isLength(definition.depth, KNURL_DEPTH_BOUNDS);

            if (definition.profile == KnurlProfile.V)
            {
                annotation { "Name" : "Included tip angle" }
                isAngle(definition.tipAngle, KNURL_TIP_ANGLE_BOUNDS);
            }
            else if (definition.profile == KnurlProfile.ROUND)
            {
                annotation { "Name" : "Cutter radius" }
                isLength(definition.cutterRadius, KNURL_CUTTER_SIZE_BOUNDS);
            }
            else if (definition.profile == KnurlProfile.SQUARE)
            {
                annotation { "Name" : "Groove width" }
                isLength(definition.width, KNURL_CUTTER_SIZE_BOUNDS);
            }
        }

        annotation { "Group Name" : "Limits", "Collapsed By Default" : true }
        {
            annotation { "Name" : "Margin from face ends (cylinders, cones)" }
            isLength(definition.margin, KNURL_MARGIN_BOUNDS);

            annotation { "Name" : "Maximum groove count" }
            isInteger(definition.maxGrooves, KNURL_MAX_GROOVES_BOUNDS);
        }
    }
    {
        knurlFeatureBody(context, id, definition);
    });

/** Feature body: validates the selection, plans the grooves, builds tools and cuts. */
function knurlFeatureBody(context is Context, id is Id, definition is map)
{
    const spec = knurlSpec(context, definition);

    const faces = evaluateQuery(context, definition.faces);
    if (size(faces) == 0)
    {
        throw regenError("Select at least one cylindrical, conical or planar face to knurl.", ["faces"]);
    }

    // ---- Plan: analyze every selection piece and lay out every groove set before building anything.
    // A piece is one face, or several edge-connected faces that form a band (Phase 4).
    var plans = [];
    var unsupported = [];
    var totalGrooves = 0;
    for (var component in connectedComponents(context, faces))
    {
        var geo;
        if (size(component) > 1)
        {
            geo = analyzeBand(context, component, spec);
        }
        else
        {
            const surface = evSurfaceDefinition(context, { "face" : component[0] });
            if (surface is Cylinder || surface is Cone)
                geo = analyzeRevolved(context, component[0], surface, spec);
            else if (surface is Plane)
                geo = analyzePlane(context, component[0], spec);
            else
            {
                unsupported = append(unsupported, component[0]);
                continue;
            }
        }
        const pieceFaces = qUnion(component);
        var layouts = [];
        for (var grooveSet in spec.sets)
        {
            var layout;
            if (geo.kind == "PLANE")
                layout = planarLayout(context, geo, spec, grooveSet);
            else if (geo.kind == "BAND")
                layout = bandLayout(geo, spec, grooveSet);
            else
                layout = revolvedLayout(geo, spec, grooveSet);
            const minimum = geo.kind == "PLANE" ? 1 : 3;
            if (layout.count < minimum)
            {
                throw regenError("Pitch is too large for the highlighted faces: it leaves fewer than " ~ minimum ~ " grooves. Reduce the pitch.", ["pitch"], pieceFaces);
            }
            layouts = append(layouts, layout);
            totalGrooves += layout.count;
        }
        plans = append(plans, { "face" : component[0], "geo" : geo, "layouts" : layouts });
    }
    if (size(unsupported) > 0)
    {
        throw regenError("Only cylindrical, conical and planar faces are supported. The highlighted faces are not.", ["faces"], qUnion(unsupported));
    }
    if (totalGrooves > spec.maxGrooves)
    {
        throw regenError("This knurl needs " ~ totalGrooves ~ " grooves, more than the maximum of " ~ spec.maxGrooves ~
                ". Increase the pitch, reduce the groove count, or raise the limit under Limits (regeneration gets slow).",
            ["maxGrooves"], definition.faces);
    }

    // ---- Build tools, grouped by owner body, groove set and batch:
    // toolsByBody[body]["<set>_<batch>"] = tool queries cut together in one boolean.
    // Builders return batches of tools that do not overlap each other (see buildBandSet).
    var toolsByBody = {};
    var helpers = [];
    var summary = [];
    for (var i = 0; i < size(plans); i += 1)
    {
        const plan = plans[i];
        // Evaluated (transient) body query, so faces of the same body share one map key.
        const body = evaluateQuery(context, qOwnerBody(plan.face))[0];
        var toolGroups = toolsByBody[body] == undefined ? {} : toolsByBody[body];
        for (var k = 0; k < size(spec.sets); k += 1)
        {
            const setId = id + ("face" ~ i ~ "set" ~ k);
            const layout = plan.layouts[k];
            var built;
            if (plan.geo.kind == "PLANE")
                built = buildPlanarSet(context, setId, plan.geo, spec, layout);
            else if (plan.geo.kind == "BAND")
                built = buildBandSet(context, setId, plan.geo, spec, spec.sets[k], layout);
            else
                built = buildRevolvedSet(context, setId, plan.geo, spec, spec.sets[k], layout);
            for (var batch = 0; batch < size(built.batches); batch += 1)
            {
                const key = k ~ "_" ~ batch;
                toolGroups[key] = append(toolGroups[key] == undefined ? [] : toolGroups[key], built.batches[batch]);
            }
            helpers = concatenateArrays([helpers, built.helpers]);
            summary = append(summary, layout.summary);
        }
        toolsByBody[body] = toolGroups;
    }

    // ---- Cut: one subtraction per body and groove set. Crossing sets are cut one after the other: a single
    // boolean would also have to intersect the crossing tools with each other above the surface,
    // which is wasted work and the dominant regen cost for diamond knurls.
    var b = 0;
    var cutBodies = [];
    for (var entry in toolsByBody)
    {
        for (var group in entry.value)
        {
            const toolQuery = qUnion(group.value);
            if (size(evaluateQuery(context, toolQuery)) == 0)
            {
                throw regenError("Could not build the knurl cutter for the highlighted faces.", ["faces"], definition.faces);
            }
            try
            {
                opBoolean(context, id + ("cut" ~ b), {
                            "tools" : toolQuery,
                            "targets" : entry.key,
                            "operationType" : BooleanOperationType.SUBTRACTION
                        });
            }
            catch (error)
            {
                const reason = error is map && error.message != undefined ? " (" ~ toString(error.message) ~ ")" : "";
                throw regenError("Cutting the knurl failed" ~ reason ~ ". Try a smaller depth, a larger pitch, or a different angle.", ["faces"], definition.faces);
            }
            b += 1;
        }
        cutBodies = append(cutBodies, entry.key);
    }

    if (size(helpers) > 0)
    {
        opDeleteBodies(context, id + "cleanup", { "entities" : qUnion(helpers) });
    }

    const faceCount = size(evaluateQuery(context, qOwnedByBody(qUnion(cutBodies), EntityType.FACE)));
    const message = "Knurl: " ~ join(uniqueValues(summary), "; ") ~ ". Result: " ~ faceCount ~ " faces.";
    if (faceCount > KNURL_FACE_WARNING)
    {
        reportFeatureWarning(context, id, message ~ " Heavy model: expect slow regeneration downstream.");
    }
    else
    {
        reportFeatureInfo(context, id, message);
    }
}

// ===================================== 2. Spec layer =====================================

/** Normalizes the dialog definition into a spec map. Groove sets carry a signed hand: +1 right, -1 left. */
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
        if (abs(definition.angle) < 1e-6 * degree && abs(secondAngle) < 1e-6 * degree)
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

/** Half width of the groove at the original surface, measured normal to the groove (unscaled). */
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

function formatLength(value is ValueWithUnits) returns string
{
    return roundToPrecision(value / millimeter, 3) ~ " mm";
}

function formatAngle(grooveSet is map) returns string
{
    var text = roundToPrecision(grooveSet.angle / degree, 2) ~ " deg";
    if (abs(grooveSet.angle) > 1e-6 * degree)
    {
        text ~= (grooveSet.hand > 0 ? " RH" : " LH");
    }
    return text;
}

function uniqueValues(values is array) returns array
{
    var seen = {};
    var result = [];
    for (var v in values)
    {
        if (seen[v] == undefined)
        {
            seen[v] = true;
            result = append(result, v);
        }
    }
    return result;
}

// ===================================== 3. Planning =====================================

/**
 * Axis, radii, side (external/internal) and axial extent of a cylindrical or conical face.
 * Positions along the axis (zStart, zEnd) are in the surface's coordinate system; for cones the apex
 * is at z = 0 and radius(z) = z * tan(halfAngle). Throws regenErrors (highlighting the face) for unsupported cases.
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

    // Axial extent in the surface's own coordinate system.
    const extent = evBox3d(context, { "topology" : face, "cSys" : cSys, "tight" : true });
    const zMin = extent.minCorner[2];
    const zMax = extent.maxCorner[2];
    const rMid = radiusAt((zMin + zMax) / 2);

    // On a cone the profile scales with the radius, so this also holds at the small end.
    if (spec.depth >= rMid / 2)
    {
        throw regenError("Knurl depth must be less than half the radius (" ~ formatLength(rMid) ~ ").", ["depth"], face);
    }

    // External (material inside) or internal (bore, countersink): face normal vs radial direction.
    const probe = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) });
    const fromAxis = probe.origin - cSys.origin;
    const radial = fromAxis - dot(fromAxis, axis) * axis;
    const side = dot(probe.normal, radial) > 0 * meter ? 1 : -1;

    // The face must go all the way around. Sample rings at three heights; one complete ring is enough
    // (a ring can be interrupted by a cross hole).
    var isFull = false;
    for (var fraction in [0.5, 0.25, 0.75])
    {
        const z = zMin + (zMax - zMin) * fraction;
        const r = radiusAt(z);
        var ringComplete = true;
        for (var k = 0; k < 24; k += 1)
        {
            const phi = k * 15 * degree;
            const point = cSys.origin + z * axis + r * (cos(phi) * xDir + sin(phi) * yDir);
            if (evDistance(context, { "side0" : face, "side1" : point }).distance > KNURL_TOLERANCE)
            {
                ringComplete = false;
                break;
            }
        }
        if (ringComplete)
        {
            isFull = true;
            break;
        }
    }
    if (!isFull)
    {
        throw regenError("Partial cylindrical or conical faces are not supported yet: the highlighted face does not go all the way around.", ["faces"], face);
    }

    // Groove extent. At an open end (flat end face pointing away) the grooves run out past the
    // end so the cut is clean. Anywhere else (shoulder, chamfer, fillet) they stop at the face boundary.
    const overrun = 2 * spec.depth;
    var zStart = zMin + spec.margin;
    var zEnd = zMax - spec.margin;
    if (spec.margin < KNURL_TOLERANCE)
    {
        if (isOpenEnd(context, face, cSys, zMin, false, qNothing()))
            zStart = zMin - overrun;
        if (isOpenEnd(context, face, cSys, zMax, true, qNothing()))
            zEnd = zMax + overrun;
    }
    if (isCone)
    {
        // Never run out across the apex.
        zStart = max(zStart, zMin / 2);
    }
    if (zEnd - zStart < 2 * KNURL_TOLERANCE)
    {
        throw regenError("Margin is too large: nothing is left to knurl on the highlighted face.", ["margin"], face);
    }

    return {
            "kind" : isCone ? "CONE" : "CYLINDER",
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

/**
 * True if every face touching this end of the surface is a flat end face whose normal points away from it.
 * Faces in `ignore` (the other faces of a band) are not considered.
 */
function isOpenEnd(context is Context, face is Query, cSys is CoordSystem, zEnd is ValueWithUnits, isMaxEnd is boolean, ignore is Query) returns boolean
{
    var touching = false;
    for (var neighbor in evaluateQuery(context, qSubtraction(qAdjacent(face, AdjacencyType.EDGE, EntityType.FACE), ignore)))
    {
        const extent = evBox3d(context, { "topology" : neighbor, "cSys" : cSys, "tight" : true });
        if (extent.minCorner[2] > zEnd + KNURL_TOLERANCE || extent.maxCorner[2] < zEnd - KNURL_TOLERANCE)
            continue;
        touching = true;
        if (!(evSurfaceDefinition(context, { "face" : neighbor }) is Plane))
            return false;
        const normal = evFaceTangentPlane(context, { "face" : neighbor, "parameter" : vector(0.5, 0.5) }).normal;
        const alignment = dot(normal, cSys.zAxis);
        if (isMaxEnd ? alignment < 1 - 1e-6 : alignment > -1 + 1e-6)
            return false;
    }
    return touching;
}

/** Groove count around a revolved face. Pitch and count refer to the mid radius. */
function revolvedLayout(geo is map, spec is map, grooveSet is map) returns map
{
    // Helix angle is measured against the generator (cone: the slanted surface line).
    const circumference = 2 * PI * geo.rMid;
    const count = spec.spacing == KnurlSpacing.COUNT ? spec.count : round(circumference * cos(grooveSet.angle) / spec.pitch);
    const normalPitch = count > 0 ? circumference * cos(grooveSet.angle) / count : 0 * meter;
    var summary = count ~ " grooves at " ~ formatAngle(grooveSet) ~ ", pitch " ~ formatLength(normalPitch) ~ " on R" ~ formatLength(geo.rMid);
    if (geo.kind == "CONE")
    {
        summary ~= " (cone mid radius; scales along the cone)";
    }
    return { "count" : count, "summary" : summary };
}

/** Plane, outward normal and default reference direction of a planar face. */
function analyzePlane(context is Context, face is Query, spec is map) returns map
{
    const tangentPlane = evFaceTangentPlane(context, { "face" : face, "parameter" : vector(0.5, 0.5) });
    const normal = tangentPlane.normal;

    var reference;
    if (spec.referenceDirection != undefined)
    {
        reference = spec.referenceDirection - dot(spec.referenceDirection, normal) * normal;
        if (norm(reference) < 1e-6)
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
            if (length > longest + KNURL_TOLERANCE)
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
    return { "kind" : "PLANE", "face" : face, "origin" : tangentPlane.origin, "normal" : normal, "reference" : reference };
}

/**
 * Groove layout of one set on a planar face: groove direction (reference turned by hand * angle about the
 * normal), the face extent along and across the grooves, and the groove positions across the face.
 * Count mode: grooves across the face width. Pitch mode: grooves at the pitch, centred on the face.
 */
function planarLayout(context is Context, geo is map, spec is map, grooveSet is map) returns map
{
    const turn = grooveSet.hand * grooveSet.angle;
    const along = normalize(cos(turn) * geo.reference + sin(turn) * cross(geo.normal, geo.reference));
    const across = cross(geo.normal, along);
    const cSys = coordSystem(geo.origin, along, geo.normal);
    const extent = evBox3d(context, { "topology" : geo.face, "cSys" : cSys, "tight" : true });
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
        if (half > spec.maxGrooves)
        {
            throw regenError("This knurl needs more than " ~ spec.maxGrooves ~ " grooves. Increase the pitch or raise the limit under Limits.",
                ["maxGrooves"], geo.face);
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

/**
 * Phase 4: a closed band of planes and cylinders that all run along one direction (e.g. the sides and
 * vertical fillets of a rounded box). The band's cross-section perpendicular to that direction is an
 * ordered loop of line and arc segments in section coordinates (origin, xDir, yDir), oriented
 * counterclockwise about the direction, with arc length `length`.
 */
function analyzeBand(context is Context, faces is array, spec is map) returns map
{
    const all = qUnion(faces);

    // Band direction from the first cylinder (fillet).
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
                "To knurl the faces separately, use one Knurl feature per face.", ["faces"], all);
    }
    const xDir = perpendicularVector(direction);
    const yDir = cross(direction, xDir);
    const toSection = function(point is Vector) returns Vector
        {
            const r = point - origin;
            return vector(dot(r, xDir), dot(r, yDir));
        };

    // Per face: surface and its two side edges (straight edges along the band direction).
    var items = [];
    var edgeOwners = {};
    for (var i = 0; i < size(faces); i += 1)
    {
        const face = faces[i];
        const surface = evSurfaceDefinition(context, { "face" : face });
        const runsAlong = (surface is Cylinder && abs(dot(surface.coordSystem.zAxis, direction)) > 1 - 1e-6) ||
            (surface is Plane && abs(dot(surface.normal, direction)) < 1e-6);
        if (!runsAlong)
        {
            throw regenError("A band can only contain planes and cylinders that all run along the same direction. " ~
                    "The highlighted face does not (top fillets and corner blends are not supported).", ["faces"], face);
        }
        var sides = [];
        for (var edge in evaluateQuery(context, qGeometry(qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE), GeometryType.LINE)))
        {
            if (abs(dot(evLine(context, { "edge" : edge }).direction, direction)) > 1 - 1e-6)
            {
                sides = append(sides, edge);
                edgeOwners[edge] = append(edgeOwners[edge] == undefined ? [] : edgeOwners[edge], i);
            }
        }
        if (size(sides) != 2)
        {
            throw regenError("The highlighted face is not bounded by two straight edges along the band direction, so it cannot be part of a band.",
                ["faces"], face);
        }
        items = append(items, { "face" : face, "surface" : surface, "sides" : sides });
    }

    // Walk around the loop through the shared side edges.
    var order = [];
    var current = 0;
    var entry = items[0].sides[0];
    for (var step = 0; step < size(items); step += 1)
    {
        const sides = items[current].sides;
        const exit = sides[0] == entry ? sides[1] : sides[0];
        order = append(order, { "index" : current, "entry" : entry, "exit" : exit });
        var next;
        for (var owner in edgeOwners[exit])
        {
            if (owner != current)
                next = owner;
        }
        if (next == undefined)
        {
            throw regenError("The selected faces do not form a closed band. Select all faces around the part (open strips are not supported yet).",
                ["faces"], items[current].face);
        }
        current = next;
        entry = exit;
    }
    if (current != 0)
    {
        throw regenError("The selected faces do not form a single closed band.", ["faces"], all);
    }

    // Cross-section segments.
    var segments = [];
    var minRadius;
    for (var o in order)
    {
        const item = items[o.index];
        const a = toSection(evLine(context, { "edge" : o.entry }).origin);
        const b = toSection(evLine(context, { "edge" : o.exit }).origin);
        if (item.surface is Plane)
        {
            segments = append(segments, { "shape" : "LINE", "a" : a, "b" : b, "face" : item.face, "length" : norm(b - a) });
        }
        else
        {
            const c = toSection(item.surface.coordSystem.origin);
            const r = item.surface.radius;
            const m = toSection(evFaceTangentPlane(context, { "face" : item.face, "parameter" : vector(0.5, 0.5) }).origin);
            const angleA = atan2(a[1] - c[1], a[0] - c[0]);
            const ccwToB = wrapAngle(atan2(b[1] - c[1], b[0] - c[0]) - angleA);
            const ccwToMid = wrapAngle(atan2(m[1] - c[1], m[0] - c[0]) - angleA);
            const sweep = ccwToMid < ccwToB ? ccwToB : ccwToB - 360 * degree;
            segments = append(segments, { "shape" : "ARC", "a" : a, "b" : b, "c" : c, "r" : r, "start" : angleA, "sweep" : sweep,
                        "face" : item.face, "length" : r * abs(sweep) / radian });
            minRadius = minRadius == undefined ? r : min(minRadius, r);
        }
    }

    // Orient counterclockwise about the band direction (signed area of the loop polygon).
    var twiceArea = 0 * meter * meter;
    for (var seg in segments)
    {
        var points = [seg.a, seg.b];
        if (seg.shape == "ARC")
        {
            const mid = seg.start + seg.sweep / 2;
            points = [seg.a, seg.c + seg.r * vector(cos(mid), sin(mid)), seg.b];
        }
        for (var j = 0; j + 1 < size(points); j += 1)
        {
            twiceArea += points[j][0] * points[j + 1][1] - points[j + 1][0] * points[j][1];
        }
    }
    if (twiceArea < 0 * meter * meter)
    {
        var reversed = [];
        for (var j = size(segments) - 1; j >= 0; j -= 1)
        {
            var seg = segments[j];
            const a = seg.a;
            seg.a = seg.b;
            seg.b = a;
            if (seg.shape == "ARC")
            {
                seg.start = seg.start + seg.sweep;
                seg.sweep = -seg.sweep;
            }
            reversed = append(reversed, seg);
        }
        segments = reversed;
    }
    var length = 0 * meter;
    for (var seg in segments)
    {
        length += seg.length;
    }

    var geo = { "kind" : "BAND", "faces" : all, "origin" : origin, "axis" : direction, "xDir" : xDir, "yDir" : yDir,
        "segments" : segments, "length" : length, "side" : 1 };

    // Material side: compare the loop's outward normal with the face normal on the first segment.
    const probe = bandFrame(geo, segments[0].length / 2);
    const faceNormal = evFaceTangentPlane(context, { "face" : segments[0].face, "parameter" : vector(0.5, 0.5) }).normal;
    if (dot(vector(dot(faceNormal, xDir), dot(faceNormal, yDir)), probe.away) < 0)
    {
        geo.side = -1;
    }

    // Concave fillets must be larger than the groove, or the cutter folds over itself.
    const grooveSize = 2 * (2 * spec.depth + grooveHalfWidthAtSurface(spec));
    for (var seg in segments)
    {
        if (seg.shape != "ARC")
            continue;
        const frame = bandFrame(geo, segmentStart(geo, seg) + seg.length / 2);
        if (dot(frame.away, seg.c - frame.point) > 0 * meter && seg.r < grooveSize)
        {
            throw regenError("The highlighted concave fillet (R" ~ formatLength(seg.r) ~ ") is too tight for this groove size.", ["depth"], seg.face);
        }
    }

    // Axial extent: all band faces must start and end at the same height.
    const cSys = coordSystem(origin, xDir, direction);
    var zMin;
    var zMax;
    for (var face in faces)
    {
        const extent = evBox3d(context, { "topology" : face, "cSys" : cSys, "tight" : true });
        if (zMin != undefined && (abs(extent.minCorner[2] - zMin) > KNURL_TOLERANCE || abs(extent.maxCorner[2] - zMax) > KNURL_TOLERANCE))
        {
            throw regenError("All faces of a band must start and end at the same height along the band direction. The highlighted face does not.",
                ["faces"], face);
        }
        zMin = extent.minCorner[2];
        zMax = extent.maxCorner[2];
    }
    var zStart = zMin + spec.margin;
    var zEnd = zMax - spec.margin;
    if (spec.margin < KNURL_TOLERANCE)
    {
        var openMin = true;
        var openMax = true;
        for (var face in faces)
        {
            openMin = openMin && isOpenEnd(context, face, cSys, zMin, false, all);
            openMax = openMax && isOpenEnd(context, face, cSys, zMax, true, all);
        }
        if (openMin)
            zStart = zMin - 2 * spec.depth;
        if (openMax)
            zEnd = zMax + 2 * spec.depth;
    }
    if (zEnd - zStart < 2 * KNURL_TOLERANCE)
    {
        throw regenError("Margin is too large: nothing is left to knurl on the highlighted faces.", ["margin"], all);
    }
    geo.zStart = zStart;
    geo.zEnd = zEnd;
    geo.minRadius = minRadius;
    return geo;
}

function wrapAngle(angle is ValueWithUnits) returns ValueWithUnits
{
    return angle - floor(angle / (360 * degree)) * 360 * degree;
}

/** Arc length at which a segment starts. */
function segmentStart(geo is map, segment is map) returns ValueWithUnits
{
    var s = 0 * meter;
    for (var seg in geo.segments)
    {
        if (seg == segment)
            return s;
        s += seg.length;
    }
    return s;
}

/** Point, unit tangent (counterclockwise) and unit direction away from the material at arc length s on the band section. */
function bandFrame(geo is map, s is ValueWithUnits) returns map
{
    var t = s - floor(s / geo.length) * geo.length;
    const last = size(geo.segments) - 1;
    for (var i = 0; i <= last; i += 1)
    {
        const seg = geo.segments[i];
        if (t <= seg.length || i == last)
        {
            const f = seg.length > 0 * meter ? min(t / seg.length, 1) : 0;
            var point;
            var tangent;
            if (seg.shape == "LINE")
            {
                point = seg.a + (seg.b - seg.a) * f;
                tangent = normalize(seg.b - seg.a);
            }
            else
            {
                const theta = seg.start + seg.sweep * f;
                point = seg.c + seg.r * vector(cos(theta), sin(theta));
                tangent = (seg.sweep > 0 * degree ? 1 : -1) * vector(-sin(theta), cos(theta));
            }
            return { "point" : point, "tangent" : tangent, "away" : geo.side * vector(tangent[1], -tangent[0]) };
        }
        t -= seg.length;
    }
}

/** Groove count around a band. Pitch is measured normal to the grooves along the band surface. */
function bandLayout(geo is map, spec is map, grooveSet is map) returns map
{
    const developed = geo.length * cos(grooveSet.angle);
    const count = spec.spacing == KnurlSpacing.COUNT ? spec.count : round(developed / spec.pitch);
    const normalPitch = count > 0 ? developed / count : 0 * meter;
    return {
            "count" : count,
            "summary" : count ~ " grooves at " ~ formatAngle(grooveSet) ~ ", pitch " ~ formatLength(normalPitch) ~
                " around a band of " ~ formatLength(geo.length)
        };
}

// ===================================== 4. Geometry layer =====================================

/**
 * One groove set on a cylinder or cone as a single twisted (and for cones, scaled) sweep along the axis.
 * Returns the tool bodies and the helper bodies (sketch, path) to delete afterwards.
 */
function buildRevolvedSet(context is Context, id is Id, geo is map, spec is map, grooveSet is map, layout is map) returns map
{
    const count = layout.count;
    const cosAngle = cos(grooveSet.angle);
    const cosHalfAngle = cos(geo.halfAngle);
    const start = geo.origin + geo.zStart * geo.axis;
    const end = geo.origin + geo.zEnd * geo.axis;
    // Profile size is specified at the mid radius; the sketch sits at the start radius.
    const scaleStart = geo.rStart / geo.rMid;

    // Clearance: how far the cutter extends beyond the original surface. For bores the surface
    // curves towards the cutter, so add the sagitta over the (transverse) cutter width.
    const halfWidth = (grooveHalfWidthAtSurface(spec) + 2 * spec.depth) / cosAngle;
    var clearance = spec.depth;
    if (geo.side < 0)
    {
        clearance += 2 * halfWidth * halfWidth / geo.rMid;
    }

    // Transverse profiles: sketch plane perpendicular to the axis, x = surface x axis.
    // Local profile frame: u across the groove (stretched by 1/cos(angle) in the transverse section),
    // v radially away from the material (stretched by 1/cos(halfAngle) so the depth is normal to a cone).
    const sketchId = id + "sketch";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(start, geo.axis, geo.xDir) });
    for (var k = 0; k < count; k += 1)
    {
        const phi = (360 * k / count) * degree;
        const radial = vector(cos(phi), sin(phi));
        const tangent = vector(-sin(phi), cos(phi));
        addGrooveProfile(sketch, "groove" ~ k, spec, clearance, {
                    "base" : geo.rStart * radial,
                    "u" : tangent * (scaleStart / cosAngle),
                    "v" : radial * (geo.side * scaleStart / cosHalfAngle)
                });
    }
    skSolve(sketch);
    // If neighbouring grooves overlap, their outlines enclose the core region around the axis. Exclude it.
    const regions = qSubtraction(qSketchRegion(sketchId), qContainsPoint(qSketchRegion(sketchId), start));

    // Path: straight line on the axis. The twist turns the profiles about it, giving helices.
    const pathId = id + "path";
    opFitSpline(context, pathId, { "points" : [start, end] });

    // Twist rate so the groove meets the generators at the given angle at the mid radius
    // (exact everywhere on a cylinder; on a cone the angle varies with the radius).
    const twist = grooveSet.hand * (geo.zEnd - geo.zStart) * tan(grooveSet.angle) / (geo.rMid * cosHalfAngle);
    var sweepDefinition = { "profiles" : regions, "path" : qCreatedBy(pathId, EntityType.EDGE) };
    if (abs(twist) > 1e-9)
    {
        // opSweep twist: the std Sweep feature (sweep.fs) passes hasTwist plus a signed "angle"; without
        // hasTwist the angle is silently ignored. Positive = counterclockwise about the path direction
        // (right hand); handedness verified with scripts/probe-knurl.ts.
        sweepDefinition.hasTwist = true;
        sweepDefinition.angle = twist * radian;
    }
    if (geo.kind == "CONE")
    {
        // Same mechanism as the std Sweep feature's Scale option: hasScale + scaleFactor, linear along the path.
        sweepDefinition.hasScale = true;
        sweepDefinition.scaleFactor = geo.rEnd / geo.rStart;
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
 * one extrude across the face, then the tools are trimmed to a slab over the face.
 */
function buildPlanarSet(context is Context, id is Id, geo is map, spec is map, layout is map) returns map
{
    const overrun = 2 * spec.depth + grooveHalfWidthAtSurface(spec);
    const clearance = spec.depth;
    // Sketch plane: normal = groove direction, sketch x = across, sketch y = face normal.
    const sketchOrigin = geo.origin + (layout.alongMin - overrun) * layout.along;
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
                "endDepth" : layout.alongMax - layout.alongMin + 2 * overrun
            });
    const tools = qBodyType(qCreatedBy(extrudeId, EntityType.BODY), BodyType.SOLID);

    // Trim to the face: slab = the face extruded outwards by the clearance and inwards past the depth.
    const slabId = id + "slab";
    opExtrude(context, slabId, {
                "entities" : geo.face,
                "direction" : geo.normal,
                "endBound" : BoundingType.BLIND,
                "endDepth" : 2 * clearance,
                "startBound" : BoundingType.BLIND,
                "startDepth" : 2 * spec.depth
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
 * One groove set on a closed band. Profiles sit in a sketch perpendicular to the band direction,
 * normal to the band section.
 *   Straight (0 deg): all profiles in one sketch, one extrude along the band direction.
 *   Angled: one sweep per groove along a path sampled from the developed band (s = s0 + z tan(angle));
 *   the profile is locked perpendicular to the band direction, so it stays a transverse section.
 * Groove ends lie in planes perpendicular to the band direction, so no extra trimming is needed.
 */
function buildBandSet(context is Context, id is Id, geo is map, spec is map, grooveSet is map, layout is map) returns map
{
    const count = layout.count;
    const cosAngle = cos(grooveSet.angle);
    const spacing = geo.length / count;
    const start = geo.origin + geo.zStart * geo.axis;
    const height = geo.zEnd - geo.zStart;
    const sketchPlane = plane(start, geo.axis, geo.xDir); // sketch x = xDir, sketch y = axis x xDir = yDir
    const clearance = spec.depth;
    const frameAt = function(s is ValueWithUnits) returns map
        {
            const f = bandFrame(geo, s);
            return { "base" : f.point, "u" : f.tangent / cosAngle, "v" : f.away };
        };

    if (abs(grooveSet.angle) < 1e-6 * degree)
    {
        const sketchId = id + "sketch";
        const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
        for (var k = 0; k < count; k += 1)
        {
            addGrooveProfile(sketch, "groove" ~ k, spec, clearance, frameAt(k * spacing));
        }
        skSolve(sketch);
        // If neighbouring grooves overlap all around, their outlines enclose the band's inside. Exclude it.
        const regions = qSubtraction(qSketchRegion(sketchId), qContainsPoint(qSketchRegion(sketchId), start));
        const extrudeId = id + "extrude";
        opExtrude(context, extrudeId, {
                    "entities" : regions,
                    "direction" : geo.axis,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : height
                });
        return {
                "batches" : [qBodyType(qCreatedBy(extrudeId, EntityType.BODY), BodyType.SOLID)],
                "helpers" : [qCreatedBy(sketchId, EntityType.BODY)]
            };
    }

    // Path sampling: dense enough to follow the fillets (spline through points on the developed band).
    const slope = grooveSet.hand * tan(grooveSet.angle);
    var sStep = 0.5 * millimeter;
    if (geo.minRadius != undefined)
        sStep = min(sStep, geo.minRadius / 4);
    const zStep = min(1 * millimeter, sStep / abs(slope));
    const pointCount = min(400, ceil(height / zStep) + 1);

    // Each groove is its own swept body, and a boolean fails (BOOLEAN_INVALID) when tools in it overlap.
    // Neighbouring grooves overlap whenever the cutter (including its clearance above the surface) is wider
    // than the spacing, so cut in batches: groove k goes to batch k % batchCount. Grooves left over when the
    // count does not divide evenly each get their own batch, so the first and last groove never share one.
    var halfTop = spec.width == undefined ? 0 * meter : spec.width / 2;
    if (spec.profile == KnurlProfile.V)
        halfTop = (spec.depth + clearance) * tan(spec.tipAngle / 2);
    else if (spec.profile == KnurlProfile.ROUND)
        halfTop = spec.cutterRadius;
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
        addGrooveProfile(sketch, "profile", spec, clearance, frameAt(s0));
        skSolve(sketch);

        var points = [];
        for (var j = 0; j < pointCount; j += 1)
        {
            const z = height * j / (pointCount - 1);
            const p = bandFrame(geo, s0 + slope * z).point;
            points = append(points, start + p[0] * geo.xDir + p[1] * geo.yDir + z * geo.axis);
        }
        const pathId = grooveId + "path";
        opFitSpline(context, pathId, { "points" : points });

        const sweepId = grooveId + "sweep";
        opSweep(context, sweepId, {
                    "profiles" : qSketchRegion(sketchId),
                    "path" : qCreatedBy(pathId, EntityType.EDGE),
                    "profileControl" : ProfileControlMode.LOCK_DIRECTION,
                    "lockDirection" : geo.axis
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
 * material (0 = original surface). frame.base is the sketch point of (0, 0); frame.u and frame.v
 * map unit u / v to sketch vectors (they may be scaled, but must stay perpendicular).
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
        const halfTop = (depth + clearance) * tan(spec.tipAngle / 2);
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
        if (abs(radiusU - radiusV) < 1e-9 * meter)
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
