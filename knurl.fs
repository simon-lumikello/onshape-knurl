FeatureScript 3083;
import(path : "onshape/std/common.fs", version : "3083.0");

// Knurl: Onshape FeatureScript custom feature
// Version: 0.0.1 (Phase 0 stub: verifies the dev loop; reports the selected faces, no geometry yet)

annotation { "Feature Type Name" : "Knurl" }
export const knurl = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Faces to knurl", "Filter" : EntityType.FACE }
        definition.faces is Query;

        annotation { "Name" : "Depth" }
        isLength(definition.depth, LENGTH_BOUNDS);
    }
    {
        const faces = evaluateQuery(context, definition.faces);
        if (size(faces) == 0)
        {
            throw regenError("Select at least one face to knurl.", ["faces"]);
        }
        reportFeatureInfo(context, id, "Stub: " ~ size(faces) ~ " face(s) selected, depth " ~ toString(definition.depth));
    });
