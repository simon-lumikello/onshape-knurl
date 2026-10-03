// FeatureScript evaluation in the test Part Studio, and conversions between FeatureScript and JS values.
import { api, partStudioPath } from "./onshape.ts";

/** A compiler or runtime notice from the eval endpoint (BTNotice). */
export type Notice = {
  level: "ERROR" | "WARNING" | "INFO";
  type: string;
  message: string;
  stackTrace?: { line: number; column: number }[];
};

export type EvalResult = { value: any; notices: Notice[]; console: string };

/**
 * Runs `script` (a `function(context is Context, queries) { ... }` lambda) in the test Part Studio.
 * `rollbackBarIndex` evaluates the model as it is before that feature (-1 = end of the list).
 * Returns the converted result plus all notices; callers decide which notices are fatal.
 */
export async function evaluate(script: string, rollbackBarIndex = -1): Promise<EvalResult> {
  const response = await api("POST", partStudioPath() + "/featurescript",
    { btType: "BTFeatureScriptEvalCall-2377", script }, { rollbackBarIndex });
  return { value: toJs(response.result), notices: response.notices ?? [], console: response.console ?? "" };
}

/** Like `evaluate`, but throws on ERROR notices and prints the script's console output. */
export async function evaluateOrThrow(script: string, rollbackBarIndex = -1): Promise<any> {
  const result = await evaluate(script, rollbackBarIndex);
  const errors = result.notices.filter((notice) => notice.level === "ERROR");
  if (errors.length) throw new Error("Eval failed:\n" + errors.map((notice) => "  " + formatNotice(notice)).join("\n"));
  if (result.console) console.log(result.console.trimEnd());
  return result.value;
}

export function formatNotice(notice: Notice): string {
  const location = notice.stackTrace?.find((frame) => frame.line > 0);
  return `${notice.type}: ${notice.message}` + (location ? ` (line ${location.line}, col ${location.column})` : "");
}

/** Converts a BTFSValue tree from the eval endpoint into plain JS. */
export function toJs(value: any): any {
  if (value == null) return null;
  const type: string = value.btType ?? "";
  if (type.endsWith("BTFSValueMap")) {
    return Object.fromEntries((value.value ?? []).map((entry: any) => [String(toJs(entry.key)), toJs(entry.value)]));
  }
  if (type.endsWith("BTFSValueArray")) return (value.value ?? []).map(toJs);
  if (type.endsWith("BTFSValueWithUnits")) return `${value.value} ${JSON.stringify(value.unitToPower ?? {})}`;
  return value.value ?? null;
}

const UNIT_NAMES: Record<string, string> = {
  mm: "millimeter", cm: "centimeter", m: "meter", in: "inch", deg: "degree", rad: "radian",
};

/** Converts a dialog expression like "0.5 mm", "30 deg" or "24" into a FeatureScript expression. */
export function quantityToFs(expression: string): string {
  const match = String(expression).trim().match(/^(-?[\d.]+(?:e-?\d+)?)\s*([a-z]*)$/i);
  if (!match) throw new Error(`Cannot convert "${expression}" to FeatureScript`);
  const [, number, unit] = match;
  return unit ? `${number} * ${UNIT_NAMES[unit] ?? unit}` : number;
}
