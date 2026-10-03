// Compile diagnostics for a Feature Studio module via the Part Studio eval endpoint.
//
// The public API does not return Feature Studio compile errors, but the eval endpoint
// (POST /partstudios/.../featurescript) reports PARSE and SEMANTIC notices with line numbers
// for a lambda. This rewrites the module into one lambda, keeping every line in place:
//   FeatureScript N;           -> "function(context is Context, queries) { var f1; var f2; ..."
//   import(...)                -> blank
//   export                     -> removed
//   top-level annotation {...} -> blank (single-line annotations only)
//   function name(             -> KNURLFN[].name = function(   and every call name( -> (KNURLFN[].name)(
//   enum E { annotation.. A, } -> const E = { "A" : "A", };   and "is E" -> "is string"
// FeatureScript closures capture variables by value, so module functions live in a `box`
// (reference type) created on line 1; lambdas then see functions defined after them.
// Errors that come from this rewrite itself are possible; treat results as diagnostics.
import { api, psPath } from "./onshape.ts";

export type Diagnostic = { level: string; type: string; message: string; line: number; text: string };

export function toLambda(source: string, tail = "return 0;"): string {
  const lines = source.replace(/\r\n/g, "\n").split("\n");
  const fnNames = new Set<string>();
  const enumNames: string[] = [];
  let inEnum = false;
  let inFunction = false;
  const out = lines.map((raw, i) => {
    let line = raw;
    if (i === 0 && /^\s*FeatureScript\s+\d+\s*;/.test(line)) return "@@HEADER@@";
    if (/^import\s*\(/.test(line)) return "";
    line = line.replace(/^export\s+/, "");
    if (/^annotation\s*\{.*\}\s*$/.test(line)) return "";
    if (inEnum) {
      if (/^\s*annotation\s*\{.*\}\s*$/.test(line)) return "";
      if (/^\s*\{\s*$/.test(line)) return line;
      if (/^\s*\}\s*$/.test(line)) { inEnum = false; return line.replace("}", "};"); }
      return line.replace(/^(\s*)([A-Za-z_]\w*)\s*(,?)/, '$1"$2" : "$2"$3');
    }
    let m = line.match(/^enum\s+(\w+)/);
    if (m) { enumNames.push(m[1]); inEnum = true; return line.replace(/^enum\s+(\w+)/, "const $1 ="); }
    m = line.match(/^(function|predicate)\s+(\w+)\s*\(/);
    if (m) { fnNames.add(m[2]); inFunction = true; return line.replace(/^(function|predicate)\s+(\w+)\s*\(/, "@@DEF:$2@@function("); }
    // Close the assignment: the function body ends at the first column-0 "}".
    if (inFunction && /^\}\s*$/.test(line)) { inFunction = false; return "};"; }
    return line;
  });
  let text = out.join("\n");
  for (const n of fnNames) text = text.replace(new RegExp(`(?<![.\\w])${n}\\s*\\(`, "g"), `(KNURLFN[].${n})(`);
  text = text.replace(/@@DEF:(\w+)@@/g, "KNURLFN[].$1 = ");
  text = text.replace("@@HEADER@@", "function(context is Context, queries) { const KNURLFN = new box({});");
  for (const e of enumNames) text = text.replace(new RegExp(`\\bis\\s+${e}\\b`, "g"), "is string");
  return text + "\n" + tail + "\n}\n";
}

/** Converts a dialog expression like "0.5 mm" / "30 deg" / "24" into FeatureScript. */
export function fsQuantity(expr: string): string {
  const units: Record<string, string> = { mm: "millimeter", cm: "centimeter", m: "meter", in: "inch", deg: "degree", rad: "radian" };
  const m = String(expr).trim().match(/^(-?[\d.]+(?:e-?\d+)?)\s*([a-z]*)$/i);
  if (!m) throw new Error(`Cannot convert "${expr}" to FeatureScript`);
  return m[2] ? `${m[1]} * ${units[m[2]] ?? m[2]}` : m[1];
}

export async function checkModule(source: string): Promise<Diagnostic[]> {
  const script = toLambda(source);
  const r = await api("POST", psPath() + "/featurescript", { btType: "BTFeatureScriptEvalCall-2377", script });
  const srcLines = source.replace(/\r\n/g, "\n").split("\n");
  const seen = new Set<string>();
  const result: Diagnostic[] = [];
  for (const n of r.notices ?? []) {
    if (n.level !== "ERROR" && n.level !== "WARNING") continue;
    const loc = (n.stackTrace ?? []).find((s: any) => s.line > 0);
    const line = loc?.line ?? 0;
    const key = `${n.level}|${n.message}|${line}`;
    if (seen.has(key)) continue;
    seen.add(key);
    result.push({ level: n.level, type: n.type, message: n.message, line, text: line ? srcLines[line - 1]?.trim() ?? "" : "" });
  }
  return result;
}

if (import.meta.main) {
  const file = Bun.argv[2] ?? "knurl.fs";
  if (Bun.argv.includes("--show")) { console.log(toLambda(await Bun.file(file).text())); process.exit(0); }
  const diags = await checkModule(await Bun.file(file).text());
  if (!diags.length) console.log("no errors or warnings");
  for (const d of diags) console.log(`${d.level} ${d.type} line ${d.line}: ${d.message}${d.text ? `\n    ${d.text}` : ""}`);
}
