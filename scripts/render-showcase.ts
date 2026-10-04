// Renders the test Part Studio (all knurl cases) to docs/images/showcase.png for the README (one API call).
//
//   bun scripts/render-showcase.ts [eye x,y,z] [width] [height] [show|hide edges]
//
// The eye vector points from the model towards the viewer; world Z stays up.
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { ROOT } from "./lib/config.ts";
import { api, partStudioPath } from "./lib/onshape.ts";

const [eyeText = "0.3,-1,0.65", widthText = "1800", heightText = "800", edges = "hide"] = Bun.argv.slice(2);

const normalize = (v: number[]) => v.map((c) => c / Math.hypot(...v));
const cross = (a: number[], b: number[]) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

// Rows of the view matrix: view right, view up, towards the viewer (model coordinates), no translation.
const toViewer = normalize(eyeText.split(",").map(Number));
const right = normalize(cross([0, 0, 1], toViewer));
const up = cross(toViewer, right);
const viewMatrix = [...right, 0, ...up, 0, ...toViewer, 0].map((c) => c.toFixed(4)).join(",");

const response = await api("GET", partStudioPath() + "/shadedviews", undefined, {
  viewMatrix,
  outputWidth: Number(widthText),
  outputHeight: Number(heightText),
  pixelSize: 0,
  edges,
  useAntiAliasing: true,
});
const outputDir = join(ROOT, "docs", "images");
mkdirSync(outputDir, { recursive: true });
const outputFile = join(outputDir, "showcase.png");
writeFileSync(outputFile, Buffer.from(response.images[0], "base64"));
console.log(`wrote ${outputFile}`);
