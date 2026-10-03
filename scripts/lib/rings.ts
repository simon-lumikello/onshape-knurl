// Shared helpers for the geometry probes: sample points in the knurled bodies through eval and
// summarize "rings" of samples as strings of "1" (solid) and "0" (air).
import { evaluateOrThrow } from "./featurescript.ts";

/** Samples whether each point (mm, world coordinates) lies inside any modifiable solid body. */
export async function sampleSolid(points: [number, number, number][]): Promise<string> {
  return evaluateOrThrow(`function(context is Context, queries) {
      var samples = "";
      for (var p in ${JSON.stringify(points)})
          samples ~= size(evaluateQuery(context, qContainsPoint(qAllModifiableSolidBodies(), vector(p[0], p[1], p[2]) * millimeter))) > 0 ? "1" : "0";
      return samples;
  }`);
}

/** Number of air runs in a closed ring of samples = grooves crossed by the ring. */
export function grooveCrossings(ring: string): number {
  let count = 0;
  for (let i = 0; i < ring.length; i++) if (ring[i] === "0" && ring[(i + ring.length - 1) % ring.length] === "1") count++;
  return count;
}

/** Fraction of samples in air. */
export function airFraction(ring: string): number {
  return [...ring].filter((sample) => sample === "0").length / Math.max(ring.length, 1);
}

/** Circular shift (in samples) that best maps ring `a` onto ring `b`, and the share of matching samples. */
export function bestShift(a: string, b: string): { shift: number; match: number } {
  const n = a.length;
  let best = { shift: 0, match: -1 };
  for (let shift = -Math.floor(n / 2); shift < Math.ceil(n / 2); shift++) {
    let matches = 0;
    for (let i = 0; i < n; i++) if (a[i] === b[(i + shift + n) % n]) matches++;
    if (matches > best.match) best = { shift, match: matches };
  }
  return { shift: best.shift, match: best.match / n };
}

export const percent = (fraction: number) => `${(fraction * 100).toFixed(0)}%`;
