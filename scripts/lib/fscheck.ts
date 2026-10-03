// Compile diagnostics and debugging for a Feature Studio module, via the Part Studio eval endpoint.
//
// The public API does not return Feature Studio compile errors, but the eval endpoint reports PARSE
// and SEMANTIC notices with line numbers for a lambda. `toLambda` rewrites a whole module into one
// lambda while keeping every source line on the same line number:
//   FeatureScript N;           -> "function(context is Context, queries) { const KNURLFN = new box({});"
//   import(...)                -> blank
//   export                     -> removed
//   top-level annotation {...} -> blank (single-line annotations only)
//   function name(             -> KNURLFN[].name = function(   and every call name( -> (KNURLFN[].name)(
//   enum E { annotation.. A, } -> const E = { "A" : "A", };     and "is E" -> "is string"
// FeatureScript closures capture variables by value, so module functions live in a `box` (a reference
// type): lambdas created early still see functions assigned later.
// The rewrite relies on the house style of knurl.fs (top-level declarations start in column 0, function
// bodies end with "}" in column 0). Notices caused by the rewrite itself are possible; treat results as
// diagnostics, the Feature Studio's own compile is authoritative.
import { evaluate, type Notice } from "./featurescript.ts";

export type Diagnostic = { level: string; type: string; message: string; line: number; text: string };

export function toLambda(source: string, tail = "return 0;"): string {
  const lines = source.replace(/\r\n/g, "\n").split("\n");
  const functionNames = new Set<string>();
  const enumNames: string[] = [];
  let inEnum = false;
  let inFunction = false;
  const rewritten = lines.map((original, index) => {
    let line = original;
    if (index === 0 && /^\s*FeatureScript\s+\d+\s*;/.test(line)) return "@@HEADER@@";
    if (/^import\s*\(/.test(line)) return "";
    line = line.replace(/^export\s+/, "");
    if (/^annotation\s*\{.*\}\s*$/.test(line)) return "";
    if (inEnum) {
      if (/^\s*annotation\s*\{.*\}\s*$/.test(line)) return "";
      if (/^\s*\{\s*$/.test(line)) return line;
      if (/^\s*\}\s*$/.test(line)) {
        inEnum = false;
        return line.replace("}", "};");
      }
      return line.replace(/^(\s*)([A-Za-z_]\w*)\s*(,?)/, '$1"$2" : "$2"$3');
    }
    let match = line.match(/^enum\s+(\w+)/);
    if (match) {
      enumNames.push(match[1]);
      inEnum = true;
      return line.replace(/^enum\s+(\w+)/, "const $1 =");
    }
    match = line.match(/^(function|predicate)\s+(\w+)\s*\(/);
    if (match) {
      functionNames.add(match[2]);
      inFunction = true;
      return line.replace(/^(function|predicate)\s+(\w+)\s*\(/, "@@DEF:$2@@function(");
    }
    // A top-level function body ends at the first "}" in column 0: close the assignment.
    if (inFunction && /^\}\s*$/.test(line)) {
      inFunction = false;
      return "};";
    }
    return line;
  });
  let text = rewritten.join("\n");
  for (const name of functionNames) text = text.replace(new RegExp(`(?<![.\\w])${name}\\s*\\(`, "g"), `(KNURLFN[].${name})(`);
  text = text.replace(/@@DEF:(\w+)@@/g, "KNURLFN[].$1 = ");
  text = text.replace("@@HEADER@@", "function(context is Context, queries) { const KNURLFN = new box({});");
  for (const name of enumNames) text = text.replace(new RegExp(`\\bis\\s+${name}\\b`, "g"), "is string");
  return text + "\n" + tail + "\n}\n";
}

/** Compile diagnostics (errors and warnings) for a module, mapped back to its source lines. */
export async function checkModule(source: string): Promise<Diagnostic[]> {
  const { notices } = await evaluate(toLambda(source));
  const sourceLines = source.replace(/\r\n/g, "\n").split("\n");
  const seen = new Set<string>();
  const diagnostics: Diagnostic[] = [];
  for (const notice of notices) {
    if (notice.level !== "ERROR" && notice.level !== "WARNING") continue;
    const line = notice.stackTrace?.find((frame) => frame.line > 0)?.line ?? 0;
    const key = `${notice.level}|${notice.message}|${line}`;
    if (seen.has(key)) continue;
    seen.add(key);
    diagnostics.push({ level: notice.level, type: notice.type, message: notice.message, line,
      text: line ? sourceLines[line - 1]?.trim() ?? "" : "" });
  }
  return diagnostics;
}

/**
 * The module as a lambda that ends by calling one feature, for running a case inside eval.
 * defineFeature is replaced by a wrapper that mimics it (start, body, error check, end) but lets
 * exceptions surface with stack traces, where a real feature would only report REGEN_ERROR.
 */
export function debugScript(source: string, featureCall: string): string {
  const wrapper = [
    "(function(f) { return function(context is Context, id is Id, definition is map) {",
    "startFeature(context, id, definition);",
    "f(context, id, definition);",
    'println("feature error: " ~ toString(getFeatureError(context, id)) ~ ", info: " ~ toString(getFeatureInfo(context, id)));',
    "endFeature(context, id);",
    'println("endFeature ok");',
    "}; })(",
  ].join(" ");
  // The wrapper goes on the same line as defineFeature, so line numbers stay aligned with the source.
  return toLambda(source, `${featureCall}\nreturn "ok";`).replace(/=\s*defineFeature\(/, "= " + wrapper);
}

/** Notices from a debug run, with stack frames mapped to source lines. */
export function formatDebugNotices(notices: Notice[], source: string, script: string, sourceName: string): string[] {
  const sourceLines = source.split("\n");
  const scriptLines = script.split("\n");
  const output: string[] = [];
  for (const notice of notices) {
    if (notice.level === "INFO") continue;
    output.push(`${notice.level} ${notice.type}: ${notice.message}`);
    for (const frame of notice.stackTrace ?? []) {
      if (!frame.line) continue;
      output.push(frame.line <= sourceLines.length
        ? `    at ${sourceName}:${frame.line}  ${sourceLines[frame.line - 1]?.trim()}`
        : `    at debug call: ${scriptLines[frame.line - 1]?.trim().slice(0, 120)}`);
    }
  }
  return output;
}
