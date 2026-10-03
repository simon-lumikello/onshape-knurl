// Test Part Studio: regression cases, feature upserts, query resolution and status reports.
import { seed } from "./config.ts";
import { evaluateOrThrow, quantityToFs } from "./featurescript.ts";
import { api, partStudioPath, withConflictRetry } from "./onshape.ts";
import { DevError } from "./studio.ts";

/** One regression case: a Knurl feature with these parameter values (see test/cases.json). */
export type TestCase = { name: string; suppressed?: boolean; params: Record<string, string | number | boolean> };
export type CaseFile = { featureType: string; cases: TestCase[] };

/** Feature list of the test Part Studio (features, featureStates, ...). */
export async function getFeatures(): Promise<any> {
  return api("GET", partStudioPath() + "/features", undefined, { rollbackBarIndex: -1 });
}

/** Id of the seed feature that builds the test bodies (referenced as $seed in case queries). */
export function findSeedFeatureId(features: any[]): string | undefined {
  const seeds = features.filter((feature) => feature.featureType === seed.featureType);
  if (seeds.length > 1) console.log(`! ${seeds.length} seed features found; using "${seed.featureName}". Delete the extras in Onshape.`);
  return (seeds.find((feature) => feature.name === seed.featureName) ?? seeds[0])?.featureId;
}

/** Replaces `$seed` in a FeatureScript query expression with the seed feature's id. */
export function expandSeed(expression: string, seedFeatureId: string | undefined): string {
  if (!expression.includes("$seed")) return expression;
  if (!seedFeatureId) throw new DevError(`A query uses $seed, but there is no "${seed.featureName}" feature. Run: bun run seed`);
  return expression.replaceAll("$seed", `makeId("${seedFeatureId}")`);
}

/** Deterministic ids matched by a FeatureScript query expression, with the model rolled back to `rollbackBarIndex`. */
export async function resolveQuery(expression: string, seedFeatureId: string | undefined, rollbackBarIndex = -1): Promise<string[]> {
  const script = `function(context is Context, queries) {
      return transientQueriesToStrings(evaluateQuery(context, ${expandSeed(expression, seedFeatureId)}));
  }`;
  return (await evaluateOrThrow(script, rollbackBarIndex)) ?? [];
}

// ---------- parameter values ----------

/** A parameter value, independent of how it is sent (API parameter or FeatureScript argument). */
type ParameterValue =
  | { kind: "quantity"; expression: string }
  | { kind: "enum" | "string"; value: string }
  | { kind: "boolean"; value: boolean }
  | { kind: "query"; expression: string | null }; // null = empty selection

/**
 * Values for every parameter of the feature spec: the case's value where given, else the spec default.
 * All parameters are always sent because features added through the API do not get defaults filled in;
 * a missing parameter then fails the precondition with a bare REGEN_ERROR.
 */
function parameterValues(spec: any, testCase: TestCase): { parameterSpec: any; value: ParameterValue }[] {
  for (const id of Object.keys(testCase.params)) {
    if (!spec.parameters.some((p: any) => p.parameterId === id)) {
      throw new DevError(`Case "${testCase.name}": unknown parameter "${id}". The feature has: ${spec.parameters.map((p: any) => p.parameterId).join(", ")}`);
    }
  }
  return spec.parameters.map((parameterSpec: any) => {
    const given = testCase.params[parameterSpec.parameterId];
    const value = given === undefined ? defaultValue(parameterSpec) : caseValue(parameterSpec, given, testCase.name);
    return { parameterSpec, value };
  });
}

function parameterKind(parameterSpec: any): ParameterValue["kind"] {
  const type: string = parameterSpec.btType;
  if (type.startsWith("BTParameterSpecQuantity")) return "quantity";
  if (type.startsWith("BTParameterSpecEnum")) return "enum";
  if (type.startsWith("BTParameterSpecBoolean")) return "boolean";
  if (type.startsWith("BTParameterSpecString")) return "string";
  if (type.startsWith("BTParameterSpecQuery")) return "query";
  throw new DevError(`Parameter type ${type} ("${parameterSpec.parameterId}") is not supported by the dev tooling yet`);
}

