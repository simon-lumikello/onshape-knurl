// Feature Studio sync: push a local .fs file, protect edits made in Onshape, check that it compiles.
import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { paths, ROOT } from "./config.ts";
import { checkModule } from "./fscheck.ts";
import { api, elementsPath, featureStudioPath, newFeatureStudioPath, withConflictRetry } from "./onshape.ts";

export class DevError extends Error {}

const stateDir = join(ROOT, paths.stateDir);
const stateFile = join(stateDir, "state.json");

/** Reads a project file with normalized line endings. */
export function readProjectFile(relativePath: string): string {
  return readFileSync(join(ROOT, relativePath), "utf8").replace(/\r\n/g, "\n");
}

const hash = (text: string) => createHash("sha256").update(text.replace(/\r\n/g, "\n")).digest("hex");

/** Hash of the contents we last pushed, per Feature Studio element id. */
function loadPushState(): Record<string, string> {
  try {
    return JSON.parse(readFileSync(stateFile, "utf8"));
  } catch {
    return {};
  }
}

function savePushState(state: Record<string, string>) {
  mkdirSync(stateDir, { recursive: true });
  writeFileSync(stateFile, JSON.stringify(state, null, 2));
}

/** Element id of the Feature Studio with this name, creating it if `create` is set. */
export async function findFeatureStudio(name: string, create = false): Promise<string | undefined> {
  const elements = await api<any[]>("GET", elementsPath());
  const existing = elements.find((element) => element.name === name && element.elementType === "FEATURESTUDIO")?.id;
  if (existing || !create) return existing;
  const created = await api("POST", newFeatureStudioPath(), { name });
  console.log(`• created Feature Studio "${name}"`);
  return created.id;
}

/**
 * Pushes `contents` to a Feature Studio.
 * If the Feature Studio was edited in Onshape since our last push, the remote copy is saved to the
 * state directory and the push stops (unless `force`), so manual edits are never lost silently.
 * The write sends the microversion it read with rejectMicroversionSkew, so a concurrent change
 * fails with 409 and is retried from a fresh read.
 */
export async function pushFeatureStudio(elementId: string, contents: string, label: string, force: boolean): Promise<void> {
  const state = loadPushState();
  await withConflictRetry(async () => {
    const remote = await api("GET", featureStudioPath(elementId));
    const remoteText = String(remote.contents ?? "").replace(/\r\n/g, "\n");
    if (hash(remoteText) === hash(contents)) {
      console.log(`• ${label}: unchanged`);
      return;
    }
    const lastPushed = state[elementId];
    if (hash(remoteText) !== lastPushed) {
      mkdirSync(stateDir, { recursive: true });
      const backup = join(stateDir, `backup-${label.replace(/\W+/g, "_")}-${Date.now()}.fs`);
      writeFileSync(backup, remoteText);
      if (lastPushed && !force) {
        throw new DevError(`${label} was edited in Onshape since the last push. The remote copy is saved to ${backup}. ` +
          "Merge it into the local file, or rerun with --force to overwrite it.");
      }
      console.log(`• ${label}: remote copy backed up to ${backup}`);
    }
    await api("POST", featureStudioPath(elementId), {
      btType: "BTFeatureStudioContents-2239",
      contents,
      serializationVersion: remote.serializationVersion,
      sourceMicroversion: remote.sourceMicroversion,
      rejectMicroversionSkew: true,
    });
    console.log(`• ${label}: pushed`);
  }, () => console.log(`• ${label}: document changed during the push (409), retrying`));
  state[elementId] = hash(contents);
  savePushState(state);
}

/**
 * Returns the spec of `featureType` from a Feature Studio. An empty spec list means the studio does
 * not compile; the API gives no messages for that, so they are fetched through fscheck.
 */
export async function requireFeatureSpec(elementId: string, featureType: string, label: string, source: string): Promise<any> {
  const response = await api("GET", featureStudioPath(elementId) + "/featurespecs");
  const specs: any[] = response.featureSpecs ?? [];
  const spec = specs.find((candidate) => candidate.featureType === featureType);
  if (spec) {
    console.log(`• ${label}: compiles, feature type "${featureType}" found`);
    return spec;
  }
  if (specs.length) {
    throw new DevError(`${label} compiles, but has no feature type "${featureType}". Found: ${specs.map((s) => s.featureType).join(", ")}`);
  }
  const diagnostics = await checkModule(source);
  const lines = diagnostics.map((d) => `  ${d.level} ${d.type} line ${d.line}: ${d.message}${d.text ? `\n      ${d.text}` : ""}`);
  throw new DevError(`${label} does not compile.\n${lines.join("\n") || "  (no details from eval; open the Feature Studio in Onshape)"}`);
}
