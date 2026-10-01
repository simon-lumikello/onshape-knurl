FeatureScript 3083;
import(path : "onshape/std/common.fs", version : "3083.0");

// Knurl dev: seed test geometry. Pushed to the "Test geometry" Feature Studio by `bun scripts/dev.ts seed`.
// Every body gets its own sub-id so cases.json can select faces with qCreatedBy($seed + "<name>", ...).

annotation { "Feature Type Name" : "Knurl test geometry" }
export const knurlTestGeometry = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
    }
    {
        // Cylinder: Ø20 x 40, axis = world Z.
        fCylinder(context, id + "cylinder", {
                    "bottomCenter" : vector(0, 0, 0) * millimeter,
                    "topCenter" : vector(0, 0, 40) * millimeter,
                    "radius" : 10 * millimeter
                });

        // Rounded cube: 30 x 30 x 30, R5 on all edges.
        fCuboid(context, id + "cube", {
                    "corner1" : vector(40, -15, 0) * millimeter,
                    "corner2" : vector(70, 15, 30) * millimeter
                });
        opFillet(context, id + "cubeFillet", {
                    "entities" : qCreatedBy(id + "cube", EntityType.EDGE),
                    "radius" : 5 * millimeter
                });

        // Flat plate: 60 x 40 x 5.
        fCuboid(context, id + "plate", {
                    "corner1" : vector(90, -20, 0) * millimeter,
                    "corner2" : vector(150, 20, 5) * millimeter
                });

        // Cone frustum: Ø30 -> Ø15, height 30.
        fCone(context, id + "cone", {
                    "bottomCenter" : vector(180, 0, 0) * millimeter,
                    "topCenter" : vector(180, 0, 30) * millimeter,
                    "bottomRadius" : 15 * millimeter,
                    "topRadius" : 7.5 * millimeter
                });

        // Tilted cylinder: Ø20 x 40, axis not aligned to any world axis.
        const tiltBase = vector(0, 60, 0) * millimeter;
        fCylinder(context, id + "tiltedCylinder", {
                    "bottomCenter" : tiltBase,
                    "topCenter" : tiltBase + 40 * millimeter * normalize(vector(1, 0.6, 2)),
                    "radius" : 10 * millimeter
                });
    });
