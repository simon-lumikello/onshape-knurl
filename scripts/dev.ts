// Knurl dev loop CLI. Run from the project root (Bun loads .env from the working directory).
// See docs/DEVELOPMENT.md for setup and workflow.
import { debugFeatureId, onshape, paths, seed } from "./lib/config.ts";
import { evaluate, evaluateOrThrow } from "./lib/featurescript.ts";
import { checkModule, debugScript, formatDebugNotices } from "./lib/fscheck.ts";
import { ApiError, featureStudioPath, api } from "./lib/onshape.ts";
import {
  type CaseFile, featureScriptDefinition, findSeedFeatureId, getFeatures, reportStatus, resolveQuery,
  setSuppressed, upsertFeature,
} from "./lib/partstudio.ts";
import { DevError, findFeatureStudio, pushFeatureStudio, readProjectFile, requireFeatureSpec } from "./lib/studio.ts";

const HELP = `Usage: bun scripts/dev.ts [command] [options]

Commands:
  run (default)        push knurl.fs, check it compiles, add/update the test cases, report status
  seed                 push test/geometry.fs and add the seed feature (once per test document)
  status               report the status of the seed and the test cases (no push, no updates)
  check                compile diagnostics for knurl.fs with line numbers (via eval, no push)
  debug <case>         run one case inside eval, with stack traces (case name substring)
  eval <script>        evaluate a FeatureScript lambda in the test Part Studio
  faces <query>        list the deterministic ids a query matches ($seed = seed feature id)
  suppress <name>...   suppress test features whose name contains a given substring
  unsuppress <name>... unsuppress them again
  help                 show this text

Options:
  --case <substring>   run: only add/update cases whose name contains the substring
  --cases <file>       use another case file instead of ${paths.cases}
  --force              run/seed: overwrite a Feature Studio that was edited in Onshape`;

const args = Bun.argv.slice(2);
const OPTIONS_WITH_VALUES = ["--case", "--cases"];
const hasFlag = (name: string) => args.includes(name);
const optionValue = (name: string) => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
};
const positional = args.filter((arg, index) => !arg.startsWith("--") && !OPTIONS_WITH_VALUES.includes(args[index - 1]));
const [command = "run", ...commandArgs] = positional;

const loadCases = (): CaseFile => JSON.parse(readProjectFile(optionValue("--cases") ?? paths.cases));

async function run(): Promise<number> {
  const caseFile = loadCases();
  const source = readProjectFile(paths.feature);
  await pushFeatureStudio(onshape.featureStudioId, source, paths.feature, hasFlag("--force"));
  const spec = await requireFeatureSpec(onshape.featureStudioId, caseFile.featureType, paths.feature, source);
  const filter = optionValue("--case");
  const cases = caseFile.cases.filter((testCase) => !filter || testCase.name.includes(filter));
  const { features } = await getFeatures();
  const seedFeatureId = findSeedFeatureId(features);
  const featureIds: string[] = [];
  for (const testCase of cases) {
    const featureId = await upsertFeature(spec, testCase, features, seedFeatureId);
    featureIds.push(featureId);
    // Keep the local feature list current instead of fetching it again (saves API quota).
    if (!features.some((feature: any) => feature.featureId === featureId)) {
      features.push({ featureId, name: testCase.name, featureType: spec.featureType });
    }
  }
  return (await reportStatus(featureIds)) ? 0 : 2;
}

async function seedTestDocument(): Promise<number> {
  const elementId = (await findFeatureStudio(seed.studioName, true))!;
  const source = readProjectFile(paths.seedGeometry);
  await pushFeatureStudio(elementId, source, seed.studioName, hasFlag("--force"));
  const spec = await requireFeatureSpec(elementId, seed.featureType, seed.studioName, source);
  const { features } = await getFeatures();
  const featureId = await upsertFeature(spec, { name: seed.featureName, params: {} }, features, undefined);
  // A newly added seed lands at the end of the list; existing test features would then come before it.
  if (features.length && features[0].featureId !== featureId) {
    console.log(`! "${seed.featureName}" is not the first feature. Drag it to the top of the feature list in Onshape.`);
  }
  return (await reportStatus([featureId])) ? 0 : 2;
}

async function status(): Promise<number> {
  const caseNames = new Set(loadCases().cases.map((testCase) => testCase.name));
  const { features } = await getFeatures();
  const ids = features
    .filter((feature: any) => feature.featureType === seed.featureType || caseNames.has(feature.name))
    .map((feature: any) => feature.featureId);
  return (await reportStatus(ids)) ? 0 : 2;
}

async function check(): Promise<number> {
  const diagnostics = await checkModule(readProjectFile(paths.feature));
  if (!diagnostics.length) console.log("no errors or warnings");
  for (const d of diagnostics) console.log(`${d.level} ${d.type} line ${d.line}: ${d.message}${d.text ? `\n    ${d.text}` : ""}`);
  return diagnostics.some((d) => d.level === "ERROR") ? 1 : 0;
}

/** Runs one case inside eval at the case's rollback position, so std exceptions come back with stack traces. */
async function debug(caseName: string): Promise<number> {
  const caseFile = loadCases();
  const testCase = caseFile.cases.find((candidate) => candidate.name.includes(caseName));
  if (!testCase) throw new DevError(`No case name contains "${caseName}"`);
  const { features } = await getFeatures();
  const index = features.findIndex((feature: any) => feature.name === testCase.name);
  const specs = await api("GET", featureStudioPath() + "/featurespecs");
  const spec = specs.featureSpecs?.find((candidate: any) => candidate.featureType === caseFile.featureType);
  if (!spec) throw new DevError(`${paths.feature} does not compile in Onshape; run "bun scripts/dev.ts check" or "bun run dev" first`);

  const source = readProjectFile(paths.feature);
  const featureConst = source.match(/export\s+const\s+(\w+)\s*=\s*defineFeature/)?.[1] ?? spec.featureType;
  const definition = featureScriptDefinition(spec, testCase, findSeedFeatureId(features));
  const script = debugScript(source, `${featureConst}(context, makeId("${debugFeatureId}"), ${definition});`);
  const result = await evaluate(script, index >= 0 ? index : -1);
  for (const line of formatDebugNotices(result.notices, source, script, paths.feature)) console.log(line);
  if (result.console) console.log("console:\n" + result.console.trimEnd());
  console.log("result:", JSON.stringify(result.value));
  return result.notices.some((notice) => notice.level === "ERROR") ? 1 : 0;
}

async function main(): Promise<number> {
  switch (command) {
    case "run": return run();
    case "seed": return seedTestDocument();
    case "status": return status();
    case "check": return check();
    case "debug": return debug(commandArgs[0] ?? "");
    case "eval":
      console.log(JSON.stringify(await evaluateOrThrow(commandArgs[0]), null, 2));
      return 0;
    case "faces": {
      const { features } = await getFeatures();
      console.log(await resolveQuery(commandArgs[0], findSeedFeatureId(features)));
      return 0;
    }
    case "suppress":
    case "unsuppress":
      await setSuppressed(commandArgs, command === "suppress");
      return 0;
    case "help":
      console.log(HELP);
      return 0;
    default:
      console.error(`Unknown command "${command}"\n\n${HELP}`);
      return 1;
  }
}

try {
  process.exit(await main());
} catch (error) {
  // DevError and ApiError carry user-facing messages; anything else is a bug and keeps its stack.
  if (!(error instanceof DevError || error instanceof ApiError)) throw error;
  console.error("✗ " + error.message);
  process.exit(1);
}