function caseValue(parameterSpec: any, given: string | number | boolean, caseName: string): ParameterValue {
  const kind = parameterKind(parameterSpec);
  switch (kind) {
    case "quantity":
    case "query":
      return { kind, expression: String(given) };
    case "boolean":
      return { kind, value: Boolean(given) };
    case "enum":
      if (parameterSpec.options && !parameterSpec.options.includes(given)) {
        throw new DevError(`Case "${caseName}": ${parameterSpec.parameterId}="${given}" is not one of ${parameterSpec.options.join(", ")}`);
      }
      return { kind, value: String(given) };
    case "string":
      return { kind, value: String(given) };
  }
}

function defaultValue(parameterSpec: any): ParameterValue {
  const kind = parameterKind(parameterSpec);
  const fallback = parameterSpec.defaultValue ?? {};
  switch (kind) {
    case "quantity":
      return { kind, expression: quantityExpression(Number(fallback.value), fallback.units ?? "") };
    case "query":
      return { kind, expression: null };
    case "boolean":
      return { kind, value: Boolean(fallback.value) };
    case "enum":
      return { kind, value: String(fallback.value ?? parameterSpec.options?.[0]) };
    case "string":
      return { kind, value: String(fallback.value ?? "") };
  }
}

/** Dialog expression for a spec default given in SI units, e.g. 0.0004 meter -> "0.4 mm". */
function quantityExpression(value: number, units: string): string {
  if (units === "meter") return `${+(value * 1000).toPrecision(12)} mm`;
  if (units === "degree") return `${value} deg`;
  if (units === "radian") return `${value} rad`;
  return units ? `${value} ${units}` : `${value}`;
}

/** BTM parameters for the API. Query expressions are resolved against the model at `rollbackBarIndex`. */
async function apiParameters(spec: any, testCase: TestCase, seedFeatureId: string | undefined, rollbackBarIndex: number) {
  const parameters: any[] = [];
  for (const { parameterSpec, value } of parameterValues(spec, testCase)) {
    const parameterId = parameterSpec.parameterId;
    switch (value.kind) {
      case "quantity":
        parameters.push({ btType: "BTMParameterQuantity-147", parameterId, expression: value.expression });
        break;
      case "enum":
        parameters.push({ btType: "BTMParameterEnum-145", parameterId, value: value.value,
          enumName: parameterSpec.enumName, namespace: parameterSpec.namespace ?? "" });
        break;
      case "boolean":
        parameters.push({ btType: "BTMParameterBoolean-144", parameterId, value: value.value });
        break;
      case "string":
        parameters.push({ btType: "BTMParameterString-149", parameterId, value: value.value });
        break;
      case "query": {
        let queries: any[] = [];
        if (value.expression !== null) {
          const ids = await resolveQuery(value.expression, seedFeatureId, rollbackBarIndex);
          if (!ids.length) throw new DevError(`Case "${testCase.name}": the query for "${parameterId}" matched nothing: ${value.expression}`);
          queries = [{ btType: "BTMIndividualQuery-138", deterministicIds: ids }];
        }
        parameters.push({ btType: "BTMParameterQueryList-148", parameterId, queries });
        break;
      }
    }
  }
  return parameters;
}

/** The definition map as a FeatureScript expression, for calling the feature directly (debug runs). */
export function featureScriptDefinition(spec: any, testCase: TestCase, seedFeatureId: string | undefined): string {
  const entries = parameterValues(spec, testCase).map(({ parameterSpec, value }) => {
    let expression: string;
    switch (value.kind) {
      case "quantity":
        expression = quantityToFs(value.expression);
        break;
      case "query":
        expression = value.expression === null ? "qNothing()" : expandSeed(value.expression, seedFeatureId);
        break;
      case "boolean":
        expression = String(value.value);
        break;
      default:
        // Enums become their value strings in the debug rewrite (see fscheck.ts).
        expression = JSON.stringify(value.value);
    }
    return `"${parameterSpec.parameterId}" : ${expression}`;
  });
  return `{ ${entries.join(", ")} }`;
}

// ---------- features ----------

