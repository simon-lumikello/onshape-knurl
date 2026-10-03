// Geometry probe for a knurled band around a rounded rectangle with a vertical (+Z) axis (one API call).
//
// Samples the band perimeter `inset` below the original surface at each height and reports grooves
// crossed and the air fraction, split into flat sides and corner fillets (equal fractions mean the
// pattern continues across the fillets).
//
//   bun scripts/probe-band.ts <center x,y mm> <half width x mm> <half width y mm> <fillet radius mm> <inset mm> <z mm>...
//
// Example (test rounded box): bun scripts/probe-band.ts -60,0 15 15 6 0.2 6.75 12.75
import { grooveCrossings, percent, sampleSolid } from "./lib/rings.ts";

const SAMPLES_PER_RING = 1200;

const args = Bun.argv.slice(2);
if (args.length < 6) {
  console.log("Usage: bun scripts/probe-band.ts <center x,y mm> <half width x mm> <half width y mm> <fillet radius mm> <inset mm> <z mm>...");
  process.exit(1);
}
const [centerText, halfXText, halfYText, filletText, insetText, ...heights] = args;
const [centerX, centerY] = centerText.split(",").map(Number);
const halfX = Number(halfXText), halfY = Number(halfYText), filletRadius = Number(filletText), inset = Number(insetText);

// Rounded-rectangle loop, inset into the material, counterclockwise from the middle of the +X side.
type Piece = { length: number; at: (t: number) => { x: number; y: number; onFillet: boolean } };
const pieces: Piece[] = [];
const straight = (x0: number, y0: number, x1: number, y1: number) =>
  pieces.push({ length: Math.hypot(x1 - x0, y1 - y0), at: (t) => ({ x: x0 + (x1 - x0) * t, y: y0 + (y1 - y0) * t, onFillet: false }) });
const corner = (cx: number, cy: number, startAngle: number) => {
  const r = filletRadius - inset;
  pieces.push({ length: (Math.PI / 2) * r, at: (t) => {
    const angle = startAngle + (Math.PI / 2) * t;
    return { x: cx + r * Math.cos(angle), y: cy + r * Math.sin(angle), onFillet: true };
  } });
};
const flatX = halfX - filletRadius, flatY = halfY - filletRadius;
straight(halfX - inset, -flatY, halfX - inset, flatY); corner(flatX, flatY, 0);
straight(flatX, halfY - inset, -flatX, halfY - inset); corner(-flatX, flatY, Math.PI / 2);
straight(-halfX + inset, flatY, -halfX + inset, -flatY); corner(-flatX, -flatY, Math.PI);
straight(-flatX, -halfY + inset, flatX, -halfY + inset); corner(flatX, -flatY, 1.5 * Math.PI);

const perimeter = pieces.reduce((sum, piece) => sum + piece.length, 0);
const loop: { x: number; y: number; onFillet: boolean }[] = [];
for (let k = 0; k < SAMPLES_PER_RING; k++) {
  let s = (k / SAMPLES_PER_RING) * perimeter;
  for (const piece of pieces) {
    if (s <= piece.length) {
      loop.push(piece.at(s / piece.length));
      break;
    }
    s -= piece.length;
  }
}

const points = heights.flatMap((z) => loop.map((p) => [p.x + centerX, p.y + centerY, Number(z)] as [number, number, number]));
const samples = await sampleSolid(points);
heights.forEach((z, i) => {
  const ring = samples.slice(i * SAMPLES_PER_RING, (i + 1) * SAMPLES_PER_RING);
  let airFlat = 0, airFillet = 0, flatCount = 0, filletCount = 0;
  loop.forEach((point, k) => {
    const air = ring[k] === "0" ? 1 : 0;
    if (point.onFillet) { filletCount++; airFillet += air; } else { flatCount++; airFlat += air; }
  });
  console.log(`z=${z} inset ${inset}: ${grooveCrossings(ring)} groove crossings, air ${percent(airFlat / flatCount)} on flats, ` +
    `${percent(airFillet / filletCount)} on fillets`);
});
