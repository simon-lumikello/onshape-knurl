// Geometry check for a knurled cylinder: samples rings of points just under the original surface
// at two heights and reports how many grooves each ring crosses and how the groove pattern rotates
// between the rings (sign = hand: + is counterclockwise about +axis going up = right hand).
//
//   bun scripts/probe-knurl.ts <axisX,Y,Z mm> <dirX,Y,Z> <radius mm> <depth mm> <z1 mm> <z2 mm> [internal]
import { api, psPath } from "./onshape.ts";

const [o, d, r, depth, z1, z2, internal] = Bun.argv.slice(2);
const N = 720;
const sign = internal ? 1 : -1; // probe radius: inside material just below the original surface
const script = `function(context is Context, queries) {
    const origin = vector(${o}) * millimeter;
    const axis = normalize(vector(${d}));
    const xDir = perpendicularVector(axis);
    const yDir = cross(axis, xDir);
    const rho = (${r} + ${sign} * ${depth} * 0.5) * millimeter;
    const bodies = qAllModifiableSolidBodies();
    var rings = [];
    for (var z in [${z1}, ${z2}])
    {
        var ring = "";
        for (var k = 0; k < ${N}; k += 1)
        {
            const phi = k * 360 / ${N} * degree;
            const p = origin + z * millimeter * axis + rho * (cos(phi) * xDir + sin(phi) * yDir);
            ring ~= size(evaluateQuery(context, qContainsPoint(bodies, p))) > 0 ? "1" : "0";
        }
        rings = append(rings, ring);
    }
    return rings;
}`;
const res = await api("POST", psPath() + "/featurescript", { btType: "BTFeatureScriptEvalCall-2377", script });
for (const n of res.notices ?? []) if (n.level === "ERROR") { console.log(n.message); process.exit(1); }
const rings: string[] = res.result.value.map((v: any) => v.value);
const grooves = (s: string) => { let c = 0; for (let i = 0; i < s.length; i++) if (s[i] === "0" && s[(i + s.length - 1) % s.length] === "1") c++; return c; };
const air = (s: string) => [...s].filter((ch) => ch === "0").length / s.length;
// Best circular shift of ring 2 relative to ring 1 (in samples), restricted to +-half a pitch for single sets.
let best = 0, bestScore = -1;
for (let s = -N / 2; s < N / 2; s++) {
  let score = 0;
  for (let i = 0; i < N; i++) if (rings[0][i] === rings[1][(i + s + N) % N]) score++;
  if (score > bestScore) { bestScore = score; best = s; }
}
console.log(`ring z=${z1}: ${grooves(rings[0])} groove crossings, ${(air(rings[0]) * 100).toFixed(0)}% air`);
console.log(`ring z=${z2}: ${grooves(rings[1])} groove crossings, ${(air(rings[1]) * 100).toFixed(0)}% air`);
console.log(`best match shift: ${(best * 360 / N).toFixed(1)} deg (match ${(bestScore / N * 100).toFixed(0)}%)`);
console.log(rings[0].slice(0, 120));
console.log(rings[1].slice(0, 120));
