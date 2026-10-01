// Knurl dev loop. Run from the project root (Bun loads .env from the cwd).
//
//   bun scripts/dev.ts            push knurl.fs -> compile check -> upsert test cases -> report
//   bun scripts/dev.ts seed       push scripts/testgeom.fs and make sure the seed feature exists
//   bun scripts/dev.ts status     report only (no push, no case updates)
//   bun scripts/dev.ts eval "<function(context is Context, queries) {...}>"
//   bun scripts/dev.ts faces "<FS query expression>"   list deterministic ids, e.g. faces 'qCreatedBy($seed + "cylinder", EntityType.FACE)'
//
// Flags: --force (push even if the Feature Studio was edited in Onshape since our last push)
//        --case <substring> (only upsert matching cases)
//
// API calls follow the Onshape OpenAPI spec (/api/v17):
//   GET/POST /featurestudios/d/{did}/w/{wid}/e/{eid}         contents, sourceMicroversion, rejectMicroversionSkew
//   GET      /featurestudios/d/{did}/w/{wid}/e/{eid}/featurespecs   (empty when the studio does not compile)
//   GET/POST /partstudios/d/{did}/w/{wid}/e/{eid}/features[/featureid/{fid}]
//   POST     /partstudios/d/{did}/w/{wid}/e/{eid}/featurescript    (eval; supports rollbackBarIndex)
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { api, ApiError, cfg, psPath } from "./onshape.ts";

const ROOT = new URL("..", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
const STATE_DIR = ROOT + ".devstate/";
const SEED_STUDIO_NAME = "Test geometry";
const SEED_FEATURE_TYPE = "knurlTestGeometry";
const SEED_FEATURE_NAME = "Knurl test geometry";

const args = Bun.argv.slice(2);
const flag = (name: string) => args.includes(name);
const opt = (name: string) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const positional = args.filter((a, i) => !a.startsWith("--") && !(i > 0 && args[i - 1] === "--case"));
const command = positional[0] ?? "run";

// ---------- small utils ----------
const sha = (s: string) => createHash("sha256").update(s.replace(/\r\n/g, "\n")).digest("hex");
const readText = (p: string) => readFileSync(ROOT + p, "utf8").replace(/\r\n/g, "\n");
function loadState(): Record<string, string> {
  try { return JSON.parse(readFileSync(STATE_DIR + "state.json", "utf8")); } catch { return {}; }
}
function saveState(s: Record<string, string>) {
  mkdirSync(STATE_DIR, { recursive: true });
  writeFileSync(STATE_DIR + "state.json", JSON.stringify(s, null, 2));
}
function fail(msg: string): never { console.error("✗ " + msg); process.exit(1); }

// ---------- Feature Studio ----------
const studioPath = (eid: string) => `/featurestudios/d/${cfg.did}/w/${cfg.wid}/e/${eid}`;

async function findElement(name: string, type: string): Promise<string | undefined> {
  const els = await api<any[]>("GET", `/documents/d/${cfg.did}/w/${cfg.wid}/elements`);
  return els.find((e) => e.name === name && e.elementType === type)?.id;
}

/** Push contents to a Feature Studio. Refuses to clobber edits made in Onshape since our last push (unless --force). */
async function pushStudio(eid: string, contents: string, label: string): Promise<void> {
  const state = loadState();
  for (let attempt = 1; ; attempt++) {
    const remote = await api("GET", studioPath(eid));
    const remoteText = String(remote.contents ?? "").replace(/\r\n/g, "\n");
    if (sha(remoteText) === sha(contents)) { console.log(`• ${label}: unchanged`); state[eid] = sha(contents); saveState(state); return; }
    const lastPushed = state[eid];
    if (sha(remoteText) !== lastPushed) {
      mkdirSync(STATE_DIR, { recursive: true });
      const backup = `${STATE_DIR}backup-${label.replace(/\W+/g, "_")}-${Date.now()}.fs`;
      writeFileSync(backup, remoteText);
      if (lastPushed && !flag("--force")) {
        fail(`${label} was edited in Onshape since the last push. Remote copy saved to ${backup}. Merge it into the local file, or rerun with --force to overwrite.`);
      }
      console.log(`• ${label}: remote copy backed up to ${backup}`);
    }
    try {
      await api("POST", studioPath(eid), {
        btType: "BTFeatureStudioContents-2239",
        contents,
        serializationVersion: remote.serializationVersion,
        sourceMicroversion: remote.sourceMicroversion,
        rejectMicroversionSkew: true, // 409 if the document moved since our GET -> re-read and retry
      });
      state[eid] = sha(contents);
      saveState(state);
      console.log(`• ${label}: pushed`);
      return;
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && attempt < 3) { console.log(`• ${label}: microversion skew (409), retrying`); continue; }
      throw e;
    }
  }
}

