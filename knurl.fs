FeatureScript 3083;
import(path : "onshape/std/common.fs", version : "3083.0");

// Knurl: Onshape FeatureScript custom feature
// Version: 0.2.0 (Phases 1+2: cylindrical faces, single and double/diamond knurls)
//
// Structure:
//   1. UI layer: enums, bounds, feature precondition
//   2. Spec layer: knurlSpec() turns the dialog definition into a plain spec map
//   3. Geometry layer: analyzeCylinder(), buildGrooveSet(), groove profiles
//
// Method (cylinders): every groove set is built as ONE twisted sweep. The transverse groove
// profiles (N shapes in a sketch perpendicular to the cylinder axis) are swept along a straight
// line on the axis with a twist angle = length * tan(helix angle) / radius. The twist rotates
// the profiles about the axis, so each groove follows an exact helix and its end caps lie in
// planes perpendicular to the axis, which trims the grooves at the face ends for free.
// Each groove set is subtracted with one boolean per body; crossing sets go one after the other
// (one combined boolean was ~10x slower for diamond knurls).
//
// Limits (later phases): full cylinders only; planar, conical and tangent-chain faces are rejected
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
const KNURL_COUNT_BOUNDS = { (unitless) : [3, 40, 5000] } as IntegerBoundSpec;
const KNURL_MAX_GROOVES_BOUNDS = { (unitless) : [1, 500, 20000] } as IntegerBoundSpec;

/** Above this many faces on a knurled body the feature reports a regen-time warning. */
const KNURL_FACE_WARNING = 20000;