/**
 * Adds the case's feature, or updates it if a feature with the same name and type exists.
 * Queries are resolved against the model as it is just before this feature. Returns the feature id.
 */
export async function upsertFeature(spec: any, testCase: TestCase, features: any[], seedFeatureId: string | undefined): Promise<string> {
  const index = features.findIndex((feature) => feature.name === testCase.name && feature.featureType === spec.featureType);
  const existing = index >= 0 ? features[index] : undefined;
  const parameters = await apiParameters(spec, testCase, seedFeatureId, existing ? index : -1);
  const body = {
    btType: "BTFeatureDefinitionCall-1406",
    feature: {
      btType: "BTMFeature-134",
      featureType: spec.featureType,
      namespace: spec.namespace,
      name: testCase.name,
      suppressed: !!testCase.suppressed,
      parameters,
      ...(existing ? { featureId: existing.featureId } : {}),
    },
  };
  const response = await withConflictRetry(() => existing
    ? api("POST", `${partStudioPath()}/features/featureid/${existing.featureId}`, body)
    : api("POST", `${partStudioPath()}/features`, body));
  console.log(`• ${existing ? "updated" : "added"} "${testCase.name}"`);
  return response.feature.featureId;
}

/** Suppresses or unsuppresses (one batch call) the non-seed features whose name contains any of `names`. */
export async function setSuppressed(names: string[], suppressed: boolean): Promise<void> {
  const { features } = await getFeatures();
  const matches = features.filter((feature: any) => feature.featureType !== seed.featureType && names.some((name) => feature.name.includes(name)));
  if (!matches.length) throw new DevError(`No feature name contains ${names.map((n) => `"${n}"`).join(" or ")}`);
  await api("POST", partStudioPath() + "/features/updates", {
    btType: "BTUpdateFeaturesCall-1748",
    updateSuppressionAttributes: true,
    features: matches.map((feature: any) => ({ btType: "BTMFeature-134", featureId: feature.featureId, suppressed, parameters: [] })),
  });
  console.log(`• ${suppressed ? "suppressed" : "unsuppressed"}: ${matches.map((feature: any) => feature.name).join(", ")}`);
}

const STATUS_ICONS: Record<string, string> = { OK: "✓", INFO: "i", WARNING: "!" };

/**
 * Prints status and error / warning / info text for the given features, plus model totals.
 * The texts come from getFeatureError/Warning/Info through eval (the features endpoint only has the status).
 * Returns false if any feature is in ERROR.
 */
export async function reportStatus(featureIds: string[]): Promise<boolean> {
  const { features, featureStates } = await getFeatures();
  const byId = new Map(features.map((feature: any) => [feature.featureId, feature]));
  const messages = featureIds.length ? await evaluateOrThrow(`function(context is Context, queries) {
      var out = {};
      for (var featureId in ${JSON.stringify(featureIds)})
      {
          const id = makeId(featureId);
          out[featureId] = { "error" : getFeatureError(context, id), "warning" : getFeatureWarning(context, id), "info" : getFeatureInfo(context, id) };
      }
      out["__bodies"] = size(evaluateQuery(context, qAllModifiableSolidBodies()));
      out["__faces"] = size(evaluateQuery(context, qOwnedByBody(qAllModifiableSolidBodies(), EntityType.FACE)));
      return out;
  }`) : {};
  console.log("\nFeature status:");
  for (const featureId of featureIds) {
    const feature: any = byId.get(featureId);
    const status = featureStates?.[featureId]?.featureStatus ?? "?";
    const text = messages[featureId] ?? {};
    console.log(`  ${STATUS_ICONS[status] ?? "✗"} ${feature?.name ?? featureId}: ${status}${feature?.suppressed ? " (suppressed)" : ""}`);
    for (const kind of ["error", "warning", "info"]) if (text[kind] != null) console.log(`      ${kind}: ${text[kind]}`);
  }
  if (messages.__bodies != null) console.log(`  model: ${messages.__bodies} solid bodies, ${messages.__faces} faces`);
  return !featureIds.some((featureId) => featureStates?.[featureId]?.featureStatus === "ERROR");
}