/** Returns the feature spec, or exits with a compile failure report. */
async function compileCheck(eid: string, featureType: string, label: string): Promise<any> {
  const specs = await api("GET", studioPath(eid) + "/featurespecs");
  const spec = specs.featureSpecs?.find((s: any) => s.featureType === featureType);
  if (spec) { console.log(`• ${label}: compiles, feature type "${featureType}" found`); return spec; }
  if (!specs.featureSpecs?.length) {
    // The public API exposes no compile messages for Feature Studios; the editor tab shows them.
    fail(`${label} does not compile (no feature specs). Open the Feature Studio tab in Onshape to see the error, or report it back.`);
  }
  fail(`${label} compiles, but has no feature type "${featureType}". Found: ${specs.featureSpecs.map((s: any) => s.featureType).join(", ")}`);
}

// ---------- Part Studio ----------
async function getFeatures() {
  return api("GET", psPath() + "/features", undefined, { rollbackBarIndex: -1 });
}

/** Convert a BTFSValue tree into plain JS. */
function fsToJs(v: any): any {
  if (v == null) return null;
  const t: string = v.btType ?? "";
  if (t.endsWith("BTFSValueMap")) return Object.fromEntries((v.value ?? []).map((e: any) => [String(fsToJs(e.key)), fsToJs(e.value)]));
  if (t.endsWith("BTFSValueArray")) return (v.value ?? []).map(fsToJs);
  if (t.endsWith("BTFSValueWithUnits")) return `${v.value} ${JSON.stringify(v.unitToPower ?? {})}`;
  return v.value ?? null;
}

async function evalFS(script: string, rollbackBarIndex = -1): Promise<any> {
  const r = await api("POST", psPath() + "/featurescript", { btType: "BTFeatureScriptEvalCall-2377", script }, { rollbackBarIndex });
  const errors = (r.notices ?? []).filter((n: any) => n.level === "ERROR");
  if (errors.length) {
    throw new Error("Eval failed:\n" + errors.map((n: any) => `  ${n.type}: ${n.message}` + (n.stackTrace?.[0] ? ` (line ${n.stackTrace[0].line}, col ${n.stackTrace[0].column})` : "")).join("\n"));
  }
  if (r.console) console.log(r.console.trimEnd());
  return fsToJs(r.result);
}

async function seedId(features: any[]): Promise<string | undefined> {
  const seeds = features.filter((f: any) => f.featureType === SEED_FEATURE_TYPE);
  if (seeds.length > 1) console.log(`! ${seeds.length} seed features found; using "${SEED_FEATURE_NAME}". Delete the extras in Onshape.`);
  return (seeds.find((f: any) => f.name === SEED_FEATURE_NAME) ?? seeds[0])?.featureId;
}

function expandQuery(expr: string, seed: string | undefined): string {
  if (expr.includes("$seed")) {
    if (!seed) fail(`Query uses $seed but there is no "${SEED_FEATURE_NAME}" feature. Run: bun scripts/dev.ts seed`);
    expr = expr.replaceAll("$seed", `makeId("${seed}")`);
  }
  return expr;
}