annotation { "Feature Type Name" : "Knurl", "Feature Type Description" : "Cuts a straight, diagonal or diamond knurl into cylindrical faces." }
export const knurl = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Faces to knurl", "Filter" : EntityType.FACE && GeometryType.CYLINDER && ConstructionObject.NO && SketchObject.NO }
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
            annotation { "Name" : "Margin from face ends" }
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
    const spec = knurlSpec(definition);

    const faces = evaluateQuery(context, definition.faces);
    if (size(faces) == 0)
    {
        throw regenError("Select at least one cylindrical face to knurl.", ["faces"]);
    }

    // Classify faces. Only full cylinders are supported in this version.
    var targets = [];
    var notCylinders = [];
    for (var face in faces)
    {
        const surface = evSurfaceDefinition(context, { "face" : face });
        if (surface is Cylinder)
            targets = append(targets, { "face" : face, "cylinder" : surface });
        else
            notCylinders = append(notCylinders, face);
    }
    if (size(notCylinders) > 0)
    {
        throw regenError("Only cylindrical faces are supported so far. The highlighted faces are not cylinders.", ["faces"], qUnion(notCylinders));
    }

    // Analyze all faces and check the groove budget before building anything.
    var plans = [];
    var totalGrooves = 0;
    for (var target in targets)
    {
        const geo = analyzeCylinder(context, target.face, target.cylinder, spec);
        var counts = [];
        for (var groupSet in spec.sets)
        {
            const n = grooveCount(spec, groupSet, geo.radius);
            if (n < 3)
            {
                throw regenError("Pitch is too large for this face: it leaves fewer than 3 grooves. Reduce the pitch.", ["pitch"], target.face);
            }
            counts = append(counts, n);
            totalGrooves += n;
        }
        plans = append(plans, { "face" : target.face, "geo" : geo, "counts" : counts });
    }
    if (totalGrooves > spec.maxGrooves)
    {
        throw regenError("This knurl needs " ~ totalGrooves ~ " grooves, more than the maximum of " ~ spec.maxGrooves ~
                ". Increase the pitch, reduce the groove count, or raise the limit under Limits (regeneration gets slow).",
            ["maxGrooves"], definition.faces);
    }

    // Build tools, grouped by owner body and groove set: toolsByBody[body][k] = tool queries of set k.
    var toolsByBody = {};
    var helpers = [];
    var summary = [];
    for (var i = 0; i < size(plans); i += 1)
    {
        const plan = plans[i];
        // Evaluated (transient) body query, so faces of the same body share one map key.
        const body = evaluateQuery(context, qOwnerBody(plan.face))[0];
        var toolsBySet = toolsByBody[body] == undefined ? makeArray(size(spec.sets), []) : toolsByBody[body];
        for (var k = 0; k < size(spec.sets); k += 1)
        {
            const setId = id + ("face" ~ i ~ "set" ~ k);
            const built = buildGrooveSet(context, setId, plan.geo, spec, spec.sets[k], plan.counts[k]);
            toolsBySet[k] = append(toolsBySet[k], built.tools);
            helpers = concatenateArrays([helpers, built.helpers]);
            summary = append(summary, grooveSummary(spec.sets[k], plan.counts[k], plan.geo.radius));
        }
        toolsByBody[body] = toolsBySet;
    }

    // One subtraction per body and groove set. Crossing sets are cut one after the other: a single
    // boolean would also have to intersect the crossing tools with each other above the surface,
    // which is wasted work and the dominant regen cost for diamond knurls.
    var b = 0;
    var cutBodies = [];
    for (var entry in toolsByBody)
    {
        for (var setTools in entry.value)
        {
            const toolQuery = qUnion(setTools);
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
                throw regenError("Cutting the knurl failed. Try a smaller depth, a larger pitch, or a different angle.", ["faces"], definition.faces);
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
function knurlSpec(definition is map) returns map
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
    return spec;
}

/** Number of grooves around the circumference for one groove set. */
function grooveCount(spec is map, grooveSet is map, radius is ValueWithUnits) returns number
{
    if (spec.spacing == KnurlSpacing.COUNT)
        return spec.count;
    return round(2 * PI * radius * cos(grooveSet.angle) / spec.pitch);
}

/** Half width of the groove at the original surface, measured normal to the groove. */
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

function grooveSummary(grooveSet is map, count is number, radius is ValueWithUnits) returns string
{
    const normalPitch = 2 * PI * radius * cos(grooveSet.angle) / count;
    var text = count ~ " grooves at " ~ roundToPrecision(grooveSet.angle / degree, 2) ~ " deg";
    if (abs(grooveSet.angle) > 1e-6 * degree)
    {
        text ~= (grooveSet.hand > 0 ? " RH" : " LH");
    }
    return text ~ ", pitch " ~ formatLength(normalPitch) ~ " on R" ~ formatLength(radius);
}

function formatLength(value is ValueWithUnits) returns string
{
    return roundToPrecision(value / millimeter, 3) ~ " mm";
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

// ===================================== 3. Geometry layer =====================================

/**
 * Axis, radius, side (external/internal) and axial extent of a cylindrical face.
 * Throws regenErrors (highlighting the face) for unsupported cases.
 */
function analyzeCylinder(context is Context, face is Query, cylinder is map, spec is map) returns map
{
    const cSys = cylinder.coordSystem;
    const axis = cSys.zAxis;
    const xDir = cSys.xAxis;
    const yDir = cross(axis, xDir);
    const radius = cylinder.radius;
    const tolerance = 1e-5 * meter;

    if (spec.depth >= radius / 2)
    {
        throw regenError("Knurl depth must be less than half the cylinder radius (" ~ formatLength(radius) ~ ").", ["depth"], face);
    }

    // Axial extent in the cylinder's own coordinate system.
    const extent = evBox3d(context, { "topology" : face, "cSys" : cSys, "tight" : true });
    const zMin = extent.minCorner[2];
    const zMax = extent.maxCorner[2];

    // External (material inside) or internal (bore): compare face normal with the radial direction.
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
        var ringComplete = true;
        for (var k = 0; k < 24; k += 1)
        {
            const phi = k * 15 * degree;
            const point = cSys.origin + z * axis + radius * (cos(phi) * xDir + sin(phi) * yDir);
            if (evDistance(context, { "side0" : face, "side1" : point }).distance > tolerance)
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
        throw regenError("Partial cylindrical faces are not supported yet: the highlighted face does not go all the way around.", ["faces"], face);
    }

    // Groove extent. At an open end (flat end face pointing away) the grooves run out past the
    // end so the cut is clean. Anywhere else (shoulder, chamfer, fillet) they stop at the face boundary.
    const overrun = 2 * spec.depth;
    var zStart = zMin + spec.margin;
    var zEnd = zMax - spec.margin;
    if (spec.margin < tolerance)
    {
        if (isOpenEnd(context, face, cSys, zMin, false))
            zStart = zMin - overrun;
        if (isOpenEnd(context, face, cSys, zMax, true))
            zEnd = zMax + overrun;
    }
    if (zEnd - zStart < 2 * tolerance)
    {
        throw regenError("Margin is too large: nothing is left to knurl on the highlighted face.", ["margin"], face);
    }

    return {
            "origin" : cSys.origin,
            "axis" : axis,
            "xDir" : xDir,
            "radius" : radius,
            "side" : side,
            "zStart" : zStart,
            "zEnd" : zEnd
        };
}

/** True if every face touching this end of the cylinder is a flat end face whose normal points away from the cylinder. */
function isOpenEnd(context is Context, face is Query, cSys is CoordSystem, zEnd is ValueWithUnits, isMaxEnd is boolean) returns boolean
{
    const tolerance = 1e-5 * meter;
    var touching = false;
    for (var neighbor in evaluateQuery(context, qAdjacent(face, AdjacencyType.EDGE, EntityType.FACE)))
    {
        const extent = evBox3d(context, { "topology" : neighbor, "cSys" : cSys, "tight" : true });
        if (extent.minCorner[2] > zEnd + tolerance || extent.maxCorner[2] < zEnd - tolerance)
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

/**
 * Builds one groove set (all grooves of one direction) on one cylinder as a single twisted sweep.
 * Returns the tool bodies and the helper bodies (sketch, path) to delete afterwards.
 */
function buildGrooveSet(context is Context, id is Id, geo is map, spec is map, grooveSet is map, count is number) returns map
{
    const cosAngle = cos(grooveSet.angle);
    const start = geo.origin + geo.zStart * geo.axis;
    const end = geo.origin + geo.zEnd * geo.axis;

    // Clearance: how far the cutter extends beyond the original surface. For bores the surface
    // curves towards the cutter, so add the sagitta over the (transverse) cutter width.
    const halfWidth = (grooveHalfWidthAtSurface(spec) + 2 * spec.depth) / cosAngle;
    var clearance = spec.depth;
    if (geo.side < 0)
    {
        clearance += 2 * halfWidth * halfWidth / geo.radius;
    }

    // Transverse profiles: sketch plane perpendicular to the axis, x = cylinder x axis.
    const sketchId = id + "sketch";
    const sketch = newSketchOnPlane(context, sketchId, { "sketchPlane" : plane(start, geo.axis, geo.xDir) });
    for (var k = 0; k < count; k += 1)
    {
        const phi = (360 * k / count) * degree;
        addGrooveProfile(sketch, "groove" ~ k, geo, spec, vector(cos(phi), sin(phi)), vector(-sin(phi), cos(phi)), cosAngle, clearance);
    }
    skSolve(sketch);
    // If neighbouring grooves overlap, their outlines enclose the core region around the axis. Exclude it.
    const regions = qSubtraction(qSketchRegion(sketchId), qContainsPoint(qSketchRegion(sketchId), start));

    // Path: straight line on the axis. The twist turns the profiles about it, giving exact helices.
    const pathId = id + "path";
    opFitSpline(context, pathId, { "points" : [start, end] });

    const twist = grooveSet.hand * (geo.zEnd - geo.zStart) * tan(grooveSet.angle) / geo.radius;
    var sweepDefinition = { "profiles" : regions, "path" : qCreatedBy(pathId, EntityType.EDGE) };
    if (abs(twist) > 1e-9)
    {
        // opSweep twist: the std Sweep feature (sweep.fs) passes hasTwist plus a signed "angle"; without
        // hasTwist the angle is silently ignored. Positive = counterclockwise about the path direction
        // (right hand); handedness verified with scripts/probe-knurl.ts.
        sweepDefinition.hasTwist = true;
        sweepDefinition.angle = twist * radian;
    }
    const sweepId = id + "sweep";
    opSweep(context, sweepId, sweepDefinition);

    return {
            "tools" : qBodyType(qCreatedBy(sweepId, EntityType.BODY), BodyType.SOLID),
            "helpers" : [qCreatedBy(sketchId, EntityType.BODY), qCreatedBy(pathId, EntityType.BODY)]
        };
}

/**
 * Adds one groove profile to the transverse sketch.
 * Local profile coordinates: u across the groove (normal section), v away from the material (0 = original surface).
 * Transverse section = normal section stretched by 1/cos(angle) across the groove.
 */
function addGrooveProfile(sketch is Sketch, profileId is string, geo is map, spec is map, radial is Vector, tangent is Vector,
    cosAngle is number, clearance is ValueWithUnits)
{
    const radius = geo.radius;
    const side = geo.side;
    const toSketch = function(u is ValueWithUnits, v is ValueWithUnits) returns Vector
        {
            return (radius + side * v) * radial + (u / cosAngle) * tangent;
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
        const r = spec.cutterRadius;
        const center = toSketch(0 * meter, r - depth);
        if (abs(cosAngle - 1) < 1e-9)
        {
            skCircle(sketch, profileId, { "center" : center, "radius" : r });
        }
        else
        {
            skEllipse(sketch, profileId, { "center" : center, "majorRadius" : r / cosAngle, "minorRadius" : r, "majorAxis" : tangent });
        }
    }
}
