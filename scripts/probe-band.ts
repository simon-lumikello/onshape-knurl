// Geometry check for a knurled rounded-rectangle band (axis +Z): samples the band perimeter at a given
// inset below the original surface and height, and reports groove crossings and air fraction,
// split into flat sides and corner fillets.
//
//   bun scripts/probe-band.ts <centerX,Y mm> <halfX mm> <halfY mm> <filletR mm> <inset mm> <z mm>...
import { api, psPath } from "./onshape.ts";

const [c, hx, hy, r, inset, ...zs] = Bun.argv.slice(2);
const [cx, cy] = c.split(",").map(Number);
const HX = +hx, HY = +hy, R = +r, D = +inset;
// Rounded rectangle perimeter, counterclockwise from the middle of the +X side.
const fx = HX - R, fy = HY - R;
const pieces: { len: number; at: (t: number) => [number, number, boolean] }[] = [];
const line = (x0: number, y0: number, x1: number, y1: number) =>
  pieces.push({ len: Math.hypot(x1 - x0, y1 - y0), at: (t) => [x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, false] });
const arc = (ox: number, oy: number, a0: number) =>
  pieces.push({ len: (Math.PI / 2) * (R - D), at: (t) => { const a = a0 + (Math.PI / 2) * t; return [ox + (R - D) * Math.cos(a), oy + (R - D) * Math.sin(a), true]; } });
line(HX - D, -fy, HX - D, fy); arc(fx, fy, 0);
line(fx, HY - D, -fx, HY - D); arc(-fx, fy, Math.PI / 2);
line(-HX + D, fy, -HX + D, -fy); arc(-fx, -fy, Math.PI);
line(-fx, -HY + D, fx, -HY + D); arc(fx, -fy, 1.5 * Math.PI);
const total = pieces.reduce((s, p) => s + p.len, 0);
const N = 1200;
const pts: [number, number, boolean][] = [];
for (let i = 0; i < N; i++) {
  let s = (i / N) * total;
  for (const p of pieces) { if (s <= p.len) { pts.push(p.at(s / p.len)); break; } s -= p.len; }
}
const xy = JSON.stringify(pts.map(([x, y]) => [x + cx, y + cy]));
for (const z of zs) {
  const script = `function(context is Context, queries) {
      var s = "";
      for (var p in ${xy})
          s ~= size(evaluateQuery(context, qContainsPoint(qAllModifiableSolidBodies(), vector(p[0], p[1], ${z}) * millimeter))) > 0 ? "1" : "0";
      return s;
  }`;
  const res = await api("POST", psPath() + "/featurescript", { btType: "BTFeatureScriptEvalCall-2377", script });
  for (const n of res.notices ?? []) if (n.level === "ERROR") { console.log(n.message); process.exit(1); }
  const ring: string = res.result.value;
  let crossings = 0, airFlat = 0, airArc = 0, nFlat = 0, nArc = 0;
  for (let i = 0; i < N; i++) {
    if (ring[i] === "0" && ring[(i + N - 1) % N] === "1") crossings++;
    if (pts[i][2]) { nArc++; if (ring[i] === "0") airArc++; } else { nFlat++; if (ring[i] === "0") airFlat++; }
  }
  console.log(`z=${z} inset ${D}: ${crossings} groove crossings, air ${(100 * airFlat / nFlat).toFixed(0)}% on flats, ${(100 * airArc / nArc).toFixed(0)}% on fillets`);
}