async function resolveQuery(expr: string, seed: string | undefined, rollbackBarIndex: number): Promise<string[]> {
  const ids: string[] = await evalFS(
    `function(context is Context, queries) { return transientQueriesToStrings(evaluateQuery(context, ${expandQuery(expr, seed)})); }`,
    rollbackBarIndex,
  );
  return ids ?? [];
}

type Case = { name: string; featureType?: string; suppressed?: boolean; params: Record<string, any> };

/** Build BTM parameters from case values, typed by the feature spec. */
async function buildParams(spec: any, c: Case, seed: string | undefined, rollbackBarIndex: number) {
  const out: any[] = [];
  for (const [pid, val] of Object.entries(c.params)) {
    const ps = spec.parameters.find((p: any) => p.parameterId === pid);
    if (!ps) fail(`Case "${c.name}": unknown parameter "${pid}". Spec has: ${spec.parameters.map((p: any) => p.parameterId).join(", ")}`);
    const t: string = ps.btType;
    if (t.startsWith("BTParameterSpecQuantity")) {
      out.push({ btType: "BTMParameterQuantity-147", parameterId: pid, expression: String(val) });
    } else if (t.startsWith("BTParameterSpecEnum")) {
      if (ps.options && !ps.options.includes(val)) fail(`Case "${c.name}": ${pid}="${val}" is not one of ${ps.options.join(", ")}`);
      out.push({ btType: "BTMParameterEnum-145", parameterId: pid, enumName: ps.enumName, namespace: ps.namespace ?? "", value: String(val) });
    } else if (t.startsWith("BTParameterSpecBoolean")) {
      out.push({ btType: "BTMParameterBoolean-144", parameterId: pid, value: Boolean(val) });
    } else if (t.startsWith("BTParameterSpecString")) {
      out.push({ btType: "BTMParameterString-149", parameterId: pid, value: String(val) });
    } else if (t.startsWith("BTParameterSpecQuery")) {
      const ids = await resolveQuery(String(val), seed, rollbackBarIndex);
      if (!ids.length) fail(`Case "${c.name}": query for "${pid}" matched nothing: ${val}`);
      out.push({ btType: "BTMParameterQueryList-148", parameterId: pid, queries: [{ btType: "BTMIndividualQuery-138", deterministicIds: ids }] });
    } else {
      fail(`Case "${c.name}": parameter type ${t} for "${pid}" not supported by dev.ts yet`);
    }
  }
  return out;
}

/** Add or update a feature (matched by name and type). Returns its featureId. */
async function upsertFeature(spec: any, c: Case, features: any[], seed: string | undefined): Promise<string> {
  const idx = features.findIndex((f: any) => f.name === c.name && f.featureType === spec.featureType);
  const existing = idx >= 0 ? features[idx] : undefined;
  // Resolve queries against the model as it is just before this feature.
  const parameters = await buildParams(spec, c, seed, existing ? idx : -1);
  const feature = {
    btType: "BTMFeature-134",
    featureType: spec.featureType,
    namespace: spec.namespace,
    name: c.name,
    suppressed: !!c.suppressed,
    parameters,
    ...(existing ? { featureId: existing.featureId } : {}),
  };
  const body = { btType: "BTFeatureDefinitionCall-1406", feature };
  const r = existing
    ? await api("POST", `${psPath()}/features/featureid/${existing.featureId}`, body)
    : await api("POST", `${psPath()}/features`, body);
  console.log(`• ${existing ? "updated" : "added"} "${c.name}"`);
  return r.feature.featureId;
}

