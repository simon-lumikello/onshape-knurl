// Geometry probe for a knurled cylinder or cone (one API call).
//
// Samples a ring of points at each height, `inset` below the original surface (on the material side),
// and reports grooves crossed, air fraction and, between consecutive rings, the rotation of the groove
// pattern. Positive rotation = counterclockwise about +axis going up = right hand.
// For cones pass the radius at each height via repeated `--radius`, or probe one height at a time.
//
//   bun scripts/probe-revolved.ts <origin x,y,z mm> <axis x,y,z> <radius mm> <inset mm> <z mm>... [--internal]
//
// Example (test cylinder, groove depth 0.5): bun scripts/probe-revolved.ts 0,0,0 0,0,1 10 0.25 10 30
import { airFraction, bestShift, grooveCrossings, percent, sampleSolid } from "./lib/rings.ts";

const SAMPLES_PER_RING = 720;

const args = Bun.argv.slice(2).filter((arg) => arg !== "--internal");
const internal = Bun.argv.includes("--internal");
if (args.length < 5) {
  console.log("Usage: bun scripts/probe-revolved.ts <origin x,y,z mm> <axis x,y,z> <radius mm> <inset mm> <z mm>... [--internal]");
  process.exit(1);
}
const [originText, axisText, radiusText, insetText, ...heights] = args;
const origin = originText.split(",").map(Number);
const axis = normalize(axisText.split(",").map(Number));
const xDir = normalize(perpendicular(axis));
const yDir = cross(axis, xDir);
// Inside the material: below the surface for external faces, beyond it for bores.
const sampleRadius = Number(radiusText) + (internal ? 1 : -1) * Number(insetText);

const points: [number, number, number][] = [];
for (const height of heights.map(Number)) {
  for (let k = 0; k < SAMPLES_PER_RING; k++) {
    const phi = (2 * Math.PI * k) / SAMPLES_PER_RING;
    points.push([0, 1, 2].map((i) =>
      origin[i] + height * axis[i] + sampleRadius * (Math.cos(phi) * xDir[i] + Math.sin(phi) * yDir[i])) as [number, number, number]);
  }
}
const samples = await sampleSolid(points);
const rings = heights.map((_, i) => samples.slice(i * SAMPLES_PER_RING, (i + 1) * SAMPLES_PER_RING));

rings.forEach((ring, i) => {
  console.log(`z=${heights[i]}: ${grooveCrossings(ring)} groove crossings, ${percent(airFraction(ring))} air`);
  if (i > 0) {
    const { shift, match } = bestShift(rings[i - 1], ring);
    console.log(`  pattern rotation from z=${heights[i - 1]}: ${((shift * 360) / SAMPLES_PER_RING).toFixed(1)} deg ` +
      `(match ${percent(match)}; ambiguous by multiples of the groove pitch)`);
  }
});

function normalize(v: number[]): number[] {
  const length = Math.hypot(...v);
  return v.map((c) => c / length);
}
function cross(a: number[], b: number[]): number[] {
  return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
}
function perpendicular(v: number[]): number[] {
  return Math.abs(v[0]) < 0.9 ? cross(v, [1, 0, 0]) : cross(v, [0, 1, 0]);
}