/** Status plus error/warning/info text for the given features. */
async function report(fids: string[]) {
  const feats = await getFeatures();
  const byId = new Map(feats.features.map((f: any) => [f.featureId, f]));
  const msgs = fids.length
    ? await evalFS(`function(context is Context, queries) {
        var out = {};
        for (var fid in ${JSON.stringify(fids)})
        {
            const id = makeId(fid);
            out[fid] = { "error" : getFeatureError(context, id), "warning" : getFeatureWarning(context, id), "info" : getFeatureInfo(context, id) };
        }
        out["__bodies"] = size(evaluateQuery(context, qAllModifiableSolidBodies()));
        out["__faces"] = size(evaluateQuery(context, qOwnedByBody(qAllModifiableSolidBodies(), EntityType.FACE)));
        return out;
    }`)
    : {};
  console.log("\nFeature status:");
  for (const fid of fids) {
    const f: any = byId.get(fid);
    const st = feats.featureStates?.[fid]?.featureStatus ?? "?";
    const m = msgs[fid] ?? {};
    const icon = st === "OK" ? "✓" : st === "WARNING" ? "!" : st === "INFO" ? "i" : "✗";
    console.log(`  ${icon} ${f?.name ?? fid}: ${st}${f?.suppressed ? " (suppressed)" : ""}`);
    for (const k of ["error", "warning", "info"]) if (m[k] != null) console.log(`      ${k}: ${m[k]}`);
  }
  if (msgs.__bodies != null) console.log(`  model: ${msgs.__bodies} solid bodies, ${msgs.__faces} faces`);
  const bad = fids.filter((fid) => feats.featureStates?.[fid]?.featureStatus === "ERROR");
  return bad.length === 0;
}

// ---------- commands ----------
async function cmdSeed() {
  let eid = await findElement(SEED_STUDIO_NAME, "FEATURESTUDIO");
  if (!eid) {
    const r = await api("POST", `/featurestudios/d/${cfg.did}/w/${cfg.wid}`, { name: SEED_STUDIO_NAME });
    eid = r.id as string;
    console.log(`• created Feature Studio "${SEED_STUDIO_NAME}"`);
  }
  await pushStudio(eid, readText("scripts/testgeom.fs"), SEED_STUDIO_NAME);
  const spec = await compileCheck(eid, SEED_FEATURE_TYPE, SEED_STUDIO_NAME);
  const feats = await getFeatures();
  const fid = await upsertFeature(spec, { name: SEED_FEATURE_NAME, params: {} }, feats.features, undefined);
  const after = await getFeatures();
  if (after.features[0]?.featureId !== fid) {
    console.log(`! "${SEED_FEATURE_NAME}" is not the first feature. Drag it to the top of the feature list in Onshape.`);
  }
  await report([fid]);
}

async function cmdRun() {
  const casesFile = JSON.parse(readText("scripts/cases.json"));
  await pushStudio(cfg.fsEid, readText("knurl.fs"), "knurl.fs");
  const featureType: string = casesFile.featureType;
  const spec = await compileCheck(cfg.fsEid, featureType, "knurl.fs");
  const filter = opt("--case");
  const cases: Case[] = casesFile.cases.filter((c: Case) => !filter || c.name.includes(filter));
  let feats = await getFeatures();
  const seed = await seedId(feats.features);
  const fids: string[] = [];
  for (const c of cases) {
    fids.push(await upsertFeature(spec, c, feats.features, seed));
    feats = await getFeatures();
  }
  const ok = await report(fids);
  process.exit(ok ? 0 : 2);
}

async function cmdStatus() {
  const casesFile = JSON.parse(readText("scripts/cases.json"));
  const feats = await getFeatures();
  const names = new Set(casesFile.cases.map((c: Case) => c.name));
  await report(feats.features.filter((f: any) => f.featureType === SEED_FEATURE_TYPE || names.has(f.name)).map((f: any) => f.featureId));
}

try {
  if (command === "run") await cmdRun();
  else if (command === "seed") await cmdSeed();
  else if (command === "status") await cmdStatus();
  else if (command === "eval") console.log(JSON.stringify(await evalFS(positional[1]), null, 2));
  else if (command === "faces") {
    const feats = await getFeatures();
    console.log(await resolveQuery(positional[1], await seedId(feats.features), -1));
  } else fail(`Unknown command "${command}"`);
} catch (e) {
  if (e instanceof ApiError) fail(e.message);
  fail((e as Error).message);
}
